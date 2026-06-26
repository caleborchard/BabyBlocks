using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MelonLoader;
using UnityEngine;

namespace BabyBlocks
{
    // Discovers props by scanning the Addressables catalog (raw + base64-decoded
    // m_KeyDataString) for prefab/mesh asset paths, and registers them into
    // PropLibrary's registry. Also provides a diagnostic helper that logs which
    // bundles/scenes reference a given prop's asset when all load methods fail.
    internal static class CatalogEnumerator
    {
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

        internal static void LogCatalogBundleHint(string propId)
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

                var bundles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var scenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                        int wStart = Math.Max(0, hit - 2048);
                        int wEnd = Math.Min(text.Length, hit + 2048);
                        string window = text.Substring(wStart, wEnd - wStart);

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
                    BBLog.Msg($"[PropLibrary] Catalog hint for \"{fileName}\": scenes → {string.Join(", ", scenes)}");
                else if (bundles.Count > 0)
                    BBLog.Msg($"[PropLibrary] Catalog hint for \"{fileName}\": bundles → {string.Join(", ", System.Linq.Enumerable.Take(bundles, 5))} {(bundles.Count > 5 ? $"(+{bundles.Count - 5} more)" : "")}");
                else
                    BBLog.Msg($"[PropLibrary] Catalog hint: \"{fileName}\" not found in catalog text.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropLibrary] LogCatalogBundleHint failed: {e.Message}");
            }
        }

        internal static void EnumerateFromCatalog()
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
                    && !PropLibrary.IsMeshAssetPath(entry))
                    continue;

                if (PropLibrary.IsLowerLodVariant(entry)) continue;

                string name = Path.GetFileNameWithoutExtension(entry);
                if (entry.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    if (name.EndsWith("_player", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.EndsWith("_Player", StringComparison.OrdinalIgnoreCase)) continue;
                }

                if (IsExcludedPath(entry)) continue;

                if (!seen.Add(entry)) continue;
                var info = new PropInfo(entry, name);
                PropLibrary._all.Add(info);
                PropLibrary._byId[entry] = info;
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
    }
}
