using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppInterop.Runtime.Attributes;
using Il2CppNWH.DWP2.WaterObjects;
using MelonLoader;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BabyBlocks
{
    // Toggles the "Base Map" (terrain, props, vegetation, water/audio) on/off for
    // building on an empty canvas, and tracks everything needed to restore it
    // exactly. Pure logic — extracted from LevelEditorManager so it can be reused
    // by any future UI.
    internal static class BaseMapController
    {
        public static bool BaseMapEnabled = true;

        // While true, Core.OnUpdate's "!BaseMapEnabled" block must not force
        // brl.off = true on its own — ApplyLoadedBaseMapStateDelayed is mid-rescan
        // and needs BRL to keep streaming so trailing chunks finish loading and get
        // hidden, before it sets brl.off = true itself.
        public static bool DeferBrlOff;

        // Selected WeatherMan "Day Weather Playlist" (Menu.curChapter index) applied
        // while the base map is hidden. Edited via the Save/Load window dropdown,
        // which is only enabled while BaseMapEnabled == false.
        public static int DayWeatherPlaylist;
        // Menu.curChapter value to restore once the base map is shown again.
        public static int RestoreDayWeatherPlaylist;
        static bool _weatherPlaylistOverridden;

        static readonly List<GameObject> _hiddenAudioObjects = new();
        // Third-party rendering objects (TVE, GPUI) disabled by type-name search.
        static readonly List<GameObject> _hiddenRenderObjects = new();
        // Renderers hidden on small physics props (rocks, debris). These often carry
        // Rigidbody/WaterObject(DWP2) components whose OnDisable touches native
        // jobs/NativeArrays — SetActive(false) on them can crash, so we only ever
        // toggle Renderer.enabled and leave the GameObject active.
        static readonly List<Renderer> _hiddenPropRenderers = new();
        // Cached BRL child renderers (MegaProxy LODs, GPUI pool objects) so OnUpdate
        // can suppress newly-activated ones cheaply without calling GetComponentsInChildren.
        internal static Renderer[] _brlRendererCache = Array.Empty<Renderer>();
        // Cached BRL child lights (e.g. day/night sun rig parented directly under BRL,
        // outside any chunk's loadedChunk) so they can be hidden/restored alongside
        // _brlRendererCache. Parallel array records each light's enabled state at
        // toggle-off time so it can be restored exactly.
        internal static Light[] _brlLightCache = Array.Empty<Light>();
        internal static bool[] _brlLightOrigStates = Array.Empty<bool>();
        static readonly List<MonoBehaviour> _disabledTerrainComponents = new();
        static readonly List<Collider> _disabledTerrainColliders = new();
        // Tracks loadedChunk.GetInstanceID() for chunks already scanned by
        // SetBaseMapEnabled/RescanLoadedChunksForBaseMapOff, so the rescan can skip
        // chunks it's already handled and only pick up newly-streamed-in ones.
        static readonly HashSet<int> _scannedChunkIds = new();
        // Player WaterObjects (torso + feet) frozen below sea level while Base
        // Map is off, so the player doesn't keep "swimming" in the hidden ocean.
        static readonly List<WaterObject> _suppressedWaterObjects = new();
        // Non-terrain colliders found inside loaded chunks and elsewhere (trash, rocks,
        // candles, cutscene/convo trigger volumes, etc.). Gathered regardless of their
        // current enabled state — some triggers start disabled and get enabled later by
        // quest/cutscene logic, which would otherwise re-arm them while Base Map is off.
        // _disabledPropCollidersOrigState is the parallel "was enabled before we hid it"
        // record used to restore exactly.
        static readonly List<Collider> _disabledPropColliders = new();
        static readonly List<bool> _disabledPropCollidersOrigState = new();
        // Lights hidden alongside renderers/colliders (see HidePropRenderers). Also
        // gathered regardless of current enabled state — some lights (e.g. streetlamps
        // driven by PointAtPlayer's day/night logic) start off and get switched on
        // later, which would otherwise make them pop back on while Base Map is off.
        // _hiddenLightsOrigState is the parallel "was enabled before we hid it" record.
        static readonly List<Light> _hiddenLights = new();
        static readonly List<bool> _hiddenLightsOrigState = new();
        // Light-controller MonoBehaviours (e.g. PointAtPlayer) disabled alongside a
        // hidden Light — without this, their Update() keeps flipping Light.enabled back
        // on every frame based on time-of-day/etc, fighting the suppression above.
        // Always re-enabled (they were enabled to have been driving the light at all).
        static readonly List<MonoBehaviour> _disabledLightControllers = new();

        // Editor-placed props' snow-suppressed material clones, shared across every
        // renderer that referenced the same original ("catalog") material. Two
        // unrelated snow mechanisms:
        //  - Kronnect "Better Lit" shader: the `_SNOW` LOCAL keyword is the master
        //    switch (a `_SnowMode` property exists but only selects a sub-mode once
        //    _SNOW is enabled — setting _SnowMode=0 alone does NOT disable the effect).
        //  - MicroSplat "Terrain_TerrainRockBlend" (rock props, e.g. "[MicroSplat]
        //    Layer 0"): no keyword involved; `_SnowAmount` (0..1) directly controls
        //    how much of the height/angle-based snow (_SnowHeightAngleRange/
        //    _SnowParams) is blended in. Distinguish from Better Lit by
        //    `!mat.HasProperty("_SnowMode") && mat.HasProperty("_SnowAmount")`.
        //
        // Previously this read r.materials, which clones one material PER RENDERER —
        // on a map with ~4800 prop renderers sharing ~22 materials, that turned 22
        // shared materials into ~4800 unique ones (one toggle of Base Map was enough
        // to wreck batching for the whole map). Instead we clone each original
        // material once, share that single clone across every renderer that used the
        // original, and just flip the clone's snow state — preserving sharing while
        // still never mutating the original catalog material asset.
        static readonly Dictionary<Material, Material> _propSnowCloneByOriginal = new();
        static readonly HashSet<Material> _propSnowClones = new();
        static readonly HashSet<Material> _snowKeywordClones = new();
        static readonly Dictionary<Material, (float origAmount, Vector4 origHeightRange)> _snowAmountClones = new();

        // Suppresses (disable=true) or restores (disable=false) the altitude/area snow
        // effect on every editor-placed prop's renderer materials. Called from
        // SetBaseMapEnabled via SetEditorPropsSnowDisabled(!enabled), and re-run after
        // FarTeleportCo's ReapplyAllMaterialOverrides — that repair pass repoints
        // override renderers at freshly-resolved original materials, orphaning
        // whatever clone this method previously assigned. Re-running with
        // disable=true is idempotent/cheap: original materials we've already cloned
        // are recognized via _propSnowCloneByOriginal and re-pointed at the existing
        // shared clone (no new clone created), and renderers already on a clone are
        // skipped entirely.
        [HideFromIl2Cpp]
        internal static void SetEditorPropsSnowDisabled(bool disable)
        {
            if (disable)
            {
                var propsContainer = LevelEditorManager.PropsContainer;
                if (propsContainer == null) return;

                int reassigned = 0;
                foreach (var r in propsContainer.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    var mats = r.sharedMaterials;
                    bool changed = false;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var mat = mats[i];
                        if (mat == null || _propSnowClones.Contains(mat)) continue;

                        if (!_propSnowCloneByOriginal.TryGetValue(mat, out var clone))
                        {
                            bool hasSnowKeyword = mat.IsKeywordEnabled("_SNOW");
                            bool hasSnowAmount = !mat.HasProperty("_SnowMode") && mat.HasProperty("_SnowAmount");
                            if (!hasSnowKeyword && !hasSnowAmount) continue;

                            clone = new Material(mat) { name = mat.name };
                            _propSnowCloneByOriginal[mat] = clone;
                            _propSnowClones.Add(clone);

                            if (hasSnowKeyword) _snowKeywordClones.Add(clone);
                            if (hasSnowAmount)
                            {
                                float origAmount = mat.GetFloat("_SnowAmount");
                                Vector4 origRange = mat.HasProperty("_SnowHeightAngleRange")
                                    ? mat.GetVector("_SnowHeightAngleRange") : default;
                                _snowAmountClones[clone] = (origAmount, origRange);
                            }
                        }

                        mats[i] = clone;
                        changed = true;
                        reassigned++;
                    }
                    if (changed) r.sharedMaterials = mats;
                }

                foreach (var clone in _snowKeywordClones)
                    clone.DisableKeyword("_SNOW");
                foreach (var kvp in _snowAmountClones)
                {
                    var (_, origRange) = kvp.Value;
                    kvp.Key.SetFloat("_SnowAmount", 0f);
                    // Also push the snow line height threshold (x) far above any real
                    // world height with a tiny falloff range (y), in case this shader
                    // variant gates the height/angle blend on _SnowHeightAngleRange
                    // rather than (or in addition to) _SnowAmount.
                    if (kvp.Key.HasProperty("_SnowHeightAngleRange"))
                        kvp.Key.SetVector("_SnowHeightAngleRange", new Vector4(1000000f, 1f, origRange.z, origRange.w));
                }

                BBLog.Msg($"[BaseMap] suppressed snow on {_snowKeywordClones.Count} keyword + " +
                    $"{_snowAmountClones.Count} _SnowAmount clone(s), {reassigned} renderer slot(s) repointed");
            }
            else
            {
                foreach (var clone in _snowKeywordClones)
                    clone.EnableKeyword("_SNOW");
                foreach (var kvp in _snowAmountClones)
                {
                    var (origAmount, origRange) = kvp.Value;
                    kvp.Key.SetFloat("_SnowAmount", origAmount);
                    if (kvp.Key.HasProperty("_SnowHeightAngleRange"))
                        kvp.Key.SetVector("_SnowHeightAngleRange", origRange);
                }

                BBLog.Msg($"[BaseMap] restored snow on {_snowKeywordClones.Count} keyword + " +
                    $"{_snowAmountClones.Count} _SnowAmount clone(s)");
            }
        }

        static IEnumerable<GameObject> AllSceneRoots()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
                foreach (var root in SceneManager.GetSceneAt(i).GetRootGameObjects())
                    yield return root;
        }

        static bool HasCameraComponent(GameObject go)
        {
            foreach (var comp in go.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue;
                try
                {
                    var name = comp.GetIl2CppType().Name;
                    if (name.Contains("Camera") || name.Contains("Cinemachine")
                        || name.Contains("FlyCam") || name.Contains("PlayerMovement")) return true;
                }
                catch { }
            }
            return false;
        }

        // Hide a GameObject visually without touching SetActive/OnDisable — safe for
        // props with Rigidbody/WaterObject components. Tracks hidden renderers so
        // they can be restored exactly. Also disables non-terrain, non-trigger
        // colliders on the same hierarchy (Collider.enabled = false, unlike
        // SetActive, doesn't fire OnDisable so it's safe for WaterObject/Rigidbody
        // props) so the player can't collide with now-invisible props. Trigger
        // colliders (cutscene/convo volumes etc.) are left alone — those are gated
        // instead via FlyCamController.SuppressCutsceneTriggers so OnTriggerEnter
        // keeps firing normally and replays correctly when Base Map is re-enabled.
        // Also disables Lights (Light.enabled = false) so light sources go dark
        // with the rest of the map.
        static void HidePropRenderers(GameObject go)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r != null && r.enabled)
                {
                    r.enabled = false;
                    _hiddenPropRenderers.Add(r);
                }
            }

            foreach (var col in go.GetComponentsInChildren<Collider>(true))
            {
                if (col == null) continue;
                if (col.isTrigger) continue;
                try
                {
                    if (col.GetIl2CppType().Name == "TerrainCollider") continue;
                }
                catch { }
                _disabledPropCollidersOrigState.Add(col.enabled);
                col.enabled = false;
                _disabledPropColliders.Add(col);
            }

            foreach (var light in go.GetComponentsInChildren<Light>(true))
                HideLight(light);
        }

        // Disables a Light and, alongside it, any sibling MonoBehaviour whose type
        // name looks like a light controller (e.g. Il2Cpp.PointAtPlayer's day/night
        // logic) — otherwise that controller's Update() keeps flipping
        // Light.enabled back on every frame, fighting our suppression.
        static void HideLight(Light light)
        {
            if (light == null) return;
            _hiddenLightsOrigState.Add(light.enabled);
            light.enabled = false;
            _hiddenLights.Add(light);
            DisableLightControllers(light);
        }

        // Walks up from the light's own GameObject through a few ancestor levels —
        // e.g. "Beacon/Point Light" -> "Beacon" carries BeaconLight/ConductedBeacon,
        // which drive the child Light's .enabled every frame based on beacon state.
        static void DisableLightControllers(Light light)
        {
            var t = light.transform;
            for (int depth = 0; t != null && depth < 3; t = t.parent, depth++)
            {
                foreach (var mb in t.gameObject.GetComponents<MonoBehaviour>())
                {
                    if (mb == null || !mb.enabled) continue;
                    try
                    {
                        var n = mb.GetIl2CppType().Name;
                        if (n.Contains("PointAtPlayer") || n.Contains("Beacon"))
                        {
                            mb.enabled = false;
                            _disabledLightControllers.Add(mb);
                        }
                    }
                    catch { }
                }
            }
        }

        // True only if `go` itself (not its children) carries a camera-related
        // component — i.e. this GameObject IS part of the player/fly cam rig.
        static bool HasOwnCameraComponent(GameObject go)
        {
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;
                try
                {
                    var name = comp.GetIl2CppType().Name;
                    if (name.Contains("Camera") || name.Contains("Cinemachine")
                        || name.Contains("FlyCam") || name.Contains("PlayerMovement")) return true;
                }
                catch { }
            }
            return false;
        }

        // Recursively hides a scene-root subtree, except for branches that ARE the
        // player/fly cam rig (which must stay alive). Used so we don't have to skip
        // an entire root just because *some* descendant (e.g. BigManagerPrefab >
        // Camera) carries a camera component — sibling branches (lights, cutscene
        // trigger volumes, etc.) under the same root still get hidden.
        static void HideNonCameraSubtree(GameObject go)
        {
            if (go == null) return;
            var brl = BestRegionLoader.me;
            if (brl != null && go == brl.gameObject) return;

            // Never touch our own placed props — everything spawned by the level
            // editor lives under Instance._propsContainer ("Baby Blocks").
            if (go == LevelEditorManager.PropsContainer) return;

            // This GameObject is itself part of the camera/player rig — protect the
            // whole subtree, don't hide anything underneath it.
            if (HasOwnCameraComponent(go)) return;

            if (HasCameraComponent(go))
            {
                // Camera/player lives somewhere in a descendant; recurse so sibling
                // branches still get hidden.
                for (int i = 0; i < go.transform.childCount; i++)
                    HideNonCameraSubtree(go.transform.GetChild(i).gameObject);
                return;
            }

            HidePropRenderers(go);
        }

        // Disable GameObjects whose Il2Cpp type name matches any fragment.
        // Uses SetActive on the whole GO so OnDisable fires and Camera callbacks unregister.
        static void SetRenderObjectsActive(bool enabled, List<GameObject> tracked,
                                           params string[] typeFragments)
        {
            if (!enabled)
            {
                tracked.Clear();
                foreach (var root in AllSceneRoots())
                {
                    foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (mb == null) continue;
                        try
                        {
                            var typeName = mb.GetIl2CppType().Name;
                            foreach (var frag in typeFragments)
                            {
                                if (typeName.Contains(frag))
                                {
                                    var go = mb.gameObject;
                                    if (go.activeSelf && !tracked.Contains(go) && !HasCameraComponent(go))
                                    {
                                        BBLog.Msg($"[BaseMap] SetActive(false) on '{go.name}' ({typeName})");
                                        go.SetActive(false);
                                        tracked.Add(go);
                                    }
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            else
            {
                foreach (var go in tracked)
                    if (go != null) go.SetActive(true);
                tracked.Clear();
            }
        }

        // Hides a single loaded chunk's terrain (Terrain/MicroSplat MonoBehaviours),
        // TerrainCollider, and decoration props (via HidePropRenderers). Factored out
        // of SetBaseMapEnabled's chunk loop so RescanLoadedChunksForBaseMapOff can
        // apply the same hide logic to chunks that stream in afterwards.
        //
        // disableNow:false (used by SetBaseMapEnabled) only gathers terrain/colliders
        // into _disabledTerrainComponents/_disabledTerrainColliders for the caller's
        // own bulk-disable pass; disableNow:true (used by the rescan) disables them
        // immediately since there's no later bulk pass for these chunks.
        // HidePropRenderers always disables immediately regardless of disableNow.
        static void ScanChunkTerrainAndProps(GameObject loadedChunk, bool disableNow)
        {
            foreach (var mb in loadedChunk.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null || !mb.enabled) continue;
                try
                {
                    var n = mb.GetIl2CppType().Name;
                    if (n == "Terrain" || n.Contains("MicroSplat") || n.Contains("Terrain"))
                    {
                        _disabledTerrainComponents.Add(mb);
                        if (disableNow) mb.enabled = false;
                    }
                }
                catch { }
            }

            foreach (var col in loadedChunk.GetComponentsInChildren<Collider>(true))
            {
                if (col == null || !col.enabled) continue;
                try
                {
                    if (col.GetIl2CppType().Name == "TerrainCollider")
                    {
                        _disabledTerrainColliders.Add(col);
                        if (disableNow) col.enabled = false;
                    }
                }
                catch { }
            }

            // Decoration props (trash, rocks, candles, etc.) placed directly in the
            // chunk scene rather than GPUI-instanced. Hide via Renderer.enabled only —
            // same reasoning as elsewhere in HidePropRenderers.
            HidePropRenderers(loadedChunk);
        }

        // Catches chunks that were still mid-load (loadedChunk == null in
        // SetBaseMapEnabled's chunkMap scan) and finished streaming in afterwards —
        // their terrain/MicroSplat/TerrainCollider/decoration props would otherwise
        // stay fully active and visible ("terrain loading back in" after a
        // save-load teleport with Base Map off). Idempotent via _scannedChunkIds;
        // safe to call repeatedly.
        public static void RescanLoadedChunksForBaseMapOff()
        {
            if (BaseMapEnabled) return;
            var brl = BestRegionLoader.me;
            if (brl?.chunkMap == null) return;

            // GetComponentsInChildren on BRL's children crashes natively while
            // brl.off == true — see the brl.off ordering note on SetBaseMapEnabled.
            // Callers must keep brl.off == false for the duration they expect this
            // to actually do anything (see ApplyLoadedBaseMapStateDelayed).
            if (brl.off) return;

            foreach (var cell in brl.chunkMap)
            {
                if (cell?.loadedChunk == null) continue;
                if (!_scannedChunkIds.Add(cell.loadedChunk.GetInstanceID())) continue;
                ScanChunkTerrainAndProps(cell.loadedChunk, disableNow: true);
            }
        }

        // Toggles the base map (terrain, props, vegetation, water/audio) for an empty
        // canvas to build on. The fly cam and player are left untouched throughout.
        //
        // IMPORTANT: brl.off must be flipped to true LAST (when hiding) and to false
        // FIRST (when restoring). Calling GetComponentsInChildren on BRL or its
        // children (or on scene roots that include BRL) while brl.off == true causes
        // a native Il2Cpp crash with no managed exception/log.
        // deferBrlOff: when hiding (enabled == false), skip the final brl.off = true
        // so BRL keeps streaming normally afterward. Used by
        // ApplyLoadedBaseMapStateDelayed, which follows up with
        // RescanLoadedChunksForBaseMapOff over the next ~1.5s to hide chunks that
        // finish loading after this call returns, then sets brl.off = true itself.
        public static void SetBaseMapEnabled(bool enabled, bool captureRestoreChapter = true, bool deferBrlOff = false)
        {
            BBLog.Msg($"[BaseMap] SetBaseMapEnabled({enabled}) — start");
            BaseMapEnabled = enabled;
            ApplyDayWeatherPlaylistOverride(enabled, captureRestoreChapter);
            SetEditorPropsSnowDisabled(!enabled);

            var brl = BestRegionLoader.me;
            BBLog.Msg($"[BaseMap] brl={(brl != null ? "found" : "null")}");

            // Clear here (before any HidePropRenderers calls below) so hiding
            // performed during the chunk-scan isn't wiped by the prop-container
            // section's scan further down.
            if (!enabled)
            {
                _hiddenPropRenderers.Clear();
                _hiddenLights.Clear();
                _hiddenLightsOrigState.Clear();
                _disabledLightControllers.Clear();
            }

            if (enabled)
            {
                // Restore the BRL world-position reference before touching anything else.
                if (brl != null) brl.off = false;
                BBLog.Msg("[BaseMap] brl.off=false done");

                foreach (var mb in _disabledTerrainComponents)
                    if (mb != null) mb.enabled = true;
                _disabledTerrainComponents.Clear();

                foreach (var col in _disabledTerrainColliders)
                    if (col != null) col.enabled = true;
                _disabledTerrainColliders.Clear();
                _scannedChunkIds.Clear();

                for (int i = 0; i < _disabledPropColliders.Count; i++)
                    if (_disabledPropColliders[i] != null) _disabledPropColliders[i].enabled = _disabledPropCollidersOrigState[i];
                _disabledPropColliders.Clear();
                _disabledPropCollidersOrigState.Clear();

                foreach (var r in _brlRendererCache)
                    if (r != null) r.enabled = true;
                _brlRendererCache = Array.Empty<Renderer>();

                for (int i = 0; i < _brlLightCache.Length; i++)
                    if (_brlLightCache[i] != null) _brlLightCache[i].enabled = _brlLightOrigStates[i];
                _brlLightCache = Array.Empty<Light>();
                _brlLightOrigStates = Array.Empty<bool>();

                foreach (var mb in _disabledLightControllers)
                    if (mb != null) mb.enabled = true;
                _disabledLightControllers.Clear();
                BBLog.Msg("[BaseMap] terrain/renderer/light restore done");
            }
            else if (brl != null)
            {
                // Gather everything we need via GetComponentsInChildren BEFORE
                // setting brl.off, to avoid the native crash described above.
                _brlRendererCache = brl.GetComponentsInChildren<Renderer>(true);
                BBLog.Msg($"[BaseMap] brl renderer cache gathered: {_brlRendererCache.Length}");

                // Lights parented directly under BRL (e.g. day/night sun rig), outside
                // any chunk's loadedChunk — those are handled separately via
                // HidePropRenderers(cell.loadedChunk) below. Gathered regardless of
                // current enabled state (see _hiddenLights) so day/night-driven lights
                // that are off right now but get switched on later stay suppressed.
                _brlLightCache = brl.GetComponentsInChildren<Light>(true)
                    .Where(l => l != null).ToArray();
                _brlLightOrigStates = _brlLightCache.Select(l => l.enabled).ToArray();
                foreach (var light in _brlLightCache)
                    DisableLightControllers(light);
                BBLog.Msg($"[BaseMap] brl light cache gathered: {_brlLightCache.Length}");

                _disabledTerrainComponents.Clear();
                _disabledTerrainColliders.Clear();
                _disabledPropColliders.Clear();
                _disabledPropCollidersOrigState.Clear();
                _scannedChunkIds.Clear();
                if (brl.chunkMap != null)
                {
                    BBLog.Msg($"[BaseMap] chunkMap count: {brl.chunkMap.Length}");
                    foreach (var cell in brl.chunkMap)
                    {
                        if (cell?.loadedChunk == null) continue;
                        _scannedChunkIds.Add(cell.loadedChunk.GetInstanceID());
                        // disableNow:false — terrain/colliders are bulk-disabled at the
                        // end of this method, alongside _brlRendererCache/brl.off.
                        ScanChunkTerrainAndProps(cell.loadedChunk, disableNow: false);
                    }
                }
                BBLog.Msg($"[BaseMap] terrain components gathered: {_disabledTerrainComponents.Count}, terrain colliders: {_disabledTerrainColliders.Count}, prop colliders: {_disabledPropColliders.Count}");
            }

            // Everything else in the scene not already handled above: non-GPUI prop
            // instance containers, DynamicPropSpitter-spawned props, cutscene/convo
            // trigger volumes, standalone lights, etc. Hidden via Renderer.enabled /
            // Collider.enabled / Light.enabled only — never SetActive — because
            // SetActive(false) on a WaterObject (DWP2) can crash natively mid-simulation.
            // BRL is excluded (handled separately above); branches under a root that
            // carry a camera are recursed into rather than skipped wholesale, so
            // siblings of the camera (e.g. lights/triggers under BigManagerPrefab)
            // still get hidden. Done before brl.off is flipped so this scan is crash-safe.
            if (!enabled)
            {
                int rootCount = 0;
                foreach (var root in AllSceneRoots())
                {
                    rootCount++;
                    BBLog.Msg($"[BaseMap] scanning root #{rootCount} '{root.name}'");
                    int beforeR = _hiddenPropRenderers.Count;
                    int beforeL = _hiddenLights.Count;
                    HideNonCameraSubtree(root);
                    BBLog.Msg($"[BaseMap]   hid {_hiddenPropRenderers.Count - beforeR} renderers, {_hiddenLights.Count - beforeL} lights on '{root.name}'");
                }
                BBLog.Msg($"[BaseMap] hid {_hiddenPropRenderers.Count} renderers, {_hiddenLights.Count} lights across {rootCount} scene roots");
            }
            else
            {
                foreach (var r in _hiddenPropRenderers)
                    if (r != null) r.enabled = true;
                _hiddenPropRenderers.Clear();

                for (int i = 0; i < _hiddenLights.Count; i++)
                    if (_hiddenLights[i] != null) _hiddenLights[i].enabled = _hiddenLightsOrigState[i];
                _hiddenLights.Clear();
                _hiddenLightsOrigState.Clear();
                BBLog.Msg("[BaseMap] restored prop renderers and lights");
            }

            // TVE (The Vegetation Engine) and GPUI managers/pool objects.
            SetRenderObjectsActive(enabled, _hiddenRenderObjects,
                                   "TVE", "GPUInstancer");
            BBLog.Msg("[BaseMap] SetRenderObjectsActive done");

            // GlobalObjectParent: water, fog, NPCs, etc. Hidden via renderer toggling
            // only (never SetActive) — water bodies carry DWP2 WaterObject components
            // whose OnDisable crashes natively mid-simulation, same as small props above.
            var gop = GameObject.Find("GlobalObjectParent");
            if (gop != null)
            {
                if (!enabled)
                {
                    for (int i = 0; i < gop.transform.childCount; i++)
                    {
                        var child = gop.transform.GetChild(i).gameObject;
                        if (HasCameraComponent(child)) continue;
                        int before = _hiddenPropRenderers.Count;
                        HidePropRenderers(child);
                        BBLog.Msg($"[BaseMap] GOP child '{child.name}' hid {_hiddenPropRenderers.Count - before} renderers");
                    }
                }
                // Restoration of these renderers is handled by the prop-renderer
                // restore block above (shared _hiddenPropRenderers list).
            }
            BBLog.Msg("[BaseMap] GlobalObjectParent done");

            // Player swim/buoyancy: Crest's water data provider keeps WaterHeights
            // at its last computed value once the ocean is hidden (GetWaterHeights
            // is skipped while calculateWaterHeights == false, rather than reset),
            // so the player would keep "swimming" in the now-invisible water.
            // Freeze each of the player's WaterObjects (torso + feet) at a height
            // far below any terrain so ResultForce/ResultStates read as "not in
            // water"; re-enabling calculateWaterHeights lets Crest repopulate real
            // heights again.
            var player = PlayerMovement.me;
            if (player != null)
            {
                if (!enabled)
                {
                    _suppressedWaterObjects.Clear();
                    var allWaterObjs = (player.waterObjects ?? Array.Empty<WaterObject>())
                        .Concat(player.footWaterObjs ?? Array.Empty<WaterObject>());
                    foreach (var wo in allWaterObjs)
                    {
                        if (wo == null || !wo.calculateWaterHeights) continue;
                        wo.calculateWaterHeights = false;
                        if (wo.WaterHeights.IsCreated)
                            for (int i = 0; i < wo.WaterHeights.Length; i++)
                                wo.WaterHeights[i] = -10000f;
                        _suppressedWaterObjects.Add(wo);
                    }
                    BBLog.Msg($"[BaseMap] suppressed {_suppressedWaterObjects.Count} player water objects");
                }
                else
                {
                    foreach (var wo in _suppressedWaterObjects)
                        if (wo != null) wo.calculateWaterHeights = true;
                    _suppressedWaterObjects.Clear();
                    BBLog.Msg("[BaseMap] restored player water objects");
                }
            }

            // Crest ocean visuals: toggle the whole CrestWaterRenderer object.
            // GameObject.Find can't locate it once it's inactive (e.g. on the
            // enabled==true restore path after a prior hide) — use Transform.Find on
            // the (always-active) BigManagerPrefab instead, which finds inactive
            // children fine.
            var crestWater = GameObject.Find("BigManagerPrefab")?.transform.Find("CrestWaterRenderer")?.gameObject;
            if (crestWater != null)
            {
                crestWater.SetActive(enabled);
                BBLog.Msg($"[BaseMap] CrestWaterRenderer SetActive({enabled})");
            }

            // Audio
            if (!enabled)
            {
                _hiddenAudioObjects.Clear();
                foreach (string audioName in new[] { "Arranger", "Gimbal" })
                {
                    var go = GameObject.Find(audioName);
                    if (go != null && !HasCameraComponent(go))
                    {
                        BBLog.Msg($"[BaseMap] SetActive(false) on audio '{audioName}'");
                        go.SetActive(false);
                        _hiddenAudioObjects.Add(go);
                    }
                }
            }
            else
            {
                foreach (var go in _hiddenAudioObjects)
                    if (go != null) go.SetActive(true);
                _hiddenAudioObjects.Clear();
            }
            BBLog.Msg("[BaseMap] audio done");

            if (!enabled)
            {
                // Now actually disable the gathered terrain components/colliders/renderers,
                // and finally tell BRL to stop streaming. This must be last.
                foreach (var mb in _disabledTerrainComponents)
                    if (mb != null) mb.enabled = false;
                foreach (var col in _disabledTerrainColliders)
                    if (col != null) col.enabled = false;
                foreach (var col in _disabledPropColliders)
                    if (col != null) col.enabled = false;
                foreach (var r in _brlRendererCache)
                    if (r != null) r.enabled = false;
                foreach (var light in _brlLightCache)
                    if (light != null) light.enabled = false;
                BBLog.Msg("[BaseMap] terrain/renderer/light disable done");

                // brl.off stops UpdateDirtyGpuis → GPUI instance buffers go stale → rocks fade.
                // Chunks must stay ACTIVE (not SetActive false) so OnPreCull keeps its
                // world-position terrain reference and the camera doesn't break.
                if (brl != null && !deferBrlOff) brl.off = true;
                BBLog.Msg($"[BaseMap] brl.off=true done (deferred={deferBrlOff})");

                _nextLightSweepTime = 0f;
            }

            BBLog.Msg($"[BaseMap] SetBaseMapEnabled({enabled}) — end");
        }

        // Some lights (e.g. PointAtPlayer-driven streetlamps) aren't switched on by
        // their day/night logic until well after the hide pass runs — a one-time scan
        // at toggle time can never catch these. This sweep finds any Light that is
        // *currently* enabled but not one we're tracking, and hides it on the spot
        // (excluding the props container and the player/fly-cam rig). Origin state is
        // recorded as "on" so re-enabling Base Map turns it back on correctly.
        static void SuppressNewlyEnabledLights()
        {
            foreach (var light in Resources.FindObjectsOfTypeAll<Light>())
            {
                if (light == null || !light.enabled) continue;
                if (!light.gameObject.scene.IsValid()) continue;
                if (_hiddenLights.Contains(light)) continue;
                if (_brlLightCache.Contains(light)) continue;
                if (IsLightProtected(light)) continue;

                var sb = new System.Text.StringBuilder(light.gameObject.name);
                for (var p = light.transform.parent; p != null; p = p.parent)
                    sb.Insert(0, p.name + "/");
                BBLog.Msg($"[BaseMap] Hiding newly-enabled light: {sb}");
                HideLight(light);
            }
        }

        // True if `light` (or any ancestor) is part of the editor's props container
        // or the player/fly-cam rig, and so must never be hidden.
        static bool IsLightProtected(Light light)
        {
            for (var t = light.transform; t != null; t = t.parent)
            {
                if (t.gameObject == LevelEditorManager.PropsContainer) return true;
                if (HasOwnCameraComponent(t.gameObject)) return true;
            }
            return false;
        }

        // Called every frame (from Core.OnUpdate) while Base Map is off. The one-time
        // hide in SetBaseMapEnabled snapshots renderer/collider/light state at the
        // moment of toggling, but the world keeps running: BRL renderer proxies get
        // re-enabled by streaming, and lights/cutscene triggers driven by day-night or
        // quest logic (e.g. PointAtPlayer-controlled streetlamps, BBConvoTrigger
        // colliders gated on quest state) can flip from off to on afterwards. Re-assert
        // "off"/"disabled" on everything we're tracking so those changes don't leak
        // through while the map is hidden.
        internal static void SuppressHiddenWhileBaseMapOff()
        {
            foreach (var r in _brlRendererCache)
                if (r != null && r.enabled) r.enabled = false;

            foreach (var light in _brlLightCache)
                if (light != null && light.enabled) light.enabled = false;

            foreach (var light in _hiddenLights)
                if (light != null && light.enabled) light.enabled = false;

            foreach (var col in _disabledPropColliders)
                if (col != null && col.enabled) col.enabled = false;

            foreach (var mb in _disabledLightControllers)
                if (mb != null && mb.enabled) mb.enabled = false;

            // Resources.FindObjectsOfTypeAll is relatively expensive — throttle to a
            // few times a second rather than every frame.
            if (Time.unscaledTime >= _nextLightSweepTime)
            {
                _nextLightSweepTime = Time.unscaledTime + 0.5f;
                SuppressNewlyEnabledLights();
            }
        }

        static float _nextLightSweepTime;

        // Applies/restores the Day Weather Playlist override that goes with the base
        // map toggle. Disabling the base map captures Menu.curChapter (so it can be
        // restored later) and switches to DayWeatherPlaylist; re-enabling restores
        // the captured chapter. captureRestoreChapter=false is used when applying a
        // loaded level that already specifies the chapter to restore to.
        static void ApplyDayWeatherPlaylistOverride(bool baseMapEnabled, bool captureRestoreChapter)
        {
            if (Menu.me == null || Menu.me.campfireDatas == null || Menu.me.campfireDatas.Length == 0) return;

            if (!baseMapEnabled)
            {
                if (captureRestoreChapter && !_weatherPlaylistOverridden)
                    RestoreDayWeatherPlaylist = Menu.me.curChapter;
                _weatherPlaylistOverridden = true;
                SetCurChapter(DayWeatherPlaylist);
            }
            else if (_weatherPlaylistOverridden)
            {
                _weatherPlaylistOverridden = false;
                SetCurChapter(RestoreDayWeatherPlaylist);
            }
        }

        // Only drives the live Menu.curChapter (which WeatherMan reads each frame for
        // weather/time-of-day) — deliberately does NOT touch SaveGod.theSave.lastCampfire,
        // which is the persisted "current chapter" written to the player's actual save
        // file. Writing it here would let this editor-only preview leak into the real
        // save if the player saves & quits while the override is active.
        static void SetCurChapter(int index)
        {
            index = Mathf.Clamp(index, 0, Menu.me.campfireDatas.Length - 1);
            Menu.me.curChapter = index;
        }

        // Number of WeatherMan "Day Weather Playlist" options (Menu.campfireDatas
        // entries). 0 if Menu isn't ready yet.
        public static int DayWeatherPlaylistCount =>
            Menu.me != null && Menu.me.campfireDatas != null ? Menu.me.campfireDatas.Length : 0;

        // Sets the Day Weather Playlist dropdown selection. Applies it immediately
        // if the base map is currently hidden (the override is active).
        public static void SetDayWeatherPlaylist(int index)
        {
            DayWeatherPlaylist = index;
            if (!BaseMapEnabled && Menu.me != null && Menu.me.campfireDatas != null && Menu.me.campfireDatas.Length > 0)
                SetCurChapter(index);
        }

        // Applies a loaded level's saved base-map/weather state. restoreChapter comes
        // from the save file rather than being captured from the live Menu.curChapter.
        // deferBrlOff is forwarded to SetBaseMapEnabled — see ApplyLoadedBaseMapStateDelayed.
        public static void ApplyLoadedBaseMapState(bool baseMapOff, int dayWeatherPlaylist, int restoreChapter, bool deferBrlOff = false)
        {
            DayWeatherPlaylist = dayWeatherPlaylist;
            RestoreDayWeatherPlaylist = restoreChapter;
            _weatherPlaylistOverridden = false;
            SetBaseMapEnabled(!baseMapOff, captureRestoreChapter: false, deferBrlOff: deferBrlOff);
        }

        // Defers ApplyLoadedBaseMapState until Menu.me.Teleport's coroutine (started by
        // TeleportToSpawnPoint) finishes. Menu.TeleportCo waits across many frames for
        // BestRegionLoader.fullyLoaded while the player is mid-teleport; setting
        // brl.off = true (from SetBaseMapEnabled(false)) during that window stalls BRL
        // forever, leaving Menu.me.teleporting stuck true — which breaks the spawn-point
        // teleport, leaves chunks around the destination unloaded ("missing" props), and
        // blocks input that's gated on !Menu.me.teleporting (e.g. tool-mode/edit-mode keys).
        [HideFromIl2Cpp]
        internal static IEnumerator ApplyLoadedBaseMapStateDelayed(bool baseMapOff, int dayWeatherPlaylist, int restoreChapter)
        {
            while (Menu.me != null && Menu.me.teleporting) yield return null;

            if (!baseMapOff)
            {
                ApplyLoadedBaseMapState(baseMapOff, dayWeatherPlaylist, restoreChapter);
                yield break;
            }

            // Hide what's loaded now, but leave brl.off == false (deferBrlOff) so BRL
            // keeps streaming normally for a moment — any chunks still mid-load near
            // the destination finish loading and get caught by the rescan below,
            // instead of staying fully visible/active ("terrain loading back in").
            // DeferBrlOff also stops Core.OnUpdate from forcing brl.off = true behind
            // our back the moment BaseMapEnabled flips false.
            DeferBrlOff = true;
            ApplyLoadedBaseMapState(baseMapOff, dayWeatherPlaylist, restoreChapter, deferBrlOff: true);

            for (int i = 0; i < 90 && !BaseMapEnabled; i++)
            {
                yield return null;
                RescanLoadedChunksForBaseMapOff();
            }

            DeferBrlOff = false;
            var brl = BestRegionLoader.me;
            if (brl != null && !BaseMapEnabled) brl.off = true;
        }
    }
}
