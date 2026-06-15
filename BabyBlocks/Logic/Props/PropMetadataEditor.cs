using System;
using System.Collections.Generic;
using UnityEngine;

namespace BabyBlocks
{
    // Pure logic / state for the "Prop Details" editing session: tracks the currently-selected
    // prop's editable fields, applies material/collider/bush changes to the live object, and
    // persists via PropMetadataStore. UI drawing lives in Props/PropMetadataPanel.cs, which
    // reads/writes this state and calls these methods.
    internal static class PropMetadataEditor
    {
        internal const float AutoSaveDelay = 0.75f;

        public static readonly string[] KnownSurfaceTags =
        {
            "",            // (none — don't override tag)
            "Rock",
            "Cliff",
            "Stone",
            "Dirt",
            "Mud",
            "Sand",
            "Gravel",
            "Riverbed",
            "Grass",
            "DeadGrass",
            "ForestFloor",
            "Moss",
            "MossyRock",
            "Snow",
            "Ice",
            "Wood",
            "MassiveWood",
            "Metal",
            "Fabric",
            "Cactus",
            "SandCastle",
            "Milk",
        };

        internal static readonly (string label, int value)[] KnownGrassTypes =
        {
            ("Normal",       1),
            ("None (silent)",0),
            ("Tall",         2),
            ("Dry Tall",     3),
            ("Fern",         4),
            ("Dry Leaf",     5),
            ("Wet Leaf",     6),
            ("Leafy Shrub",  7),
            ("Dry Shrub",    8),
            ("Wild Flower",  9),
            ("Twiggy",      10),
            ("Needle",      11),
            ("Cedar",       12),
            ("Reed",        13),
        };

        internal static string GrassTypeName(int value)
        {
            foreach (var (label, val) in KnownGrassTypes)
                if (val == value) return label;
            return value.ToString();
        }

        internal static string PropId;
        internal static string DisplayName = "";
        internal static string Category = "";
        internal static bool Excluded;
        internal static bool UseRenderMeshCollider;
        internal static string ColliderIgnoredSubmeshes = "";
        internal static string OverrideMaterialName = "";
        internal static string SelectedMaterialName = "";
        internal static string DefaultMaterialName = "";
        internal static int Index = -1;
        internal static bool Dirty;
        internal static bool MaterialExplicitlyChosen;
        internal static float LastChangeTime;
        internal static bool IsBush;
        internal static float BushRadius;
        internal static int SoundGrassType = 1;
        internal static bool KeepOriginalHierarchy;
        internal static string SurfaceType = "";
        internal static readonly HashSet<string> DisabledRendererPaths = new(StringComparer.Ordinal);
        internal static readonly List<string> PerSlotSelected = new();
        internal static readonly List<string> PerSlotDefault = new();
        internal static readonly HashSet<int> SlotHasExplicitOverride = new();
        internal static int MaxMaterialSlots;
        internal static bool MultiMaterialEnabled;
        internal static int ForcedMaterialSlots = 2;
        internal static Renderer[] SelectedRenderers;
        internal static Material[][] SelectedDefaultMaterials;
        internal static LevelEditorObject SelectedLEO;
        internal static string PaletteSelectedId;

        internal class RendererEntry
        {
            public string path;
            public Renderer renderer;
            public Collider collider;
            public bool enabled;
        }

        internal static readonly List<RendererEntry> RendererEntries = new();

        internal static void MarkDirty()
        {
            Dirty = true;
            LastChangeTime = Time.unscaledTime;
        }

        internal static void AutoSaveIfIdle()
        {
            if (!Dirty || string.IsNullOrEmpty(PropId)) return;
            if (Time.unscaledTime - LastChangeTime < AutoSaveDelay) return;

            ApplyCurrent();
            Dirty = false;
        }

        internal static void SetPaletteSelection(string propId)
        {
            PaletteSelectedId = propId;
        }

        internal static string GetSelectedPropId(LevelEditorObject obj)
        {
            if (obj != null)
            {
                PaletteSelectedId = null;
                if (!string.IsNullOrEmpty(obj.addressableKey)) return obj.addressableKey;
                if (!string.IsNullOrEmpty(obj.objectType)) return "primitive://" + obj.objectType;
                return null;
            }
            return string.IsNullOrEmpty(PaletteSelectedId) ? null : PaletteSelectedId;
        }

        internal static string BuildPath(Transform root, Transform t)
        {
            if (t == null) return "(null)";
            if (root == null) return t.name;

            var parts = new List<string>();
            var cur = t;
            while (cur != null)
            {
                parts.Add(cur.name);
                if (cur == root) break;
                cur = cur.parent;
            }
            parts.Reverse();

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) sb.Append('/');
                sb.Append(parts[i]);
            }
            return sb.ToString();
        }

        internal static string GetOverrideLabel()
        {
            if (string.IsNullOrEmpty(SelectedMaterialName))
                return PropMetadataStore.NoOverrideLabel;

            if (string.IsNullOrEmpty(OverrideMaterialName)
                && !string.IsNullOrEmpty(DefaultMaterialName)
                && string.Equals(SelectedMaterialName, DefaultMaterialName, StringComparison.OrdinalIgnoreCase))
                return SelectedMaterialName + " (default)";

            return SelectedMaterialName;
        }

        internal static void SelectMaterialByIndex(int index)
        {
            if (index <= 0 || index >= MaterialCatalog.MaterialNames.Count)
            {
                SelectedMaterialName = "";
                return;
            }

            SelectedMaterialName = MaterialCatalog.MaterialNames[index];
        }

        internal static int GetMaterialIndex(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            for (int i = 0; i < MaterialCatalog.MaterialNames.Count; i++)
            {
                if (string.Equals(MaterialCatalog.MaterialNames[i], name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return 0;
        }

        internal static void AddRendererMaterialsToList()
        {
            if (SelectedRenderers == null) return;
            for (int i = 0; i < SelectedRenderers.Length; i++)
            {
                var r = SelectedRenderers[i];
                if (r == null) continue;
                var mats = r.sharedMaterials;
                if (mats == null) continue;
                for (int m = 0; m < mats.Length; m++)
                {
                    var mat = mats[m];
                    if (mat == null || string.IsNullOrEmpty(mat.name)) continue;
                    if (MaterialVariantTracker.ShouldHideMaterial(mat.name)) continue;
                    // Track which prop this material came from and propagate to all saved entries
                    // that use it as an override but had no source recorded yet.
                    // Never record a source from a contaminated renderer: if the renderer material
                    // matches the saved override name, the renderer may be showing the override rather
                    // than the native material, so this prop is NOT the true source of that material.
                    // Note: we cannot rely on DefaultMaterialName here because the nativeMaterialName
                    // correction in SyncFromSelection runs after this method, so DefaultMaterialName
                    // may still reflect the contaminated state at this point.
                    bool isUntrustedOverride =
                        !string.IsNullOrEmpty(OverrideMaterialName)
                        && string.Equals(mat.name, OverrideMaterialName, StringComparison.OrdinalIgnoreCase);

                    if (!string.IsNullOrEmpty(PropId) && !isUntrustedOverride)
                    {
                        if (MaterialCatalog.BackfillMaterialSource(mat.name, PropId))
                            PropMetadataStore.Save();
                    }
                    // Lookup only — display list is owned by seenCount + catalog + scene variants.
                    if (!MaterialCatalog.MaterialByName.ContainsKey(mat.name))
                        MaterialCatalog.MaterialByName[mat.name] = mat;
                }
            }
        }

        internal static void ApplyCurrent()
        {
            if (string.IsNullOrEmpty(PropId)) return;

            if (MultiMaterialEnabled || MaxMaterialSlots > 1)
            {
                int effSlots = MultiMaterialEnabled
                    ? Math.Max(ForcedMaterialSlots, MaxMaterialSlots)
                    : MaxMaterialSlots;
                var perSlotToSave = new List<string>(effSlots);
                for (int s = 0; s < effSlots; s++)
                {
                    string sel = s < PerSlotSelected.Count ? PerSlotSelected[s] : string.Empty;
                    perSlotToSave.Add(SlotHasExplicitOverride.Contains(s) ? sel : string.Empty);
                }
                int slotCountToSave = MultiMaterialEnabled ? ForcedMaterialSlots : 0;
                var multiInfo = PropMetadataStore.Apply(PropId, DisplayName, Category, Excluded, UseRenderMeshCollider,
                    string.Empty, DefaultMaterialName, string.Empty, SurfaceType, DisabledRendererPaths,
                    ColliderIgnoredSubmeshes, perSlotToSave, slotCountToSave, IsBush, BushRadius, SoundGrassType,
                    KeepOriginalHierarchy);
                if (multiInfo != null)
                    Index = multiInfo.index;
                MaterialExplicitlyChosen = false;
                return;
            }

            string overrideToSave = GetOverrideToSave();
            MaterialCatalog.KnownMaterialSources.TryGetValue(overrideToSave ?? string.Empty, out string srcPropId);
            var info = PropMetadataStore.Apply(PropId, DisplayName, Category, Excluded, UseRenderMeshCollider,
                overrideToSave, DefaultMaterialName, srcPropId, SurfaceType, DisabledRendererPaths,
                ColliderIgnoredSubmeshes, null, 0, IsBush, BushRadius, SoundGrassType,
                KeepOriginalHierarchy);
            if (info != null)
                Index = info.index;
            OverrideMaterialName = overrideToSave ?? string.Empty;
            MaterialExplicitlyChosen = false;
        }

        internal static string GetOverrideToSave()
        {
            if (string.IsNullOrEmpty(SelectedMaterialName)) return string.Empty;

            if (string.Equals(SelectedMaterialName, PropMetadataStore.NoOverrideLabel, StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            // Only treat selected == default as "no override" when:
            //   • the user did NOT explicitly pick it this session, AND
            //   • it doesn't match a previously-saved override name (which would mean the renderer is
            //     contaminated — the game persisted the override and CacheDefaultMaterials read it as
            //     the native material).
            if (!string.IsNullOrEmpty(DefaultMaterialName)
                && string.Equals(SelectedMaterialName, DefaultMaterialName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(SelectedMaterialName, OverrideMaterialName, StringComparison.OrdinalIgnoreCase)
                && !MaterialExplicitlyChosen)
                return string.Empty;

            return SelectedMaterialName;
        }

        // Returns true if the selection actually changed (and a full resync ran). The UI uses
        // this to know when to reset its own dropdown/scroll state.
        internal static bool SyncFromSelection(LevelEditorObject selectedObject)
        {
            SelectedLEO = selectedObject;
            string id = GetSelectedPropId(selectedObject);
            if (string.Equals(id, PropId, StringComparison.Ordinal)) return false;

            if (Dirty && !string.IsNullOrEmpty(PropId))
            {
                ApplyCurrent();
                Dirty = false;
            }

            PropId = id;
            DisplayName = string.Empty;
            Category = string.Empty;
            OverrideMaterialName = string.Empty;
            SelectedMaterialName = string.Empty;
            DefaultMaterialName = string.Empty;
            CacheDefaultMaterials(selectedObject);
            Excluded = false;
            UseRenderMeshCollider = false;
            ColliderIgnoredSubmeshes = string.Empty;
            SurfaceType = string.Empty;
            IsBush = false;
            BushRadius = 0f;
            SoundGrassType = 1;
            KeepOriginalHierarchy = false;
            Index = -1;
            Dirty = false;
            MaterialExplicitlyChosen = false;
            DisabledRendererPaths.Clear();
            RendererEntries.Clear();
            PerSlotSelected.Clear();
            SlotHasExplicitOverride.Clear();
            MultiMaterialEnabled = false;
            ForcedMaterialSlots = 2;

            if (string.IsNullOrEmpty(id)) return true;

            var propLibInfo = PropLibrary.FindById(id);
            UseRenderMeshCollider = propLibInfo == null || !propLibInfo.HasColliderParts;

            List<string> savedSlots = null;
            if (PropMetadataStore.TryGet(id, out var info) && info != null)
            {
                DisplayName = info.displayName ?? string.Empty;
                Category = info.category ?? string.Empty;
                OverrideMaterialName = info.overrideMaterialId ?? string.Empty;
                SurfaceType = info.surfaceType ?? string.Empty;
                Excluded = info.excluded;
                UseRenderMeshCollider = info.useRenderMeshCollider;
                ColliderIgnoredSubmeshes = info.colliderIgnoredSubmeshes ?? string.Empty;
                IsBush = info.isBush;
                BushRadius = info.bushRadius;
                SoundGrassType = info.soundGrassType;
                KeepOriginalHierarchy = info.keepOriginalHierarchy;
                Index = info.index;
                if (info.disabledRenderers != null)
                {
                    for (int i = 0; i < info.disabledRenderers.Count; i++)
                    {
                        var path = info.disabledRenderers[i];
                        if (!string.IsNullOrEmpty(path)) DisabledRendererPaths.Add(path);
                    }
                }
                if (info.forcedMaterialSlots > 1)
                {
                    MultiMaterialEnabled = true;
                    ForcedMaterialSlots = info.forcedMaterialSlots;
                }
                savedSlots = info.perSlotMaterialOverrides;
            }

            SelectedMaterialName = string.IsNullOrEmpty(OverrideMaterialName)
                ? DefaultMaterialName
                : OverrideMaterialName;

            int effectiveSlots = MultiMaterialEnabled
                ? Math.Max(ForcedMaterialSlots, MaxMaterialSlots)
                : MaxMaterialSlots;

            while (PerSlotDefault.Count < effectiveSlots) PerSlotDefault.Add(string.Empty);
            for (int s = 0; s < effectiveSlots; s++)
            {
                string saved = savedSlots != null && s < savedSlots.Count ? savedSlots[s] ?? string.Empty : string.Empty;
                string def = s < PerSlotDefault.Count ? PerSlotDefault[s] : string.Empty;
                PerSlotSelected.Add(string.IsNullOrEmpty(saved) ? def : saved);
                if (!string.IsNullOrEmpty(saved))
                    SlotHasExplicitOverride.Add(s);
            }

            BuildRendererEntries(selectedObject);
            ApplyRendererVisibility();
            MaterialCatalog.EnsureMaterialList();
            AddRendererMaterialsToList();

            // GPUI props have no live renderers — scan their loaded parts for native materials.
            // Load on first selection so source recording works even for old-format entries.
            if (propLibInfo != null && propLibInfo.IsGpui)
            {
                if (!propLibInfo.isLoaded) PropLibrary.LoadPropData(propLibInfo);
                if (propLibInfo.isLoaded) MaterialCatalog.AddPartsToMaterialList(propLibInfo);
            }

            // If any saved override material (single-slot or per-slot) is still not in the list,
            // do a fresh Resources scan — GPUI loads material instances lazily as the player moves,
            // so a material may have entered memory after EnsureMaterialSources ran at startup.
            // Also try the catalog path for any per-slot override still absent after the scan.
            bool needsFreshScan = (!string.IsNullOrEmpty(OverrideMaterialName)
                                   && !MaterialCatalog.MaterialByName.ContainsKey(OverrideMaterialName));
            if (!needsFreshScan)
            {
                foreach (var s in SlotHasExplicitOverride)
                {
                    string slotMat = s < PerSlotSelected.Count ? PerSlotSelected[s] : string.Empty;
                    if (!string.IsNullOrEmpty(slotMat) && !MaterialCatalog.MaterialByName.ContainsKey(slotMat))
                    { needsFreshScan = true; break; }
                }
            }

            if (needsFreshScan)
            {
                // Update the lookup map only — do not push these into the display list, which is
                // managed by the seenCount scan and catalog to keep variant hashing consistent.
                try
                {
                    var allMats = Resources.FindObjectsOfTypeAll<Material>();
                    for (int i = 0; i < allMats.Length; i++)
                    {
                        var m = allMats[i];
                        if (m == null || string.IsNullOrEmpty(m.name)) continue;
                        if (MaterialVariantTracker.ShouldHideMaterial(m.name)) continue;
                        if (!MaterialCatalog.MaterialByName.ContainsKey(m.name))
                            MaterialCatalog.MaterialByName[m.name] = m;
                    }
                }
                catch { }

                // Single-slot: if the override is still absent after the Resources scan, try the
                // catalog. This handles saved names with "(Instance)" suffixes where the scan
                // added the material under the clean name but the saved name still has no entry.
                // Scene-variant clones are runtime-only and won't be in the catalog — skip them.
                bool isVariantKey = MaterialVariantTracker.SceneVariantByDisplayName.ContainsKey(OverrideMaterialName);
                if (!string.IsNullOrEmpty(OverrideMaterialName) && !MaterialCatalog.MaterialByName.ContainsKey(OverrideMaterialName)
                    && !OverrideMaterialName.StartsWith("[MicroSplat]", StringComparison.Ordinal)
                    && !isVariantKey)
                {
                    try
                    {
                        var mat = MaterialPathCatalog.TryLoadMaterialByName(OverrideMaterialName, info.materialSourcePropId);
                        if (mat != null)
                        {
                            if (!MaterialCatalog.MaterialByName.ContainsKey(mat.name))
                            {
                                string shaderName = mat.shader != null ? mat.shader.name : string.Empty;
                                string label = string.IsNullOrEmpty(shaderName) ? mat.name : $"{mat.name}  [{shaderName}]";
                                MaterialCatalog.MaterialNames.Add(mat.name);
                                MaterialCatalog.MaterialLabels.Add(label);
                                MaterialCatalog.MaterialByName[mat.name] = mat;
                            }
                            if (!string.Equals(mat.name, OverrideMaterialName, StringComparison.OrdinalIgnoreCase)
                                && !MaterialCatalog.MaterialByName.ContainsKey(OverrideMaterialName))
                                MaterialCatalog.MaterialByName[OverrideMaterialName] = mat;
                        }
                    }
                    catch { }
                }

                // Anything still missing after the Resources scan: try the catalog (covers
                // per-slot materials like Araucaria_Pine_SharedMat that are only in memory
                // near their native world area but are always findable via the catalog path).
                // Also handles saved names with "(Instance)" suffixes — TryLoadMaterialByName
                // strips those before searching, so we register the result under both names.
                foreach (var s in SlotHasExplicitOverride)
                {
                    string slotMat = s < PerSlotSelected.Count ? PerSlotSelected[s] : string.Empty;
                    if (string.IsNullOrEmpty(slotMat) || MaterialCatalog.MaterialByName.ContainsKey(slotMat)) continue;
                    if (slotMat.StartsWith("[MicroSplat]", StringComparison.Ordinal)) continue;
                    // Scene-variant clones are runtime-only; skip catalog lookup for them.
                    if (MaterialVariantTracker.SceneVariantByDisplayName.ContainsKey(slotMat)) continue;
                    try
                    {
                        var mat = MaterialPathCatalog.TryLoadMaterialByName(slotMat, info.materialSourcePropId);
                        if (mat == null) continue;
                        if (!MaterialCatalog.MaterialByName.ContainsKey(mat.name))
                        {
                            string shaderName = mat.shader != null ? mat.shader.name : string.Empty;
                            string label = string.IsNullOrEmpty(shaderName) ? mat.name : $"{mat.name}  [{shaderName}]";
                            MaterialCatalog.MaterialNames.Add(mat.name);
                            MaterialCatalog.MaterialLabels.Add(label);
                            MaterialCatalog.MaterialByName[mat.name] = mat;
                        }
                        // Alias so lookups using the saved "(Instance)"-suffixed name also resolve.
                        if (!string.Equals(mat.name, slotMat, StringComparison.OrdinalIgnoreCase)
                            && !MaterialCatalog.MaterialByName.ContainsKey(slotMat))
                            MaterialCatalog.MaterialByName[slotMat] = mat;
                    }
                    catch { }
                }
            }

            // If a native material name was persisted, use it to un-contaminate DefaultMaterialName.
            // CacheDefaultMaterials reads the live renderer, which may already have the override applied.
            if (PropMetadataStore.TryGet(PropId, out var metaInfo) && metaInfo != null)
            {
                // Only trust nativeMaterialName when it differs from the override. If they match, the
                // value was stored when the renderer was contaminated (override already applied) and
                // using it would make ApplyPreviewMaterial treat the override as "restoring to default",
                // which either does nothing or restores a null material for GPUI props.
                if (!string.IsNullOrEmpty(metaInfo.nativeMaterialName)
                    && !string.Equals(metaInfo.nativeMaterialName, metaInfo.overrideMaterialId,
                                      StringComparison.OrdinalIgnoreCase))
                    DefaultMaterialName = metaInfo.nativeMaterialName;

                // Lazy migration: old entries have overrideMaterialId but no nativeMaterialName or
                // materialSourcePropId. Fill them in now while the renderer is fresh (after restart the
                // renderer shows the true original material, not the override).
                bool migrationDirty = false;
                if (!string.IsNullOrEmpty(metaInfo.overrideMaterialId))
                {
                    // Clean up stale nativeMaterialName == overrideMaterialId (stored when renderer was
                    // contaminated — useless and causes ApplyPreviewMaterial to misbehave).
                    if (!string.IsNullOrEmpty(metaInfo.nativeMaterialName)
                        && string.Equals(metaInfo.nativeMaterialName, metaInfo.overrideMaterialId,
                                         StringComparison.OrdinalIgnoreCase))
                    {
                        metaInfo.nativeMaterialName = string.Empty;
                        migrationDirty = true;
                    }

                    // If the renderer shows the override material (game persisted it), the default
                    // looks contaminated. Try to recover the true native from the prop's loaded parts.
                    if (string.IsNullOrEmpty(metaInfo.nativeMaterialName)
                        && string.Equals(DefaultMaterialName, metaInfo.overrideMaterialId,
                                         StringComparison.OrdinalIgnoreCase))
                    {
                        string recovered = MaterialCatalog.FindNativeFromParts(propLibInfo, metaInfo.overrideMaterialId);
                        if (!string.IsNullOrEmpty(recovered))
                            DefaultMaterialName = recovered;
                    }

                    if (string.IsNullOrEmpty(metaInfo.nativeMaterialName)
                        && !string.IsNullOrEmpty(DefaultMaterialName)
                        && !string.Equals(DefaultMaterialName, metaInfo.overrideMaterialId,
                                          StringComparison.OrdinalIgnoreCase))
                    {
                        metaInfo.nativeMaterialName = DefaultMaterialName;
                        migrationDirty = true;
                    }

                    if (string.IsNullOrEmpty(metaInfo.materialSourcePropId)
                        && MaterialCatalog.KnownMaterialSources.TryGetValue(metaInfo.overrideMaterialId, out string knownSrc)
                        && !string.IsNullOrEmpty(knownSrc))
                    {
                        if (MaterialCatalog.BackfillMaterialSource(metaInfo.overrideMaterialId, knownSrc))
                            migrationDirty = true;
                    }
                }

                if (migrationDirty) PropMetadataStore.Save();
            }

            if (MultiMaterialEnabled || MaxMaterialSlots > 1)
                ApplyAllSlotMaterials();
            else
                ApplyPreviewMaterial(SelectedMaterialName);
            PropInstanceServices.ApplySurfaceType(selectedObject, SurfaceType);

            return true;
        }

        internal static void CacheDefaultMaterials(LevelEditorObject obj)
        {
            SelectedRenderers = null;
            SelectedDefaultMaterials = null;
            if (obj == null) return;

            try
            {
                var renderers = obj.GetComponentsInChildren<Renderer>(true);
                if (renderers == null || renderers.Length == 0) return;
                SelectedRenderers = renderers;
                SelectedDefaultMaterials = new Material[renderers.Length][];
                DefaultMaterialName = string.Empty;
                for (int i = 0; i < renderers.Length; i++)
                {
                    var r = renderers[i];
                    if (r == null) continue;
                    var mats = r.sharedMaterials;
                    if (mats == null || mats.Length == 0)
                    {
                        var single = r.sharedMaterial;
                        if (single != null)
                        {
                            SelectedDefaultMaterials[i] = new[] { single };
                            if (string.IsNullOrEmpty(DefaultMaterialName))
                                DefaultMaterialName = single.name ?? string.Empty;
                        }
                        else
                        {
                            SelectedDefaultMaterials[i] = null;
                        }
                    }
                    else
                    {
                        var copy = new Material[mats.Length];
                        for (int m = 0; m < mats.Length; m++)
                            copy[m] = mats[m];
                        SelectedDefaultMaterials[i] = copy;

                        if (string.IsNullOrEmpty(DefaultMaterialName))
                        {
                            for (int m = 0; m < copy.Length; m++)
                            {
                                var mat = copy[m];
                                if (mat != null && !string.IsNullOrEmpty(mat.name))
                                {
                                    DefaultMaterialName = mat.name;
                                    break;
                                }
                            }
                        }
                    }
                }

                // Compute max material slot count and per-slot default names.
                MaxMaterialSlots = 0;
                PerSlotDefault.Clear();
                for (int i = 0; i < SelectedDefaultMaterials.Length; i++)
                {
                    var m = SelectedDefaultMaterials[i];
                    if (m != null && m.Length > MaxMaterialSlots)
                        MaxMaterialSlots = m.Length;
                }
                for (int s = 0; s < MaxMaterialSlots; s++)
                    PerSlotDefault.Add(string.Empty);
                for (int i = 0; i < SelectedDefaultMaterials.Length; i++)
                {
                    var m = SelectedDefaultMaterials[i];
                    if (m == null || m.Length != MaxMaterialSlots) continue;
                    for (int s = 0; s < MaxMaterialSlots; s++)
                        PerSlotDefault[s] = m[s] != null ? m[s].name ?? string.Empty : string.Empty;
                    break;
                }
            }
            catch { }
        }

        internal static void BuildRendererEntries(LevelEditorObject obj)
        {
            RendererEntries.Clear();
            if (obj == null) return;

            var root = obj.transform;

            if (SelectedRenderers != null)
            {
                for (int i = 0; i < SelectedRenderers.Length; i++)
                {
                    var r = SelectedRenderers[i];
                    if (r == null) continue;
                    string path = BuildPath(root, r.transform);
                    var entry = new RendererEntry
                    {
                        path = path,
                        renderer = r,
                        enabled = r.enabled
                    };
                    if (DisabledRendererPaths.Contains(path)) entry.enabled = false;
                    RendererEntries.Add(entry);
                }
            }

            var colliders = obj.GetComponentsInChildren<Collider>(true);
            if (colliders != null)
            {
                for (int i = 0; i < colliders.Length; i++)
                {
                    var c = colliders[i];
                    if (c == null) continue;
                    string path = BuildPath(root, c.transform) + " [" + c.GetType().Name + "]";
                    var entry = new RendererEntry
                    {
                        path = path,
                        collider = c,
                        enabled = c.enabled
                    };
                    if (DisabledRendererPaths.Contains(path)) entry.enabled = false;
                    RendererEntries.Add(entry);
                }
            }
        }

        internal static void ApplyBushCollider(LevelEditorObject leo, bool enable)
        {
            if (leo == null) return;
            var root = leo.gameObject;

            var existingBush = root.GetComponent<Il2Cpp.BushCollider>();
            if (existingBush != null) UnityEngine.Object.DestroyImmediate(existingBush);
            var existingSphere = root.GetComponent<SphereCollider>();
            if (existingSphere != null) UnityEngine.Object.DestroyImmediate(existingSphere);

            PropInstanceServices.BushAudioTracker.Unregister(root.transform);
            // Restore / set trigger state on all physics colliders (BushCollider sphere excluded,
            // since it hasn't been added yet and its isTrigger is set explicitly below).
            PropInstanceServices.SetBushPassthrough(root, enable);

            if (!enable) { BushRadius = 0f; return; }

            BushRadius = PropInstanceServices.ComputeBushRadius(leo);
            PropInstanceServices.BushAudioTracker.Register(root.transform, BushRadius, SoundGrassType);
            var sphere = root.AddComponent<SphereCollider>();
            sphere.radius = BushRadius;
            sphere.isTrigger = true;
            var bush = root.AddComponent<Il2Cpp.BushCollider>();
            // Set rad immediately; BushCollider.Start() mirrors this but may not have run yet.
            bush.rad = sphere.radius * root.transform.localScale.x;
        }

        internal static void ApplyColliderToSelected(bool enable)
        {
            if (SelectedLEO == null) return;

            var info = PropLibrary.FindById(SelectedLEO.addressableKey);

            var toDestroy = new List<GameObject>();
            foreach (var t in SelectedLEO.GetComponentsInChildren<Transform>(true))
                if (t != null && t != SelectedLEO.transform && t.gameObject.name.StartsWith("PropCollider"))
                    toDestroy.Add(t.gameObject);
            foreach (var go in toDestroy)
                if (go != null) UnityEngine.Object.DestroyImmediate(go);

            var stale = new List<string>();
            foreach (var p in DisabledRendererPaths)
                if (p.Contains("PropCollider")) stale.Add(p);
            foreach (var p in stale) DisabledRendererPaths.Remove(p);

            if (info != null)
                PhysicsObjectManager.ApplyColliderParts(SelectedLEO.gameObject, info, enable);
        }

        internal static void ApplyRendererVisibility()
        {
            if (RendererEntries.Count == 0) return;
            for (int i = 0; i < RendererEntries.Count; i++)
            {
                var entry = RendererEntries[i];
                bool enabled = !DisabledRendererPaths.Contains(entry.path);
                if (entry.renderer != null) entry.renderer.enabled = enabled;
                else if (entry.collider != null) entry.collider.enabled = enabled;
                entry.enabled = enabled;
            }
        }

        internal static void ApplyPreviewMaterial(string materialName)
        {
            if (SelectedRenderers == null || SelectedRenderers.Length == 0) return;

            if (string.IsNullOrEmpty(materialName)
                || string.Equals(materialName, PropMetadataStore.NoOverrideLabel, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(DefaultMaterialName)
                    && string.Equals(materialName, DefaultMaterialName, StringComparison.OrdinalIgnoreCase)))
            {
                RestoreDefaultMaterials();
                return;
            }

            if (!MaterialCatalog.MaterialByName.TryGetValue(materialName, out var mat) || mat == null)
            {
                mat = MaterialPathCatalog.TryLoadMaterialByName(materialName, MaterialCatalog.GetKnownMaterialSource(materialName));
                if (mat != null)
                {
                    if (!MaterialCatalog.MaterialByName.ContainsKey(mat.name)) MaterialCatalog.MaterialByName[mat.name] = mat;
                    if (!string.Equals(mat.name, materialName, StringComparison.OrdinalIgnoreCase)
                        && !MaterialCatalog.MaterialByName.ContainsKey(materialName))
                        MaterialCatalog.MaterialByName[materialName] = mat;
                }
            }
            if (mat == null) { RestoreDefaultMaterials(); return; }
            for (int i = 0; i < SelectedRenderers.Length; i++)
            {
                var r = SelectedRenderers[i];
                if (r == null) continue;
                int count = 1;
                var existing = r.sharedMaterials;
                if (existing != null && existing.Length > 0) count = existing.Length;
                var mats = new Material[count];
                for (int m = 0; m < count; m++) mats[m] = mat;
                r.sharedMaterials = mats;
            }
        }

        internal static void RestoreDefaultMaterials()
        {
            if (SelectedDefaultMaterials == null) return;
            for (int i = 0; i < SelectedRenderers.Length; i++)
            {
                var r = SelectedRenderers[i];
                if (r == null) continue;
                var mats = SelectedDefaultMaterials[i];
                if (mats == null) continue;
                var copy = new Material[mats.Length];
                for (int m = 0; m < mats.Length; m++)
                    copy[m] = mats[m];
                r.sharedMaterials = copy;
            }
        }

        internal static void ApplySlotMaterial(int slot, string materialName)
        {
            if (SelectedRenderers == null) return;
            bool restore = string.IsNullOrEmpty(materialName)
                || string.Equals(materialName, PropMetadataStore.NoOverrideLabel, StringComparison.OrdinalIgnoreCase)
                || (slot < PerSlotDefault.Count && string.Equals(materialName, PerSlotDefault[slot], StringComparison.OrdinalIgnoreCase));

            for (int i = 0; i < SelectedRenderers.Length; i++)
            {
                var r = SelectedRenderers[i];
                if (r == null) continue;
                var mats = r.sharedMaterials;
                if (mats == null) mats = new Material[0];

                // Expand the array if the slot exceeds what the renderer currently has.
                if (slot >= mats.Length)
                {
                    if (restore) continue; // nothing to restore for a slot that doesn't exist yet
                    var expanded = new Material[slot + 1];
                    for (int m = 0; m < mats.Length; m++) expanded[m] = mats[m];
                    mats = expanded;
                }

                if (restore)
                {
                    var defaults = SelectedDefaultMaterials?[i];
                    mats[slot] = defaults != null && slot < defaults.Length ? defaults[slot] : null;
                }
                else
                {
                    if (!MaterialCatalog.MaterialByName.TryGetValue(materialName, out var mat) || mat == null)
                    {
                        mat = MaterialPathCatalog.TryLoadMaterialByName(materialName, MaterialCatalog.GetKnownMaterialSource(materialName));
                        if (mat != null)
                        {
                            if (!MaterialCatalog.MaterialByName.ContainsKey(mat.name)) MaterialCatalog.MaterialByName[mat.name] = mat;
                            if (!string.Equals(mat.name, materialName, StringComparison.OrdinalIgnoreCase)
                                && !MaterialCatalog.MaterialByName.ContainsKey(materialName))
                                MaterialCatalog.MaterialByName[materialName] = mat;
                        }
                    }
                    if (mat == null) continue;
                    mats[slot] = mat;
                }
                r.sharedMaterials = mats;
            }
        }

        internal static void ApplyAllSlotMaterials()
        {
            for (int s = 0; s < PerSlotSelected.Count; s++)
                ApplySlotMaterial(s, PerSlotSelected[s]);
        }
    }
}
