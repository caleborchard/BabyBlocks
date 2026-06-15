using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MelonLoader;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using MelonLoader.Utils;

namespace BabyBlocks
{
    public static class PropLibrary
    {
        internal const string NegativeCollisionPropId = "special://negative-hole";
        internal const string SpawnPointPropId = "special://spawn-point";

        internal static readonly List<PropInfo> _all = new();
        static readonly List<PropInfo> _filtered = new();
        internal static readonly Dictionary<string, PropInfo> _byId = new(StringComparer.Ordinal);
        internal static readonly Dictionary<string, string> _idAliases = new(StringComparer.Ordinal);

        internal static readonly string[] PrimitiveNames = { "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad", "Torus", "Cone", "Helix", "Egg" };
        static Type _bestRegionType;

        static readonly Dictionary<string, int>   _refCounts    = new(StringComparer.Ordinal); // propId → live instance count
        static readonly Dictionary<string, float> _zeroRefTime  = new(StringComparer.Ordinal); // propId → Time.realtimeSinceStartup when count hit 0
        const float UnloadDelay = 30f; // seconds after last instance removed before mesh data is freed

        public static IReadOnlyList<PropInfo> AllProps => _all;
        public static IReadOnlyList<PropInfo> FilteredProps => _filtered;

        static string GpuiCachePath =>
            Path.Combine(MelonEnvironment.UserDataDirectory, "BabyBlocks", "GpuiCache.txt");

        internal static bool TryLoadGpuiCache(string catalogPath,
            out List<(string baseName, string id, string visualPath, string prefabName)> entries)
        {
            entries = null;
            try
            {
                if (!File.Exists(GpuiCachePath)) return false;
                var lines = File.ReadAllLines(GpuiCachePath);
                if (lines.Length == 0 || !lines[0].StartsWith("MTIME=")) return false;
                entries = new List<(string, string, string, string)>();
                for (int i = 1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (line.StartsWith("PROP|"))
                    {
                        var p = line.Substring(5).Split('|');
                        if (p.Length >= 4) entries.Add((p[0], p[1], p[2], p[3]));
                    }
                    else if (line.StartsWith("MAT|"))
                    {
                        var p = line.Substring(4).Split('|');
                        if (p.Length >= 2 && !string.IsNullOrEmpty(p[0]) && !string.IsNullOrEmpty(p[1]))
                        {
                            MaterialPathCatalog.MaterialCatalogPathsDict[p[0]] = p[1];
                            MaterialPathCatalog.MaterialPathToKey[p[1]] = p[0];
                            if (p[0] == "__IDX__") MaterialPathCatalog.CatalogIndexed = true;
                        }
                    }
                }
                return true;
            }
            catch { return false; }
        }

        internal static bool TryParseGpuiIndex(string id, out int index)
        {
            index = -1;
            if (string.IsNullOrEmpty(id)) return false;

            const string Prefix = "gpui://";
            if (!id.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
            return int.TryParse(id.Substring(Prefix.Length), out index) && index >= 0;
        }

        internal static string BuildStableGpuiId(string baseName, string prefabName, string visualPath)
        {
            string key = string.IsNullOrWhiteSpace(prefabName) ? baseName : prefabName;
            if (string.IsNullOrWhiteSpace(key))
                key = "unnamed";

            key = key.Trim();
            if (key.IndexOf('|') >= 0)
                key = key.Replace("|", "_");

            string stable = $"gpui://{key}";
            if (!_byId.TryGetValue(stable, out var existing))
                return stable;

            // Resolve collisions deterministically so IDs remain stable across launches.
            string existingFingerprint = (existing.gpuiPrefabName ?? "") + "|" + (existing.visualPath ?? "");
            string incomingFingerprint = (prefabName ?? "") + "|" + (visualPath ?? "");
            if (string.Equals(existingFingerprint, incomingFingerprint, StringComparison.OrdinalIgnoreCase))
                return stable;

            return stable + "#" + MaterialPathCatalog.ComputeStableHash(incomingFingerprint);
        }

        internal static void SaveGpuiCache(string catalogPath,
            List<(string baseName, string id, string visualPath, string prefabName)> entries)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(GpuiCachePath));
                string mtime = File.Exists(catalogPath)
                    ? File.GetLastWriteTimeUtc(catalogPath).Ticks.ToString()
                    : "0";
                var sb = new StringBuilder();
                sb.Append("MTIME=").AppendLine(mtime);
                foreach (var (baseName, id, visualPath, prefabName) in entries)
                    sb.Append("PROP|").Append(baseName).Append('|').Append(id).Append('|')
                      .Append(visualPath).Append('|').AppendLine(prefabName);
                foreach (var kvp in MaterialPathCatalog.MaterialCatalogPathsDict)
                    sb.Append("MAT|").Append(kvp.Key).Append('|').AppendLine(kvp.Value);
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
            _idAliases.Clear();
            _filtered.Clear();
            GpuiPropScanner.GpuiScannedNames.Clear();

            foreach (var name in PrimitiveNames)
            {
                var id = $"primitive://{name}";
                var pi = new PropInfo(id, name);
                _all.Add(pi);
                _byId[id] = pi;

                // Load eagerly here (at editor-open time) rather than lazily on first
                // drag. LoadPrimitive's CreatePrimitive/Destroy churn produces garbage
                // that the GC can collect a few frames later — if that lands mid-drag
                // it can swallow the held mouse-button state for a frame, making the
                // very first drag of a given primitive appear to release early.
                LoadPropData(pi);
            }

            var holeProp = new PropInfo(NegativeCollisionPropId, "Hole");
            _all.Add(holeProp);
            _byId[holeProp.id] = holeProp;

            var spawnPointProp = new PropInfo(SpawnPointPropId, "Spawn Point");
            _all.Add(spawnPointProp);
            _byId[spawnPointProp.id] = spawnPointProp;

            int primitiveCount = _all.Count;

            try { CatalogEnumerator.EnumerateFromCatalog(); }
            catch { }

            // Keep primitives pinned at the top; sort everything else by name.
            if (_all.Count > primitiveCount)
            {
                _all.Sort(primitiveCount, _all.Count - primitiveCount,
                    Comparer<PropInfo>.Create((a, b) =>
                        NaturalStringCompare(a.displayName, b.displayName)));
            }

            IsInitialized = true;
            BuildFiltered();
        }

        public static void RebuildFiltered() => BuildFiltered();

        static void BuildFiltered()
        {
            _filtered.Clear();

            bool hasSearch = !string.IsNullOrEmpty(SearchText);

            if (Core.DebugMode)
            {
                foreach (var p in _all)
                {
                    if (hasSearch
                        && p.displayName.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) < 0
                        && p.id.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    _filtered.Add(p);
                }
            }
            else
            {
                // Non-debug: only props that have a category in metadata, filtered by selected category.
                string selectedCategory = PropPalette.SelectedCategory;
                foreach (var p in _all)
                {
                    if (!PropMetadataStore.HasCategory(p.id)) continue;
                    string cat = PropMetadataStore.GetCategory(p.id);
                    if (selectedCategory != null
                        && !string.Equals(cat, selectedCategory, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (hasSearch)
                    {
                        string displayName = PropMetadataStore.GetDisplayName(p.id) ?? p.displayName;
                        if (displayName.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) < 0
                            && p.id.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) < 0
                            && (cat == null || cat.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) < 0))
                            continue;
                    }
                    _filtered.Add(p);
                }
            }

            // Sort alphabetically by the string that is actually displayed.
            // Debug mode displays raw prop.displayName, so sort by that (underscores treated as spaces).
            // Non-debug mode displays the metadata display name, so sort by that.
            _filtered.Sort((a, b) =>
            {
                string nameA, nameB;
                if (Core.DebugMode)
                {
                    nameA = a.displayName.Replace("_", " ").Trim();
                    nameB = b.displayName.Replace("_", " ").Trim();
                }
                else
                {
                    nameA = PropMetadataStore.GetDisplayName(a.id) ?? a.displayName;
                    nameB = PropMetadataStore.GetDisplayName(b.id) ?? b.displayName;
                }
                return NaturalStringCompare(nameA, nameB);
            });
        }

        public static void AddRef(string propId)
        {
            if (string.IsNullOrEmpty(propId)) return;
            _zeroRefTime.Remove(propId); // cancel any pending unload
            _refCounts[propId] = _refCounts.TryGetValue(propId, out int n) ? n + 1 : 1;
        }

        public static void RemoveRef(string propId)
        {
            if (string.IsNullOrEmpty(propId)) return;
            if (!_refCounts.TryGetValue(propId, out int n)) return;
            n--;
            if (n <= 0)
            {
                _refCounts.Remove(propId);
                _zeroRefTime[propId] = Time.realtimeSinceStartup;
            }
            else
            {
                _refCounts[propId] = n;
            }
        }

        public static void ProcessUnloadQueue()
        {
            if (_zeroRefTime.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            List<string> toUnload = null;
            foreach (var kvp in _zeroRefTime)
            {
                if (now - kvp.Value >= UnloadDelay)
                    (toUnload ??= new List<string>()).Add(kvp.Key);
            }
            if (toUnload == null) return;
            foreach (var id in toUnload)
            {
                _zeroRefTime.Remove(id);
                if (_byId.TryGetValue(id, out var info))
                    UnloadPropData(info);
            }
            // Do not force GC.Collect() here — a full collection pause can swallow input events
            // (e.g. teleport clicks) if it fires at the wrong moment. Addressables.Release already
            // frees the native/GPU memory; the managed heap is collected on its own schedule.

            // Releasing a prop's addressable asset can drop a shared MicroSplat texture array's
            // refcount to zero (e.g. the last MicroSplat-layered prop instance is removed via
            // RemoveAll() during a level load), causing it to be released/recreated and leaving
            // the cached "[MicroSplat] Layer N" prop materials referencing destroyed textures.
            MaterialCatalog.RefreshMicroSplatLayerMaterials();
        }

        static void UnloadPropData(PropInfo info)
        {
            if (!info.isLoaded && !info.HasMesh && info._addressableAsset == null) return;
            BBLog.Msg($"[PropLibrary] Unloading \"{info.displayName}\" — no live instances.");

            PhysicsObjectManager.ReleasePhysicsMeshes(info);

            if (info._addressableAsset != null)
            {
                try { Addressables.Release(info._addressableAsset); } catch { }
                info._addressableAsset = null;
            }

            info.parts.Clear();
            info.colliderParts.Clear();
            info.isLoaded  = false;
            info.isInvalid = false;
        }

        internal static bool IsLowerLodVariant(string key)
        {
            string name = Path.GetFileNameWithoutExtension(key);
            int idx = name.IndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            int after = idx + 4;
            if (after >= name.Length) return false;
            char c = name[after];
            return c >= '1' && c <= '9';
        }

        // Returns the prop's combined mesh bounds in prop-root local space.
        // Returns null if no reliable data is available or bounds are implausibly large.
        public static Bounds? GetPropBounds(PropInfo info)
        {
            if (info == null) return null;

            Bounds? acc = null;

            if (info.parts != null)
            {
                for (int i = 0; i < info.parts.Count; i++)
                {
                    var p = info.parts[i];
                    if (p == null || p.mesh == null) continue;
                    var mb = p.mesh.bounds;
                    Vector3 center = mb.center;
                    Vector3 ext    = mb.extents;
                    for (int xi = -1; xi <= 1; xi += 2)
                    for (int yi = -1; yi <= 1; yi += 2)
                    for (int zi = -1; zi <= 1; zi += 2)
                    {
                        var corner    = center + new Vector3(xi * ext.x, yi * ext.y, zi * ext.z);
                        var worldLocal = p.localPosition + p.localRotation * Vector3.Scale(corner, p.localScale);
                        if (acc == null) acc = new Bounds(worldLocal, Vector3.zero);
                        else { var b = acc.Value; b.Encapsulate(worldLocal); acc = b; }
                    }
                }
            }

            if (acc == null && info.colliderParts != null)
            {
                for (int i = 0; i < info.colliderParts.Count; i++)
                {
                    var cp = info.colliderParts[i];
                    if (cp == null) continue;
                    if (cp.mesh != null)
                    {
                        var mb = cp.mesh.bounds;
                        Vector3 center = mb.center;
                        Vector3 ext    = mb.extents;
                        for (int xi = -1; xi <= 1; xi += 2)
                        for (int yi = -1; yi <= 1; yi += 2)
                        for (int zi = -1; zi <= 1; zi += 2)
                        {
                            var corner     = center + new Vector3(xi * ext.x, yi * ext.y, zi * ext.z);
                            var worldLocal = cp.localPosition + cp.localRotation * Vector3.Scale(corner, cp.localScale);
                            if (acc == null) acc = new Bounds(worldLocal, Vector3.zero);
                            else { var b = acc.Value; b.Encapsulate(worldLocal); acc = b; }
                        }
                    }
                    else if (cp.type == PropColliderPart.ColliderType.Box)
                    {
                        // Encapsulate all 8 corners of the box so bounds include full extents.
                        Vector3 hs = cp.size * 0.5f;
                        for (int xi = -1; xi <= 1; xi += 2)
                        for (int yi = -1; yi <= 1; yi += 2)
                        for (int zi = -1; zi <= 1; zi += 2)
                        {
                            var corner     = cp.center + new Vector3(xi * hs.x, yi * hs.y, zi * hs.z);
                            var worldLocal = cp.localPosition + cp.localRotation * Vector3.Scale(corner, cp.localScale);
                            if (acc == null) acc = new Bounds(worldLocal, Vector3.zero);
                            else { var b = acc.Value; b.Encapsulate(worldLocal); acc = b; }
                        }
                    }
                    else
                    {
                        // Sphere / Capsule: encapsulate center + an approximate radius.
                        var worldCenter = cp.localPosition + cp.localRotation * Vector3.Scale(cp.center, cp.localScale);
                        float r = cp.radius * Mathf.Max(cp.localScale.x, cp.localScale.y, cp.localScale.z);
                        if (r < 0.01f) r = 0.01f;
                        var sphereBounds = new Bounds(worldCenter, Vector3.one * (r * 2f));
                        if (acc == null) acc = sphereBounds;
                        else { var b = acc.Value; b.Encapsulate(sphereBounds); acc = b; }
                    }
                }
            }

            if (acc == null) return null;
            var size = acc.Value.size;
            if (size.x > 100f || size.y > 100f || size.z > 100f) return null;
            return acc;
        }

        // Returns Vector3.zero if no reliable bounds data is available.
        public static Vector3 GetPropPivotCenter(PropInfo info)
        {
            var b = GetPropBounds(info);
            return b.HasValue ? b.Value.center : Vector3.zero;
        }

        // Natural string comparison: compares alphabetic parts case-insensitively
        // and numeric parts by numeric value so names like "Rock 2" sort before "Rock 11".
        static int NaturalStringCompare(string a, string b)
        {
            if (a == b) return 0;
            if (a == null) return -1;
            if (b == null) return 1;

            int i = 0, j = 0;
            int na = a.Length, nb = b.Length;
            while (i < na && j < nb)
            {
                char ca = a[i];
                char cb = b[j];

                if (char.IsDigit(ca) && char.IsDigit(cb))
                {
                    int ia = i; while (ia < na && char.IsDigit(a[ia])) ia++;
                    int ib = j; while (ib < nb && char.IsDigit(b[ib])) ib++;

                    int sa = i; while (sa < ia && a[sa] == '0') sa++;
                    int sb = j; while (sb < ib && b[sb] == '0') sb++;

                    int lena = ia - sa;
                    int lenb = ib - sb;
                    if (lena != lenb) return lena < lenb ? -1 : 1;

                    for (int k = 0; k < lena; k++)
                    {
                        char da = a[sa + k];
                        char db = b[sb + k];
                        if (da != db) return da < db ? -1 : 1;
                    }

                    int origLenA = ia - i;
                    int origLenB = ib - j;
                    if (origLenA != origLenB) return origLenA < origLenB ? -1 : 1;

                    i = ia; j = ib;
                    continue;
                }

                int ja = i; while (ja < na && !char.IsDigit(a[ja])) ja++;
                int jb2 = j; while (jb2 < nb && !char.IsDigit(b[jb2])) jb2++;

                int lenA = ja - i;
                int lenB = jb2 - j;
                int cmp = string.Compare(a, i, b, j, Math.Min(lenA, lenB), StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
                if (lenA != lenB) return lenA < lenB ? -1 : 1;

                i = ja; j = jb2;
            }

            if (i < na) return 1;
            if (j < nb) return -1;
            return 0;
        }

        public static string ResolveCanonicalId(string id)
        {
            if (string.IsNullOrEmpty(id)) return id;
            if (_byId.ContainsKey(id)) return id;

            string current = id;
            for (int i = 0; i < 8; i++)
            {
                if (!_idAliases.TryGetValue(current, out var mapped) || string.IsNullOrEmpty(mapped))
                    break;
                if (_byId.ContainsKey(mapped))
                    return mapped;
                if (string.Equals(mapped, current, StringComparison.Ordinal))
                    break;
                current = mapped;
            }

            if (TryParseGpuiIndex(id, out int legacyIndex))
            {
                for (int i = 0; i < _all.Count; i++)
                {
                    var info = _all[i];
                    if (info == null || !info.IsGpui) continue;
                    if (info.gpuiIndex != legacyIndex) continue;
                    _idAliases[id] = info.id;
                    return info.id;
                }
            }

            return id;
        }

        public static PropInfo FindById(string id)
        {
            if (_byId.TryGetValue(id, out var direct)) return direct;

            string canonical = ResolveCanonicalId(id);
            return _byId.TryGetValue(canonical, out var resolved) ? resolved : null;
        }

        public static void LoadPropData(PropInfo info)
        {
            if (info == null) return;

            if (info.isLoaded)
            {
                if (info.HasMesh && info.parts[0].mesh != null)
                    return; // Valid cached data — nothing to do.

                // Either no mesh was ever extracted, or the mesh was destroyed when a scene unloaded.
                // Reset so every method below gets a fresh attempt.
                if (info.HasMesh)
                    MelonLogger.Warning($"[PropLibrary] Mesh for \"{info.displayName}\" was destroyed — retrying.");
                info.parts.Clear();
                info.colliderParts.Clear();
                info.isLoaded  = false;
                info.isInvalid = false;
            }

            if (info.IsPrimitive)
            {
                LoadPrimitive(info);
                return;
            }

            if (IsNegativeCollisionProp(info.id))
            {
                LoadNegativeCollisionProp(info);
                return;
            }

            if (IsSpawnPointProp(info.id))
            {
                LoadSpawnPointProp(info);
                return;
            }

            if (info.IsGpui)
            {
                LoadGpuiPropData(info);
                return;
            }

            try
            {
                if (TryLoadFromBestRegion(info)) return;
            }
            catch (Exception e) { MelonLogger.Warning($"[PropLibrary] BestRegion failed for \"{info.displayName}\": {e.Message}"); }

            try
            {
                if (TryLoadAddressable(info)) return;
            }
            catch (Exception e) { MelonLogger.Warning($"[PropLibrary] Addressable failed for \"{info.displayName}\": {e.Message}"); }

            // All methods failed — prop asset not available (wrong area, rare prop, etc.).
            MelonLogger.Warning($"[PropLibrary] All load methods failed for \"{info.displayName}\" (id: {info.id}).");
            CatalogEnumerator.LogCatalogBundleHint(info.id);
        }

        static bool TryLoadFromBestRegion(PropInfo info)
        {
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

                    if (info.HasMesh)
                    {
                        info.sourcePrefab = go;
                        info.isLoaded  = true;
                        info.isInvalid = false;
                        return true;
                    }
                    // Found the GO but got no mesh — don't stamp isLoaded; let other methods try.
                    MelonLogger.Warning($"[PropLibrary] BestRegion found \"{go.name}\" but extracted no mesh.");
                    return false;
                }
            }
            catch { }
            return false;
        }

        static bool TryLoadFromLoadedScenes(PropInfo info)
        {
            string target = NormalizePropName(NormalizeIdToName(info.id));
            int sceneCount = SceneManager.sceneCount;

            BBLog.Msg($"[PropLibrary] Scene scan for \"{target}\": {sceneCount} scene(s) loaded.");

            for (int s = 0; s < sceneCount; s++)
            {
                Scene scene;
                try { scene = SceneManager.GetSceneAt(s); }
                catch (Exception e) { MelonLogger.Warning($"[PropLibrary] GetSceneAt({s}) threw: {e.Message}"); continue; }

                if (!scene.isLoaded) { BBLog.Msg($"[PropLibrary]   Scene[{s}] \"{scene.name}\" not loaded, skipping."); continue; }

                GameObject[] roots;
                try { roots = scene.GetRootGameObjects(); }
                catch (Exception e) { MelonLogger.Warning($"[PropLibrary]   GetRootGameObjects threw for \"{scene.name}\": {e.Message}"); continue; }

                BBLog.Msg($"[PropLibrary]   Scene[{s}] \"{scene.name}\": {roots.Length} root(s).");

                foreach (var root in roots)
                {
                    if (root == null) continue;

                    if (string.Equals(NormalizePropName(root.name), target, StringComparison.OrdinalIgnoreCase))
                    {
                        ExtractPartsFromInstance(root, info);
                        if (info.HasMesh)
                        {
                            info.isLoaded  = true;
                            info.isInvalid = false;
                            BBLog.Msg($"[PropLibrary] Found \"{info.displayName}\" in scene \"{scene.name}\" (root).");
                            return true;
                        }
                        info.parts.Clear();
                        info.colliderParts.Clear();
                        continue;
                    }

                    Transform[] transforms;
                    try { transforms = root.GetComponentsInChildren<Transform>(true); }
                    catch { continue; }

                    foreach (var t in transforms)
                    {
                        if (t == null || t.gameObject == null) continue;
                        if (!string.Equals(NormalizePropName(t.gameObject.name), target, StringComparison.OrdinalIgnoreCase)) continue;

                        ExtractPartsFromInstance(t.gameObject, info);
                        if (info.HasMesh)
                        {
                            info.isLoaded  = true;
                            info.isInvalid = false;
                            BBLog.Msg($"[PropLibrary] Found \"{info.displayName}\" in scene \"{scene.name}\" (child of \"{root.name}\").");
                            return true;
                        }
                        info.parts.Clear();
                        info.colliderParts.Clear();
                    }
                }
            }

            BBLog.Msg($"[PropLibrary] Scene scan: \"{target}\" not found in any loaded scene.");
            return false;
        }

        static void LoadGpuiPropData(PropInfo info)
        {
            BBLog.Msg($"[PropLibrary] GPUI \"{info.displayName}\": visualPath=\"{info.visualPath}\" prefabName=\"{info.gpuiPrefabName}\"");

            if (!string.IsNullOrEmpty(info.visualPath))
            {
                try
                {
                    var handle   = Addressables.LoadAssetAsync<GameObject>(info.visualPath);
                    var visualGO = handle.WaitForCompletion();
                    if (visualGO != null)
                    {
                        info._addressableAsset = visualGO;
                        var instance = UnityEngine.Object.Instantiate(
                            visualGO, new Vector3(0f, -99999f, 0f), Quaternion.identity);
                        try   { ExtractPartsFromInstance(instance, info); }
                        finally { UnityEngine.Object.Destroy(instance); }
                        BBLog.Msg($"[PropLibrary] GPUI \"{info.displayName}\" visual: extracted {info.parts.Count} part(s).");
                    }
                    else
                    {
                        MelonLogger.Warning($"[PropLibrary] GPUI \"{info.displayName}\" visual load returned null (path: {info.visualPath}).");
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[PropLibrary] GPUI \"{info.displayName}\" visual load threw: {e.Message}");
                }
            }
            else
            {
                BBLog.Msg($"[PropLibrary] GPUI \"{info.displayName}\" has no visualPath — cannot load visual.");
            }

            if (!info.HasMesh && !string.IsNullOrEmpty(info.gpuiPrefabName))
            {
                try
                {
                    var loaded = TryGetLoadedProps();
                    if (loaded != null)
                    {
                        string target = NormalizePropName(info.gpuiPrefabName);
                        bool found = false;
                        for (int i = 0; i < loaded.Length; i++)
                        {
                            var go = loaded[i];
                            if (go == null) continue;
                            if (!string.Equals(NormalizePropName(go.name), target,
                                    StringComparison.OrdinalIgnoreCase)) continue;
                            found = true;
                            var instance = UnityEngine.Object.Instantiate(
                                go, new Vector3(0f, -99999f, 0f), Quaternion.identity);
                            try   { GpuiPropScanner.ExtractPartsFromColliders(instance, info); }
                            catch { }
                            UnityEngine.Object.Destroy(instance);
                            BBLog.Msg($"[PropLibrary] GPUI \"{info.displayName}\" collider fallback: extracted {info.parts.Count} part(s).");
                            break;
                        }
                        if (!found)
                            BBLog.Msg($"[PropLibrary] GPUI \"{info.displayName}\" collider fallback: \"{target}\" not found in loadedProps ({loaded.Length} entries).");
                    }
                    else
                    {
                        MelonLogger.Warning($"[PropLibrary] GPUI \"{info.displayName}\" collider fallback: loadedProps unavailable.");
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[PropLibrary] GPUI \"{info.displayName}\" collider load threw: {e.Message}");
                }
            }

            // Addressable fallback: load the _player prefab directly when it's not in loadedProps.
            if (!info.HasMesh && !string.IsNullOrEmpty(info.gpuiPrefabName))
            {
                var playerPaths = GpuiPropScanner.GetGpuiPlayerPaths();
                if (playerPaths.TryGetValue(info.gpuiPrefabName, out string playerPath))
                {
                    try
                    {
                        var handle  = Addressables.LoadAssetAsync<GameObject>(playerPath);
                        var prefab  = handle.WaitForCompletion();
                        if (prefab != null)
                        {
                            if (info._addressableAsset == null) info._addressableAsset = prefab;
                            var inst = UnityEngine.Object.Instantiate(prefab, new Vector3(0f, -99999f, 0f), Quaternion.identity);
                            try   { GpuiPropScanner.ExtractPartsFromColliders(inst, info); }
                            catch { }
                            UnityEngine.Object.Destroy(inst);
                            BBLog.Msg($"[PropLibrary] GPUI \"{info.displayName}\" addressable player: {info.parts.Count} part(s) from {playerPath}");
                        }
                        else
                        {
                            MelonLogger.Warning($"[PropLibrary] GPUI \"{info.displayName}\" addressable player returned null ({playerPath}).");
                        }
                    }
                    catch (Exception e)
                    {
                        MelonLogger.Warning($"[PropLibrary] GPUI \"{info.displayName}\" addressable player failed: {e.Message}");
                    }
                }
                else
                {
                    MelonLogger.Warning($"[PropLibrary] GPUI \"{info.displayName}\": player prefab \"{info.gpuiPrefabName}\" not found in catalog.");
                }
            }

            BBLog.Msg($"[PropLibrary] GPUI \"{info.displayName}\" done: HasMesh={info.HasMesh}");
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
                return false; // asset not available; let caller retry later

            info._addressableAsset = prefab;
            info.sourcePrefab = prefab;
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
                return false; // asset not available; let caller retry later

            info._addressableAsset = mesh;
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

        internal static bool IsMeshAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var lower = path.ToLowerInvariant();
            if (!lower.StartsWith("assets/", StringComparison.Ordinal)) return false;

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

        internal static GameObject[] TryGetLoadedProps()
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

        internal static string NormalizePropName(string name)
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

                Mesh proceduralMesh = typeName switch
                {
                    "Torus" => PrimitiveMeshGen.BuildTorus(),
                    "Cone"  => PrimitiveMeshGen.BuildCone(),
                    "Helix" => PrimitiveMeshGen.BuildHelix(),
                    "Egg"   => PrimitiveMeshGen.BuildEgg(),
                    _       => null,
                };

                if (proceduralMesh != null)
                {
                    // Steal a default material from a temporary Unity sphere.
                    var tempGo  = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    var tempMr  = tempGo.GetComponent<MeshRenderer>();
                    var defMats = tempMr != null ? tempMr.sharedMaterials : null;
                    UnityEngine.Object.Destroy(tempGo);

                    info.parts.Add(new PropMeshPart
                    {
                        mesh          = proceduralMesh,
                        materials     = defMats,
                        localPosition = Vector3.zero,
                        localRotation = Quaternion.identity,
                        localScale    = Vector3.one,
                    });

                    info.isLoaded  = true;
                    info.isInvalid = false;
                    return;
                }

                if (!Enum.TryParse<PrimitiveType>(typeName, out var pt))
                {
                    info.isLoaded  = true;
                    info.isInvalid = true;
                    return;
                }

                var go = GameObject.CreatePrimitive(pt);
                go.transform.position = new Vector3(0f, -99999f, 0f);

                var mf = go.GetComponent<MeshFilter>();
                var mr = go.GetComponent<MeshRenderer>();
                if (mf != null && mf.sharedMesh != null)
                {
                    var part = new PropMeshPart
                    {
                        mesh          = mf.sharedMesh,
                        materials     = mr != null ? mr.sharedMaterials : null,
                        localPosition = Vector3.zero,
                        localRotation = Quaternion.identity,
                        localScale    = go.transform.localScale,
                    };
                    info.parts.Add(part);
                }

                UnityEngine.Object.Destroy(go);

                info.isLoaded  = true;
                info.isInvalid = !info.HasMesh;
            }
            catch
            {
                info.isLoaded  = true;
                info.isInvalid = true;
            }
        }

        static void LoadNegativeCollisionProp(PropInfo info)
        {
            try
            {
                var tempGo = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                var mesh = tempGo.GetComponent<MeshFilter>()?.sharedMesh;
                var tempMr = tempGo.GetComponent<MeshRenderer>();
                var defMats = tempMr != null ? tempMr.sharedMaterials : null;
                UnityEngine.Object.Destroy(tempGo);

                info.parts.Add(new PropMeshPart
                {
                    mesh          = mesh,
                    materials     = defMats,
                    localPosition = Vector3.zero,
                    localRotation = Quaternion.identity,
                    localScale    = Vector3.one,
                });

                info.isLoaded  = true;
                info.isInvalid = false;
            }
            catch
            {
                info.isLoaded  = true;
                info.isInvalid = true;
            }
        }

        public static bool IsNegativeCollisionProp(string id)
        {
            return !string.IsNullOrEmpty(id) && string.Equals(id, NegativeCollisionPropId, StringComparison.Ordinal);
        }

        static void LoadSpawnPointProp(PropInfo info)
        {
            info.parts.Clear();

            try
            {
                var tempGo = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                var mesh = tempGo.GetComponent<MeshFilter>()?.sharedMesh;
                var tempMr = tempGo.GetComponent<MeshRenderer>();
                var defMats = tempMr != null ? tempMr.sharedMaterials : null;
                UnityEngine.Object.Destroy(tempGo);

                if (mesh == null)
                {
                    info.isLoaded  = true;
                    info.isInvalid = true;
                    return;
                }

                info.parts.Add(new PropMeshPart
                {
                    mesh          = mesh,
                    materials     = defMats,
                    localPosition = new Vector3(0f, 1f, 0f),
                    localRotation = Quaternion.identity,
                    localScale    = Vector3.one,
                });

                info.isLoaded  = true;
                info.isInvalid = false;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropLibrary] Failed to build Spawn Point placeholder: {e.Message}");
                info.isLoaded  = true;
                info.isInvalid = true;
            }
        }

        public static bool IsSpawnPointProp(string id)
        {
            return !string.IsNullOrEmpty(id) && string.Equals(id, SpawnPointPropId, StringComparison.Ordinal);
        }

        internal static void AddPart(PropInfo info, Mesh mesh, Material[] materials, Transform t, Transform rootT)
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
