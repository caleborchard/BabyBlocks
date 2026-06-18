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

            if (_farTeleportActive)
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

                const float w = 400f, h = 60f, margin = 12f;
                var shadow = new Rect(margin + 1, Screen.height - margin - h + 1, w, h);
                var front  = new Rect(margin,     Screen.height - margin - h,     w, h);

                var prev = _teleportLabelStyle.normal.textColor;
                _teleportLabelStyle.normal.textColor = Color.black;
                GUI.Label(shadow, "Teleporting...", _teleportLabelStyle);
                _teleportLabelStyle.normal.textColor = prev;
                GUI.Label(front, "Teleporting...", _teleportLabelStyle);
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

            _farTeleportActive = true;
            MelonCoroutines.Start(FlyCamTeleportCo(player, hit.point));
        }

        static IEnumerator FlyCamTeleportCo(PlayerMovement player, Vector3 target)
        {
            // When Base Map is off, Core.OnUpdate sets brl.off=true each frame to suppress
            // streaming. That would stall TeleportCo's fullyLoaded wait forever. Flip it
            // off here; Core.OnUpdate skips the block while FarTeleportActive is true, then
            // its post-teleport rescan window re-enables it once surrounding chunks settle.
            var brl = BestRegionLoader.me;
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
            while (Menu.me.teleporting) yield return null;

            if (crestWater != null && !BaseMapController.BaseMapEnabled)
                crestWater.SetActive(false);

            SaveGod.me.stopSaving = false;

            // Snap fly cam to new body position so BRL streams terrain around
            // the right area when the player walks after exiting fly-cam mode.
            player.flyCam.transform.position = player.torsoRbs[0].transform.position;

            // Re-establish fly-cam streaming and freeze.
            if (brl != null) brl.loadingTransform = player.flyCam.transform;
            FreezePlayer(player, true);

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
