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
        public Mesh  mesh;
        public bool  convex;
        public Vector3 center;
        public Vector3 size;
        public float radius;
        public float height;
        public int   direction;
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
        public string visualPath     = "";
        public string gpuiPrefabName = "";

        public bool HasMesh => parts != null && parts.Count > 0;
        public bool IsPrimitive => id.StartsWith("primitive://", StringComparison.Ordinal);

        // Holds the Addressables-loaded asset (Mesh or GameObject prefab) so it can be
        // properly released when the prop is unloaded. Null for BestRegion-sourced props.
        internal UnityEngine.Object _addressableAsset;

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

        static readonly string[] PrimitiveNames = { "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad", "Torus", "Cone", "Helix", "Egg" };
        static Type _bestRegionType;
        static readonly HashSet<string> _gpuiScannedNames = new(StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, string> _gpuiPlayerPaths; // prefabName → full catalog path
        static readonly Dictionary<string, string> _materialCatalogPaths = new(StringComparer.OrdinalIgnoreCase); // materialName → full catalog path
        static bool _catalogIndexed; // true once IndexAllCatalogMaterials has run (or cache proves it already did)

        public static IReadOnlyDictionary<string, string> MaterialCatalogPaths => _materialCatalogPaths;

        static readonly Dictionary<string, int>   _refCounts    = new(StringComparer.Ordinal); // propId → live instance count
        static readonly Dictionary<string, float> _zeroRefTime  = new(StringComparer.Ordinal); // propId → Time.realtimeSinceStartup when count hit 0
        const float UnloadDelay = 30f; // seconds after last instance removed before mesh data is freed

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
            Path.Combine(MelonEnvironment.UserDataDirectory, "BabyBlocks", "GpuiCache.txt");

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
                            _materialCatalogPaths[p[0]] = p[1];
                            if (p[0] == "__IDX__") _catalogIndexed = true; // full index was built in a previous session
                        }
                    }
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
                Directory.CreateDirectory(Path.GetDirectoryName(GpuiCachePath));
                string mtime = File.Exists(catalogPath)
                    ? File.GetLastWriteTimeUtc(catalogPath).Ticks.ToString()
                    : "0";
                var sb = new StringBuilder();
                sb.Append("MTIME=").AppendLine(mtime);
                foreach (var (baseName, id, visualPath, prefabName) in entries)
                    sb.Append("PROP|").Append(baseName).Append('|').Append(id).Append('|')
                      .Append(visualPath).Append('|').AppendLine(prefabName);
                foreach (var kvp in _materialCatalogPaths)
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
                        NaturalStringCompare(a.displayName, b.displayName)));
            }

            IsInitialized = true;
            BuildFiltered();
        }

        public static void RebuildFiltered() => BuildFiltered();

        static void BuildFiltered()
        {
            _filtered.Clear();

            if (Core.DebugMode)
            {
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
                return;
            }

            // Non-debug: only props that have a category in metadata, filtered by selected category.
            string selectedCategory = PropPalette.SelectedCategory;
            foreach (var p in _all)
            {
                if (!PropMetadataPanel.HasCategory(p.id)) continue;
                if (selectedCategory != null)
                {
                    string cat = PropMetadataPanel.GetCategory(p.id);
                    if (!string.Equals(cat, selectedCategory, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                _filtered.Add(p);
            }

            // Sort alphabetically by metadata displayName within the filtered set.
            _filtered.Sort((a, b) =>
            {
                string nameA = PropMetadataPanel.GetDisplayName(a.id) ?? a.displayName;
                string nameB = PropMetadataPanel.GetDisplayName(b.id) ?? b.displayName;
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
        }

        static void UnloadPropData(PropInfo info)
        {
            if (!info.isLoaded && !info.HasMesh && info._addressableAsset == null) return;
            MelonLogger.Msg($"[PropLibrary] Unloading \"{info.displayName}\" — no live instances.");

            LevelEditorManager.ReleasePhysicsMeshes(info);

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

        public static void ScanGpuiProps()
        {
            if (!IsInitialized) return;

            string catalogPath = Path.Combine(Application.streamingAssetsPath, "aa", "catalog.json");
            int insertAt = PrimitiveNames.Length;
            int added    = 0;

            if (TryLoadGpuiCache(catalogPath, out var cached) && cached != null)
            {
                int gi = 0;
                foreach (var (baseName, id, visualPath, prefabName) in cached)
                {
                    if (_byId.ContainsKey(id) || _gpuiScannedNames.Contains(baseName)) continue;
                    var info = new PropInfo(id, baseName)
                    {
                        gpuiIndex      = gi++,
                        visualPath     = visualPath,
                        gpuiPrefabName = prefabName,
                        isLoaded       = false,
                        isInvalid      = false,
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

        static void LogCatalogBundleHint(string propId)
        {
            try
            {
                string catalogPath = Path.Combine(Application.streamingAssetsPath, "aa", "catalog.json");
                if (!File.Exists(catalogPath)) return;

                string fileName = Path.GetFileName(propId); // e.g. "Spruce_Norway_Desktop_Stump_Var2.prefab"
                if (string.IsNullOrEmpty(fileName)) return;

                string json = File.ReadAllText(catalogPath);

                // Also check the decoded key data.
                string decoded = "";
                int kdIdx = json.IndexOf("\"m_KeyDataString\"", StringComparison.Ordinal);
                if (kdIdx >= 0)
                {
                    int vs = json.IndexOf('"', kdIdx + 17) + 1;
                    int ve = json.IndexOf('"', vs);
                    if (vs > 0 && ve > vs)
                    {
                        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(json.Substring(vs, ve - vs))); }
                        catch { }
                    }
                }

                var bundles  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var scenes   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var text in new[] { json, decoded })
                {
                    if (string.IsNullOrEmpty(text)) continue;

                    // Find all occurrences of the prop filename in this text block.
                    int searchFrom = 0;
                    while (searchFrom < text.Length)
                    {
                        int hit = text.IndexOf(fileName, searchFrom, StringComparison.OrdinalIgnoreCase);
                        if (hit < 0) break;
                        searchFrom = hit + 1;

                        // Scan a ±2 KB window around the hit for bundle/scene references.
                        int wStart = Math.Max(0, hit - 2048);
                        int wEnd   = Math.Min(text.Length, hit + 2048);
                        string window = text.Substring(wStart, wEnd - wStart);

                        // Collect *.bundle names.
                        int bi = 0;
                        while (bi < window.Length)
                        {
                            int bHit = window.IndexOf(".bundle", bi, StringComparison.OrdinalIgnoreCase);
                            if (bHit < 0) break;
                            // Walk backwards to the start of the token.
                            int bStart = bHit;
                            while (bStart > 0 && window[bStart - 1] != '"' && window[bStart - 1] != '/'
                                              && window[bStart - 1] != '\\'&& window[bStart - 1] > ' ')
                                bStart--;
                            bundles.Add(window.Substring(bStart, bHit - bStart + 7));
                            bi = bHit + 7;
                        }

                        // Collect *.unity scene paths in the window.
                        int si = 0;
                        while (si < window.Length)
                        {
                            int sHit = window.IndexOf(".unity", si, StringComparison.OrdinalIgnoreCase);
                            if (sHit < 0) break;
                            int sStart = sHit;
                            while (sStart > 0 && window[sStart - 1] != '"' && window[sStart - 1] > ' ')
                                sStart--;
                            scenes.Add(window.Substring(sStart, sHit - sStart + 6));
                            si = sHit + 6;
                        }
                    }
                }

                if (scenes.Count > 0)
                    MelonLogger.Msg($"[PropLibrary] Catalog hint for \"{fileName}\": scenes → {string.Join(", ", scenes)}");
                else if (bundles.Count > 0)
                    MelonLogger.Msg($"[PropLibrary] Catalog hint for \"{fileName}\": bundles → {string.Join(", ", System.Linq.Enumerable.Take(bundles, 5))} {(bundles.Count > 5 ? $"(+{bundles.Count - 5} more)" : "")}");
                else
                    MelonLogger.Msg($"[PropLibrary] Catalog hint: \"{fileName}\" not found in catalog text.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropLibrary] LogCatalogBundleHint failed: {e.Message}");
            }
        }

        static void EnumerateFromCatalog()
        {
            string catalogPath = Path.Combine(Application.streamingAssetsPath, "aa", "catalog.json");
            if (!File.Exists(catalogPath)) return;

            string json = File.ReadAllText(catalogPath);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            ScanTextForPaths(json, seen);

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
                        ScanTextForPaths(decodedKeys, seen);
                    }
                    catch { }
                }
            }
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

                if (IsExcludedPath(entry)) continue;

                if (!seen.Add(entry)) continue;
                var info = new PropInfo(entry, name);
                _all.Add(info);
                _byId[entry] = info;
            }
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

        public static PropInfo FindById(string id) => _byId.TryGetValue(id, out var p) ? p : null;

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
            LogCatalogBundleHint(info.id);
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

            MelonLogger.Msg($"[PropLibrary] Scene scan for \"{target}\": {sceneCount} scene(s) loaded.");

            for (int s = 0; s < sceneCount; s++)
            {
                Scene scene;
                try { scene = SceneManager.GetSceneAt(s); }
                catch (Exception e) { MelonLogger.Warning($"[PropLibrary] GetSceneAt({s}) threw: {e.Message}"); continue; }

                if (!scene.isLoaded) { MelonLogger.Msg($"[PropLibrary]   Scene[{s}] \"{scene.name}\" not loaded, skipping."); continue; }

                GameObject[] roots;
                try { roots = scene.GetRootGameObjects(); }
                catch (Exception e) { MelonLogger.Warning($"[PropLibrary]   GetRootGameObjects threw for \"{scene.name}\": {e.Message}"); continue; }

                MelonLogger.Msg($"[PropLibrary]   Scene[{s}] \"{scene.name}\": {roots.Length} root(s).");

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
                            MelonLogger.Msg($"[PropLibrary] Found \"{info.displayName}\" in scene \"{scene.name}\" (root).");
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
                            MelonLogger.Msg($"[PropLibrary] Found \"{info.displayName}\" in scene \"{scene.name}\" (child of \"{root.name}\").");
                            return true;
                        }
                        info.parts.Clear();
                        info.colliderParts.Clear();
                    }
                }
            }

            MelonLogger.Msg($"[PropLibrary] Scene scan: \"{target}\" not found in any loaded scene.");
            return false;
        }

        static Dictionary<string, string> GetGpuiPlayerPaths()
        {
            if (_gpuiPlayerPaths != null) return _gpuiPlayerPaths;
            _gpuiPlayerPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string catalogPath = Path.Combine(Application.streamingAssetsPath, "aa", "catalog.json");
                if (!File.Exists(catalogPath)) return _gpuiPlayerPaths;
                string json = File.ReadAllText(catalogPath);

                AddPlayerPathsToLookup(json, _gpuiPlayerPaths);

                int kdIdx = json.IndexOf("\"m_KeyDataString\"", StringComparison.Ordinal);
                if (kdIdx >= 0)
                {
                    int vs = json.IndexOf('"', kdIdx + 17) + 1;
                    int ve = json.IndexOf('"', vs);
                    if (vs > 0 && ve > vs)
                    {
                        try
                        {
                            string decoded = Encoding.UTF8.GetString(
                                Convert.FromBase64String(json.Substring(vs, ve - vs)));
                            AddPlayerPathsToLookup(decoded, _gpuiPlayerPaths);
                        }
                        catch { }
                    }
                }

                MelonLogger.Msg($"[PropLibrary] Built GPUI player-path lookup: {_gpuiPlayerPaths.Count} entries.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropLibrary] GetGpuiPlayerPaths failed: {e.Message}");
            }
            return _gpuiPlayerPaths;
        }

        static void AddPlayerPathsToLookup(string text, Dictionary<string, string> lookup)
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
                if (!name.EndsWith("_player", StringComparison.OrdinalIgnoreCase)) continue;
                if (!lookup.ContainsKey(name))
                    lookup[name] = path;
            }
        }

        // Searches the Addressables catalog for a .mat file whose filename matches materialName,
        // loads it, and returns it. Returns null if not found or load fails.
        // The resolved catalog path is cached in GpuiCache.txt so catalog scanning is skipped on future launches.
        public static Material TryLoadMaterialByName(string materialName)
        {
            if (string.IsNullOrEmpty(materialName)) return null;
            try
            {
                // Unity appends " (Instance)" to a material's name every time renderer.material
                // (not sharedMaterial) is accessed — the actual catalog .mat asset never contains
                // that suffix. Strip all trailing occurrences before searching.
                const string InstanceSuffix = " (Instance)";
                string cleanName = materialName;
                while (cleanName.EndsWith(InstanceSuffix, StringComparison.Ordinal))
                    cleanName = cleanName.Substring(0, cleanName.Length - InstanceSuffix.Length);

                // Check in-memory cache first under both the original and clean name.
                if (!_materialCatalogPaths.TryGetValue(materialName, out string matPath)
                    && !_materialCatalogPaths.TryGetValue(cleanName, out matPath))
                {
                    string catalogPath = Path.Combine(Application.streamingAssetsPath, "aa", "catalog.json");
                    if (!File.Exists(catalogPath)) return null;
                    string json = File.ReadAllText(catalogPath);

                    // Search using the clean name (no "(Instance)" suffix).
                    matPath = FindMaterialPath(json, cleanName);
                    if (matPath == null)
                    {
                        int kdIdx = json.IndexOf("\"m_KeyDataString\"", StringComparison.Ordinal);
                        if (kdIdx >= 0)
                        {
                            int vs = json.IndexOf('"', kdIdx + 17) + 1;
                            int ve = json.IndexOf('"', vs);
                            if (vs > 0 && ve > vs)
                            {
                                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(json.Substring(vs, ve - vs)));
                                matPath = FindMaterialPath(decoded, cleanName);
                            }
                        }
                    }

                    if (matPath == null)
                    {
                        MelonLogger.Warning($"[PropLibrary] TryLoadMaterialByName: no catalog path found for \"{cleanName}\"" +
                            (!string.Equals(cleanName, materialName, StringComparison.Ordinal) ? $" (saved as \"{materialName}\")" : ""));
                        return null;
                    }

                    // Cache under both the clean name and the original saved name so either
                    // lookup hits the cache on the next call.
                    _materialCatalogPaths[cleanName] = matPath;
                    if (!string.Equals(cleanName, materialName, StringComparison.Ordinal))
                        _materialCatalogPaths[materialName] = matPath;
                    SaveMaterialPathCache();
                }

                var handle = Addressables.LoadAssetAsync<Material>(matPath);
                var mat = handle.WaitForCompletion();
                if (mat != null)
                    MelonLogger.Msg($"[PropLibrary] Loaded material \"{cleanName}\" from catalog: {matPath}");
                else
                    MelonLogger.Warning($"[PropLibrary] Catalog path found but load returned null for \"{cleanName}\": {matPath}");
                return mat;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropLibrary] TryLoadMaterialByName failed for \"{materialName}\": {e.Message}");
                return null;
            }
        }

        // Rewrites GpuiCache.txt preserving GPUI prop entries and adding the current material paths.
        static void SaveMaterialPathCache()
        {
            try
            {
                string catalogPath = Path.Combine(Application.streamingAssetsPath, "aa", "catalog.json");
                var gpuiEntries = new List<(string, string, string, string)>();
                foreach (var info in _all)
                {
                    if (!info.IsGpui) continue;
                    gpuiEntries.Add((info.displayName, info.id, info.visualPath ?? "", info.gpuiPrefabName ?? ""));
                }
                SaveGpuiCache(catalogPath, gpuiEntries);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropLibrary] SaveMaterialPathCache failed: {e.Message}");
            }
        }

        // Scans the Addressables catalog for ALL material assets (.mat files and FBX/prefab
        // sub-assets) and populates _materialCatalogPaths with name → key entries. Returns the
        // list of newly discovered names. On subsequent calls (or when the cache already contains
        // the full index) this is a no-op and returns an empty list — callers should iterate
        // MaterialCatalogPaths directly to get all known names.
        public static IReadOnlyList<string> IndexAllCatalogMaterials()
        {
            if (_catalogIndexed) return Array.Empty<string>();
            _catalogIndexed = true;

            var names = new List<string>();
            try
            {
                string catalogFile = Path.Combine(Application.streamingAssetsPath, "aa", "catalog.json");
                if (!File.Exists(catalogFile)) return names;
                string json = File.ReadAllText(catalogFile);

                IndexMaterialPathsInText(json, names);

                int kdIdx = json.IndexOf("\"m_KeyDataString\"", StringComparison.Ordinal);
                if (kdIdx >= 0)
                {
                    int vs = json.IndexOf('"', kdIdx + 17) + 1;
                    int ve = json.IndexOf('"', vs);
                    if (vs > 0 && ve > vs)
                    {
                        string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(json.Substring(vs, ve - vs)));
                        IndexMaterialPathsInText(decoded, names);
                    }
                }

                // Sentinel so the next session's cache load can skip this scan.
                _materialCatalogPaths["__IDX__"] = "1";
                SaveMaterialPathCache();
                MelonLogger.Msg($"[PropLibrary] Full catalog material index built: {names.Count} material(s) found.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropLibrary] IndexAllCatalogMaterials failed: {e.Message}");
            }
            return names;
        }

        static void IndexMaterialPathsInText(string text, List<string> result)
        {
            int len = text.Length;

            // Pass 1: standalone .mat assets — e.g. Assets/Materials/SomeMaterial.mat
            const string MatExt = ".mat";
            for (int i = 0; i < len; )
            {
                int pos = text.IndexOf(MatExt, i, StringComparison.OrdinalIgnoreCase);
                if (pos < 0) break;
                int end = pos + MatExt.Length;
                // Skip if .mat is the start of a longer extension (e.g. ".material").
                if (end < len && char.IsLetterOrDigit(text[end])) { i = end; continue; }

                int start = pos;
                while (start > 0 && text[start - 1] != '"' && text[start - 1] != '\0' && text[start - 1] >= ' ')
                    start--;

                string path = text.Substring(start, end - start);
                if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    int slash = path.LastIndexOf('/');
                    if (slash >= 0)
                    {
                        string name = path.Substring(slash + 1, path.Length - slash - 1 - MatExt.Length);
                        if (name.Length > 0 && !_materialCatalogPaths.ContainsKey(name))
                        {
                            _materialCatalogPaths[name] = path;
                            result.Add(name);
                        }
                    }
                }
                i = end;
            }

            // Pass 2: FBX/prefab sub-assets — e.g. Assets/Meshes/File.fbx[MaterialName]
            for (int j = 0; j < len; )
            {
                int open = text.IndexOf('[', j);
                if (open < 0) break;
                int close = text.IndexOf(']', open + 1);
                if (close < 0) break;

                string name = text.Substring(open + 1, close - open - 1);
                if (name.Length >= 2 && name.Length <= 100
                    && name.IndexOf('/') < 0 && name.IndexOf('\\') < 0)
                {
                    int start = open;
                    while (start > 0 && text[start - 1] != '"' && text[start - 1] != '\0' && text[start - 1] >= ' ')
                        start--;

                    string path = text.Substring(start, close - start + 1);
                    if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                        && !_materialCatalogPaths.ContainsKey(name))
                    {
                        _materialCatalogPaths[name] = path;
                        result.Add(name);
                    }
                }
                j = close + 1;
            }
        }

        static string FindMaterialPath(string text, string materialName)
        {
            // Pass 1: standalone .mat asset — e.g. Assets/Materials/Foo.mat
            string suffix = "/" + materialName + ".mat";
            int i = 0;
            while (i < text.Length)
            {
                int idx = text.IndexOf(suffix, i, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) break;
                int end = idx + suffix.Length;
                int start = idx;
                while (start > 0 && text[start - 1] != '"' && text[start - 1] != '\0' && text[start - 1] >= ' ')
                    start--;
                string path = text.Substring(start, end - start);
                if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                    return path;
                i = end;
            }

            // Pass 2: FBX sub-asset — e.g. Assets/Meshes/File.fbx[MaterialName]
            // Unity embeds materials inside FBX files; their Addressables key uses bracket notation.
            string subSuffix = "[" + materialName + "]";
            int j = 0;
            while (j < text.Length)
            {
                int idx = text.IndexOf(subSuffix, j, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) break;
                int end = idx + subSuffix.Length;
                int start = idx;
                while (start > 0 && text[start - 1] != '"' && text[start - 1] != '\0' && text[start - 1] >= ' ')
                    start--;
                string path = text.Substring(start, end - start);
                if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                    return path;
                j = end;
            }

            return null;
        }

        static void LoadGpuiPropData(PropInfo info)
        {
            MelonLogger.Msg($"[PropLibrary] GPUI \"{info.displayName}\": visualPath=\"{info.visualPath}\" prefabName=\"{info.gpuiPrefabName}\"");

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
                        MelonLogger.Msg($"[PropLibrary] GPUI \"{info.displayName}\" visual: extracted {info.parts.Count} part(s).");
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
                MelonLogger.Warning($"[PropLibrary] GPUI \"{info.displayName}\" has no visualPath — cannot load visual.");
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
                            try   { ExtractPartsFromColliders(instance, info); }
                            catch { }
                            UnityEngine.Object.Destroy(instance);
                            MelonLogger.Msg($"[PropLibrary] GPUI \"{info.displayName}\" collider fallback: extracted {info.parts.Count} part(s).");
                            break;
                        }
                        if (!found)
                            MelonLogger.Warning($"[PropLibrary] GPUI \"{info.displayName}\" collider fallback: \"{target}\" not found in loadedProps ({loaded.Length} entries).");
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
                var playerPaths = GetGpuiPlayerPaths();
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
                            try   { ExtractPartsFromColliders(inst, info); }
                            catch { }
                            UnityEngine.Object.Destroy(inst);
                            MelonLogger.Msg($"[PropLibrary] GPUI \"{info.displayName}\" addressable player: {info.parts.Count} part(s) from {playerPath}");
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

            MelonLogger.Msg($"[PropLibrary] GPUI \"{info.displayName}\" done: HasMesh={info.HasMesh}");
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

        static bool IsMeshAssetPath(string path)
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
