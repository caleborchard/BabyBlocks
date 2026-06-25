using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace BabyBlocks
{
    // Tracks scene-specific "variant" materials — instances that share a name but have different
    // textures depending on which area is loaded — so the material catalog can offer each distinct
    // texture state as its own entry (display name "Base [hash]").
    internal static class MaterialVariantTracker
    {
        const float VariantScanInterval = 5f;

        // Owned clones of scene-specific material variants captured across all visited areas.
        // SceneVariantMats: (baseName, texSig) → clone, for dedup during capture.
        // SceneVariantByDisplayName: displayName → clone, for O(1) lookup without touching .name on Il2Cpp objects.
        // _materialSnapshotByInstance: instanceId → shallow clone/sig taken on first sight for that
        //   specific material instance. Using instance ids avoids false flip-flops when multiple
        //   different materials share the same name at the same time.
        internal static readonly Dictionary<(string baseName, string texSig), Material> SceneVariantMats = new();
        internal static readonly Dictionary<string, Material> SceneVariantByDisplayName = new(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<int, (string baseName, Material clone, string sig)> _materialSnapshotByInstance = new();
        internal static readonly Dictionary<string, Material> SceneCurrentByName = new(StringComparer.OrdinalIgnoreCase);
        // Live references to materials we're watching for texture-state changes.
        // Populated once by the initial full scan; subsequent checks iterate only this set.
        static readonly Dictionary<int, Material> _watchedLiveMaterials = new();
        // Instance IDs of every Material we created (snapshots + variant clones).
        // The main EnsureMaterialList scan skips these so our clones never shadow the live material.
        static readonly HashSet<int> _ownedMaterialIds = new();
        internal static bool MaterialsLoaded;
        static bool _initialVariantScanDone;
        static float _lastVariantScanTime = -999f;

        internal static bool ShouldHideMaterial(string name) =>
            name.IndexOf("Imposter", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Impostor", StringComparison.OrdinalIgnoreCase) >= 0
            || name.EndsWith(" (Instance)", StringComparison.Ordinal);

        // Computes a stable display hash for a material based on its texture content.
        // Uses the same GetTextureSig path as RegisterVariant so both systems produce
        // identical hashes for identical texture state — preventing duplicate entries.
        internal static string ComputeMatHash(Material m)
        {
            try
            {
                string sig = GetTextureSig(m);
                if (!string.IsNullOrEmpty(sig))
                    return MaterialPathCatalog.ComputeStableHash(sig);
                // No textures at all — use shader name for some distinction.
                string shaderSig = m.shader != null ? m.shader.name : string.Empty;
                return MaterialPathCatalog.ComputeStableHash(shaderSig.Length > 0 ? shaderSig : m.GetInstanceID().ToString());
            }
            catch { return MaterialPathCatalog.ComputeStableHash(m.GetInstanceID().ToString()); }
        }

        static bool ShouldSkipSceneVariantCapture(Material m)
        {
            if (m == null || string.IsNullOrEmpty(m.name)) return true;
            if (ShouldHideMaterial(m.name)) return true; // covers " (Instance)" too

            // Skip classes of materials that change instance ID constantly and are not
            // area-variant materials we care about tracking.
            if (m.name.StartsWith("MicroSplat", StringComparison.OrdinalIgnoreCase)) return true;
            if (m.name.StartsWith("Hidden/", StringComparison.OrdinalIgnoreCase)) return true;
            if (m.name.IndexOf("MegaProxy", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (m.name.IndexOf("TVE Material", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (m.name.IndexOf("TVE Texture", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (m.name.IndexOf("(TVE", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (m.name.EndsWith("(Clone)", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Called on scene load. Immediately scans for material instances that share a name but
        // have different textures (scene-variant materials), clones each new variant into
        // SceneVariantMats, then invalidates the display list so variants are registered on
        // the next palette open.
        public static void InvalidateMaterialCache()
        {
            MaterialsLoaded = false;
            _lastVariantScanTime = -999f; // force immediate re-scan on next palette open
            SceneCurrentByName.Clear();
            // Force the next CaptureSceneVariants call to do a full FindObjectsOfTypeAll scan
            // so materials that came into memory after the previous initial scan (e.g. GPUI
            // props loaded by ScanGpuiProps) are added to the watched set.
            _initialVariantScanDone = false;
        }

        // Builds a signature string from all texture properties on a material so we can detect
        // when a material's texture state changes between areas (even with non-standard shaders
        // that don't use _MainTex, which makes mainTexture return null).
        internal static string GetTextureSig(Material m)
        {
            try
            {
                var names = m.GetTexturePropertyNames();
                if (names == null || names.Length == 0) return string.Empty;
                var sb = new System.Text.StringBuilder();
                foreach (var prop in names)
                {
                    var tex = m.GetTexture(prop);
                    if (tex != null) sb.Append(tex.name).Append('\n');
                }
                return sb.ToString();
            }
            catch { return string.Empty; }
        }

        // Returns the first non-empty texture name on a material, for use in display names.
        internal static string GetFirstTextureName(Material m)
        {
            try
            {
                var names = m.GetTexturePropertyNames();
                if (names == null) return string.Empty;
                foreach (var prop in names)
                {
                    var tex = m.GetTexture(prop);
                    if (tex != null && !string.IsNullOrEmpty(tex.name)) return tex.name;
                }
            }
            catch { }
            return string.Empty;
        }

        // On every palette open: for each visible material in memory, compare its current texture
        // sig against the snapshot taken on first sight. If the sig has changed, we now have two
        // distinct texture states — register both as variants. Uses only shallow clones (no GPU
        // readback) so it's cheap even with thousands of materials in memory.
        internal static void CaptureSceneVariants()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastVariantScanTime < VariantScanInterval) return;
            _lastVariantScanTime = now;

            try
            {
                if (!_initialVariantScanDone)
                {
                    // First call: full scan to populate the watched-materials set.
                    _initialVariantScanDone = true;
                    var mats = Resources.FindObjectsOfTypeAll<Material>();
                    if (mats == null) return;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        try
                        {
                            var m = mats[i];
                            if (ShouldSkipSceneVariantCapture(m)) continue;
                            if (_ownedMaterialIds.Contains(m.GetInstanceID())) continue;
                            _watchedLiveMaterials[m.GetInstanceID()] = m;
                            CheckMaterialVariant(m);
                        }
                        catch { }
                    }
                }
                else
                {
                    // Subsequent calls: only re-check watched materials — no FindObjectsOfTypeAll.
                    foreach (var kvp in _watchedLiveMaterials)
                    {
                        try
                        {
                            var m = kvp.Value;
                            if (m == null) continue;
                            CheckMaterialVariant(m);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropMetadata] CaptureSceneVariants failed: {e.Message}");
            }
        }

        static void CheckMaterialVariant(Material m)
        {
            int instanceId = m.GetInstanceID();
            SceneCurrentByName[m.name] = m;
            string sig = GetTextureSig(m);

            if (!_materialSnapshotByInstance.TryGetValue(instanceId, out var snap)
                || !string.Equals(snap.baseName, m.name, StringComparison.Ordinal))
            {
                var snapClone = new Material(m);
                _ownedMaterialIds.Add(snapClone.GetInstanceID());
                _materialSnapshotByInstance[instanceId] = (m.name, snapClone, sig);
                return;
            }

            if (string.Equals(sig, snap.sig, StringComparison.Ordinal)) return;

            RegisterVariant(snap.baseName, snap.clone, snap.sig);
            RegisterVariant(m.name, m, sig);

            var updatedClone = new Material(m);
            _ownedMaterialIds.Add(updatedClone.GetInstanceID());
            _materialSnapshotByInstance[instanceId] = (m.name, updatedClone, sig);
        }

        static void RegisterVariant(string baseName, Material source, string sig)
        {
            var key = (baseName, sig);
            if (SceneVariantMats.ContainsKey(key)) return;

            // All variants always get a hash of their texture signature so the display name is
            // stable and consistent with the seenCount two-pass naming.
            string displayName = $"{baseName} [{MaterialPathCatalog.ComputeStableHash(sig)}]";
            int n = 2;
            while (SceneVariantByDisplayName.ContainsKey(displayName))
                displayName = $"{baseName} [{MaterialPathCatalog.ComputeStableHash(sig + n++)}]";

            var clone = new Material(source) { name = displayName };
            _ownedMaterialIds.Add(clone.GetInstanceID());
            SceneVariantMats[key] = clone;
            SceneVariantByDisplayName[displayName] = clone;

            if (baseName.IndexOf("New Material", StringComparison.OrdinalIgnoreCase) >= 0)
                MelonLogger.Msg($"[MatDiag] RegisterVariant \"{displayName}\" shader={source.shader?.name ?? "null"} tex={source.mainTexture?.name ?? "null"} sig={sig?.Replace("\n",";")}");
        }

        // Skips entries created by RegisterVariant/CheckMaterialVariant (owned clones) when the
        // main catalog scan walks Resources.FindObjectsOfTypeAll, so our clones never shadow the
        // live material they were copied from.
        internal static bool IsOwnedMaterial(int instanceId) => _ownedMaterialIds.Contains(instanceId);
    }
}
