using System.Collections;
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

        static float _noiseAmplitude = -1f;
        static bool  _refreezePending;
        static bool  _farTeleportActive;

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
            if (Input.GetKeyDown(KeyCode.R) && PlayerMovement.me != null
                && !Menu.me.teleporting && !Core.IsKeyboardCaptured
                && !PropPalette.IsDragging)
                ToggleFlyEditorMode();

            if (Input.GetKeyDown(KeyCode.BackQuote) && PlayerMovement.me != null
                && !Menu.me.teleporting && !Core.IsKeyboardCaptured)
                ToggleTeleportMode();

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
        }

        // R key: toggle fly cam without cursor/editor.
        static void ToggleTeleportMode()
        {
            ToggleFlyMode();
        }

        // BackQuote key: toggle fly cam with cursor/editor.
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
                PropLibrary.ScanGpuiProps();
                PropMetadataPanel.InvalidateMaterialSources();

                var noise = player.flyCam.GetComponentInChildren<CinemachineBasicMultiChannelPerlin>();
                if (noise != null)
                {
                    if (_noiseAmplitude < 0f) _noiseAmplitude = noise.m_AmplitudeGain;
                    noise.m_AmplitudeGain = 0f;
                }
            }
            else
            {
                LevelEditorManager.Instance?.SetEditorModeActive(false);
                FlyCamActive     = false;
                CursorMode       = false;
                _refreezePending = false;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible   = false;
                FreezePlayer(player, false);

                player.pm.SwitchToActiveMode();
                player.pm.SwitchModes();
                player.OnStandUp();
                LevelEditor.HideGizmo();

                if (_noiseAmplitude >= 0f)
                {
                    var noise = player.flyCam.GetComponentInChildren<CinemachineBasicMultiChannelPerlin>();
                    if (noise != null) noise.m_AmplitudeGain = _noiseAmplitude;
                }
            }
        }

        static void ToggleCursorMode()
        {
            CursorMode       = !CursorMode;
            Cursor.lockState = CursorMode ? CursorLockMode.Confined : CursorLockMode.Locked;
            Cursor.visible   = CursorMode;
            if (LevelEditorManager.Instance != null)
                LevelEditorManager.Instance.SetEditorModeActive(CursorMode && FlyCamActive);
            if (!CursorMode) LevelEditor.HideGizmo();
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

        // LMB in non-cursor fly mode — kept for the standard short-range click-teleport.
        public static void HandleTeleport(FlyCam flyCam)
        {
            if (Menu.me.teleporting || _refreezePending || _farTeleportActive) return;

            var ray = Camera.main.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f));
            if (!Physics.Raycast(ray, out var hit, 1000f, LayerCache.PropTerrainMask))
                return;

            var player = PlayerMovement.me;
            if (player == null) return;

            Vector3 checkPos;
            if (Physics.Raycast(hit.point + Vector3.up * 0.5f, Vector3.down, out var groundHit, 10f, LayerCache.PropTerrainMask))
                checkPos = groundHit.point + Vector3.up;
            else
                checkPos = hit.point + Vector3.up;

            FreezePlayer(player, false);
            player.anim.transform.rotation = Quaternion.Euler(0f, flyCam.transform.eulerAngles.y, 0f);
            player.pm.SwitchToActiveMode();
            player.pm.SwitchModes();

            Menu.me.Teleport(checkPos);

            _refreezePending = true;
        }

        // G key: fast unified teleport for any distance.
        // Non-cursor mode: aims along the crosshair with infinite range.
        // Cursor mode: uses fly-cam position.
        // Both paths go through FarTeleportCo.
        static void HandleFarTeleport()
        {
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
            // Pause autosave; stamp target so any stray save writes a consistent position.
            SaveGod.me.stopSaving = true;
            SaveGod.theSave.continuePt = target;

            // Freeze the loader so nothing new starts while we drain.
            BestRegionLoader.me.off = true;

            // Wait for any in-flight async op to settle before we touch loadStates.
            while (BestRegionLoader.somebodyLoading) yield return null;

            // --- Parallel chunk drain ---
            // Kick off every loaded chunk's unload simultaneously. The game normally
            // serialises these one at a time (UnloadAll); firing them all at once lets
            // Addressables pipeline the scene unloads concurrently.
            var chunkMap = BestRegionLoader.me.chunkMap;
            for (int i = 0; i < chunkMap.Length; i++)
            {
                if (chunkMap[i].loadState == LoadState.loaded)
                    chunkMap[i].Unload();
            }

            // Any chunk still mid-load: wait for it to finish, then unload it too.
            bool anyInFlight;
            do
            {
                yield return null;
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
            bool allClear;
            do
            {
                yield return null;
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
            // Zero all prop distances so only the single chunk terrain scene is required.
            // chunkLoadDist = 1f means only the chunk whose bounding box contains the
            // target point (distance == 0 < 1) gets shouldLoad=true.
            var br = BestRegionLoader.me;
            float origLoad   = br.chunkLoadDist;
            float origUnload = br.chunkUnloadDist;
            br.chunkLoadDist   = 1f;
            br.chunkUnloadDist = 1f;

            var propLoad   = br.propLoadDists;
            var propUnload = br.propUnloadDists;
            var origPropLoad   = new float[propLoad.Length];
            var origPropUnload = new float[propUnload.Length];
            for (int i = 0; i < propLoad.Length; i++)   { origPropLoad[i]   = propLoad[i];   propLoad[i]   = 0f; }
            for (int i = 0; i < propUnload.Length; i++) { origPropUnload[i] = propUnload[i]; propUnload[i] = 0f; }

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

            // Hand off to the game's teleport machinery. It forces one BestRegionLoader
            // update, waits for fullyLoaded (1 chunk = very fast), then runs the full
            // ragdoll foot-placement sequence and clears Menu.me.teleporting.
            Menu.me.Teleport(target);
            while (Menu.me.teleporting) yield return null;

            // Restore all distances — surrounding terrain and props stream in normally.
            br.chunkLoadDist   = origLoad;
            br.chunkUnloadDist = origUnload;
            for (int i = 0; i < propLoad.Length; i++)   propLoad[i]   = origPropLoad[i];
            for (int i = 0; i < propUnload.Length; i++) propUnload[i] = origPropUnload[i];

            // Back into fly-cam state.
            FreezePlayer(player, true);
            br.loadingTransform = player.flyCam.transform;

            SaveGod.me.stopSaving = false;
            SkipGame.me.blackScreenAlpha = 0f;
            _farTeleportActive = false;
        }
    }
}
