using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MelonLoader;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace BabyBlocks
{
    // Resolves material names to Addressables catalog paths by scanning
    // StreamingAssets/aa/catalog.json, and caches the results (alongside
    // PropLibrary's GPUI prop cache, via PropLibrary.SaveGpuiCache/TryLoadGpuiCache)
    // so the scan only has to run once.
    internal static class MaterialPathCatalog
    {
        internal static readonly Dictionary<string, string> MaterialCatalogPathsDict = new(StringComparer.OrdinalIgnoreCase); // materialName → full catalog path
        internal static readonly Dictionary<string, string> MaterialPathToKey        = new(StringComparer.OrdinalIgnoreCase); // catalog path → assigned key (prevents double-numbering the same path)
        internal static bool CatalogIndexed; // true once IndexAllCatalogMaterials has run (or cache proves it already did)
        public static IReadOnlyDictionary<string, string> MaterialCatalogPaths => MaterialCatalogPathsDict;

        public static string ComputeStableHash(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                string text = value ?? "";
                for (int i = 0; i < text.Length; i++)
                {
                    hash ^= text[i];
                    hash *= 16777619;
                }
                return hash.ToString("x8");
            }
        }

        // Searches the Addressables catalog for a .mat file whose filename matches materialName,
        // loads it, and returns it. Returns null if not found or load fails.
        // The resolved catalog path is cached in GpuiCache.txt so catalog scanning is skipped on future launches.
        public static Material TryLoadMaterialByName(string materialName)
        {
            return TryLoadMaterialByName(materialName, null);
        }

        // Source-aware fallback: if a saved prop source is known, try to resolve the material from
        // that prop's embedded parts before falling back to the catalog/name cache.
        public static Material TryLoadMaterialByName(string materialName, string sourcePropId)
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
                if (!MaterialCatalogPathsDict.TryGetValue(materialName, out string matPath)
                    && !MaterialCatalogPathsDict.TryGetValue(cleanName, out matPath))
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
                        return null;

                    // Cache under both the clean name and the original saved name so either
                    // lookup hits the cache on the next call.
                    MaterialCatalogPathsDict[cleanName] = matPath;
                    if (!string.Equals(cleanName, materialName, StringComparison.Ordinal))
                        MaterialCatalogPathsDict[materialName] = matPath;
                    SaveMaterialPathCache();
                }

                var handle = Addressables.LoadAssetAsync<Material>(matPath);
                var mat = handle.WaitForCompletion();
                if (mat != null)
                    BBLog.Msg($"[PropLibrary] Loaded material \"{cleanName}\" from catalog: {matPath}");
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
                foreach (var info in PropLibrary.AllProps)
                {
                    if (!info.IsGpui) continue;
                    gpuiEntries.Add((info.displayName, info.id, info.visualPath ?? "", info.gpuiPrefabName ?? ""));
                }
                PropLibrary.SaveGpuiCache(catalogPath, gpuiEntries);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropLibrary] SaveMaterialPathCache failed: {e.Message}");
            }
        }

        // Scans the Addressables catalog for ALL material assets (.mat files and FBX/prefab
        // sub-assets) and populates MaterialCatalogPathsDict with name → key entries. Returns the
        // list of newly discovered names. On subsequent calls (or when the cache already contains
        // the full index) this is a no-op and returns an empty list — callers should iterate
        // MaterialCatalogPaths directly to get all known names.
        public static IReadOnlyList<string> IndexAllCatalogMaterials()
        {
            if (CatalogIndexed) return Array.Empty<string>();
            CatalogIndexed = true;

            var names = new List<string>();
            try
            {
                string catalogFile = Path.Combine(Application.streamingAssetsPath, "aa", "catalog.json");
                if (!File.Exists(catalogFile)) return names;
                string json = File.ReadAllText(catalogFile);

                string decoded = string.Empty;
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

                // Phase 1: standalone .mat files whose filename has NO _N suffix.
                // These claim their plain names first so they are never bumped to a number.
                ScanStandaloneMatFiles(json,    names, suffixedOnly: false);
                ScanStandaloneMatFiles(decoded, names, suffixedOnly: false);

                // Phase 2: standalone .mat files whose filename ends with _N (Unity dedup copies).
                // Their base name is already in the dict so they become "Name 2", "Name 3", etc.
                ScanStandaloneMatFiles(json,    names, suffixedOnly: true);
                ScanStandaloneMatFiles(decoded, names, suffixedOnly: true);

                // Phase 3: FBX/prefab sub-assets. Only fills names not already claimed above.
                ScanSubAssets(json,    names);
                ScanSubAssets(decoded, names);

                // Sentinel so the next session's cache load can skip this scan.
                MaterialCatalogPathsDict["__IDX__"] = "1";
                SaveMaterialPathCache();
                BBLog.Msg($"[PropLibrary] Full catalog material index built: {names.Count} material(s) found.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropLibrary] IndexAllCatalogMaterials failed: {e.Message}");
            }
            return names;
        }

        static bool TryStripUnityDupSuffix(string name, out string baseName)
        {
            int u = name.LastIndexOf('_');
            if (u > 0)
            {
                bool allDigits = true;
                for (int k = u + 1; k < name.Length; k++)
                    if (!char.IsDigit(name[k])) { allDigits = false; break; }
                if (allDigits && u + 1 < name.Length)
                {
                    baseName = name.Substring(0, u);
                    return true;
                }
            }
            baseName = name;
            return false;
        }

        static void ScanStandaloneMatFiles(string text, List<string> result, bool suffixedOnly)
        {
            if (string.IsNullOrEmpty(text)) return;
            const string MatExt = ".mat";
            int len = text.Length;
            for (int i = 0; i < len; )
            {
                int pos = text.IndexOf(MatExt, i, StringComparison.OrdinalIgnoreCase);
                if (pos < 0) break;
                int end = pos + MatExt.Length;
                if (end < len && char.IsLetterOrDigit(text[end])) { i = end; continue; }

                int start = pos;
                while (start > 0 && text[start - 1] != '"' && text[start - 1] != '\0' && text[start - 1] >= ' ')
                    start--;

                string path = text.Substring(start, end - start);
                if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) { i = end; continue; }
                if (MaterialPathToKey.ContainsKey(path)) { i = end; continue; }

                int slash = path.LastIndexOf('/');
                if (slash < 0) { i = end; continue; }
                string name = path.Substring(slash + 1, path.Length - slash - 1 - MatExt.Length);
                if (name.Length == 0) { i = end; continue; }

                bool hasDupSuffix = TryStripUnityDupSuffix(name, out string baseName);
                if (hasDupSuffix != suffixedOnly) { i = end; continue; }

                string key;
                if (hasDupSuffix && MaterialCatalogPathsDict.ContainsKey(baseName))
                {
                    // Variant of a known base name — use a hash of the path as a stable unique suffix.
                    key = $"{baseName} [{ComputeStableHash(path)}]";
                    // Collision is astronomically unlikely but handle it just in case.
                    int dup = 2;
                    while (MaterialCatalogPathsDict.ContainsKey(key))
                        key = $"{baseName} [{ComputeStableHash(path + dup++)}]";
                }
                else
                {
                    key = name;
                    int dup = 2;
                    while (MaterialCatalogPathsDict.ContainsKey(key))
                        key = $"{name} [{ComputeStableHash(path + dup++)}]";
                }

                MaterialCatalogPathsDict[key] = path;
                MaterialPathToKey[path] = key;
                result.Add(key);
                i = end;
            }
        }

        static void ScanSubAssets(string text, List<string> result)
        {
            if (string.IsNullOrEmpty(text)) return;
            int len = text.Length;
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
                        && !MaterialPathToKey.ContainsKey(path)
                        && !MaterialCatalogPathsDict.ContainsKey(name))
                    {
                        MaterialCatalogPathsDict[name] = path;
                        MaterialPathToKey[path] = name;
                        result.Add(name);
                    }
                }
                j = close + 1;
            }
        }

        static string FindMaterialPath(string text, string materialName)
        {
            // If materialName is a numbered variant ("Foo 2"), try the _N-suffixed file first
            // ("Foo_0.mat" for n=2, "Foo_1.mat" for n=3, …) since that's how ScanStandaloneMatFiles
            // maps them. Fall back to plain-name scanning if not found.
            string baseName = materialName;
            int targetN = 1;
            int lastSpace = materialName.LastIndexOf(' ');
            if (lastSpace > 0 && int.TryParse(materialName.Substring(lastSpace + 1), out int parsed) && parsed >= 2)
            {
                baseName = materialName.Substring(0, lastSpace);
                targetN = parsed;
            }

            if (targetN >= 2)
            {
                string dupSuffix = "/" + baseName + "_" + (targetN - 2) + ".mat";
                int k = 0;
                while (k < text.Length)
                {
                    int idx = text.IndexOf(dupSuffix, k, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) break;
                    int end2 = idx + dupSuffix.Length;
                    int start2 = idx;
                    while (start2 > 0 && text[start2 - 1] != '"' && text[start2 - 1] != '\0' && text[start2 - 1] >= ' ')
                        start2--;
                    string p2 = text.Substring(start2, end2 - start2);
                    if (p2.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) return p2;
                    k = end2;
                }
            }

            // Pass 1: standalone .mat asset by plain name — e.g. Assets/Materials/Foo.mat
            string suffix = "/" + baseName + ".mat";
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
            if (targetN == 1)
            {
                string subSuffix = "[" + baseName + "]";
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
            }

            return null;
        }
    }
}
