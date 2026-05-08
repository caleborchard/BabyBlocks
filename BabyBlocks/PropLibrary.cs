using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace BabyBlocks
{
    public class PropMeshPart
    {
        public Mesh       mesh;
        public Material[] materials;
        public Vector3    localPosition;
        public Quaternion localRotation;
        public Vector3    localScale;
    }

    public class PropInfo
    {
        public readonly string id;
        public          string displayName;

        public List<PropMeshPart> parts     = new();
        public bool               isLoaded;
        public bool               isInvalid;

        public int  gpuiIndex  = -1;
        public bool IsGpui     => gpuiIndex >= 0;

        public bool HasMesh    => parts != null && parts.Count > 0;
        public bool IsPrimitive => id.StartsWith("primitive://", StringComparison.Ordinal);

        AsyncOperationHandle<GameObject> _handle;

        public PropInfo(string key, string name = null)
        {
            id          = key;
            displayName = name ?? Path.GetFileNameWithoutExtension(key)
                .Replace("_player", "").Replace("_Player", "");
        }

        public void SetHandle(AsyncOperationHandle<GameObject> h) => _handle = h;
    }

    public static class PropLibrary
    {
        public static bool IncludePlayerProps = false;
        public static bool IncludeMeshes      = true;
        public static bool IncludeAnimals     = false;
        public static bool IncludeHats        = false;
        public static bool IncludeNPCs        = false;

        static readonly List<PropInfo>               _all      = new();
        static readonly List<PropInfo>               _filtered = new();
        static readonly Dictionary<string, PropInfo> _byId     = new();

        public static IReadOnlyList<PropInfo> AllProps      => _all;
        public static IReadOnlyList<PropInfo> FilteredProps => _filtered;
        public static bool                    IsInitialized { get; private set; }

        public static PropInfo FindById(string id) => _byId.TryGetValue(id, out var p) ? p : null;

        static readonly string[] PrimitiveNames = { "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad" };

        public static void Init()
        {
            _all.Clear();
            _filtered.Clear();
            _byId.Clear();
            IsInitialized = false;

            // Always add Unity primitives first (pinned at top, not sorted).
            foreach (var name in PrimitiveNames)
                _all.Add(new PropInfo($"primitive://{name}", name));

            int primitiveCount = _all.Count;

            try { EnumerateFromCatalog(); }
            catch (Exception e) { MelonLogger.Error($"[PropLibrary] Catalog parse failed: {e.Message}"); }

            // Sort only the non-primitive entries.
            _all.Sort(primitiveCount, _all.Count - primitiveCount,
                Comparer<PropInfo>.Create((a, b) =>
                    string.Compare(a.displayName, b.displayName, StringComparison.OrdinalIgnoreCase)));

            foreach (var p in _all) _byId[p.id] = p;
            BuildFiltered();
            IsInitialized = true;
            MelonLogger.Msg($"[PropLibrary] {_all.Count} props enumerated ({primitiveCount} primitives), {_filtered.Count} after filter.");
        }

        // ── Catalog enumeration ──────────────────────────────────────────────

        static void EnumerateFromCatalog()
        {
            string catalogPath = Path.Combine(Application.streamingAssetsPath, "aa", "catalog.json");
            if (!File.Exists(catalogPath))
            {
                MelonLogger.Error($"[PropLibrary] catalog.json not found at {catalogPath}");
                return;
            }

            string json = File.ReadAllText(catalogPath);
            var    seen = new HashSet<string>(StringComparer.Ordinal);

            ScanTextForPaths(json, seen);

            int kdIdx = json.IndexOf("\"m_KeyDataString\"", StringComparison.Ordinal);
            if (kdIdx >= 0)
            {
                int valStart = json.IndexOf('"', kdIdx + 17) + 1;
                int valEnd   = json.IndexOf('"', valStart);
                if (valStart > 0 && valEnd > valStart)
                {
                    try
                    {
                        byte[] bytes       = Convert.FromBase64String(json.Substring(valStart, valEnd - valStart));
                        string decodedKeys = Encoding.UTF8.GetString(bytes);
                        ScanTextForPaths(decodedKeys, seen);
                    }
                    catch (Exception e)
                    {
                        MelonLogger.Warning($"[PropLibrary] m_KeyDataString decode failed: {e.Message}");
                    }
                }
            }

            MelonLogger.Msg($"[PropLibrary] Catalog scan → {_all.Count} props.");
        }

        static void ScanTextForPaths(string text, HashSet<string> seen)
        {
            int i = 0;
            while (i < text.Length)
            {
                int start = text.IndexOf("Assets/", i, StringComparison.Ordinal);
                if (start < 0) break;
                int end = start;
                while (end < text.Length)
                {
                    char c = text[end];
                    if (c == '"' || c == '\0' || c < ' ') break;
                    end++;
                }
                i = end + 1;
                string entry = text.Substring(start, end - start);
                if (!entry.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsLowerLodVariant(entry)) continue;
                if (!ShouldInclude(entry)) continue;
                if (!seen.Add(entry)) continue;
                _all.Add(new PropInfo(entry));
            }
        }

        static bool IsLowerLodVariant(string key)
        {
            string name  = Path.GetFileNameWithoutExtension(key);
            int    idx   = name.IndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            int after = idx + 4;
            if (after >= name.Length) return false;
            char c = name[after];
            return c >= '1' && c <= '9';
        }

        static bool ShouldInclude(string key)
        {
            if (IncludePlayerProps && key.StartsWith("Assets/_Props/_PlayerProps/", StringComparison.Ordinal)) return true;
            if (IncludeMeshes      && key.StartsWith("Assets/Meshes/",              StringComparison.Ordinal)) return true;
            if (IncludeAnimals     && key.StartsWith("Assets/ANIMALS/",             StringComparison.Ordinal)) return true;
            if (IncludeHats        && key.StartsWith("Assets/Prefabs/Hats/",        StringComparison.Ordinal)) return true;
            if (IncludeNPCs        && key.StartsWith("Assets/Prefabs/NPCS/",        StringComparison.Ordinal)) return true;
            return false;
        }

        static void BuildFiltered()
        {
            _filtered.Clear();
            foreach (var p in _all) _filtered.Add(p);
        }

        // ── GPUI prop scan ───────────────────────────────────────────────────

        // Called once BestRegionLoader is live. Identifies GPUI _player prefabs in
        // loadedProps (no MeshRenderer, only MeshCollider), then for each one looks
        // up the matching visual prefab in the addressable catalog (same name minus
        // "_player" suffix, under Assets/_Props/), loads it via Addressables to get
        // real meshes + materials, and falls back to collider-only if that fails.
        public static void ScanGpuiProps()
        {
            var brl = BestRegionLoader.me;
            if (brl == null)
            {
                MelonLogger.Warning("[PropLibrary] BestRegionLoader not ready — call ScanGpuiProps later.");
                return;
            }

            var loaded = brl.loadedProps;
            if (loaded == null || loaded.Length == 0)
            {
                MelonLogger.Warning("[PropLibrary] loadedProps is null/empty.");
                return;
            }

            // Build basename → addressable path lookup from the catalog.
            // Visual GPUI props live under Assets/_Props/ without the _player suffix.
            var visualLookup = BuildGpuiVisualLookup();
            MelonLogger.Msg($"[PropLibrary] Visual prop lookup: {visualLookup.Count} entries.");

            int insertAt = PrimitiveNames.Length;
            int added    = 0;
            int skipped  = 0;
            int gpuiIdx  = 0;

            for (int i = 0; i < loaded.Length; i++)
            {
                var prefabGO = loaded[i];
                if (prefabGO == null) continue;

                bool hasRenderer = prefabGO.GetComponentInChildren<MeshRenderer>() != null
                                || prefabGO.GetComponentInChildren<SkinnedMeshRenderer>() != null;
                if (hasRenderer) continue;

                bool hasCollider = prefabGO.GetComponentInChildren<MeshCollider>() != null;
                if (!hasCollider) continue;

                int    gi     = gpuiIdx++;
                string gpuiId = $"gpui://{gi}";
                if (_byId.ContainsKey(gpuiId)) { skipped++; continue; }

                string baseName = prefabGO.name
                    .Replace("(Clone)", "")
                    .Replace("_player", "")
                    .Replace("_Player", "")
                    .Trim();

                var info = new PropInfo(gpuiId, baseName);
                info.gpuiIndex = gi;

                // Try to load the visual prefab (has mesh + materials).
                if (visualLookup.TryGetValue(baseName, out string visualPath))
                {
                    MelonLogger.Msg($"[PropLibrary] GPUI[{gi}] \"{baseName}\" → loading \"{visualPath}\"");
                    try
                    {
                        var handle   = Addressables.LoadAssetAsync<GameObject>(visualPath);
                        var visualGO = handle.WaitForCompletion();
                        if (visualGO != null)
                        {
                            var instance = UnityEngine.Object.Instantiate(
                                visualGO, new Vector3(0f, -99999f, 0f), Quaternion.identity);
                            ExtractParts(instance, info);
                            if (!info.HasMesh)
                            {
                                MelonLogger.Warning($"[PropLibrary] GPUI[{gi}] visual prefab loaded but ExtractParts found no mesh — dumping hierarchy:");
                                LogHierarchy(instance);
                            }
                            UnityEngine.Object.Destroy(instance);
                        }
                        else
                        {
                            MelonLogger.Warning($"[PropLibrary] GPUI[{gi}] Addressables returned null for \"{visualPath}\"");
                        }
                    }
                    catch (Exception e)
                    {
                        MelonLogger.Warning($"[PropLibrary] GPUI[{gi}] visual load failed: {e.Message}");
                    }
                }
                else
                {
                    MelonLogger.Warning($"[PropLibrary] GPUI[{gi}] \"{baseName}\" — no visual path in catalog, using collider fallback");
                }

                // Fall back to collision-mesh extraction if visual load didn't yield parts.
                if (!info.HasMesh)
                {
                    var instance = UnityEngine.Object.Instantiate(
                        prefabGO, new Vector3(0f, -99999f, 0f), Quaternion.identity);
                    try   { ExtractPartsFromColliders(instance, info); }
                    catch (Exception e) { MelonLogger.Warning($"[PropLibrary] GPUI[{gi}] collider extract failed: {e.Message}"); }
                    UnityEngine.Object.Destroy(instance);
                }

                info.isLoaded  = true;
                info.isInvalid = !info.HasMesh;

                if (info.HasMesh)
                {
                    _all.Insert(insertAt, info);
                    _byId[gpuiId] = info;
                    insertAt++;
                    added++;
                    MelonLogger.Msg($"[PropLibrary] GPUI[{gi}] \"{baseName}\" — {info.parts.Count} part(s)");
                }
                else
                {
                    skipped++;
                    MelonLogger.Warning($"[PropLibrary] GPUI[{gi}] \"{baseName}\" — no mesh found, skipping");
                }
            }

            BuildFiltered();
            MelonLogger.Msg($"[PropLibrary] GPUI scan complete: {added} added, {skipped} skipped.");
        }

        // Scans the addressable catalog for visual prop prefabs under Assets/_Props/
        // and returns a dictionary from basename (no extension, no _player suffix) to
        // the full addressable path.
        static Dictionary<string, string> BuildGpuiVisualLookup()
        {
            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string catalogPath = Path.Combine(Application.streamingAssetsPath, "aa", "catalog.json");
            if (!File.Exists(catalogPath)) return lookup;

            try
            {
                string json = File.ReadAllText(catalogPath);
                AddVisualPathsToLookup(json, lookup);

                int kdIdx = json.IndexOf("\"m_KeyDataString\"", StringComparison.Ordinal);
                if (kdIdx >= 0)
                {
                    int valStart = json.IndexOf('"', kdIdx + 17) + 1;
                    int valEnd   = json.IndexOf('"', valStart);
                    if (valStart > 0 && valEnd > valStart)
                    {
                        byte[] bytes   = Convert.FromBase64String(json.Substring(valStart, valEnd - valStart));
                        string decoded = Encoding.UTF8.GetString(bytes);
                        AddVisualPathsToLookup(decoded, lookup);
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropLibrary] GPUI visual lookup build failed: {e.Message}");
            }

            return lookup;
        }

        static void AddVisualPathsToLookup(string text, Dictionary<string, string> lookup)
        {
            int i = 0;
            while (i < text.Length)
            {
                int start = text.IndexOf("Assets/_Props/", i, StringComparison.Ordinal);
                if (start < 0) break;
                int end = start;
                while (end < text.Length)
                {
                    char c = text[end];
                    if (c == '"' || c == '\0' || c < ' ') break;
                    end++;
                }
                i = end + 1;
                string path = text.Substring(start, end - start);
                if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;

                string name = Path.GetFileNameWithoutExtension(path);
                // Skip _player collision prefabs and lower LOD variants.
                if (name.EndsWith("_player", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith("_Player", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsLowerLodVariant(path)) continue;

                if (!lookup.ContainsKey(name))
                    lookup[name] = path;
            }
        }

        // _player prefabs have MeshColliders but no MeshRenderers; extract their
        // shared meshes so we have geometry to display in the level editor.
        // Materials will be null (renders grey) until GPUI rendering is wired up.
        static void ExtractPartsFromColliders(GameObject root, PropInfo info)
        {
            var colliders = new List<MeshCollider>();
            CollectMeshColliders(root.transform, colliders);

            var rootT = root.transform;
            foreach (var mc in colliders)
            {
                if (mc.sharedMesh == null) continue;
                AddPart(info, mc.sharedMesh, null, mc.transform, rootT);
            }
        }

        static void CollectMeshColliders(Transform t, List<MeshCollider> result)
        {
            var mc = t.GetComponent<MeshCollider>();
            if (mc != null && mc.sharedMesh != null) result.Add(mc);
            for (int i = 0; i < t.childCount; i++)
                CollectMeshColliders(t.GetChild(i), result);
        }

        // ── Addressable prop loading ─────────────────────────────────────────

        static void LoadPrimitive(PropInfo info)
        {
            try
            {
                var typeName = info.id.Substring("primitive://".Length);
                var pt       = (PrimitiveType)Enum.Parse(typeof(PrimitiveType), typeName);
                var go       = GameObject.CreatePrimitive(pt);
                go.transform.position = new Vector3(0f, -99999f, 0f);

                ExtractParts(go, info);
                GameObject.Destroy(go);

                info.isLoaded  = true;
                info.isInvalid = !info.HasMesh;
                if (info.HasMesh)
                    MelonLogger.Msg($"[PropLibrary] Loaded primitive: {info.displayName}");
                else
                    MelonLogger.Warning($"[PropLibrary] Primitive {info.displayName} yielded no mesh.");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[PropLibrary] Failed to load primitive {info.id}: {e.Message}");
                info.isLoaded  = true;
                info.isInvalid = true;
            }
        }

        public static void LoadPropData(PropInfo info)
        {
            if (info.isLoaded) return;

            if (info.IsPrimitive)
            {
                LoadPrimitive(info);
                return;
            }

            try
            {
                var handle = Addressables.LoadAssetAsync<GameObject>(info.id);
                var prefab = handle.WaitForCompletion();
                if (prefab == null)
                {
                    MelonLogger.Warning($"[PropLibrary] Null prefab for {info.id}");
                    info.isLoaded  = true;
                    info.isInvalid = true;
                    return;
                }

                var instance = UnityEngine.Object.Instantiate(
                    prefab, new Vector3(0f, -99999f, 0f), Quaternion.identity);

                ExtractParts(instance, info);

                if (!info.HasMesh)
                {
                    MelonLogger.Warning($"[PropLibrary] No mesh in {info.displayName} — dumping instance hierarchy:");
                    LogHierarchy(instance);
                }

                UnityEngine.Object.Destroy(instance);

                info.SetHandle(handle);
                info.isLoaded = true;

                if (!info.HasMesh)
                    info.isInvalid = true;
                else
                    MelonLogger.Msg($"[PropLibrary] Loaded {info.displayName} ({info.parts.Count} part(s))");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[PropLibrary] Failed to load {info.id}: {e.Message}");
                info.isLoaded  = true;
                info.isInvalid = true;
            }
        }

        // ── Hierarchy logging ────────────────────────────────────────────────

        static void LogHierarchy(GameObject root)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"  {root.name} (root)");
            for (int i = 0; i < root.transform.childCount; i++)
                AppendNode(root.transform.GetChild(i), sb, 1);
            MelonLogger.Msg($"[PropLibrary] {sb}");
        }

        static void AppendNode(Transform t, StringBuilder sb, int depth)
        {
            var indent = new string(' ', depth * 2);
            var go     = t.gameObject;

            var names = new StringBuilder();
            var comps = go.GetComponents<Component>();
            if (comps != null)
            {
                for (int i = 0; i < comps.Length; i++)
                {
                    if (comps[i] == null) continue;
                    if (names.Length > 0) names.Append(", ");
                    string n;
                    try   { n = comps[i].GetType().Name; }
                    catch { n = "?"; }
                    names.Append(n);
                }
            }

            var hints = new StringBuilder();
            if (go.GetComponent<MeshFilter>()          != null) hints.Append(" +MF");
            if (go.GetComponent<MeshRenderer>()        != null) hints.Append(" +MR");
            if (go.GetComponent<SkinnedMeshRenderer>() != null) hints.Append(" +SMR");
            var rend = go.GetComponent<Renderer>();
            if (rend != null && go.GetComponent<MeshRenderer>() == null && go.GetComponent<SkinnedMeshRenderer>() == null)
                hints.Append(" +Rend(unknown)");
            if (go.GetComponent<MeshCollider>()  != null) hints.Append(" +MeshCol");
            if (go.GetComponent<BoxCollider>()   != null) hints.Append(" +BoxCol");
            if (go.GetComponent<LODGroup>()      != null) hints.Append(" +LOD");

            sb.AppendLine($"{indent}{go.name}  [{names}]{hints}");
            for (int i = 0; i < t.childCount; i++)
                AppendNode(t.GetChild(i), sb, depth + 1);
        }

        // ── Mesh extraction helpers ──────────────────────────────────────────

        static void ExtractParts(GameObject root, PropInfo info)
        {
            var mrList  = new List<MeshRenderer>();
            var smrList = new List<SkinnedMeshRenderer>();
            CollectRenderers(root.transform, mrList, smrList);

            HashSet<int> lod0Ids = null;
            var lodGroup = root.GetComponent<LODGroup>()
                        ?? root.GetComponentInChildren<LODGroup>();
            if (lodGroup != null && (mrList.Count + smrList.Count) > 0)
            {
                try
                {
                    var lods = lodGroup.GetLODs();
                    if (lods != null && lods.Length > 0 && lods[0].renderers != null)
                    {
                        var lod0Rend = lods[0].renderers;
                        if (lod0Rend.Length > 0)
                        {
                            lod0Ids = new HashSet<int>();
                            for (int j = 0; j < lod0Rend.Length; j++)
                                if (lod0Rend[j] != null) lod0Ids.Add(lod0Rend[j].GetInstanceID());
                        }
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[PropLibrary] GetLODs() failed for {info.displayName}: {e.Message}");
                }
            }

            var rootT = root.transform;
            foreach (var mr in mrList)
            {
                if (lod0Ids != null && !lod0Ids.Contains(mr.GetInstanceID())) continue;
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                AddPart(info, mf.sharedMesh, mr.sharedMaterials, mr.transform, rootT);
            }
            foreach (var smr in smrList)
            {
                if (lod0Ids != null && !lod0Ids.Contains(smr.GetInstanceID())) continue;
                if (smr.sharedMesh == null) continue;
                AddPart(info, smr.sharedMesh, smr.sharedMaterials, smr.transform, rootT);
            }
        }

        static void AddPart(PropInfo info, Mesh mesh, Material[] materials, Transform t, Transform rootT)
        {
            var ws = t.lossyScale;
            var rs = rootT.lossyScale;
            info.parts.Add(new PropMeshPart
            {
                mesh          = mesh,
                materials     = materials,
                localPosition = rootT.InverseTransformPoint(t.position),
                localRotation = Quaternion.Inverse(rootT.rotation) * t.rotation,
                localScale    = new Vector3(
                    rs.x != 0f ? ws.x / rs.x : 1f,
                    rs.y != 0f ? ws.y / rs.y : 1f,
                    rs.z != 0f ? ws.z / rs.z : 1f),
            });
        }

        static void CollectRenderers(Transform t, List<MeshRenderer> mrList, List<SkinnedMeshRenderer> smrList)
        {
            var mr  = t.GetComponent<MeshRenderer>();
            var smr = t.GetComponent<SkinnedMeshRenderer>();
            if (mr  != null) mrList.Add(mr);
            if (smr != null) smrList.Add(smr);

            if (mr == null && smr == null)
            {
                var rend = t.GetComponent<Renderer>();
                if (rend != null)
                {
                    var mrCast  = rend.TryCast<MeshRenderer>();
                    var smrCast = rend.TryCast<SkinnedMeshRenderer>();
                    if      (mrCast  != null) mrList.Add(mrCast);
                    else if (smrCast != null) smrList.Add(smrCast);
                }
            }

            for (int i = 0; i < t.childCount; i++)
                CollectRenderers(t.GetChild(i), mrList, smrList);
        }
    }
}
