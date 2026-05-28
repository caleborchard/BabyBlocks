using Il2Cpp;
using Il2CppCinemachine;
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
                && !Menu.me.teleporting && !Core.IsKeyboardCaptured)
                ToggleFlyEditorMode();

            if (Input.GetKeyDown(KeyCode.BackQuote) && PlayerMovement.me != null
                && !Menu.me.teleporting && !Core.IsKeyboardCaptured)
                ToggleTeleportMode();

            // After a background teleport finishes, re-freeze the player so fly cam stays active.
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

        public static void HandleTeleport(FlyCam flyCam)
        {
            if (Menu.me.teleporting || _refreezePending) return;

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

            // Unfreeze before teleport so TeleportCo's sequence runs on an active player.
            FreezePlayer(player, false);
            player.anim.transform.rotation = Quaternion.Euler(0f, flyCam.transform.eulerAngles.y, 0f);
            player.pm.SwitchToActiveMode();
            player.pm.SwitchModes();

            Menu.me.Teleport(checkPos);

            // OnUpdate re-freezes once Menu.me.teleporting drops back to false.
            _refreezePending = true;
        }
    }
}
