using System.Collections;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppCinemachine;
using MelonLoader;
using UnityEngine;

namespace BabyBlocks
{
    // Manages fly cam activation, player freeze/unfreeze, cursor mode, and teleport.
    static class FlyCamController
    {
        public static bool FlyCamActive;
        public static bool CursorMode;

        // Grace period after leaving fly/editor mode during which cutscene triggers stay
        // suppressed. The player can be placed inside a BBConvoStarter trigger volume by the
        // exit teleport itself, but Unity's OnTriggerEnter for the newly-overlapping collider
        // fires on a later FixedUpdate — after FlyCamActive has already gone false — so a
        // same-frame check alone misses it.
        const float CutsceneSuppressGraceTime = 0.3f;
        static float _cutsceneSuppressUntil = -1f;

        public static bool SuppressCutsceneTriggers =>
            FlyCamActive || !BaseMapController.BaseMapEnabled || Time.unscaledTime < _cutsceneSuppressUntil;

        // PlayCutscene calls swallowed while suppressed — OnTriggerEnter is one-shot, so
        // dropping the call silently means the cutscene would never get another chance to
        // play (the player is already standing inside the trigger volume, no further
        // enter/exit to re-fire it). Replayed once suppression lifts.
        static readonly List<BBConvoStarter> _pendingCutscenes = new();

        public static void RegisterSuppressedCutscene(BBConvoStarter bcs)
        {
            if (bcs == null) return;
            string saveName = bcs.GetSaveName();
            foreach (var pending in _pendingCutscenes)
                if (pending != null && pending.GetSaveName() == saveName) return;
            _pendingCutscenes.Add(bcs);
        }

        static void ReplaySuppressedCutscenes()
        {
            if (_pendingCutscenes.Count == 0 || SuppressCutsceneTriggers) return;

            var pending = new List<BBConvoStarter>(_pendingCutscenes);
            _pendingCutscenes.Clear();
            foreach (var bcs in pending)
            {
                if (bcs == null) continue;
                bcs.PlayCutscene();
            }
        }

        static float _noiseAmplitude = -1f;
        static bool  _refreezePending;
        static bool  _farTeleportActive;
        static bool  _editorScanDone;

        // True original BestRegionLoader.propLoadDists/propUnloadDists, cached on the first
        // far teleport. See FarTeleportCo / RampPropDistancesCo.
        static float[] _origPropLoadDists;
        static float[] _origPropUnloadDists;
        static int      _propRampToken;

        // True while RampPropDistancesCo is restoring propLoadDists/propUnloadDists after a
        // far teleport. Firing another far teleport during this window starts a fresh chunk
        // drain while a wave of LoadProp/UnloadProp tasks from the ramp is still in flight —
        // the drain can invalidate one of those in-flight Addressables loads and hang it
        // forever (somebodyLoading stuck true, freezing the whole game). Block new far
        // teleports until the ramp settles.
        static bool _propRampActive;

        // True for the whole duration of FarTeleportCo. While true, Core.OnUpdate's
        // Base-Map-off block must not touch brl.off or player/renderer state —
        // FarTeleportCo needs uncontested control of br.off (it flips it false to
        // stream the destination chunk) and of player.gameObject's active state
        // during the ragdoll handoff. See ApplyLoadedBaseMapStateDelayed/Core.OnUpdate.
        internal static bool FarTeleportActive => _farTeleportActive;

        // Player freeze state
        static bool _freezeActive;
        static PlayerMovement _frozenPlayer;
        static CharacterController _frozenController;
        static Rigidbody _frozenRigidbody;
        static Animator _frozenAnimator;

        static bool _movementWasEnabled;
        static bool _controllerWasEnabled;
        static bool _rigidbodyWasKinematic;
        static bool _rigidbodyWasUseGravity;
        static RigidbodyConstraints _rigidbodyConstraints;
        static Vector3 _rigidbodyVelocity;
        static Vector3 _rigidbodyAngularVelocity;
        static float _animatorSpeed = 1f;

        public static void OnUpdate()
        {
            ReplaySuppressedCutscenes();

            // Once the post-save-load scene load burst has settled (see
            // Core.PendingMicroSplatRefreshTime for why this is deferred rather than done in
            // OnSceneWasLoaded directly): refresh the cached MicroSplat layer materials, and
            // drop any LevelEditorObjects that a native save load destroyed (physics-managed
            // props get moved into the scene that gets wiped/rebuilt on load).
            if (Core.PendingMicroSplatRefreshTime >= 0f
                && Time.realtimeSinceStartup - Core.PendingMicroSplatRefreshTime >= Core.MicroSplatRefreshSettleDelay)
            {
                Core.PendingMicroSplatRefreshTime = -1f;
                MaterialCatalog.RefreshMicroSplatLayerMaterials();

                // The general material cache (_materialByName) may have been rebuilt mid-stream by
                // an earlier OnSceneWasLoaded -> InvalidateMaterialCache, before the destination
                // area's materials had actually settled into memory. Force one more rebuild now
                // that streaming has settled, then re-point every placed prop's material overrides
                // at the freshly resolved instances — the chunk drain during a far-teleport can
                // destroy the Material instances those overrides were previously pointing at,
                // which otherwise leaves the prop rendering pink/missing.
                MaterialVariantTracker.InvalidateMaterialCache();
                MaterialCatalog.EnsureMaterialListLoaded();
                MaterialCatalog.ReapplyAllMaterialOverrides();

                // ReapplyAllMaterialOverrides just repointed override renderers at
                // freshly-resolved shared materials, which were never snow-suppressed —
                // re-derive the suppression for whatever materials are on the renderers now.
                if (!BaseMapController.BaseMapEnabled)
                    BaseMapController.SetEditorPropsSnowDisabled(true);

                LevelEditorManager.Instance?.PruneDestroyedObjects();
                LevelEditor.PruneSelection();
                if (GizmoRenderer.IsReady) GizmoRenderer.RefreshAssets();

                // All props from the loaded save are back in the scene now —
                // bake ghost-cube collider carves so holes apply to whatever
                // ended up inside them.
                GhostCollisionCutter.BakeAllColliderCarves();
            }

            // Toggling fly/editor mode while a cutscene is playing leaves PlayerMovement and
            // the fly cam rig in an inconsistent state (player.flyCam ends up null, OnStandUp
            // NREs, etc.) — block entry/exit entirely until the cutscene finishes.
            if (Input.GetKeyDown(KeyCode.R) && PlayerMovement.me != null
                && !Menu.me.teleporting && !_farTeleportActive && !Core.IsKeyboardCaptured
                && !PropPalette.IsDragging && !LevelEditor.IsSurfaceSnapDragging)
            {
                if (!PlayerMovement.me.inCutscene)
                    ToggleFlyEditorMode();
            }

            if (Input.GetKeyDown(KeyCode.BackQuote) && PlayerMovement.me != null
                && !Menu.me.teleporting && !_farTeleportActive && !Core.IsKeyboardCaptured)
            {
                if (!PlayerMovement.me.inCutscene)
                    ToggleTeleportMode();
            }

            // G: unified teleport. In non-cursor mode aims along the crosshair (infinite
            // distance); in cursor mode drops to fly-cam position. Both paths go through
            // FarTeleportCo which drains loaded chunks in parallel then shrinks the load
            // radius to just the target chunk, so the player lands the moment that one
            // chunk's collision scene activates rather than waiting for the full region.
            if (FlyCamActive && !CursorMode && Input.GetKeyDown(KeyCode.G)
                && !Menu.me.teleporting && !_refreezePending && !_farTeleportActive
                && !Core.IsKeyboardCaptured)
                HandleFarTeleport();

            // After a short (nearby) teleport finishes, re-freeze the player.
            if (FlyCamActive && _refreezePending && !Menu.me.teleporting)
            {
                var player = PlayerMovement.me;
                if (player != null)
                    FreezePlayer(player, true);
                _refreezePending = false;
            }

            // Keep terrain streaming around the fly cam rather than the player.
            if (FlyCamActive && !Menu.me.teleporting)
            {
                var player = PlayerMovement.me;
                if (player != null)
                    BestRegionLoader.me.loadingTransform = player.flyCam.transform;
            }

            // Right-click in cursor mode: lock cursor while held so mouse-look deltas are clean.
            if (FlyCamActive && CursorMode)
            {
                if (Input.GetMouseButtonDown(1))
                {
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible   = false;
                }
                if (Input.GetMouseButtonUp(1))
                {
                    Cursor.lockState = CursorLockMode.Confined;
                    Cursor.visible   = true;
                }
            }

            if (FlyCamActive && CursorMode && !LevelEditor.IsTypingInUI && !Core.IsKeyboardCaptured
                && Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.S))
                SaveLoadWindow.TriggerSave();

            if (FlyCamActive && CursorMode && !LevelEditor.IsTypingInUI && !Core.IsKeyboardCaptured
                && Input.GetKeyDown(KeyCode.M))
                MaterialInspectorPanel.Toggle();

            if (FlyCamActive && CursorMode)
                LevelEditor.Update();
        }

        public static void OnGUI()
        {
            if (FlyCamActive && CursorMode)
                LevelEditor.OnGUI();

            // TEMP DIAGNOSTIC: while a far teleport is in flight, dump the state that
            // FlyCamUpdatePatch depends on for mouse-look, to find out why the camera
            // sometimes won't turn during far teleports.
            if (_farTeleportActive)
            {
                if (!_diagWasActive)
                {
                    _diagWasActive    = true;
                    _diagLastTime     = Time.realtimeSinceStartup;
                    _diagLastUpdates  = Core.DiagFlyCamUpdateCount;
                }

                float now = Time.realtimeSinceStartup;
                float dt  = now - _diagLastTime;
                float ups = dt > 0f ? (Core.DiagFlyCamUpdateCount - _diagLastUpdates) / dt : 0f;
                _diagLastTime    = now;
                _diagLastUpdates = Core.DiagFlyCamUpdateCount;

                var flyCam = PlayerMovement.me?.flyCam;
                string text =
                    $"FarTeleportActive\n" +
                    $"CursorMode={CursorMode}\n" +
                    $"Cursor.lockState={Cursor.lockState} visible={Cursor.visible}\n" +
                    $"Mouse X/Y={Input.GetAxis("Mouse X"):F4}/{Input.GetAxis("Mouse Y"):F4}\n" +
                    $"FlyCam.Update count={Core.DiagFlyCamUpdateCount} (~{ups:F1}/sec) mouseX/Y seen={Core.DiagMouseX:F4}/{Core.DiagMouseY:F4}\n" +
                    $"FlyCam.locked={FlyCam.locked}\n" +
                    $"flyCam.enabled={flyCam?.enabled} activeInHierarchy={flyCam?.gameObject.activeInHierarchy}\n" +
                    $"Time.timeScale={Time.timeScale} Menu.paused={Menu.me?.paused} Menu.teleporting={Menu.me?.teleporting}\n" +
                    $"BRL.off={BestRegionLoader.me?.off} somebodyLoading={BestRegionLoader.somebodyLoading}\n" +
                    $"Phase={_teleportPhase} drainIters={_diagDrainIters} convergeIters={_diagConvergeIters}\n" +
                    $"forceClears={Core.DiagSomebodyLoadingForceClears} landingRetries={_diagLandingRetries}";
                GUI.Label(new Rect(10, 10, 600, 220), text);
            }
            else
            {
                _diagWasActive = false;

                // TEMP DIAGNOSTIC: always-visible small counter so a stuck somebodyLoading
                // outside of a far teleport (e.g. during normal fly-cam movement) is visible too.
                if (FlyCamActive && Core.DiagSomebodyLoadingForceClears > 0)
                    GUI.Label(new Rect(10, 10, 400, 20),
                        $"somebodyLoading force-clears: {Core.DiagSomebodyLoadingForceClears}");
            }
        }

        static bool  _diagWasActive;
        static float _diagLastTime;
        static int   _diagLastUpdates;

        // TEMP DIAGNOSTIC: which step of FarTeleportCo is currently executing, plus
        // iteration counts for its wait loops, so a frozen overlay shows exactly where
        // execution is stuck rather than just "FarTeleportActive".
        static string _teleportPhase = "(idle)";
        static int    _diagDrainIters;
        static int    _diagConvergeIters;
        static int    _diagLandingRetries;

        // BackQuote key: direct game ↔ editor toggle (skips bare fly mode).
        static void ToggleTeleportMode()
        {
            if (!FlyCamActive)
            {
                ToggleFlyMode();
                ToggleCursorMode();
            }
            else
            {
                ToggleFlyMode();
            }
        }

        // R key: game → fly, then cycles fly ↔ editor.
        static void ToggleFlyEditorMode()
        {
            if (!FlyCamActive)
            {
                ToggleFlyMode();
                return;
            }

            ToggleCursorMode();
        }

        static void ToggleFlyMode()
        {
            var player = PlayerMovement.me;

            bool activating = player.flyCam.transform.parent != null;

            player.ToggleFlyCam();

            if (activating)
            {
                FlyCamActive     = true;
                CursorMode       = false;
                _refreezePending = false;
                Cursor.lockState = CursorLockMode.Locked;
                FreezePlayer(player, true);
                LevelEditor.EnsureManager();

                if (!_editorScanDone) MaterialCatalog.MarkMaterialSourcesPending();
                MelonCoroutines.Start(ActivateEditorScanCo());

                var noise = player.flyCam.GetComponentInChildren<CinemachineBasicMultiChannelPerlin>();
                if (noise != null)
                {
                    if (_noiseAmplitude < 0f) _noiseAmplitude = noise.m_AmplitudeGain;
                    noise.m_AmplitudeGain = 0f;
                }
            }
            else
            {
                if (LevelEditorManager.Instance != null) PhysicsObjectManager.SetEditorModeActive(false);
                CursorMode       = false;
                _refreezePending = false;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible   = false;
                FreezePlayer(player, false);

                player.pm.SwitchToActiveMode();
                player.pm.SwitchModes();
                player.OnStandUp();
                LevelEditor.HideGizmo();
                LevelEditor.ClearRemoteSelectionBroadcast();

                // Cleared only after the unfreeze/mode-switch sequence above so
                // BBConvoStarterTriggerPatch keeps suppressing cutscene triggers while the
                // player's physics/collider are being reactivated at the exit position.
                FlyCamActive = false;
                _cutsceneSuppressUntil = Time.unscaledTime + CutsceneSuppressGraceTime;

                if (_noiseAmplitude >= 0f)
                {
                    var noise = player.flyCam.GetComponentInChildren<CinemachineBasicMultiChannelPerlin>();
                    if (noise != null) noise.m_AmplitudeGain = _noiseAmplitude;
                }

            }
        }

        // Spreads the editor-activation scan across two frames so neither half lands in the same
        // frame as the other (or as ToggleFlyMode's own work) — a single ~400ms synchronous frame
        // was enough to trip the Linux compositor's freeze-detection and black the window out.
        static IEnumerator ActivateEditorScanCo()
        {
            yield return null;

            GpuiPropScanner.ScanGpuiProps();

            yield return null;

            if (!_editorScanDone)
            {
                MaterialCatalog.InvalidateMaterialSources();
                _editorScanDone = true;
            }
        }

        static void ToggleCursorMode()
        {
            CursorMode       = !CursorMode;
            Cursor.lockState = CursorMode ? CursorLockMode.Confined : CursorLockMode.Locked;
            Cursor.visible   = CursorMode;
            if (LevelEditorManager.Instance != null)
                PhysicsObjectManager.SetEditorModeActive(CursorMode && FlyCamActive);
            if (!CursorMode)
            {
                LevelEditor.HideGizmo();
                LevelEditor.ClearRemoteSelectionBroadcast();
            }
        }

        static void FreezePlayer(PlayerMovement player, bool frozen)
        {
            if (player == null) return;

            if (frozen)
            {
                if (_freezeActive && _frozenPlayer == player) return;

                _frozenPlayer  = player;
                _freezeActive  = true;

                _movementWasEnabled = player.enabled;
                player.enabled = false;

                _frozenController = player.GetComponent<CharacterController>();
                if (_frozenController != null)
                {
                    _controllerWasEnabled    = _frozenController.enabled;
                    _frozenController.enabled = false;
                }

                _frozenRigidbody = player.GetComponent<Rigidbody>();
                if (_frozenRigidbody != null)
                {
                    _rigidbodyWasKinematic   = _frozenRigidbody.isKinematic;
                    _rigidbodyWasUseGravity  = _frozenRigidbody.useGravity;
                    _rigidbodyConstraints    = _frozenRigidbody.constraints;
                    _rigidbodyVelocity       = _frozenRigidbody.velocity;
                    _rigidbodyAngularVelocity = _frozenRigidbody.angularVelocity;

                    _frozenRigidbody.velocity        = Vector3.zero;
                    _frozenRigidbody.angularVelocity  = Vector3.zero;
                    _frozenRigidbody.isKinematic      = true;
                    _frozenRigidbody.useGravity       = false;
                    _frozenRigidbody.constraints      = RigidbodyConstraints.FreezeAll;
                }

                _frozenAnimator = player.anim;
                if (_frozenAnimator != null)
                {
                    _animatorSpeed        = _frozenAnimator.speed;
                    _frozenAnimator.speed = 0f;
                }

                Physics.SyncTransforms();
                return;
            }

            if (!_freezeActive || _frozenPlayer != player) return;

            if (_frozenAnimator != null)
                _frozenAnimator.speed = _animatorSpeed;

            if (_frozenRigidbody != null)
            {
                _frozenRigidbody.constraints     = _rigidbodyConstraints;
                _frozenRigidbody.isKinematic     = _rigidbodyWasKinematic;
                _frozenRigidbody.useGravity      = _rigidbodyWasUseGravity;
                _frozenRigidbody.velocity        = _rigidbodyVelocity;
                _frozenRigidbody.angularVelocity = _rigidbodyAngularVelocity;
            }

            if (_frozenController != null)
                _frozenController.enabled = _controllerWasEnabled;

            player.enabled = _movementWasEnabled;

            _freezeActive     = false;
            _frozenPlayer     = null;
            _frozenController = null;
            _frozenRigidbody  = null;
            _frozenAnimator   = null;
        }

        // LMB / G key: unified teleport for any distance.
        // Non-cursor mode: aims along the crosshair with infinite range.
        // Cursor mode: uses fly-cam position.
        // Both paths go through FarTeleportCo.
        public static void HandleFarTeleport()
        {
            if (Menu.me.teleporting || _refreezePending || _farTeleportActive || _propRampActive) return;

            var player = PlayerMovement.me;
            if (player == null) return;

            float facingY = player.flyCam.transform.eulerAngles.y;
            Vector3 target;

            if (!CursorMode)
            {
                // Infinite-range crosshair raycast — hits any loaded terrain/prop.
                var ray = Camera.main.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f));
                if (Physics.Raycast(ray, out var hit, Mathf.Infinity, LayerCache.PropTerrainMask))
                    target = hit.point;
                else
                    // Nothing in sights — target fly-cam XZ at a low Y so TeleportCo's
                    // upward ground search starts well below terrain.
                    target = new Vector3(player.flyCam.transform.position.x, -100f,
                                         player.flyCam.transform.position.z);
            }
            else
            {
                target = new Vector3(player.flyCam.transform.position.x, -100f,
                                     player.flyCam.transform.position.z);
            }

            _farTeleportActive = true;
            SkipGame.me.fullscreenColor = Color.black;
            SkipGame.me.blackScreenAlpha = 1f;

            MelonCoroutines.Start(FarTeleportCo(player, target, facingY));
        }

        // The fast teleport coroutine. Mirrors what RestartScene does but without any
        // menu state changes, and with two key optimisations over plain Menu.me.Teleport():
        //
        //   1. Parallel chunk drain: all loaded chunk scenes are unloaded simultaneously
        //      (rather than one-at-a-time as in UnloadAll), clearing BestRegionLoader's
        //      serial somebodyLoading queue as fast as possible.
        //
        //   2. Minimal load radius: chunkLoadDist/chunkUnloadDist are set to 1f before
        //      handing off to TeleportCo. BestRegionLoader.fullyLoaded therefore becomes
        //      true the moment the single chunk under the target finishes loading — its
        //      colliders are active at that point (OnChunkLoaded activates them immediately)
        //      so the player can be placed right away. Surrounding chunks stream in normally
        //      after the player lands, hidden by the black screen.
        //
        //   3. SaveGod continuePt is stamped with the target so if the save loop fires
        //      during the sequence it records a consistent position.
        static IEnumerator FarTeleportCo(PlayerMovement player, Vector3 target, float facingY)
        {
            _diagDrainIters = 0;
            _diagConvergeIters = 0;
            _diagLandingRetries = 0;

            // Pause autosave; stamp target so any stray save writes a consistent position.
            SaveGod.me.stopSaving = true;
            SaveGod.theSave.continuePt = target;

            var br = BestRegionLoader.me;

            // Wait for any in-flight async op to settle before we touch anything.
            _teleportPhase = "wait-somebodyLoading-pre";
            while (BestRegionLoader.somebodyLoading) yield return null;

            // --- Ramp prop load/unload distances down to zero ---
            // The old (instant) version zeroed propLoadDists/propUnloadDists in a single frame
            // right before flipping br.off back on near the new position. UpdateLoaderCells
            // then sees every prop-loader cell around the OLD position as instantly
            // out-of-range (their dist thresholds are now 0) and fires UnloadProp/UnloadPropLOD/
            // UnloadGC for every loaded prop type in a single UpdatePropLoading pass — a
            // dozen-plus async tasks all spinning on the single shared `somebodyLoading` flag at
            // once. Whichever loses the race sits stuck until Core.WatchSomebodyLoading's 1.5s
            // timeout force-clears it, then the next one immediately re-stalls — the repeating
            // "stuck true for 1.5s, force-clearing" storm seen on far teleports (and on very far
            // ones, some never resolve at all, leaving Menu.me.teleporting stuck true forever).
            //
            // Instead, ramp the distances down to zero over PropDistRampDuration while br.off is
            // still false and loadingTransform hasn't moved yet — UpdateLoaderCells then marks
            // cells out-of-range gradually, the same one-or-two-per-frame way normal player
            // movement does, so the unload tasks it queues never pile up on somebodyLoading all
            // at once. Symmetric with RampPropDistancesCo's ramp back up at the end.
            var propLoad   = br.propLoadDists;
            var propUnload = br.propUnloadDists;

            // Cache the game's real prop load/unload distances once. We can't just snapshot
            // "current" values here — if a previous teleport's ramp-up (below) is still in
            // progress, "current" would be some partially-ramped value, not the true original.
            if (_origPropLoadDists == null)
            {
                _origPropLoadDists   = new float[propLoad.Length];
                _origPropUnloadDists = new float[propUnload.Length];
                for (int i = 0; i < propLoad.Length; i++)   _origPropLoadDists[i]   = propLoad[i];
                for (int i = 0; i < propUnload.Length; i++) _origPropUnloadDists[i] = propUnload[i];
            }
            _propRampToken++;

            _teleportPhase = "ramp-props-down";
            float rampT = 0f;
            while (rampT < PropDistRampDuration)
            {
                yield return null;
                _diagDrainIters++;
                rampT += Time.unscaledDeltaTime;
                float frac = 1f - Mathf.Clamp01(rampT / PropDistRampDuration);
                for (int i = 0; i < propLoad.Length; i++)   propLoad[i]   = _origPropLoadDists[i]   * frac;
                for (int i = 0; i < propUnload.Length; i++) propUnload[i] = _origPropUnloadDists[i] * frac;
            }
            for (int i = 0; i < propLoad.Length; i++)   propLoad[i]   = 0f;
            for (int i = 0; i < propUnload.Length; i++) propUnload[i] = 0f;

            // Let any unload tasks the ramp-down just queued settle before the chunk drain
            // starts its own (much larger) wave of somebodyLoading users.
            _teleportPhase = "wait-somebodyLoading-post-ramp";
            while (BestRegionLoader.somebodyLoading) yield return null;

            // Freeze the loader so nothing new starts while we drain chunks.
            br.off = true;

            // --- Parallel chunk drain ---
            // Kick off every loaded chunk's unload simultaneously. The game normally
            // serialises these one at a time (UnloadAll); firing them all at once lets
            // Addressables pipeline the scene unloads concurrently.
            _teleportPhase = "drain-kickoff";
            var chunkMap = br.chunkMap;
            for (int i = 0; i < chunkMap.Length; i++)
            {
                if (chunkMap[i].loadState == LoadState.loaded)
                    chunkMap[i].Unload();
            }

            // Any chunk still mid-load: wait for it to finish, then unload it too.
            _teleportPhase = "drain-wait-inflight";
            bool anyInFlight;
            do
            {
                yield return null;
                _diagDrainIters++;
                anyInFlight = false;
                for (int i = 0; i < chunkMap.Length; i++)
                {
                    switch (chunkMap[i].loadState)
                    {
                        case LoadState.loading:
                            anyInFlight = true;
                            break;
                        case LoadState.loaded:
                            chunkMap[i].Unload();
                            anyInFlight = true;
                            break;
                    }
                }
            } while (anyInFlight);

            // Wait for all unload callbacks to complete.
            _teleportPhase = "drain-wait-allclear";
            bool allClear;
            do
            {
                yield return null;
                _diagDrainIters++;
                allClear = true;
                for (int i = 0; i < chunkMap.Length; i++)
                {
                    if (chunkMap[i].loadState != LoadState.unloaded) { allClear = false; break; }
                }
            } while (!allClear);

            // Parallel callbacks may leave somebodyLoading stuck; reset it explicitly.
            BestRegionLoader.somebodyLoading = false;

            // --- Absolute minimum load: 1 chunk terrain scene, zero props ---
            // UpdateLoaderCells() also contributes to fullyLoaded via its own shouldLoad
            // check, so any props within propLoadDists would gate the wait just like chunks.
            // propLoadDists/propUnloadDists are already 0 from the ramp-down above, so only the
            // single chunk terrain scene is required. chunkLoadDist = 1f means only the chunk
            // whose bounding box contains the target point (distance == 0 < 1) gets
            // shouldLoad=true.
            float origLoad   = br.chunkLoadDist;
            float origUnload = br.chunkUnloadDist;
            br.chunkLoadDist   = 1f;
            br.chunkUnloadDist = 1f;

            br.off = false;

            // Set up player so TeleportCo's ragdoll sequence has the right puppet state.
            FreezePlayer(player, false);
            player.anim.transform.rotation = Quaternion.Euler(0f, facingY, 0f);
            player.pm.SwitchToActiveMode();
            player.pm.SwitchModes();

            // Deactivate immediately after mode switches so the player can't fall through
            // the now-empty world or trigger scream/water-splash sounds while we wait for
            // the single chunk to load. TeleportCo's own SetActive(false) becomes a no-op;
            // its SetActive(true) re-enables the player correctly at the new position.
            player.gameObject.SetActive(false);

            // --- Pre-converge the chunk-position grid toward target ---
            // BestRegionLoader.LoopChunkMapPositions (called from br.Update()) recenters the
            // chunkPositions[] grid toward loadingTransform.position by shifting whole
            // columns/rows by one grid-width per call, only once they're more than ~60% of
            // the world size away. During normal flight this keeps pace with the fly cam, but
            // a fast G-teleport can outrun it — chunkPositions[] is still centered on the OLD
            // area when Menu.me.Teleport() runs below. TeleportCo then checks
            // BestRegionLoader.fullyLoaded against a grid that doesn't cover `target` at all:
            // every cell already reads "shouldn't load"/unloaded, so fullyLoaded is vacuously
            // true on the very first check, TeleportCo's raycast finds no terrain near target,
            // and it falls back to a void position — the camera then looks "stuck" while
            // chunkPositions[] slowly catches up over many subsequent frames. Drive the
            // recentering to convergence here first, synchronously, before handing off.
            //
            // Each LoopChunkMapPositions pass shifts whichever columns/rows are individually
            // out of range — not all in lockstep — so checking a single cell (e.g. (0,0)) can
            // report "converged" while other cells are still mid-shift, leaving the grid in an
            // inconsistent intermediate state. Compare the WHOLE array each pass instead.
            _teleportPhase = "converge-chunk-positions";
            ConvergeChunkPositions(br, target, player.flyCam.transform);

            // Menu.TeleportCo unconditionally calls OceanRenderer.Instance.RebuildOcean()
            // (no null check) as part of its foot-placement sequence. When Base Map is off,
            // SetBaseMapEnabled(false) has SetActive(false)'d BigManagerPrefab/CrestWaterRenderer,
            // which nulls Crest's OceanRenderer.Instance singleton via OnDisable — the
            // dereference throws an NRE that silently kills this IL2CPP coroutine partway
            // through (player.gameObject.SetActive(true) never runs), leaving
            // Menu.me.teleporting stuck true forever and the player permanently inactive
            // (invisible, shadow only). Re-enable CrestWaterRenderer for the duration of the
            // handoff below — the screen is fully black (SkipGame blackScreenAlpha=1) so the
            // ocean briefly reappearing is not visible — then hide it again afterward.
            _teleportPhase = "crest-water-handoff";
            GameObject crestWater = null;
            if (!BaseMapController.BaseMapEnabled)
            {
                // GameObject.Find can't locate an inactive GameObject (even via a path
                // through an active parent), so it always returned null here once
                // SetBaseMapEnabled(false) had deactivated CrestWaterRenderer.
                // Transform.Find on the (active) BigManagerPrefab finds inactive
                // children fine.
                var bigManager = GameObject.Find("BigManagerPrefab");
                var crestTransform = bigManager?.transform.Find("CrestWaterRenderer");
                crestWater = crestTransform?.gameObject;

                if (crestWater != null && !crestWater.activeSelf)
                {
                    crestWater.SetActive(true);
                }
                else
                {
                    crestWater = null;
                }
            }

            // Hand off to the game's teleport machinery. It forces one BestRegionLoader
            // update, waits for fullyLoaded (1 chunk = very fast), then runs the full
            // ragdoll foot-placement sequence and clears Menu.me.teleporting.
            _teleportPhase = "menu-teleport-call";
            Menu.me.Teleport(target);

            _teleportPhase = "wait-menu-teleporting";
            while (Menu.me.teleporting) yield return null;

            // Landing sanity check. If the destination chunk's real terrain/prop colliders
            // hadn't actually finished loading yet (fullyLoaded was true too early - e.g.
            // chunkPositions hadn't fully converged, or a stuck somebodyLoading was
            // force-cleared by Core.WatchSomebodyLoading before its load truly settled),
            // TeleportCo's upward ground-search raycast hits whatever low-LOD/proxy collider
            // it can find instead, landing the player far from (often high above) `target`.
            // Give the real terrain more time to load and retry placement rather than
            // stranding the player on a proxy with no real colliders around it.
            _teleportPhase = "landing-check";
            const float BadLandingDistanceSq = 15f * 15f;
            while (_diagLandingRetries < 3 &&
                   (player.torsoRbs[0].transform.position - target).sqrMagnitude > BadLandingDistanceSq)
            {
                _diagLandingRetries++;

                _teleportPhase = $"landing-retry{_diagLandingRetries}-wait-fullyLoaded";
                int waitFrames = 0;
                while (!BestRegionLoader.fullyLoaded && waitFrames < 300)
                {
                    yield return null;
                    waitFrames++;
                }

                _teleportPhase = $"landing-retry{_diagLandingRetries}-converge";
                ConvergeChunkPositions(br, target, player.flyCam.transform);

                _teleportPhase = $"landing-retry{_diagLandingRetries}-teleport";
                Menu.me.Teleport(target);
                while (Menu.me.teleporting) yield return null;
            }

            _teleportPhase = "post-teleport-material-refresh";
            if (crestWater != null && !BaseMapController.BaseMapEnabled)
                crestWater.SetActive(false);

            // Re-point the cached "[MicroSplat] Layer N" prop materials at the freshly loaded
            // terrain's texture arrays. The parallel chunk drain above can drop the shared
            // terrain texture arrays' refcount to zero, causing Addressables to release and
            // later recreate them as new instances - leaving the cached materials pointing at
            // destroyed textures (visually "weird" with no logged errors).
            MaterialCatalog.RefreshMicroSplatLayerMaterials();

            // Do the same for the general material cache and every placed prop's overrides right
            // away — the single destination chunk is already fully loaded at this point (we waited
            // for fullyLoaded above), so most overrides can be repointed immediately rather than
            // waiting out the full settle delay below and showing pink for a second. The
            // settle-delay pass (OnUpdate) still runs afterwards as a safety net for any material
            // that hadn't settled yet.
            MaterialVariantTracker.InvalidateMaterialCache();
            MaterialCatalog.EnsureMaterialListLoaded();
            MaterialCatalog.ReapplyAllMaterialOverrides();

            // ReapplyAllMaterialOverrides just repointed override renderers at
            // freshly-resolved shared materials, which were never snow-suppressed —
            // re-derive the suppression for whatever materials are on the renderers now.
            if (!BaseMapController.BaseMapEnabled)
                BaseMapController.SetEditorPropsSnowDisabled(true);

            // Snap the fly cam to the player's new body position so that BestRegionLoader
            // streams terrain around the correct area. Without this, the fly cam stays at
            // the old look-from position and the chunk grid gets looped around there instead
            // of the teleport destination — which breaks terrain loading at the loop seam
            // when the player walks around after exiting fly-cam mode.
            player.flyCam.transform.position = player.torsoRbs[0].transform.position;

            // Restore chunk distances immediately — surrounding terrain streams in normally.
            br.chunkLoadDist   = origLoad;
            br.chunkUnloadDist = origUnload;

            // Prop distances are ramped back up gradually rather than restored instantly —
            // see RampPropDistancesCo for why.
            _propRampActive = true;
            MelonCoroutines.Start(RampPropDistancesCo(propLoad, propUnload, _propRampToken));

            // Back into fly-cam state.
            FreezePlayer(player, true);
            br.loadingTransform = player.flyCam.transform;

            SaveGod.me.stopSaving = false;

            SkipGame.me.blackScreenAlpha = 0f;

            _farTeleportActive = false;
            _teleportPhase = "(idle)";
        }

        // Drives BestRegionLoader.chunkPositions[] toward convergence on `target` by calling
        // br.Update() synchronously up to 64 times (each LoopChunkMapPositions pass shifts
        // whichever columns/rows are individually out of range, not all in lockstep, so a
        // single cell can read "converged" while others are still mid-shift — compare the
        // whole array each pass). See FarTeleportCo's original comment for why this matters:
        // without it, BestRegionLoader.fullyLoaded can read true before the grid actually
        // covers `target`, and TeleportCo's ground raycast then finds nothing near `target`.
        static void ConvergeChunkPositions(BestRegionLoader br, Vector3 target, Transform flyCamTransform)
        {
            flyCamTransform.position = target;
            br.loadingTransform = flyCamTransform;
            var chunkPositions = br.chunkPositions;
            var prevPositions = new Vector3[chunkPositions.Length];
            for (int i = 0; i < 64; i++)
            {
                for (int j = 0; j < chunkPositions.Length; j++) prevPositions[j] = chunkPositions[j];
                br.Update();
                _diagConvergeIters++;
                bool changed = false;
                for (int j = 0; j < chunkPositions.Length; j++)
                {
                    if (chunkPositions[j] != prevPositions[j]) { changed = true; break; }
                }
                if (!changed) break;
            }
        }

        // Restoring propLoadDists/propUnloadDists to their full values in a single frame (the
        // old behavior) lets BestRegionLoader.UpdateLoaderCells mark every prop cell within
        // that radius as shouldLoad on the very next Update, all at once. For GPUI'd props
        // (e.g. base-game rocks) that dumps hundreds of instances into gpuiLoadedCellIndices
        // in one go, pushing gpuiPositions[].Count past instanceCountGPUICutOff (100). Past
        // that cutoff the game deactivates ALL of that prop's collider pool objects and falls
        // back to GPU-instanced visuals only — full LOD, but zero collisions. Ramping the
        // distances up over a couple seconds instead lets cells get picked up a few at a time,
        // same as normal walking, so the loaded instance count never spikes past the cutoff.
        const float PropDistRampDuration = 2f;
        static IEnumerator RampPropDistancesCo(float[] propLoad, float[] propUnload, int token)
        {
            float t = 0f;
            while (t < PropDistRampDuration)
            {
                // A newer far teleport has zeroed these arrays out again and started its own
                // ramp — let that one drive from here.
                if (token != _propRampToken) yield break;

                yield return null;
                t += Time.unscaledDeltaTime;
                float frac = Mathf.Clamp01(t / PropDistRampDuration);
                for (int i = 0; i < propLoad.Length; i++)   propLoad[i]   = _origPropLoadDists[i]   * frac;
                for (int i = 0; i < propUnload.Length; i++) propUnload[i] = _origPropUnloadDists[i] * frac;
            }

            if (token == _propRampToken) _propRampActive = false;
        }
    }
}
