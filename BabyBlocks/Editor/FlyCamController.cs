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

        // True while TeleportToSpawnPoint's Menu.me.Teleport is running (file-load trigger).
        // Shows the "Teleporting..." overlay without requiring _farTeleportActive.
        public static bool LevelLoadTeleportActive;

        // True while a network level transfer is in progress on this client.
        // Shows "Loading level..." overlay.
        public static bool NetworkLevelTransferActive;

        // Called by LevelSaveLoad.TeleportToSpawnPoint before Menu.me.Teleport fires.
        public static void BeginLevelLoadTeleport() => LevelLoadTeleportActive = true;

        // Called by LevelSaveLoad.SnapFlyCamToPlayer once the teleport has settled.
        public static void EndLevelLoadTeleport() => LevelLoadTeleportActive = false;

        // Called by ModNetworking when a level transfer begins (joining server / peer-initiated load).
        public static void BeginNetworkLevelTransfer() => NetworkLevelTransferActive = true;

        // Called by ModNetworking once the transfer finishes loading, times out, or the channel tears down.
        public static void EndNetworkLevelTransfer() => NetworkLevelTransferActive = false;

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
        static bool  _editorScanDone;

        // True for the whole duration of FlyCamTeleportCo. While true, Core.OnUpdate's
        // Base-Map-off block skips touching brl.off so the native TeleportCo can drive
        // chunk loading uncontested.
        static bool _farTeleportActive;
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

            // G: crosshair teleport (non-cursor mode only). Mirrors LMB handling.
            if (FlyCamActive && !CursorMode && Input.GetKeyDown(KeyCode.G)
                && !Menu.me.teleporting && !_farTeleportActive
                && !Core.IsKeyboardCaptured)
                HandleFarTeleport();

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

        static GUIStyle _teleportLabelStyle;

        public static void OnGUI()
        {
            if (FlyCamActive && CursorMode && !UI.PropBrowserUI.Ready)
                LevelEditor.OnGUI();

            bool isTeleporting   = _farTeleportActive || LevelLoadTeleportActive;
            bool isLoadingLevel  = NetworkLevelTransferActive;

            if (isTeleporting || isLoadingLevel)
            {
                if (_teleportLabelStyle == null)
                {
                    _teleportLabelStyle = new GUIStyle(GUI.skin.label)
                    {
                        fontSize  = 48,
                        fontStyle = FontStyle.Bold,
                    };
                    _teleportLabelStyle.normal.textColor = Color.white;
                }

                string text = isLoadingLevel && !isTeleporting ? "Loading level..." : "Teleporting...";

                const float w = 400f, h = 60f, margin = 12f;
                var shadow = new Rect(margin + 1, Screen.height - margin - h + 1, w, h);
                var front  = new Rect(margin,     Screen.height - margin - h,     w, h);

                var prev = _teleportLabelStyle.normal.textColor;
                _teleportLabelStyle.normal.textColor = Color.black;
                GUI.Label(shadow, text, _teleportLabelStyle);
                _teleportLabelStyle.normal.textColor = prev;
                GUI.Label(front, text, _teleportLabelStyle);
            }
        }

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

        // Called by the top-bar button — same guards as the R key handler.
        public static void InvokeRKeyAction()
        {
            if (PlayerMovement.me == null || Menu.me == null) return;
            if (Menu.me.teleporting || _farTeleportActive) return;
            if (PlayerMovement.me.inCutscene) return;
            ToggleFlyEditorMode();
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

        // LMB (non-cursor mode) / G key: crosshair teleport.
        // Mirrors native FlyCam.Update's LMB teleport: raycast from center screen,
        // call Menu.me.Teleport() on hit, then restore fly-cam state afterward.
        public static void HandleFarTeleport()
        {
            if (Menu.me.teleporting || _farTeleportActive) return;

            var player = PlayerMovement.me;
            if (player == null) return;

            var ray = Camera.main.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f));
            if (!Physics.Raycast(ray, out var hit, 1000f, LayerCache.PropTerrainMask)) return;

            MelonLogger.Msg($"[FarTeleport] Click → {hit.point}  collider={hit.collider?.name}");
            _farTeleportActive = true;
            MelonCoroutines.Start(FlyCamTeleportCo(player, hit.point));
        }

        static IEnumerator FlyCamTeleportCo(PlayerMovement player, Vector3 target)
        {
            var brl = BestRegionLoader.me;
            float startDist = player.torsoRbs != null && player.torsoRbs.Length > 0
                ? Vector3.Distance(player.torsoRbs[0].transform.position, target) : 0f;
            MelonLogger.Msg($"[FarTeleport] Start  dist={startDist:F0}m  target={target}" +
                            $"  brl.off={brl?.off}  fullyLoaded={BestRegionLoader.fullyLoaded}");

            // When Base Map is off, Core.OnUpdate sets brl.off=true each frame to suppress
            // streaming. That would stall TeleportCo's fullyLoaded wait forever. Flip it
            // off here; Core.OnUpdate skips the block while FarTeleportActive is true, then
            // its post-teleport rescan window re-enables it once surrounding chunks settle.
            if (!BaseMapController.BaseMapEnabled && brl != null && brl.off)
                brl.off = false;

            // Unfreeze so TeleportCo's internal mode switches and ragdoll sequence work.
            FreezePlayer(player, false);

            // Face the player in the fly-cam's direction so the landing pose looks right.
            float facingY = player.flyCam.transform.eulerAngles.y;
            player.anim.transform.rotation = Quaternion.Euler(0f, facingY, 0f);

            // Put the puppet in active mode so TeleportCo's ragdoll handoff works.
            player.pm.SwitchToActiveMode();
            player.pm.SwitchModes();

            // Deactivate before handing off to TeleportCo so the unfrozen player can't
            // fall through unloaded terrain or trigger scream/splash sounds while waiting
            // for the destination chunk to load. TeleportCo's own SetActive(false) becomes
            // a no-op; its SetActive(true) at the end re-enables the player correctly.
            player.gameObject.SetActive(false);

            // Menu.TeleportCo calls OceanRenderer.Instance.RebuildOcean() with no null check.
            // When Base Map is off, CrestWaterRenderer is inactive and OceanRenderer.Instance
            // is null — the NRE silently kills the coroutine, leaving Menu.me.teleporting
            // stuck true and the player permanently SetActive(false). Re-enable it briefly.
            GameObject crestWater = null;
            if (!BaseMapController.BaseMapEnabled)
            {
                var bigManager = GameObject.Find("BigManagerPrefab");
                crestWater = bigManager?.transform.Find("CrestWaterRenderer")?.gameObject;
                if (crestWater != null && !crestWater.activeSelf)
                    crestWater.SetActive(true);
                else
                    crestWater = null;
            }

            SaveGod.me.stopSaving = true;
            SaveGod.theSave.continuePt = target;
            Menu.me.Teleport(target);

            // Wait for TeleportCo. The native wait is `while (!BestRegionLoader.fullyLoaded)`
            // which requires EVERY chunk in the streaming radius to settle — for a far jump
            // this means sequentially loading new chunks AND unloading old ones, potentially
            // taking minutes. Instead, we poll with a downward raycast until the terrain at
            // the target point is physically present, then override fullyLoaded each frame so
            // TeleportCo breaks out of its wait and places the player. Our coroutine (yield
            // null) runs after BRL.Update() in Unity's Update order but before TeleportCo's
            // next WaitForFixedUpdate resume, so the override is visible on TeleportCo's next
            // check. We do NOT force-clear Menu.me.teleporting — TeleportCo must run to
            // completion so the player is actually moved to the target.
            {
                float waitStart      = Time.unscaledTime;
                float lastLogSec     = -1f;
                bool  slWasTrue      = false;
                float slStuckSince   = 0f;
                bool  targetLoaded   = false;

                while (Menu.me.teleporting)
                {
                    float elapsed    = Time.unscaledTime - waitStart;
                    int   elapsedSec = Mathf.FloorToInt(elapsed);

                    if (elapsedSec != Mathf.FloorToInt(lastLogSec))
                    {
                        lastLogSec = elapsed;
                        MelonLogger.Msg($"[FarTeleport] t={elapsedSec}s" +
                                        $"  fullyLoaded={BestRegionLoader.fullyLoaded}" +
                                        $"  somebodyLoading={BestRegionLoader.somebodyLoading}" +
                                        $"  targetLoaded={targetLoaded}");
                    }

                    // somebodyLoading watchdog: if an async chunk op's Addressables handle
                    // gets invalidated, somebodyLoading stays true forever and no further
                    // chunks can load. Use a generous threshold (5s) so normal chunk loads
                    // (~3-4s each) are never interrupted — only truly hung handles are cleared.
                    if (BestRegionLoader.somebodyLoading)
                    {
                        if (!slWasTrue) { slWasTrue = true; slStuckSince = Time.unscaledTime; }
                        else if (Time.unscaledTime - slStuckSince > 5f)
                        {
                            MelonLogger.Warning($"[FarTeleport] somebodyLoading stuck" +
                                                $" {(Time.unscaledTime - slStuckSince):F1}s — force-clearing");
                            BestRegionLoader.somebodyLoading = false;
                            slWasTrue = false;
                        }
                    }
                    else { slWasTrue = false; }

                    // Once the chunk at the target is loaded (raycast confirms terrain is
                    // present), or after a 60s hard fallback, override fullyLoaded so
                    // TeleportCo can proceed to place the player. Do NOT touch somebodyLoading
                    // here — BRL loads chunks sequentially using that flag, and interrupting
                    // a normal in-flight load corrupts BRL's state and breaks post-teleport
                    // terrain streaming. TeleportCo only gates on fullyLoaded, not somebodyLoading.
                    if (!targetLoaded && elapsed >= 1f)
                    {
                        var origin = new Vector3(target.x, target.y + 100f, target.z);
                        if (Physics.Raycast(origin, Vector3.down, 300f, LayerCache.PropTerrainMask))
                        {
                            targetLoaded = true;
                            MelonLogger.Msg($"[FarTeleport] Target terrain visible at t={elapsed:F1}s — overriding fullyLoaded");
                        }
                    }

                    if (targetLoaded || elapsed > 60f)
                    {
                        if (elapsed > 60f && !targetLoaded)
                            MelonLogger.Warning("[FarTeleport] 60s with no terrain hit — forcing anyway");
                        BestRegionLoader.fullyLoaded = true;
                    }

                    yield return null;
                }

                MelonLogger.Msg($"[FarTeleport] TeleportCo done in {(Time.unscaledTime - waitStart):F1}s");
            }

            if (crestWater != null && !BaseMapController.BaseMapEnabled)
                crestWater.SetActive(false);

            SaveGod.me.stopSaving = false;

            // Fly cam stays at the pre-moved target position — the user is in control of
            // the camera and doesn't want it snapping to the player after a click teleport.
            // Re-establish fly-cam streaming and freeze.
            if (brl != null) brl.loadingTransform = player.flyCam.transform;
            FreezePlayer(player, true);

            // Second-pass landing check: the initial click raycast may have hit an unloaded
            // LOD placeholder at the wrong height. TeleportCo does its own raycast too, but
            // only searches 500m upward from the original target — if that target was deep
            // inside unloaded geometry it can still land the player somewhere bad. Verify
            // the landing now that terrain is fully loaded and re-teleport if needed.
            if (player.gameObject.activeInHierarchy
                && player.torsoRbs != null && player.torsoRbs.Length > 0)
            {
                float playerY     = player.torsoRbs[0].transform.position.y;
                var   camPos      = player.flyCam.transform.position;
                var   camToTarget = target - camPos;
                float camDist     = camToTarget.magnitude;
                if (camDist > 0.1f && Physics.Raycast(camPos, camToTarget / camDist, out var verifyHit, camDist + 100f, LayerCache.PropTerrainMask))
                {
                    float delta = Mathf.Abs(playerY - verifyHit.point.y);
                    MelonLogger.Msg($"[FarTeleport] Landing check: playerY={playerY:F1}  hitY={verifyHit.point.y:F1}  delta={delta:F1}m  collider={verifyHit.collider?.name}");
                    if (delta > 3f)
                    {
                        var corrected = verifyHit.point;
                        MelonLogger.Msg($"[FarTeleport] Correcting landing → {corrected}");

                        FreezePlayer(player, false);
                        player.gameObject.SetActive(false);

                        if (!BaseMapController.BaseMapEnabled && crestWater != null)
                            crestWater.SetActive(true);

                        SaveGod.theSave.continuePt = corrected;
                        Menu.me.Teleport(corrected);

                        // Terrain is already loaded — override fullyLoaded immediately so
                        // TeleportCo places the player without waiting for surrounding chunks.
                        float corrStart = Time.unscaledTime;
                        while (Menu.me.teleporting)
                        {
                            BestRegionLoader.fullyLoaded = true;
                            if (Time.unscaledTime - corrStart > 10f)
                            {
                                MelonLogger.Warning("[FarTeleport] Correction teleport stuck 10s — forcing");
                                BestRegionLoader.fullyLoaded = true;
                            }
                            yield return null;
                        }
                        MelonLogger.Msg($"[FarTeleport] Correction done in {(Time.unscaledTime - corrStart):F1}s");

                        if (!BaseMapController.BaseMapEnabled && crestWater != null)
                            crestWater.SetActive(false);

                        if (brl != null) brl.loadingTransform = player.flyCam.transform;
                        FreezePlayer(player, true);
                    }
                }
                else
                {
                    MelonLogger.Warning($"[FarTeleport] Landing check: camera-direction raycast missed, playerY={playerY:F1}");
                }
            }

            // Freshly loaded terrain can invalidate cached material instances —
            // repoint all placed props at the new resolved materials.
            MaterialVariantTracker.InvalidateMaterialCache();
            MaterialCatalog.EnsureMaterialListLoaded();
            MaterialCatalog.ReapplyAllMaterialOverrides();
            if (!BaseMapController.BaseMapEnabled)
                BaseMapController.SetEditorPropsSnowDisabled(true);

            // Clear last: Core.OnUpdate detects the true→false transition to start
            // its post-teleport rescan window that hides streaming chunks.
            _farTeleportActive = false;
        }
    }
}
