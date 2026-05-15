using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MelonLoader;
using UnityEngine;
using System.Collections;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using MelonLoader.Utils;

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

    public class PropColliderPart
    {
        public enum ColliderType { Mesh, Box, Sphere, Capsule }
        public ColliderType type;
        // Mesh
        public Mesh  mesh;
        public bool  convex;
        // Box
        public Vector3 center;
        public Vector3 size;
        // Sphere / Capsule
        public float radius;
        // Capsule
        public float height;
        public int   direction;
        // Local transform relative to prop root
        public Vector3    localPosition;
        public Quaternion localRotation;
        public Vector3    localScale;
    }

    public class PropInfo
    {
        public readonly string id;
        public          string displayName;

        public List<PropMeshPart>      parts         = new();
        public List<PropColliderPart>  colliderParts = new();
        public bool HasColliderParts => colliderParts != null && colliderParts.Count > 0;
        public bool               isLoaded;
        public bool               isInvalid;

        public int gpuiIndex = -1;
        public bool IsGpui => gpuiIndex >= 0;
        public string visualPath     = "";  // GPUI: addressable path to visual prefab
        public string gpuiPrefabName = "";  // GPUI: pool prefab name for collider-only fallback

        public bool HasMesh => parts != null && parts.Count > 0;
        public bool IsPrimitive => id.StartsWith("primitive://", StringComparison.Ordinal);

        public PropInfo(string key, string name = null)
        {
            id = key;
            displayName = name ?? key;
        }
    }

    public static class PropLibrary
    {
        static readonly List<PropInfo> _all = new();
        static readonly List<PropInfo> _filtered = new();
        static readonly Dictionary<string, PropInfo> _byId = new(StringComparer.Ordinal);

        static readonly string[] PrimitiveNames = { "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad" };
        static bool _loggedAssetFolders;
        static Type _bestRegionType;
        static readonly HashSet<string> _gpuiScannedNames = new(StringComparer.OrdinalIgnoreCase);

        static readonly string[] ExcludedAssetPrefixes =
        {
            "Assets/Audio/",
            "Assets/BBitsy/",
            "Assets/CC_Assets/",
            "Assets/Character/",
            "Assets/Decals/",
            "Assets/ExternalPlugins/",
            "Assets/FX/",
            "Assets/Lod/",
            "Assets/Prefabs/Debug/",
            "Assets/Scripts/",
            "Assets/SlicedTerrain/",
            "Assets/TitleAreaAssets/",
            "Assets/_Props/Beacons/",
            "Assets/_Props/Bonfires/",
            "Assets/_Props/FX/",
            "Assets/_Props/Grasses/",
            "Assets/_Props/Rocks_TerrainMat/",
            "Assets/_Props/_PlayerProps/",
        };

        public static IReadOnlyList<PropInfo> AllProps => _all;
        public static IReadOnlyList<PropInfo> FilteredProps => _filtered;

        static string GpuiCachePath =>
            Path.Combine(MelonEnvironment.UserDataDirectory, "BabyBlocks_GpuiCache.txt");

        static bool TryLoadGpuiCache(string catalogPath,
            out List<(string baseName, string id, string visualPath, string prefabName)> entries)
        {
            entries = null;
            try
            {
                if (!File.Exists(GpuiCachePath)) return false;
                var lines = File.ReadAllLines(GpuiCachePath);
                if (lines.Length == 0 || !lines[0].StartsWith("MTIME=")) return false;
                string cachedMtime = lines[0].Substring(6);
                string actualMtime = File.Exists(catalogPath)
                    ? File.GetLastWriteTimeUtc(catalogPath).Ticks.ToString()
                    : "0";
                if (cachedMtime != actualMtime) return false;
                entries = new List<(string, string, string, string)>();
                for (int i = 1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (!line.StartsWith("PROP|")) continue;
                    var p = line.Substring(5).Split('|');
                    if (p.Length >= 4) entries.Add((p[0], p[1], p[2], p[3]));
                }
                return true;
            }
            catch { return false; }
        }

        static void SaveGpuiCache(string catalogPath,
            List<(string baseName, string id, string visualPath, string prefabName)> entries)
        {
            try
            {
                string mtime = File.Exists(catalogPath)
                    ? File.GetLastWriteTimeUtc(catalogPath).Ticks.ToString()
                    : "0";
                var sb = new StringBuilder();
                sb.Append("MTIME=").AppendLine(mtime);
                foreach (var (baseName, id, visualPath, prefabName) in entries)
                    sb.Append("PROP|").Append(baseName).Append('|').Append(id).Append('|')
                      .Append(visualPath).Append('|').AppendLine(prefabName);
                File.WriteAllText(GpuiCachePath, sb.ToString());
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropLibrary] GPUI cache save failed: {e.Message}");
            }
        }

        public static bool   IsInitialized { get; private set; }
        public static string SearchText    { get; private set; } = "";

        public static void SetSearch(string text)
        {
            text ??= "";
            if (string.Equals(text, SearchText, StringComparison.OrdinalIgnoreCase)) return;
            SearchText = text;
            BuildFiltered();
        }

        public static void Init()
        {
            _all.Clear();
            _byId.Clear();
            _filtered.Clear();
            _gpuiScannedNames.Clear();

            foreach (var name in PrimitiveNames)
            {
                var id = $"primitive://{name}";
                var pi = new PropInfo(id, name);
                _all.Add(pi);
                _byId[id] = pi;
            }

            int primitiveCount = _all.Count;

            try { EnumerateFromCatalog(); }
            catch { }

            // Keep primitives pinned at the top; sort everything else by name.
            if (_all.Count > primitiveCount)
            {
                _all.Sort(primitiveCount, _all.Count - primitiveCount,
                    Comparer<PropInfo>.Create((a, b) =>
                        string.Compare(a.displayName, b.displayName, StringComparison.OrdinalIgnoreCase)));
            }

            IsInitialized = true;
            BuildFiltered();
        }

        static void BuildFiltered()
        {
            _filtered.Clear();
            if (string.IsNullOrEmpty(SearchText))
            {
                foreach (var p in _all) _filtered.Add(p);
                return;
            }
            foreach (var p in _all)
            {
                if (p.displayName.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0
                    || p.id.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0)
                    _filtered.Add(p);
            }
        }

        public static void ScanGpuiProps()
        {
            if (!IsInitialized) return;

            string catalogPath = Path.Combine(Application.streamingAssetsPath, "aa", "catalog.json");
            int insertAt = PrimitiveNames.Length;
            int added    = 0;

            // Fast path: load from cache so no catalog parsing or pool scanning needed.
            if (TryLoadGpuiCache(catalogPath, out var cached) && cached != null)
            {
                int gi = 0;
                foreach (var (baseName, id, visualPath, prefabName) in cached)
                {
                    if (_byId.ContainsKey(id) || _gpuiScannedNames.Contains(baseName)) continue;
                    var info = new PropInfo(id, baseName)
                    {
                        gpuiIndex     = gi++,
                        visualPath    = visualPath,
                        gpuiPrefabName = prefabName,
                        isLoaded      = false,
                        isInvalid     = false,
                    };
                    _all.Insert(insertAt++, info);
                    _byId[id] = info;
                    _gpuiScannedNames.Add(baseName);
                    added++;
                }
                if (added > 0) BuildFiltered();
                MelonLogger.Msg($"[PropLibrary] GPUI cache loaded: {added} props.");
                return;
            }

            // Cold path: scan the GPUI pool and catalog (no Addressables loads here).
            var loaded = TryGetLoadedProps();
            if (loaded == null || loaded.Length == 0) return;

            var visualLookup  = BuildGpuiVisualLookup();
            var cacheEntries  = new List<(string, string, string, string)>();
            int gpuiIdx       = 0;

            for (int i = 0; i < loaded.Length; i++)
            {
                var prefabGO = loaded[i];
                if (prefabGO == null) continue;

                bool hasRenderer = prefabGO.GetComponentInChildren<MeshRenderer>() != null
                                || prefabGO.GetComponentInChildren<SkinnedMeshRenderer>() != null;
                if (hasRenderer) continue;

                bool hasCollider = prefabGO.GetComponentInChildren<MeshCollider>() != null;
                if (!hasCollider) continue;

                string baseName = NormalizePropName(prefabGO.name);
                if (_gpuiScannedNames.Contains(baseName)) continue;

                int    gi     = gpuiIdx++;
                string gpuiId = $"gpui://{gi}";
                if (_byId.ContainsKey(gpuiId)) continue;

                visualLookup.TryGetValue(baseName, out string visualPath);
                visualPath   ??= "";
                string prefabName = prefabGO.name;

                var info = new PropInfo(gpuiId, baseName)
                {
                    gpuiIndex      = gi,
                    visualPath     = visualPath,
                    gpuiPrefabName = prefabName,
                    isLoaded       = false,
                    isInvalid      = false,
                };

                _gpuiScannedNames.Add(baseName);
                _all.Insert(insertAt++, info);
                _byId[gpuiId] = info;
                cacheEntries.Add((baseName, gpuiId, visualPath, prefabName));
                added++;
            }

            SaveGpuiCache(catalogPath, cacheEntries);
            if (added > 0) BuildFiltered();
            MelonLogger.Msg($"[PropLibrary] GPUI scan complete: {added} props added.");
        }

        static Dictionary<string, string> BuildGpuiVisualLookup()
        {
            var    lookup      = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                if (name.EndsWith("_player", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsLowerLodVariant(path)) continue;

                if (!lookup.ContainsKey(name))
                    lookup[name] = path;
            }
        }

        static void ExtractPartsFromColliders(GameObject root, PropInfo info)
        {
            var arr   = root.GetComponentsInChildren<MeshCollider>(true);
            var rootT = root.transform;
            foreach (var mc in arr)
            {
                if (mc == null || mc.sharedMesh == null) continue;
                AddPart(info, mc.sharedMesh, null, mc.transform, rootT);
            }
        }

        static void EnumerateFromCatalog()
        {
            string catalogPath = Path.Combine(Application.streamingAssetsPath, "aa", "catalog.json");
            if (!File.Exists(catalogPath)) return;

            string json = File.ReadAllText(catalogPath);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            ScanTextForPaths(json, seen, folders);

            int kdIdx = json.IndexOf("\"m_KeyDataString\"", StringComparison.Ordinal);
            if (kdIdx >= 0)
            {
                int valStart = json.IndexOf('"', kdIdx + 17) + 1;
                int valEnd = json.IndexOf('"', valStart);
                if (valStart > 0 && valEnd > valStart)
                {
                    try
                    {
                        byte[] bytes = Convert.FromBase64String(json.Substring(valStart, valEnd - valStart));
                        string decodedKeys = System.Text.Encoding.UTF8.GetString(bytes);
                        ScanTextForPaths(decodedKeys, seen, folders);
                    }
                    catch { }
                }
            }

            LogAssetFoldersOnce(folders);
        }

        static void ScanTextForPaths(string text, HashSet<string> seen, HashSet<string> folders)
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
                if (!entry.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                    && !IsMeshAssetPath(entry))
                    continue;

                if (IsLowerLodVariant(entry)) continue;

                string name = Path.GetFileNameWithoutExtension(entry);
                if (entry.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    if (name.EndsWith("_player", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.EndsWith("_Player", StringComparison.OrdinalIgnoreCase)) continue;
                }

                AddFolderPath(entry, folders);

                if (IsExcludedPath(entry)) continue;

                if (!seen.Add(entry)) continue;
                var info = new PropInfo(entry, name);
                _all.Add(info);
                _byId[entry] = info;
            }
        }

        static void AddFolderPath(string entry, HashSet<string> folders)
        {
            if (folders == null || string.IsNullOrEmpty(entry)) return;
            var dir = Path.GetDirectoryName(entry);
            if (string.IsNullOrEmpty(dir)) return;
            dir = dir.Replace('\\', '/');
            folders.Add(dir);
        }

        static void LogAssetFoldersOnce(HashSet<string> folders)
        {
            if (_loggedAssetFolders) return;
            _loggedAssetFolders = true;
            return;
        }

        static bool IsExcludedPath(string entry)
        {
            if (string.IsNullOrEmpty(entry)) return false;
            var normalized = entry.Replace('\\', '/');
            if (normalized.IndexOf("imposter", StringComparison.OrdinalIgnoreCase) >= 0
                || normalized.IndexOf("impostor", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            foreach (var prefix in ExcludedAssetPrefixes)
            {
                if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        static bool IsLowerLodVariant(string key)
        {
            string name = Path.GetFileNameWithoutExtension(key);
            int idx = name.IndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            int after = idx + 4;
            if (after >= name.Length) return false;
            char c = name[after];
            return c >= '1' && c <= '9';
        }

        public static PropInfo FindById(string id) => _byId.TryGetValue(id, out var p) ? p : null;

        public static void LoadPropData(PropInfo info)
        {
            if (info == null || info.isLoaded) return;

            if (info.IsPrimitive)
            {
                LoadPrimitive(info);
                return;
            }

            if (info.IsGpui)
            {
                LoadGpuiPropData(info);
                return;
            }

            // Try to clone a cached prefab from BestRegionLoader so materials remain intact.
            try
            {
                if (TryLoadFromBestRegion(info)) return;
            }
            catch { }

            // Addressable prefab fallback from Assets/ path in the catalog.
            try
            {
                if (TryLoadAddressable(info)) return;
            }
            catch { }

            info.isLoaded = true;
            info.isInvalid = true;
        }

        static bool TryLoadFromBestRegion(PropInfo info)
        {
            // Use reflection to avoid a hard compile-time dependency on BestRegionLoader.
            try
            {
                var loaded = TryGetLoadedProps();
                if (loaded == null || loaded.Length == 0) return false;

                string target = NormalizePropName(NormalizeIdToName(info.id));

                for (int i = 0; i < loaded.Length; i++)
                {
                    var go = loaded[i];
                    if (go == null) continue;
                    var nameClean = NormalizePropName(go.name);
                    if (!string.Equals(nameClean, target, StringComparison.OrdinalIgnoreCase)) continue;

                    var instance = UnityEngine.Object.Instantiate(go, new Vector3(0f, -99999f, 0f), Quaternion.identity);
                    try { ExtractPartsFromInstance(instance, info); }
                    finally { UnityEngine.Object.Destroy(instance); }

                    info.isLoaded = true;
                    info.isInvalid = !info.HasMesh;
                    return info.HasMesh;
                }
            }
            catch { }
            return false;
        }

        static void LoadGpuiPropData(PropInfo info)
        {
            // Try visual prefab first.
            if (!string.IsNullOrEmpty(info.visualPath))
            {
                try
                {
                    var handle   = Addressables.LoadAssetAsync<GameObject>(info.visualPath);
                    var visualGO = handle.WaitForCompletion();
                    if (visualGO != null)
                    {
                        var instance = UnityEngine.Object.Instantiate(
                            visualGO, new Vector3(0f, -99999f, 0f), Quaternion.identity);
                        try   { ExtractPartsFromInstance(instance, info); }
                        finally { UnityEngine.Object.Destroy(instance); }
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[PropLibrary] GPUI \"{info.displayName}\" visual load failed: {e.Message}");
                }
            }

            // Collider-mesh fallback.
            if (!info.HasMesh && !string.IsNullOrEmpty(info.gpuiPrefabName))
            {
                try
                {
                    var loaded = TryGetLoadedProps();
                    if (loaded != null)
                    {
                        string target = NormalizePropName(info.gpuiPrefabName);
                        for (int i = 0; i < loaded.Length; i++)
                        {
                            var go = loaded[i];
                            if (go == null) continue;
                            if (!string.Equals(NormalizePropName(go.name), target,
                                    StringComparison.OrdinalIgnoreCase)) continue;
                            var instance = UnityEngine.Object.Instantiate(
                                go, new Vector3(0f, -99999f, 0f), Quaternion.identity);
                            try   { ExtractPartsFromColliders(instance, info); }
                            catch { }
                            UnityEngine.Object.Destroy(instance);
                            break;
                        }
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[PropLibrary] GPUI \"{info.displayName}\" collider load failed: {e.Message}");
                }
            }

            info.isLoaded  = true;
            info.isInvalid = !info.HasMesh;
        }

        static bool TryLoadAddressable(PropInfo info)
        {
            if (string.IsNullOrEmpty(info.id)) return false;

            if (IsMeshAssetPath(info.id))
                return TryLoadAddressableMesh(info);

            AsyncOperationHandle<GameObject> handle = Addressables.LoadAssetAsync<GameObject>(info.id);
            var prefab = handle.WaitForCompletion();
            if (prefab == null)
            {
                info.isLoaded = true;
                info.isInvalid = true;
                return true;
            }

            var instance = UnityEngine.Object.Instantiate(prefab, new Vector3(0f, -99999f, 0f), Quaternion.identity);
            try { ExtractPartsFromInstance(instance, info); }
            finally { UnityEngine.Object.Destroy(instance); }

            info.isLoaded = true;
            info.isInvalid = !info.HasMesh;
            return true;
        }

        static bool TryLoadAddressableMesh(PropInfo info)
        {
            AsyncOperationHandle<Mesh> handle = Addressables.LoadAssetAsync<Mesh>(info.id);
            var mesh = handle.WaitForCompletion();
            if (mesh == null)
            {
                info.isLoaded = true;
                info.isInvalid = true;
                return true;
            }

            var mat = TryFindMaterialForMesh(mesh);
            info.parts.Add(new PropMeshPart
            {
                mesh = mesh,
                materials = mat != null ? new[] { mat } : null,
                localPosition = Vector3.zero,
                localRotation = Quaternion.identity,
                localScale = Vector3.one
            });

            info.isLoaded = true;
            info.isInvalid = !info.HasMesh;
            return true;
        }

        static bool IsMeshAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var lower = path.ToLowerInvariant();
            if (!lower.StartsWith("assets/", StringComparison.Ordinal)) return false;

            // Prefer mesh assets under Assets/Meshes/ or other root asset folders.
            bool inMeshes = lower.StartsWith("assets/meshes/", StringComparison.Ordinal);
            if (!inMeshes && !lower.StartsWith("assets/_props/", StringComparison.Ordinal)) return false;

            return lower.EndsWith(".asset", StringComparison.Ordinal)
                || lower.EndsWith(".fbx", StringComparison.Ordinal)
                || lower.EndsWith(".obj", StringComparison.Ordinal)
                || lower.EndsWith(".mesh", StringComparison.Ordinal);
        }

        static string StripLodSuffix(string name)
        {
            int idx = name.IndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
            return idx >= 0 ? name.Substring(0, idx) : name;
        }

        static Material TryFindMaterialForMesh(Mesh mesh)
        {
            if (mesh == null || string.IsNullOrEmpty(mesh.name)) return null;
            string meshName = mesh.name;
            string baseName = StripLodSuffix(meshName);
            try
            {
                var mats = Resources.FindObjectsOfTypeAll<Material>();
                Material fallback = null;
                foreach (var m in mats)
                {
                    if (m == null || string.IsNullOrEmpty(m.name)) continue;
                    if (string.Equals(m.name, meshName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(m.name, baseName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (m.mainTexture != null) return m;
                        fallback = m;
                    }
                }
                return fallback;
            }
            catch
            {
                return null;
            }
        }

        static GameObject[] TryGetLoadedProps()
        {
            try
            {
                if (!TryGetBestRegion(out var brl)) return null;

                var list = TryGetListField(brl, "loadedProps");
                if (list == null) return null;

                var arr = new GameObject[list.Count];
                for (int i = 0; i < list.Count; i++)
                    arr[i] = list[i] as GameObject;
                return arr;
            }
            catch { }

            return null;
        }

        static bool TryGetBestRegion(out object brl)
        {
            brl = null;
            try
            {
                var t = _bestRegionType;
                if (t == null)
                {
                    var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                    foreach (var asm in assemblies)
                    {
                        t = asm.GetType("BestRegionLoader");
                        if (t == null)
                        {
                            try
                            {
                                foreach (var tt in asm.GetTypes())
                                {
                                    if (tt != null && tt.Name == "BestRegionLoader") { t = tt; break; }
                                }
                            }
                            catch (System.Reflection.ReflectionTypeLoadException rtle)
                            {
                                foreach (var tt in rtle.Types)
                                {
                                    if (tt != null && tt.Name == "BestRegionLoader") { t = tt; break; }
                                }
                            }
                        }

                        if (t != null)
                        {
                            _bestRegionType = t;
                            break;
                        }
                    }
                }

                if (t == null) return false;

                var f = t.GetField("me", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (f != null) brl = f.GetValue(null);
                else
                {
                    var p = t.GetProperty("me", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (p != null) brl = p.GetValue(null);
                }

                if (brl != null) return true;
            }
            catch { }
            return false;
        }

        static List<object> TryGetListField(object obj, string memberName)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            var flags = System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance;

            var f = t.GetField(memberName, flags);
            if (f != null)
            {
                var val = f.GetValue(obj);
                return ConvertToObjectList(val);
            }

            var p = t.GetProperty(memberName, flags);
            if (p != null)
            {
                var val = p.GetValue(obj, null);
                return ConvertToObjectList(val);
            }

            return null;
        }

        static List<object> ConvertToObjectList(object val)
        {
            if (val == null) return null;

            if (val is IList list)
            {
                var result = new List<object>(list.Count);
                foreach (var item in list) result.Add(item);
                return result;
            }

            if (val is Array arr)
            {
                var result = new List<object>(arr.Length);
                foreach (var item in arr) result.Add(item);
                return result;
            }

            if (val is IEnumerable enumerable)
            {
                var result = new List<object>();
                foreach (var item in enumerable) result.Add(item);
                return result;
            }

            var type = val.GetType();
            var lengthProp = type.GetProperty("Length");
            var getItem = type.GetMethod("get_Item", new[] { typeof(int) });
            if (lengthProp != null && getItem != null)
            {
                var lenObj = lengthProp.GetValue(val, null);
                if (lenObj is int len)
                {
                    var result = new List<object>(len);
                    for (int i = 0; i < len; i++)
                        result.Add(getItem.Invoke(val, new object[] { i }));
                    return result;
                }
            }

            return null;
        }

        static object TryGetFieldOrProperty(object obj, string memberName)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            var flags = System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance;

            var f = t.GetField(memberName, flags);
            if (f != null) return f.GetValue(obj);
            var p = t.GetProperty(memberName, flags);
            if (p != null) return p.GetValue(obj, null);
            return null;
        }

        static string NormalizeIdToName(string id)
        {
            if (string.IsNullOrEmpty(id)) return string.Empty;
            var s = id;
            if (s.StartsWith("primitive://", StringComparison.Ordinal))
                s = s.Substring("primitive://".Length);
            else if (s.StartsWith("cached://", StringComparison.Ordinal))
                s = s.Substring("cached://".Length);
            else
                s = Path.GetFileNameWithoutExtension(s);

            int hashIdx = s.IndexOf('#');
            if (hashIdx > 0) s = s.Substring(0, hashIdx);
            return s;
        }

        static string NormalizePropName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            var s = name.Replace("(Clone)", "").Trim();
            s = s.Replace("_player", "", StringComparison.OrdinalIgnoreCase)
                 .Replace("_Player", "", StringComparison.OrdinalIgnoreCase)
                 .Trim();
            return s;
        }

        static void ExtractPartsFromInstance(GameObject root, PropInfo info)
        {
            if (root == null) return;
            var mfs = root.GetComponentsInChildren<MeshFilter>(true);
            foreach (var mf in mfs)
            {
                if (mf == null || mf.sharedMesh == null) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                var mats = mr != null ? mr.sharedMaterials : null;
                AddPart(info, mf.sharedMesh, mats, mf.transform, root.transform);
            }
            var smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var smr in smrs)
            {
                if (smr == null || smr.sharedMesh == null) continue;
                AddPart(info, smr.sharedMesh, smr.sharedMaterials, smr.transform, root.transform);
            }

            // Capture pre-cooked colliders from the prefab so they can be reused at spawn time.
            var rootT = root.transform;
            var rootScale = rootT.lossyScale;
            var cols = root.GetComponentsInChildren<Collider>(true);
            foreach (var col in cols)
            {
                if (col == null || col.isTrigger) continue;
                var t = col.transform;
                var cp = new PropColliderPart
                {
                    localPosition = rootT.InverseTransformPoint(t.position),
                    localRotation = Quaternion.Inverse(rootT.rotation) * t.rotation,
                    localScale    = new Vector3(
                        t.lossyScale.x / (rootScale.x != 0f ? rootScale.x : 1f),
                        t.lossyScale.y / (rootScale.y != 0f ? rootScale.y : 1f),
                        t.lossyScale.z / (rootScale.z != 0f ? rootScale.z : 1f))
                };
                var asMesh = col.TryCast<MeshCollider>();
                if (asMesh != null && asMesh.sharedMesh != null)
                {
                    cp.type   = PropColliderPart.ColliderType.Mesh;
                    cp.mesh   = asMesh.sharedMesh;
                    cp.convex = asMesh.convex;
                }
                else if (col.TryCast<BoxCollider>() is BoxCollider asBox)
                {
                    cp.type   = PropColliderPart.ColliderType.Box;
                    cp.center = asBox.center;
                    cp.size   = asBox.size;
                }
                else if (col.TryCast<SphereCollider>() is SphereCollider asSphere)
                {
                    cp.type   = PropColliderPart.ColliderType.Sphere;
                    cp.center = asSphere.center;
                    cp.radius = asSphere.radius;
                }
                else if (col.TryCast<CapsuleCollider>() is CapsuleCollider asCapsule)
                {
                    cp.type      = PropColliderPart.ColliderType.Capsule;
                    cp.center    = asCapsule.center;
                    cp.radius    = asCapsule.radius;
                    cp.height    = asCapsule.height;
                    cp.direction = asCapsule.direction;
                }
                else continue;
                info.colliderParts.Add(cp);
            }
        }

        static void LoadPrimitive(PropInfo info)
        {
            try
            {
                var typeName = info.id.Substring("primitive://".Length);
                if (!Enum.TryParse<PrimitiveType>(typeName, out var pt))
                {
                    info.isLoaded = true;
                    info.isInvalid = true;
                    return;
                }

                var go = GameObject.CreatePrimitive(pt);
                go.transform.position = new Vector3(0f, -99999f, 0f);

                // Extract a single part from the primitive
                var mf = go.GetComponent<MeshFilter>();
                var mr = go.GetComponent<MeshRenderer>();
                if (mf != null && mf.sharedMesh != null)
                {
                    var part = new PropMeshPart
                    {
                        mesh = mf.sharedMesh,
                        materials = mr != null ? mr.sharedMaterials : null,
                        localPosition = Vector3.zero,
                        localRotation = Quaternion.identity,
                        localScale = go.transform.localScale
                    };
                    info.parts.Add(part);
                }

                UnityEngine.Object.Destroy(go);

                info.isLoaded = true;
                info.isInvalid = !info.HasMesh;
            }
            catch
            {
                info.isLoaded = true;
                info.isInvalid = true;
            }
        }

        static void AddPart(PropInfo info, Mesh mesh, Material[] materials, Transform t, Transform rootT)
        {
            if (mesh == null || info == null) return;

            var ws = t.lossyScale;
            var rs = rootT.lossyScale;
            var part = new PropMeshPart
            {
                mesh = mesh,
                materials = materials,
                localPosition = rootT.InverseTransformPoint(t.position),
                localRotation = Quaternion.Inverse(rootT.rotation) * t.rotation,
                localScale = new Vector3(
                    rs.x != 0f ? ws.x / rs.x : 1f,
                    rs.y != 0f ? ws.y / rs.y : 1f,
                    rs.z != 0f ? ws.z / rs.z : 1f),
            };
            info.parts.Add(part);
        }
    }
}
