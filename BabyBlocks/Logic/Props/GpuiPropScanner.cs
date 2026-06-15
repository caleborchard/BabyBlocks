using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MelonLoader;
using UnityEngine;

namespace BabyBlocks
{
    // Discovers GPUI (Graphics Performance Unity Instancer) props by scanning the
    // currently-loaded scene plus the Addressables catalog, and assigns them stable
    // "gpui://" ids in PropLibrary's registry.
    internal static class GpuiPropScanner
    {
        internal static readonly HashSet<string> GpuiScannedNames = new(StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, string> _gpuiPlayerPaths; // prefabName → full catalog path

        public static void ScanGpuiProps()
        {
            if (!PropLibrary.IsInitialized) return;

            string catalogPath = Path.Combine(Application.streamingAssetsPath, "aa", "catalog.json");
            int insertAt = PropLibrary.PrimitiveNames.Length;
            int added    = 0;
            int nextGpuiIndex = 0;

            if (PropLibrary.TryLoadGpuiCache(catalogPath, out var cached) && cached != null)
            {
                foreach (var (baseName, id, visualPath, prefabName) in cached)
                {
                    if (PropLibrary.TryParseGpuiIndex(id, out int legacyIndex))
                        nextGpuiIndex = Math.Max(nextGpuiIndex, legacyIndex + 1);

                    string stableId = PropLibrary.BuildStableGpuiId(baseName, prefabName, visualPath);
                    if (!string.Equals(id, stableId, StringComparison.Ordinal))
                        PropLibrary._idAliases[id] = stableId;

                    if (PropLibrary._byId.ContainsKey(stableId)) continue;

                    var info = new PropInfo(stableId, baseName)
                    {
                        gpuiIndex      = nextGpuiIndex++,
                        visualPath     = visualPath,
                        gpuiPrefabName = prefabName,
                        isLoaded       = false,
                        isInvalid      = false,
                    };
                    PropLibrary._all.Insert(insertAt++, info);
                    PropLibrary._byId[stableId] = info;
                    GpuiScannedNames.Add(baseName);
                    added++;
                }
                BBLog.Msg($"[PropLibrary] GPUI cache loaded: {cached.Count} props.");
            }

            var loaded = PropLibrary.TryGetLoadedProps();
            BBLog.Msg($"[PropLibrary] ScanGpuiProps: loadedProps count={loaded?.Length ?? -1}");

            if (loaded == null || loaded.Length == 0)
            {
                PropMetadataStore.MigratePropIdsToCanonical();
                if (added > 0) PropLibrary.RebuildFiltered();
                return;
            }

            var visualLookup  = BuildGpuiVisualLookup();
            int gpuiIdx       = nextGpuiIndex;

            for (int i = 0; i < loaded.Length; i++)
            {
                var prefabGO = loaded[i];
                if (prefabGO == null) continue;

                bool hasRenderer = prefabGO.GetComponentInChildren<MeshRenderer>() != null
                                || prefabGO.GetComponentInChildren<SkinnedMeshRenderer>() != null;
                if (hasRenderer) continue;

                bool hasCollider = prefabGO.GetComponentInChildren<MeshCollider>() != null;
                if (!hasCollider) continue;

                string baseName = PropLibrary.NormalizePropName(prefabGO.name);

                visualLookup.TryGetValue(baseName, out string visualPath);
                visualPath   ??= "";
                string prefabName = prefabGO.name;

                int    gi     = gpuiIdx++;
                string gpuiId = PropLibrary.BuildStableGpuiId(baseName, prefabName, visualPath);
                if (PropLibrary._byId.ContainsKey(gpuiId)) continue;

                string legacyId = $"gpui://{gi}";
                if (!string.Equals(legacyId, gpuiId, StringComparison.Ordinal))
                    PropLibrary._idAliases[legacyId] = gpuiId;

                var info = new PropInfo(gpuiId, baseName)
                {
                    gpuiIndex      = gi,
                    visualPath     = visualPath,
                    gpuiPrefabName = prefabName,
                    isLoaded       = false,
                    isInvalid      = false,
                };

                GpuiScannedNames.Add(baseName);
                PropLibrary._all.Insert(insertAt++, info);
                PropLibrary._byId[gpuiId] = info;
                added++;
            }

            // Regeneration must not depend solely on currently loaded GPUI props. Backfill
            // from catalog player-prefab entries so off-screen props keep stable IDs.
            var playerPaths = GetGpuiPlayerPaths();
            int backfilledFromCatalog = 0;
            foreach (var kvp in playerPaths)
            {
                string prefabName = kvp.Key;
                if (string.IsNullOrEmpty(prefabName)) continue;

                string baseName = PropLibrary.NormalizePropName(prefabName);
                if (string.IsNullOrEmpty(baseName)) continue;

                visualLookup.TryGetValue(baseName, out string visualPath);
                visualPath ??= "";

                int    gi     = gpuiIdx++;
                string gpuiId = PropLibrary.BuildStableGpuiId(baseName, prefabName, visualPath);
                if (PropLibrary._byId.ContainsKey(gpuiId)) continue;

                string legacyId = $"gpui://{gi}";
                if (!string.Equals(legacyId, gpuiId, StringComparison.Ordinal))
                    PropLibrary._idAliases[legacyId] = gpuiId;

                var info = new PropInfo(gpuiId, baseName)
                {
                    gpuiIndex      = gi,
                    visualPath     = visualPath,
                    gpuiPrefabName = prefabName,
                    isLoaded       = false,
                    isInvalid      = false,
                };

                GpuiScannedNames.Add(baseName);
                PropLibrary._all.Insert(insertAt++, info);
                PropLibrary._byId[gpuiId] = info;
                added++;
                backfilledFromCatalog++;
            }

            var cacheEntries = new List<(string baseName, string id, string visualPath, string prefabName)>();
            foreach (var info in PropLibrary._all)
            {
                if (!info.IsGpui) continue;
                cacheEntries.Add((info.displayName, info.id, info.visualPath ?? "", info.gpuiPrefabName ?? ""));
            }

            PropLibrary.SaveGpuiCache(catalogPath, cacheEntries);
            PropMetadataStore.MigratePropIdsToCanonical();
            if (added > 0) PropLibrary.RebuildFiltered();
            BBLog.Msg($"[PropLibrary] GPUI catalog backfill added: {backfilledFromCatalog} props.");
            BBLog.Msg($"[PropLibrary] GPUI scan complete: {added} props added.");
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
                if (PropLibrary.IsLowerLodVariant(path)) continue;

                if (!lookup.ContainsKey(name))
                    lookup[name] = path;
            }
        }

        internal static void ExtractPartsFromColliders(GameObject root, PropInfo info)
        {
            var arr   = root.GetComponentsInChildren<MeshCollider>(true);
            var rootT = root.transform;
            foreach (var mc in arr)
            {
                if (mc == null || mc.sharedMesh == null) continue;
                PropLibrary.AddPart(info, mc.sharedMesh, null, mc.transform, rootT);
            }
        }

        internal static Dictionary<string, string> GetGpuiPlayerPaths()
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

                BBLog.Msg($"[PropLibrary] Built GPUI player-path lookup: {_gpuiPlayerPaths.Count} entries.");
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
    }
}
