using System;
using System.Collections;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace BabyBlocks
{
    // The material display list/cache used by PropMetadataPanel's override dropdown and
    // MaterialConstructionPanel: the full set of known material names/labels, the live
    // Material lookup by name, known source-prop tracking, and MicroSplat layer materials.
    internal static class MaterialCatalog
    {
        internal const float MicroSplatUVScaleMultiplier = 8f; // terrain layers tile at world scale; multiply to fit props

        // Display names, in the same order/sort as the per-prop override dropdown
        // (index 0 is always PropMetadataStore.NoOverrideLabel).
        internal static readonly List<string> MaterialNames  = new();
        internal static readonly List<string> MaterialLabels = new();
        internal static readonly Dictionary<string, Material> MaterialByName = new(StringComparer.OrdinalIgnoreCase);

        internal static readonly List<Material> MicroSplatLayerMats = new();
        internal static string[] MsControlProps;
        internal static Texture2D MsBlankControl;
        internal static readonly List<Texture2D> MsActiveControls = new();
        internal static bool MsHasPerTexUV;

        // Maps material name → prop ID of the prop whose asset natively contains that material.
        internal static readonly Dictionary<string, string> KnownMaterialSources = new(StringComparer.OrdinalIgnoreCase);
        internal static bool MaterialSourcesLoaded;
        internal static bool MaterialSourcesLoading;

        // Materials successfully (re)loaded from their recorded materialSourcePropId — the
        // canonical, correctly-textured native instance. Once verified, ResolveMaterial prefers
        // these over whatever same-named instance happens to be resident in the current area
        // (which may be a broken/textureless area-local copy, e.g. "NewMat" in the icy area).
        // Persists across area changes (InvalidateMaterialCache does not clear it).
        internal static readonly Dictionary<string, Material> VerifiedSourceMaterials = new(StringComparer.OrdinalIgnoreCase);

        internal static void SortMaterialList()
        {
            if (MaterialNames.Count <= 2) return;
            var pairs = new List<(string name, string label)>(MaterialNames.Count - 1);
            for (int i = 1; i < MaterialNames.Count; i++)
                pairs.Add((MaterialNames[i], MaterialLabels[i]));
            pairs.Sort((a, b) => string.Compare(a.label, b.label, StringComparison.OrdinalIgnoreCase));
            for (int i = 0; i < pairs.Count; i++)
            {
                MaterialNames[i + 1] = pairs[i].name;
                MaterialLabels[i + 1] = pairs[i].label;
            }
        }

        // Re-registers owned scene-variant clones into the display lists.
        // Mirrors the AddMicroSplatLayerMaterials pattern.
        internal static void AddSceneVariantMaterials()
        {
            // Count distinct texture states per base name.
            var countByBase = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in MaterialVariantTracker.SceneVariantMats.Keys)
            {
                countByBase.TryGetValue(key.baseName, out int c);
                countByBase[key.baseName] = c + 1;
            }

            // Iterate by key so we always have the real base name, not a parsed display name.
            foreach (var kvp in MaterialVariantTracker.SceneVariantMats)
            {
                string baseName = kvp.Key.baseName;
                var mat = kvp.Value;
                if (mat == null) continue;
                string displayName = mat.name;

                if (MaterialByName.ContainsKey(displayName)) continue;

                // Only show in the list if this material has 2+ distinct captured states.
                if (!countByBase.TryGetValue(baseName, out int stateCount) || stateCount < 2) continue;

                string shaderName = mat.shader != null ? mat.shader.name : string.Empty;
                string label = string.IsNullOrEmpty(shaderName) ? displayName : $"{displayName}  [{shaderName}]";
                MaterialNames.Add(displayName);
                MaterialLabels.Add(label);
                MaterialByName[displayName] = mat;
            }
        }

        // Timestamp of the last AddMicroSplatLayerMaterials() retry attempt when MicroSplatLayerMats
        // was empty (terrain not loaded at initial scan). Initialized to MinValue so the first call
        // to EnsureMaterialList always triggers an immediate attempt.
        static float _microSplatRetryTime = float.MinValue;
        // Incremented each time AddMicroSplatLayerMaterials completes a full rebuild, so
        // EnsureMaterialList can detect a rebuild happened and force a full list sort.
        static int   _microSplatBuildGen  = 0;

        internal static void EnsureMaterialList()
        {
            // If MicroSplat layer materials were never built (terrain wasn't loaded when
            // EnsureMaterialList first ran), retry AddMicroSplatLayerMaterials on a timer.
            // On success, force a full list rebuild so the new entries appear in MaterialNames.
            if (MicroSplatLayerMats.Count == 0)
            {
                float now = Time.realtimeSinceStartup;
                if (now - _microSplatRetryTime >= 1f)
                {
                    _microSplatRetryTime = now;
                    AddMicroSplatLayerMaterials();
                    if (MicroSplatLayerMats.Count > 0)
                        MaterialVariantTracker.MaterialsLoaded = false;
                }
            }

            // Run the variant capture before the guard. CaptureSceneVariants is self-rate-limited
            // and on subsequent calls only iterates the small watched-materials set (not all memory).
            int variantsBefore = MaterialVariantTracker.SceneVariantMats.Count;
            MaterialVariantTracker.CaptureSceneVariants();
            if (MaterialVariantTracker.SceneVariantMats.Count != variantsBefore)
                MaterialVariantTracker.MaterialsLoaded = false; // new variants found — force a list rebuild

            // Even when no full rebuild is needed, check whether MicroSplat layer materials
            // were GC'd by Unity/Addressables since the last build.  The fast path (all alive)
            // is just 28 null-checks; the slow path (anyDestroyed) triggers at most once per
            // destruction cycle and forces a full sort rebuild so entries stay ordered.
            if (MicroSplatLayerMats.Count > 0)
            {
                int gen = _microSplatBuildGen;
                AddMicroSplatLayerMaterials();
                if (_microSplatBuildGen != gen)                  // a rebuild happened
                    MaterialVariantTracker.MaterialsLoaded = false;
            }

            if (MaterialVariantTracker.MaterialsLoaded) return;
            MaterialVariantTracker.MaterialsLoaded = true;
            MaterialNames.Clear();
            MaterialLabels.Clear();
            MaterialByName.Clear();
            MaterialNames.Add(PropMetadataStore.NoOverrideLabel);
            MaterialLabels.Add(PropMetadataStore.NoOverrideLabel);

            try
            {
                var mats = Resources.FindObjectsOfTypeAll<Material>();
                if (mats != null)
                {
                    // Three-pass approach so we know upfront whether a name has multiple distinct
                    // texture states before assigning display names — and, crucially, so that
                    // GetTextureSig (GetTexturePropertyNames/GetTexture — slow IL2Cpp interop calls)
                    // only runs for materials that actually share a name with another loaded
                    // material. Calling it for all 3000+ scanned materials unconditionally was the
                    // single biggest contributor to the freeze on first scan (and the Linux
                    // compositor black-screen it triggers) — most names appear only once, where the
                    // signature is never even consulted (see hasVars below).
                    //
                    // Pass 1: group by name only — cheap, no texture introspection.
                    var rawGroups = new Dictionary<string, List<Material>>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var m = mats[i];
                        if (m == null || string.IsNullOrEmpty(m.name)) continue;
                        if (MaterialVariantTracker.IsOwnedMaterial(m.GetInstanceID())) continue;
                        if (MaterialVariantTracker.ShouldHideMaterial(m.name)) continue;
                        if (!rawGroups.TryGetValue(m.name, out var rawGrp))
                        {
                            rawGrp = new List<Material>();
                            rawGroups[m.name] = rawGrp;
                        }
                        rawGrp.Add(m);
                    }

                    // Pass 2: only compute texture signatures (and dedupe by them) for names that
                    // collide — single-instance names skip GetTextureSig entirely.
                    var groups = new Dictionary<string, List<(Material mat, string sig)>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kvp in rawGroups)
                    {
                        var rawGrp = kvp.Value;
                        var grp = new List<(Material, string)>();
                        if (rawGrp.Count == 1)
                        {
                            grp.Add((rawGrp[0], string.Empty));
                        }
                        else
                        {
                            foreach (var m in rawGrp)
                            {
                                string sig = MaterialVariantTracker.GetTextureSig(m);
                                bool already = false;
                                foreach (var (_, s) in grp) if (string.Equals(s, sig, StringComparison.Ordinal)) { already = true; break; }
                                if (!already) grp.Add((m, sig));
                            }
                        }
                        groups[kvp.Key] = grp;
                    }
                    // Count distinct scene-variant states per base name so that a material with
                    // known variants is treated as multi-state even when only one is in memory.
                    var sceneVarCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var key in MaterialVariantTracker.SceneVariantMats.Keys)
                    {
                        sceneVarCounts.TryGetValue(key.baseName, out int c);
                        sceneVarCounts[key.baseName] = c + 1;
                    }

                    // Pass 3: assign display names — plain when only one sig AND no known scene
                    // variants, hashed for all occurrences when multiple states exist.
                    foreach (var kvp in groups)
                    {
                        var grp      = kvp.Value;
                        sceneVarCounts.TryGetValue(kvp.Key, out int svCount);
                        bool hasVars = grp.Count > 1 || svCount >= 2;
                        foreach (var (m, sig) in grp)
                        {
                            string displayName = hasVars ? $"{m.name} [{MaterialPathCatalog.ComputeStableHash(sig)}]" : m.name;
                            if (MaterialByName.ContainsKey(displayName)) continue;
                            string shaderName = m.shader != null ? m.shader.name : string.Empty;
                            string label = string.IsNullOrEmpty(shaderName) ? displayName : $"{displayName}  [{shaderName}]";
                            MaterialNames.Add(displayName);
                            MaterialLabels.Add(label);
                            MaterialByName[displayName] = m;
                        }
                    }
                }

                AddMicroSplatLayerMaterials();
                AddSceneVariantMaterials();

                if (MaterialNames.Count > 2)
                {
                    var pairs = new List<(string name, string label)>(MaterialNames.Count - 1);
                    for (int i = 1; i < MaterialNames.Count; i++)
                        pairs.Add((MaterialNames[i], MaterialLabels[i]));
                    pairs.Sort((a, b) => string.Compare(a.label, b.label, StringComparison.OrdinalIgnoreCase));
                    for (int i = 0; i < pairs.Count; i++)
                    {
                        MaterialNames[i + 1]  = pairs[i].name;
                        MaterialLabels[i + 1] = pairs[i].label;
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropMetadata] Material scan failed: {e.Message}");
            }

            // Every rebuild above starts from a clean MaterialNames/MaterialLabels and only
            // re-populates from whatever is currently resident in memory (Resources.FindObjectsOfTypeAll)
            // — so a material from an area that's since streamed out (e.g. "NewMat_Ice" after flying
            // away) drops off the list entirely. Re-merge the full catalog index every time so
            // previously-seen, area-specific materials stay listed (resolved lazily via
            // MaterialPathCatalog.TryLoadMaterialByName when actually applied). On the very first call this
            // is also done inside EnsureMaterialSources/FinishMaterialSourcesScan, but subsequent
            // rebuilds (after InvalidateMaterialCache) need it re-applied too since
            // EnsureMaterialSources is one-shot.
            AddCatalogMaterialsToList();

            EnsureMaterialSources();
        }

        // Registers a material source and back-fills materialSourcePropId on every saved entry that
        // shares the same overrideMaterialId but had no source recorded yet. Returns true if any were updated.
        //
        // Only fills in EMPTY materialSourcePropId values — never overwrites an existing one.
        // AddPartsToMaterialList calls this for every material found in a loaded prop's parts, not
        // just the one being specifically resolved, so a generic material name (e.g. "NewMat")
        // that's incidentally also present in some unrelated prop's asset must not have its
        // already-recorded (correct) source clobbered by that unrelated prop's id.
        internal static bool BackfillMaterialSource(string materialName, string sourcePropId)
        {
            if (string.IsNullOrEmpty(materialName) || string.IsNullOrEmpty(sourcePropId)) return false;
            // MicroSplat layer materials are generated at runtime — they have no asset source prop.
            if (materialName.StartsWith("[MicroSplat]", StringComparison.Ordinal)) return false;
            if (!KnownMaterialSources.ContainsKey(materialName))
                KnownMaterialSources[materialName] = sourcePropId;

            bool anyChanged = false;
            foreach (var kvp in PropMetadataStore._byId)
            {
                var item = kvp.Value;
                if (item.excluded) continue;
                if (!string.Equals(item.overrideMaterialId, materialName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(item.materialSourcePropId)) continue; // already has a source — don't clobber
                item.materialSourcePropId = sourcePropId;
                anyChanged = true;
            }
            return anyChanged;
        }

        // For each saved override whose source prop ID is known, load that prop so its asset bundle
        // (and thus its materials) comes into memory. Runs once after the initial material list is built.
        //
        // Synchronous fallback — does the whole scan in one go. Used only if something needs the
        // sources resolved immediately and the async version (below) hasn't been started/finished.
        // Guarded against double-running alongside the coroutine via MaterialSourcesLoading.
        internal static void EnsureMaterialSources()
        {
            if (MaterialSourcesLoaded || MaterialSourcesLoading) return;
            MaterialSourcesLoaded = true;
            if (!PropLibrary.IsInitialized) return;

            PropMetadataStore.EnsureLoaded();

            var sourceCandidates = CollectSourceCandidates();
            var anyBackfilled = RunSourcePass(sourceCandidates, out bool anyLoadedA);

            var selfCandidates = CollectSelfDiscoveryCandidates();
            bool anyLoadedB = RunSelfDiscoveryPass(selfCandidates);

            FinishMaterialSourcesScan(anyLoadedA || anyLoadedB, anyBackfilled);
        }

        // Async version — spreads the same work across multiple frames (a small handful of prop
        // loads per frame, yielding in between) so no single frame freezes long enough to trip the
        // Linux Wayland/X11 compositor's "no frame presented" timeout that blacks the window.
        // While running, IsLoadingMaterialSources is true and the palette blocks dragging.
        internal static IEnumerator EnsureMaterialSourcesCo()
        {
            if (MaterialSourcesLoaded || MaterialSourcesLoading) yield break;
            MaterialSourcesLoading = true;
            MaterialSourcesLoaded  = true;

            // Iterator methods run synchronously up to their first `yield return` the moment
            // MelonCoroutines.Start calls MoveNext() — without this, CollectSourceCandidates()
            // and the first blocking prop load would execute immediately on the calling frame,
            // reproducing the exact freeze/black-screen this coroutine exists to avoid.
            yield return null;

            const int itemsPerFrame = 1;

            try
            {
                if (!PropLibrary.IsInitialized) yield break;
                PropMetadataStore.EnsureLoaded();

                var sourceCandidates = CollectSourceCandidates();
                bool anyLoaded = false;
                bool anyBackfilled = false;
                int sinceYield = 0;
                foreach (var item in sourceCandidates)
                {
                    if (RunOneSourceItem(item, out bool loaded, out bool backfilled))
                    {
                        anyLoaded |= loaded;
                        anyBackfilled |= backfilled;
                    }
                    if (++sinceYield >= itemsPerFrame) { sinceYield = 0; yield return null; }
                }
                if (anyBackfilled) PropMetadataStore.Save();

                var selfCandidates = CollectSelfDiscoveryCandidates();
                sinceYield = 0;
                foreach (var item in selfCandidates)
                {
                    if (RunOneSelfDiscoveryItem(item)) anyLoaded = true;
                    if (++sinceYield >= itemsPerFrame) { sinceYield = 0; yield return null; }
                }

                FinishMaterialSourcesScan(anyLoaded, false);
            }
            finally
            {
                MaterialSourcesLoading = false;
            }
        }

        // Kicks off the spread-out async scan (idempotent). Falls back to nothing if already done
        // or already in progress.
        public static void StartMaterialSourcesScanAsync()
        {
            if (MaterialSourcesLoaded || MaterialSourcesLoading) return;
            MelonCoroutines.Start(EnsureMaterialSourcesCo());
        }

        // True while the async source scan is spreading its loads across frames. The palette uses
        // this to block dragging — placing a prop before its override material is in memory would
        // apply the override once (at placement time) and never retry.
        public static bool IsLoadingMaterialSources => MaterialSourcesLoading;

        // Called synchronously the moment the editor first activates, before any GUI frame can
        // run — without this, PropLibrary.IsInitialized is already true (set in Awake) but
        // MaterialSourcesLoading is still false until ActivateEditorScanCo's later
        // InvalidateMaterialSources call, so the palette briefly flashes the full prop list
        // (with not-yet-fixed-up materials) before settling into "Loading materials…".
        //
        // Deliberately ignores MaterialSourcesLoaded: ApplyMaterialOverridesToRoot (run for
        // already-placed props as a save loads, before the editor is ever opened) can trigger
        // EnsureMaterialList -> EnsureMaterialSources's synchronous pass first, which sets
        // MaterialSourcesLoaded = true without ever setting MaterialSourcesLoading. If that
        // happened, this would otherwise no-op and leave MaterialSourcesLoading false for the
        // 1-2 frames before ActivateEditorScanCo's InvalidateMaterialSources call — exactly the
        // flash this function exists to prevent.
        internal static void MarkMaterialSourcesPending()
        {
            if (MaterialSourcesLoading) return;
            MaterialSourcesLoading = true;
        }

        static List<PropExtraInfo> CollectSourceCandidates()
        {
            var list = new List<PropExtraInfo>();
            foreach (var kvp in PropMetadataStore._byId)
            {
                var item = kvp.Value;
                if (string.IsNullOrEmpty(item.overrideMaterialId)) continue;
                if (string.IsNullOrEmpty(item.materialSourcePropId)) continue; // no source tracked yet

                // Already verified from its source prop in a previous pass (this area or an
                // earlier one) — just re-register it, no reload needed, unless it's been
                // destroyed (e.g. asset bundle released during a far teleport).
                if (VerifiedSourceMaterials.TryGetValue(item.overrideMaterialId, out var verified) && verified != null)
                {
                    MaterialByName[item.overrideMaterialId] = verified;
                    continue;
                }

                list.Add(item);
            }
            return list;
        }

        static List<PropExtraInfo> CollectSelfDiscoveryCandidates()
        {
            var list = new List<PropExtraInfo>();
            foreach (var kvp in PropMetadataStore._byId)
            {
                var item = kvp.Value;
                if (string.IsNullOrEmpty(item.overrideMaterialId)) continue;
                if (MaterialByName.ContainsKey(item.overrideMaterialId)) continue;
                if (!string.IsNullOrEmpty(item.materialSourcePropId)) continue;
                if (item.overrideMaterialId.StartsWith("[MicroSplat]", StringComparison.Ordinal)) continue;
                list.Add(item);
            }
            return list;
        }

        static bool RunSourcePass(List<PropExtraInfo> candidates, out bool anyLoaded)
        {
            bool anyBackfilled = false;
            anyLoaded = false;
            foreach (var item in candidates)
            {
                if (RunOneSourceItem(item, out bool loaded, out bool backfilled))
                {
                    anyLoaded |= loaded;
                    anyBackfilled |= backfilled;
                }
            }
            if (anyBackfilled) PropMetadataStore.Save();
            return anyBackfilled;
        }

        // Finds a material by exact name among a loaded prop's parts. Parts come from the loaded
        // asset — always native materials, no contamination risk (see AddPartsToMaterialList).
        static Material FindPartMaterialByName(PropInfo info, string name)
        {
            if (info?.parts == null) return null;
            foreach (var part in info.parts)
            {
                if (part?.materials == null) continue;
                foreach (var mat in part.materials)
                    if (mat != null && string.Equals(mat.name, name, StringComparison.OrdinalIgnoreCase))
                        return mat;
            }
            return null;
        }

        static bool RunOneSourceItem(PropExtraInfo item, out bool anyLoaded, out bool anyBackfilled)
        {
            anyLoaded = false;
            anyBackfilled = false;

            // Filled by an earlier item in this same pass that shares the same overrideMaterialId.
            if (VerifiedSourceMaterials.TryGetValue(item.overrideMaterialId, out var already) && already != null)
            {
                MaterialByName[item.overrideMaterialId] = already;
                return false;
            }

            var sourceInfo = PropLibrary.FindById(item.materialSourcePropId);
            if (sourceInfo == null) return false;

            try
            {
                PropLibrary.LoadPropData(sourceInfo);
                anyLoaded = true;

                // Scan parts directly — GPUI materials don't reliably appear in
                // Resources.FindObjectsOfTypeAll after loading, but parts hold the real refs.
                AddPartsToMaterialList(sourceInfo);

                // Validate: if the override material isn't among the loaded source's parts, the
                // source was recorded incorrectly (e.g. from a contaminated renderer). Clear it
                // so we don't keep trying the wrong prop.
                var loadedMat = FindPartMaterialByName(sourceInfo, item.overrideMaterialId);
                if (loadedMat == null)
                {
                    item.materialSourcePropId = string.Empty;
                    anyBackfilled = true; // trigger Save() to persist the correction
                    return true;
                }

                // Validate quality: Standard-shader materials with no mainTexture are placeholder
                // instances produced when an asset bundle isn't fully initialised (Unity falls back
                // to a default Standard material). The correct material lives in the prop's own
                // visual prefab instead. Clear the bad source and the polluted MaterialByName entry
                // so self-discovery can re-derive the real material this session.
                if (loadedMat.shader != null && loadedMat.shader.name == "Standard"
                    && loadedMat.mainTexture == null)
                {
                    item.materialSourcePropId = string.Empty;
                    anyBackfilled = true;
                    // Remove any existing Standard placeholder so self-discovery isn't blocked.
                    if (MaterialByName.TryGetValue(item.overrideMaterialId, out var existing)
                        && existing != null && existing.shader != null
                        && existing.shader.name == "Standard" && existing.mainTexture == null)
                        MaterialByName.Remove(item.overrideMaterialId);
                    VerifiedSourceMaterials.Remove(item.overrideMaterialId);
                    return true;
                }

                // This is the canonical, correctly-textured instance — overwrite any inferior
                // area-local instance that the in-memory scan may have picked up under this name.
                MaterialByName[item.overrideMaterialId] = loadedMat;
                VerifiedSourceMaterials[item.overrideMaterialId] = loadedMat;

                // Propagate this source to all other entries that share the same override material.
                if (BackfillMaterialSource(item.overrideMaterialId, item.materialSourcePropId))
                    anyBackfilled = true;
            }
            catch (Exception) { }
            return true;
        }

        static bool RunSelfDiscoveryPass(List<PropExtraInfo> candidates)
        {
            bool anyLoaded = false;
            foreach (var item in candidates)
                if (RunOneSelfDiscoveryItem(item)) anyLoaded = true;
            return anyLoaded;
        }

        static bool RunOneSelfDiscoveryItem(PropExtraInfo item)
        {
            // Skip only if a non-placeholder material is already cached; a Standard/no-texture
            // entry left by RunOneSourceItem's quality rejection still needs self-discovery.
            if (MaterialByName.TryGetValue(item.overrideMaterialId, out var cached) && cached != null
                && !(cached.shader != null && cached.shader.name == "Standard" && cached.mainTexture == null))
                return false;

            var selfInfo = PropLibrary.FindById(item.id);
            if (selfInfo == null) return false;

            try
            {
                PropLibrary.LoadPropData(selfInfo);
                AddPartsToMaterialList(selfInfo); // sets materialSourcePropId + calls Save() if found
                return true;
            }
            catch { return false; }
        }

        // Shared tail: side-effect material harvest + catalog index, run once after either the
        // synchronous or async scan finishes.
        static void FinishMaterialSourcesScan(bool anyLoaded, bool alreadySaved)
        {
            // NOTE: Bulk pre-loading of all saved override materials via TryLoadMaterialByName was
            // removed here. Each WaitForCompletion() call can trigger game asset-management
            // callbacks (streaming, bundle eviction) that intermittently destroyed physics
            // colliders on previously placed props. Materials are now lazy-loaded on first use
            // in ApplyPreviewMaterial, ApplySlotMaterial, and ApplyMaterialOverridesToRoot.
            if (anyLoaded)
            {
                // Update the lookup map with any materials that came into memory as a side-effect
                // of loading source props. The display list is not touched here — it is owned by
                // the in-memory seenCount scan and the catalog.
                try
                {
                    var allMats = Resources.FindObjectsOfTypeAll<Material>();
                    if (allMats != null)
                        for (int i = 0; i < allMats.Length; i++)
                        {
                            var m = allMats[i];
                            if (m == null || string.IsNullOrEmpty(m.name)) continue;
                            if (MaterialVariantTracker.ShouldHideMaterial(m.name)) continue;
                            if (!MaterialByName.ContainsKey(m.name))
                                MaterialByName[m.name] = m;
                        }
                }
                catch { }
            }

            AddCatalogMaterialsToList();
        }

        // Adds every material name from the full catalog index to the display list that isn't
        // already present, so the search list is complete regardless of which asset bundles are
        // currently loaded. Actual Material objects for these are lazy-loaded on first use (see
        // ApplyPreviewMaterial / ApplySlotMaterial / ResolveMaterial's MaterialPathCatalog.TryLoadMaterialByName
        // fallback). IndexAllCatalogMaterials itself is a one-time, idempotent scan (sentinel in
        // cache) — this merge is cheap and safe to re-run on every EnsureMaterialList rebuild.
        internal static void AddCatalogMaterialsToList()
        {
            MaterialPathCatalog.IndexAllCatalogMaterials();
            var alreadyListed = new HashSet<string>(MaterialNames, StringComparer.OrdinalIgnoreCase);
            bool anyCatalogAdded = false;
            foreach (var kvp in MaterialPathCatalog.MaterialCatalogPaths)
            {
                string name = kvp.Key;
                if (name == "__IDX__") continue;
                if (MaterialVariantTracker.ShouldHideMaterial(name)) continue;
                if (!alreadyListed.Add(name)) continue;
                MaterialNames.Add(name);
                MaterialLabels.Add(name);
                anyCatalogAdded = true;
            }

            if (anyCatalogAdded) SortMaterialList();
        }

        // Returns the first material name found in the prop's parts that is NOT the override.
        // Used to recover the true native material when the live renderer is contaminated.
        internal static string FindNativeFromParts(PropInfo info, string overrideMaterialName)
        {
            if (info == null) return string.Empty;
            if (!info.isLoaded) PropLibrary.LoadPropData(info);
            if (info.parts == null) return string.Empty;
            foreach (var part in info.parts)
            {
                if (part?.materials == null) continue;
                foreach (var m in part.materials)
                {
                    if (m == null || string.IsNullOrEmpty(m.name)) continue;
                    if (!string.Equals(m.name, overrideMaterialName, StringComparison.OrdinalIgnoreCase))
                        return m.name;
                }
            }
            return string.Empty;
        }

        // Scans PropInfo.parts for materials and adds them to the list.
        // Used for GPUI props (no live renderers) where AddRendererMaterialsToList yields nothing.
        // Parts come from the loaded asset — always native materials, no contamination risk.
        internal static void AddPartsToMaterialList(PropInfo info)
        {
            if (info?.parts == null) return;
            foreach (var part in info.parts)
            {
                if (part?.materials == null) continue;
                foreach (var mat in part.materials)
                {
                    if (mat == null || string.IsNullOrEmpty(mat.name)) continue;
                    if (MaterialVariantTracker.ShouldHideMaterial(mat.name)) continue;
                    if (!string.IsNullOrEmpty(info.id))
                    {
                        if (BackfillMaterialSource(mat.name, info.id))
                            PropMetadataStore.Save();
                    }
                    // Only register in the lookup map — the display list is owned by the
                    // in-memory seenCount scan and the catalog, not by prop-metadata loading.
                    if (!MaterialByName.ContainsKey(mat.name))
                        MaterialByName[mat.name] = mat;
                }
            }
        }

        // Finds a live MicroSplatTerrain material instance and its recognized control-map
        // property names. Used both to build the layer materials initially and to refresh
        // their texture-array references after a teleport replaces the loaded terrain chunks.
        static Material FindMicroSplatBaseMaterial(out string[] controlProps)
        {
            controlProps = null;
            var allMats = Resources.FindObjectsOfTypeAll<Material>();

            // Prefer a material with _CustomControl0 — it has reliably overridable UV-sampled blend textures.
            // Skip our own cached "[MicroSplat] Layer N" materials — refreshing from one of those would
            // just copy stale/destroyed texture references back onto themselves (and onto each other).
            Material baseMat = null;
            int msCount = 0;
            for (int i = 0; i < allMats.Length && baseMat == null; i++)
            {
                var m = allMats[i];
                if (m == null || m.shader == null) continue;
                if (m.name.StartsWith("[MicroSplat] Layer ", StringComparison.Ordinal)) continue;
                if (!m.shader.name.StartsWith("MicroSplat", StringComparison.OrdinalIgnoreCase)) continue;
                msCount++;
                if (m.HasProperty("_CustomControl0")) baseMat = m;
            }
            for (int i = 0; i < allMats.Length && baseMat == null; i++)
            {
                var m = allMats[i];
                if (m == null || m.shader == null) continue;
                if (m.name.StartsWith("[MicroSplat] Layer ", StringComparison.Ordinal)) continue;
                if (m.shader.name.StartsWith("MicroSplat", StringComparison.OrdinalIgnoreCase)) { msCount++; baseMat = m; }
            }
            if (baseMat == null)
                return null;

            // All slots must be blanked per clone; leaving higher slots with original terrain data bleeds through.
            bool useCustom = baseMat.HasProperty("_CustomControl0");
            var controlPropList = new List<string>();
            for (int ci = 0; ci <= 7; ci++)
            {
                string pn = useCustom ? $"_CustomControl{ci}" : $"_Control{ci}";
                if (baseMat.HasProperty(pn)) controlPropList.Add(pn);
            }
            if (controlPropList.Count == 0) return null;

            controlProps = controlPropList.ToArray();
            return baseMat;
        }

        internal static void AddMicroSplatLayerMaterials()
        {
            if (MicroSplatLayerMats.Count > 0)
            {
                bool anyDestroyed = false;
                foreach (var mat in MicroSplatLayerMats)
                    if (mat == null) { anyDestroyed = true; break; }

                if (!anyDestroyed)
                {
                    // Already built — just re-register in case MaterialByName was cleared by EnsureMaterialList.
                    foreach (var mat in MicroSplatLayerMats)
                    {
                        if (MaterialByName.ContainsKey(mat.name)) continue;
                        MaterialNames.Add(mat.name);
                        MaterialLabels.Add(mat.name);
                        MaterialByName[mat.name] = mat;
                    }
                    return;
                }

                MelonLogger.Msg($"[BB:MicroSplat] {MicroSplatLayerMats.Count} layer materials destroyed by Unity GC/Addressables — rebuilding.");
                // One or more cached layer materials were destroyed (e.g. Addressables released
                // their backing assets during a far-teleport chunk drain). Drop their stale
                // "[MicroSplat] Layer N" entries — by index, since destroyed Materials can't
                // report their own name — and rebuild below so the search list and any
                // categorized-material entries referencing them resolve again.
                for (int layer = 0; layer < MicroSplatLayerMats.Count; layer++)
                {
                    string staleName = $"[MicroSplat] Layer {layer}";
                    int idx = MaterialNames.IndexOf(staleName);
                    if (idx >= 0)
                    {
                        MaterialNames.RemoveAt(idx);
                        MaterialLabels.RemoveAt(idx);
                    }
                    MaterialByName.Remove(staleName);
                }
                MicroSplatLayerMats.Clear();
                PropPreviewRenderer.InvalidateMicroSplatSpheres();
                MsActiveControls.Clear();
                MsBlankControl = null;
                MsControlProps = null;
            }
            try
            {
                var baseMat = FindMicroSplatBaseMaterial(out var controlProps);
                if (baseMat == null)
                {
                    BBLog.Msg("[PropMetadata] MicroSplat base material not found (terrain may not be loaded yet).");
                    return;
                }

                int layerCount = 0;
                var arrays = Resources.FindObjectsOfTypeAll<Texture2DArray>();
                for (int a = 0; a < arrays.Length; a++)
                {
                    var arr = arrays[a];
                    if (arr != null && string.Equals(arr.name, "MicroSplatConfig_diff_tarray",
                            StringComparison.Ordinal))
                    { layerCount = arr.depth; break; }
                }
                if (layerCount == 0) layerCount = controlProps.Length * 4;

                MsHasPerTexUV = baseMat.HasProperty("_PerTexUVScaleRotation0");

                BBLog.Msg(
                    $"[PropMetadata] MicroSplat base: '{baseMat.name}' shader: '{baseMat.shader.name}' " +
                    $"controlSlots: {controlProps.Length} layers: {layerCount} hasPerTexUV: {MsHasPerTexUV}");

                if (MsHasPerTexUV)
                {
                    var v0 = baseMat.GetVector("_PerTexUVScaleRotation0");
                    BBLog.Msg($"[PropMetadata] _PerTexUVScaleRotation0 = ({v0.x:F4},{v0.y:F4},{v0.z:F4},{v0.w:F4})");
                }

                MsControlProps = controlProps;

                MsBlankControl = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                MsBlankControl.SetPixel(0, 0, Color.clear);
                MsBlankControl.Apply();
                MsBlankControl.name = "MicroSplat_BlankControl";

                for (int layer = 0; layer < layerCount; layer++)
                {
                    try
                    {
                        int mapIdx = layer / 4;
                        int channel = layer % 4;
                        if (mapIdx >= controlProps.Length) break;

                        var activeControl = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                        activeControl.SetPixel(0, 0, new Color(
                            channel == 0 ? 1f : 0f,
                            channel == 1 ? 1f : 0f,
                            channel == 2 ? 1f : 0f,
                            channel == 3 ? 1f : 0f));
                        activeControl.Apply();
                        activeControl.name = $"MicroSplat_SingleLayer_{layer}";
                        MsActiveControls.Add(activeControl);

                        var mat = new Material(baseMat) { name = $"[MicroSplat] Layer {layer}" };

                        for (int c = 0; c < controlProps.Length; c++)
                            mat.SetTexture(controlProps[c], MsBlankControl);
                        mat.SetTexture(controlProps[mapIdx], activeControl);

                        if (MsHasPerTexUV)
                        {
                            string uvProp = $"_PerTexUVScaleRotation{layer}";
                            var v = baseMat.GetVector(uvProp);
                            mat.SetVector(uvProp, new Vector4(
                                v.x * MicroSplatUVScaleMultiplier, v.y * MicroSplatUVScaleMultiplier, v.z, v.w));
                        }

                        MicroSplatLayerMats.Add(mat);
                        MaterialNames.Add(mat.name);
                        MaterialLabels.Add(mat.name);
                        MaterialByName[mat.name] = mat;
                    }
                    catch { }
                }

                if (MicroSplatLayerMats.Count > 0)
                    _microSplatBuildGen++;
                else
                    MelonLogger.Warning("[BB:MicroSplat] Base material found but no layer materials could be created.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropMetadata] MicroSplat layer material creation failed: {e.Message}");
            }
        }

        // Re-points the cached MicroSplat layer materials at a currently-loaded terrain's
        // texture arrays. FarTeleportCo's parallel chunk drain can drop the global terrain
        // texture arrays' refcount to zero and force Addressables to release/reload them as
        // new instances, leaving these cached materials referencing the old, now-destroyed
        // arrays. CopyPropertiesFromMaterial updates the existing Material objects in place
        // (rather than replacing them), so already-placed props referencing them are fixed too.
        public static void RefreshMicroSplatLayerMaterials()
        {
            if (MicroSplatLayerMats.Count == 0 || MsControlProps == null)
            {
                BBLog.Msg($"[PropMetadata] RefreshMicroSplatLayerMaterials: skipped " +
                    $"(MicroSplatLayerMats.Count={MicroSplatLayerMats.Count}, " +
                    $"MsControlProps={(MsControlProps == null ? "null" : "set")})");
                return;
            }

            var baseMat = FindMicroSplatBaseMaterial(out _);
            if (baseMat == null)
            {
                BBLog.Msg("[PropMetadata] RefreshMicroSplatLayerMaterials: no MicroSplat base material found.");
                return;
            }

            for (int layer = 0; layer < MicroSplatLayerMats.Count; layer++)
            {
                var mat = MicroSplatLayerMats[layer];
                if (mat == null) continue;

                var name = mat.name;
                mat.CopyPropertiesFromMaterial(baseMat);
                mat.name = name;

                int mapIdx = layer / 4;
                if (mapIdx >= MsControlProps.Length) continue;

                for (int c = 0; c < MsControlProps.Length; c++)
                    mat.SetTexture(MsControlProps[c], MsBlankControl);
                mat.SetTexture(MsControlProps[mapIdx], MsActiveControls[layer]);

                if (MsHasPerTexUV)
                {
                    string uvProp = $"_PerTexUVScaleRotation{layer}";
                    var v = baseMat.GetVector(uvProp);
                    mat.SetVector(uvProp, new Vector4(
                        v.x * MicroSplatUVScaleMultiplier, v.y * MicroSplatUVScaleMultiplier, v.z, v.w));
                }
            }

            BBLog.Msg($"[PropMetadata] Refreshed {MicroSplatLayerMats.Count} MicroSplat layer materials.");
        }

        public static void ApplyMaterialOverridesToRoot(string propId, GameObject root)
        {
            PropMetadataStore.EnsureLoaded();
            if (string.IsNullOrEmpty(propId) || root == null) return;
            if (!PropMetadataStore._byId.TryGetValue(propId, out var info))
                return;

            // Ensure the full material list is built even when the debug UI has never been shown
            // (non-debug mode, first launch). This populates MaterialByName with hashed variants
            // and source-prop materials so ResolveMaterial can find any saved override.
            EnsureMaterialList();
            AddMicroSplatLayerMaterials();

            // Per-slot overrides
            var overrides = info.perSlotMaterialOverrides;
            if (overrides != null && overrides.Count > 0)
            {
                var renderers = root.GetComponentsInChildren<Renderer>(true);
                if (renderers == null || renderers.Length == 0) return;

                for (int s = 0; s < overrides.Count; s++)
                {
                    string matName = overrides[s];
                    if (string.IsNullOrEmpty(matName)
                        || string.Equals(matName, PropMetadataStore.NoOverrideLabel, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var mat = ResolveMaterial(matName, info.materialSourcePropId);
                    if (mat == null)
                    {
                        MelonLogger.Warning(
                            $"[PropMaterial] FAIL \"{propId}\" slot={s}  override=\"{matName}\"  source=\"{info.materialSourcePropId}\"" +
                            $"  inByName={MaterialByName.ContainsKey(matName)}  inVerified={VerifiedSourceMaterials.ContainsKey(matName)}");
                        continue;
                    }

                    bool slotIsPlaceholder = mat.shader != null
                        && mat.shader.name == "Standard"
                        && mat.mainTexture == null;
                    if (slotIsPlaceholder)
                    {
                        MelonLogger.Warning(
                            $"[PropMaterial] SKIP \"{propId}\" slot={s}  override=\"{matName}\"  source=\"{info.materialSourcePropId}\"" +
                            $" — Standard/no-texture placeholder, keeping native material");
                        MaterialByName.Remove(matName);
                        VerifiedSourceMaterials.Remove(matName);
                        continue;
                    }

                    for (int i = 0; i < renderers.Length; i++)
                    {
                        var r = renderers[i];
                        if (r == null) continue;
                        var mats = r.sharedMaterials;
                        if (mats == null) mats = new Material[0];
                        if (s >= mats.Length)
                        {
                            var expanded = new Material[s + 1];
                            for (int m = 0; m < mats.Length; m++) expanded[m] = mats[m];
                            mats = expanded;
                        }
                        mats[s] = mat;
                        r.sharedMaterials = mats;
                    }
                }
                return;
            }

            // Single-slot override (overrideMaterialId)
            // This is the common case for props with one material slot: the override is stored
            // in overrideMaterialId, not perSlotMaterialOverrides. ApplyPreviewMaterial applies
            // the same material to every slot of every renderer, so we mirror that here.
            string singleOverride = info.overrideMaterialId;
            if (string.IsNullOrEmpty(singleOverride)
                || string.Equals(singleOverride, PropMetadataStore.NoOverrideLabel, StringComparison.OrdinalIgnoreCase))
                return;

            {
                var singleMat = ResolveMaterial(singleOverride, info.materialSourcePropId);
                if (singleMat == null)
                {
                    bool inByName   = MaterialByName.ContainsKey(singleOverride);
                    bool inVerified = VerifiedSourceMaterials.ContainsKey(singleOverride);
                    MelonLogger.Warning(
                        $"[PropMaterial] FAIL \"{propId}\"  override=\"{singleOverride}\"  source=\"{info.materialSourcePropId}\"" +
                        $"  inByName={inByName}  inVerified={inVerified}");
                    return;
                }

                // Standard shader with no mainTexture is a Unity placeholder produced when an
                // asset bundle isn't fully initialised. Skip it so the prop keeps the correct
                // material the visual prefab already put on its renderers.
                bool isPlaceholder = singleMat.shader != null
                    && singleMat.shader.name == "Standard"
                    && singleMat.mainTexture == null;
                if (isPlaceholder)
                {
                    MelonLogger.Warning(
                        $"[PropMaterial] SKIP \"{propId}\"  override=\"{singleOverride}\"  source=\"{info.materialSourcePropId}\"" +
                        $" — Standard/no-texture placeholder, keeping native material");
                    MaterialByName.Remove(singleOverride);
                    VerifiedSourceMaterials.Remove(singleOverride);
                    return;
                }

                var renderers = root.GetComponentsInChildren<Renderer>(true);
                if (renderers == null || renderers.Length == 0) return;

                for (int i = 0; i < renderers.Length; i++)
                {
                    var r = renderers[i];
                    if (r == null) continue;
                    int count = 1;
                    var existing = r.sharedMaterials;
                    if (existing != null && existing.Length > 0) count = existing.Length;
                    var mats = new Material[count];
                    for (int m = 0; m < count; m++) mats[m] = singleMat;
                    r.sharedMaterials = mats;
                }
            }
        }

        // Re-resolves and re-assigns every placed prop's material overrides (both the catalog-level
        // overrides handled by ApplyMaterialOverridesToRoot, and per-instance MaterialConstruction
        // overrides) against the current MaterialByName cache. Call this after the cache has been
        // rebuilt (InvalidateMaterialCache + EnsureMaterialList) following a far-teleport or area
        // change — FarTeleportCo's chunk drain can destroy the Material instances that placed props'
        // renderers were pointing at, leaving them pink/missing until re-pointed at fresh instances.
        // Does not push undo history — this is a silent repair pass, not a user edit.
        public static void ReapplyAllMaterialOverrides()
        {
            var mgr = LevelEditorManager.Instance;
            if (mgr == null) return;

            foreach (var leo in mgr.Objects)
            {
                if (leo == null) continue;
                var root = leo.gameObject;
                if (root == null) continue;

                ApplyMaterialOverridesToRoot(leo.addressableKey, root);

                if (leo.materialConstructionId < 0) continue;
                var entry = PropMetadataStore.FindMaterialConstructionById(leo.materialConstructionId);
                if (entry == null || string.IsNullOrEmpty(entry.materialName)) continue;

                var mat = ResolveMaterial(entry.materialName, leo.addressableKey);
                if (mat == null) continue;

                var renderers = root.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    var r = renderers[i];
                    if (r == null) continue;
                    var existing = r.sharedMaterials;
                    int count = existing != null && existing.Length > 0 ? existing.Length : 1;
                    var mats = new Material[count];
                    for (int m = 0; m < count; m++) mats[m] = mat;
                    r.sharedMaterials = mats;
                }
            }
        }

        // Looks up a material by name in the in-memory cache, falling back to a catalog load.
        // Caches the result so subsequent spawns of the same prop don't re-hit the catalog.
        internal static string GetKnownMaterialSource(string materialName)
        {
            if (string.IsNullOrEmpty(materialName)) return string.Empty;
            return KnownMaterialSources.TryGetValue(materialName, out var sourcePropId) ? sourcePropId : string.Empty;
        }

        internal static Material ResolveMaterial(string matName, string sourcePropId = null)
        {
            if (string.IsNullOrEmpty(matName)) return null;

            // A material verified-loaded from its recorded materialSourcePropId is the canonical,
            // correctly-textured instance — it wins over whatever same-named instance happens to
            // be resident in the current scene (which may be a broken/textureless area-local copy).
            if (VerifiedSourceMaterials.TryGetValue(matName, out var verifiedMat) && verifiedMat != null)
            {
                MaterialByName[matName] = verifiedMat;
                return verifiedMat;
            }

            if (MaterialVariantTracker.SceneCurrentByName.TryGetValue(matName, out var liveMat) && liveMat != null)
            {
                MaterialByName[matName] = liveMat;
                return liveMat;
            }
            if (MaterialByName.TryGetValue(matName, out var mat) && mat != null)
                return mat;

            // Scene-variant clones are looked up by display name — no .name access on Il2Cpp objects.
            if (MaterialVariantTracker.SceneVariantByDisplayName.TryGetValue(matName, out var clone) && clone != null)
            {
                MaterialByName[matName] = clone;
                return clone;
            }
            if (MaterialVariantTracker.SceneVariantByDisplayName.ContainsKey(matName))
            {
                // Captured but clone is gone — fall back to base name without a catalog scan.
                // Variant display names always end in " [hash]" (see RegisterVariant), not
                // any "(...)" suffix, since native names can legitimately contain parens.
                int bracket = matName.LastIndexOf(" [", StringComparison.Ordinal);
                string baseName = (bracket > 0 && matName.EndsWith("]", StringComparison.Ordinal))
                    ? matName.Substring(0, bracket)
                    : matName;
                if (MaterialByName.TryGetValue(baseName, out mat) && mat != null)
                    return mat;
                mat = MaterialPathCatalog.TryLoadMaterialByName(baseName, sourcePropId);
                if (mat != null && !MaterialByName.ContainsKey(baseName)) MaterialByName[baseName] = mat;
                return mat;
            }

            // If the name looks like a variant display name ("Base [hash]" — see RegisterVariant,
            // which always uses square brackets) but isn't in SceneVariantByDisplayName, skip the
            // catalog scan and fall back to the base name. Native material names can legitimately
            // contain parenthesized suffixes (e.g. "Spruce_Norway_BarkMat (TVE Material)"), so we
            // must only strip the square-bracket variant pattern, not any "(...)" suffix.
            int lastBracket = matName.LastIndexOf(" [", StringComparison.Ordinal);
            if (lastBracket > 0 && matName.EndsWith("]", StringComparison.Ordinal))
            {
                string baseName = matName.Substring(0, lastBracket);
                if (MaterialByName.TryGetValue(baseName, out mat) && mat != null)
                    return mat;
                mat = MaterialPathCatalog.TryLoadMaterialByName(baseName, sourcePropId);
                if (mat != null)
                {
                    if (!MaterialByName.ContainsKey(mat.name)) MaterialByName[mat.name] = mat;
                    if (!MaterialByName.ContainsKey(baseName)) MaterialByName[baseName] = mat;
                }
                return mat;
            }

            mat = MaterialPathCatalog.TryLoadMaterialByName(matName, sourcePropId);
            if (mat == null) return null;

            if (!MaterialByName.ContainsKey(mat.name)) MaterialByName[mat.name] = mat;
            if (!string.Equals(mat.name, matName, StringComparison.OrdinalIgnoreCase)
                && !MaterialByName.ContainsKey(matName))
                MaterialByName[matName] = mat;
            return mat;
        }

        // Returns a material from the in-memory cache by exact name, or null if not found.
        // Used by MaterialInspectorPanel so it benefits from the already-built material list.
        public static Material TryGetMaterialByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (MaterialByName.TryGetValue(name, out var mat) && mat != null) return mat;
            if (MaterialVariantTracker.SceneVariantByDisplayName.TryGetValue(name, out mat) && mat != null) return mat;
            return null;
        }

        // Builds (or refreshes) the in-memory material list/cache used by both
        // material-name lookups and dropdowns.
        public static void EnsureMaterialListLoaded() => EnsureMaterialList();

        // Resolves a material by display name, falling back to a catalog/asset load
        // (unlike TryGetMaterialByName, which only checks the in-memory cache).
        public static Material ResolveMaterialByName(string name) => ResolveMaterial(name);

        // Called after ScanGpuiProps, when the editor is opened, so EnsureMaterialSources can find
        // GPUI prop entries. Forces the full material list + source scan to run right now (via
        // EnsureMaterialList) instead of waiting for it to happen lazily on first prop drag — moves
        // the one-time scan cost to editor-open time, where a freeze is less disruptive than mid-drag.
        public static void InvalidateMaterialSources()
        {
            // Clear verified-source cache so the async scan can re-verify entries against
            // freshly-loaded assets. Without this, a bad entry written by an early sync scan
            // (e.g. a prop loaded before shaders fully initialise returns Standard/no-texture)
            // would never be overwritten: CollectSourceCandidates skips anything already present
            // in VerifiedSourceMaterials, so the async scan just reuses the wrong instance.
            VerifiedSourceMaterials.Clear();
            MaterialSourcesLoaded = false;

            // Build the in-memory material list now (cheap — FindObjectsOfTypeAll is ~0ms). Suppress
            // the synchronous EnsureMaterialSources that EnsureMaterialList would otherwise trigger
            // on its first-ever call — we want the spread-out async version instead, which is kicked
            // off below. (MaterialSourcesLoading also blocks dragging in the palette meanwhile.)
            MaterialSourcesLoading = true;
            EnsureMaterialList();
            MaterialSourcesLoading = false;

            StartMaterialSourcesScanAsync();
        }
    }
}
