using HarmonyLib;
using Il2Cpp;
using Il2CppCinemachine;
using Il2CppInterop.Runtime.Injection;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(BabyBlocks.Core), "Baby Blocks", "1.0.0", "Caleb Orchard", null)]
[assembly: MelonGame("DefaultCompany", "BabySteps")]

namespace BabyBlocks
{
    public class Core : MelonMod
    {
        public static bool flyCamActive;
        public static bool cursorMode;
        public static bool DebugMode = true; // For categorizing props in the library, not for general debug logging.
        static float _flyCamNoiseAmplitude = -1f;
        static bool _refreezePending;
        static bool _playerFreezeActive;
        static bool _playerMovementWasEnabled;
        static bool _playerControllerWasEnabled;
        static bool _playerRigidbodyWasKinematic;
        static bool _playerRigidbodyWasUseGravity;
        static RigidbodyConstraints _playerRigidbodyConstraints;
        static Vector3 _playerRigidbodyVelocity;
        static Vector3 _playerRigidbodyAngularVelocity;
        static float _playerAnimatorSpeed = 1f;

        static PlayerMovement _frozenPlayer;
        static CharacterController _frozenController;
        static Rigidbody _frozenRigidbody;
        static Animator _frozenAnimator;

        static MelonPreferences_Category _prefs;
        static MelonPreferences_Entry<string> _lastSavePath;

        // ── Multiplayer-mod chat detection ────────────────────────────────────────
        // Accessed via reflection so BabyBlocks compiles without a hard dependency
        // on the multiplayer mod. FieldInfo is cached after the first lookup.
        static System.Reflection.FieldInfo _mpUiManagerField;
        static System.Reflection.FieldInfo _mpShowChatTabField;
        static bool _mpReflectionDone;

        static void EnsureMpReflection()
        {
            if (_mpReflectionDone) return;
            _mpReflectionDone = true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "BabyStepsMultiplayerClient") continue;
                    var coreType  = asm.GetType("BabyStepsMultiplayerClient.Core");
                    var uiField   = coreType?.GetField("uiManager",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    var chatField = uiField?.FieldType.GetField("showChatTab",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (uiField != null && chatField != null)
                    {
                        _mpUiManagerField   = uiField;
                        _mpShowChatTabField = chatField;
                    }
                    break;
                }
            }
            catch { }
        }

        public static bool IsMultiplayerChatOpen
        {
            get
            {
                EnsureMpReflection();
                if (_mpUiManagerField == null) return false;
                try
                {
                    var mgr = _mpUiManagerField.GetValue(null);
                    return mgr != null && (bool)_mpShowChatTabField.GetValue(mgr);
                }
                catch { return false; }
            }
        }

        public static bool IsKeyboardCaptured =>
            IsMultiplayerChatOpen || (Menu.me != null && Menu.me.paused);

        public static string LastSavePath
        {
            get => _lastSavePath?.Value ?? "";
            set
            {
                if (_lastSavePath == null) return;
                _lastSavePath.Value = value;
                _prefs.SaveToFile();
            }
        }

        public override void OnInitializeMelon()
        {
            _prefs = MelonPreferences.CreateCategory("BabyBlocks");
            _lastSavePath = _prefs.CreateEntry("LastSavePath", "");

            ClassInjector.RegisterTypeInIl2Cpp<LevelEditorObject>();
            ClassInjector.RegisterTypeInIl2Cpp<LevelEditorManager>();
            ClassInjector.RegisterTypeInIl2Cpp<GizmoHandle>();

            new HarmonyLib.Harmony("BabyBlocks.Patches").PatchAll();
        }

        public override void OnUpdate()
        {
            if (Input.GetKeyDown(KeyCode.R) && PlayerMovement.me != null
                && !Menu.me.teleporting && !IsKeyboardCaptured)
                ToggleFlyEditorMode();

            if (Input.GetKeyDown(KeyCode.BackQuote) && PlayerMovement.me != null
                && !Menu.me.teleporting && !IsKeyboardCaptured)
                ToggleTeleportMode();

            // After a background teleport finishes, re-freeze the player so fly cam stays active.
            if (flyCamActive && _refreezePending && !Menu.me.teleporting)
            {
                var player = PlayerMovement.me;
                if (player != null)
                    FreezePlayer(player, true);
                _refreezePending = false;
            }

            // Keep terrain streaming around the fly cam rather than the player.
            if (flyCamActive && !Menu.me.teleporting)
            {
                var player = PlayerMovement.me;
                if (player != null)
                    BestRegionLoader.me.loadingTransform = player.flyCam.transform;
            }

            // Right-click in cursor mode: lock cursor while held so mouse-look deltas are clean.
            if (flyCamActive && cursorMode)
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

            if (flyCamActive && cursorMode && !LevelEditor.IsTypingInUI && !IsKeyboardCaptured
                && Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.S))
                SaveLoadWindow.TriggerSave();

            if (flyCamActive && cursorMode)
                LevelEditor.Update();
        }

        public override void OnGUI()
        {
            if (flyCamActive && cursorMode)
                LevelEditor.OnGUI();
        }

        static void ToggleFlyMode()
        {
            var player = PlayerMovement.me;

            // flyCam.transform.parent != null means currently parented/inactive — about to enable.
            bool activating = player.flyCam.transform.parent != null;

            player.ToggleFlyCam();

            if (activating)
            {
                flyCamActive = true;
                cursorMode   = false;
                _refreezePending = false;
                Cursor.lockState = CursorLockMode.Locked;
                FreezePlayer(player, true);
                LevelEditor.EnsureManager();
                PropLibrary.ScanGpuiProps();
                PropMetadataPanel.InvalidateMaterialSources();

                var noise = player.flyCam.GetComponentInChildren<CinemachineBasicMultiChannelPerlin>();
                if (noise != null)
                {
                    if (_flyCamNoiseAmplitude < 0f) _flyCamNoiseAmplitude = noise.m_AmplitudeGain;
                    noise.m_AmplitudeGain = 0f;
                }
            }
            else
            {
                LevelEditorManager.Instance?.SetEditorModeActive(false);
                flyCamActive = false;
                cursorMode   = false;
                _refreezePending = false;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible   = false;
                FreezePlayer(player, false);

                player.pm.SwitchToActiveMode();
                player.pm.SwitchModes();
                player.OnStandUp();
                LevelEditor.HideGizmo();

                if (_flyCamNoiseAmplitude >= 0f)
                {
                    var noise = player.flyCam.GetComponentInChildren<CinemachineBasicMultiChannelPerlin>();
                    if (noise != null) noise.m_AmplitudeGain = _flyCamNoiseAmplitude;
                }
            }
        }

        static void FreezePlayer(PlayerMovement player, bool frozen)
        {
            if (player == null)
                return;

            if (frozen)
            {
                if (_playerFreezeActive && _frozenPlayer == player)
                    return;

                _frozenPlayer = player;
                _playerFreezeActive = true;

                _playerMovementWasEnabled = player.enabled;
                player.enabled = false;

                _frozenController = player.GetComponent<CharacterController>();
                if (_frozenController != null)
                {
                    _playerControllerWasEnabled = _frozenController.enabled;
                    _frozenController.enabled = false;
                }

                _frozenRigidbody = player.GetComponent<Rigidbody>();
                if (_frozenRigidbody != null)
                {
                    _playerRigidbodyWasKinematic = _frozenRigidbody.isKinematic;
                    _playerRigidbodyWasUseGravity = _frozenRigidbody.useGravity;
                    _playerRigidbodyConstraints = _frozenRigidbody.constraints;
                    _playerRigidbodyVelocity = _frozenRigidbody.velocity;
                    _playerRigidbodyAngularVelocity = _frozenRigidbody.angularVelocity;

                    _frozenRigidbody.velocity = Vector3.zero;
                    _frozenRigidbody.angularVelocity = Vector3.zero;
                    _frozenRigidbody.isKinematic = true;
                    _frozenRigidbody.useGravity = false;
                    _frozenRigidbody.constraints = RigidbodyConstraints.FreezeAll;
                }

                _frozenAnimator = player.anim;
                if (_frozenAnimator != null)
                {
                    _playerAnimatorSpeed = _frozenAnimator.speed;
                    _frozenAnimator.speed = 0f;
                }

                Physics.SyncTransforms();
                return;
            }

            if (!_playerFreezeActive || _frozenPlayer != player)
                return;

            if (_frozenAnimator != null)
                _frozenAnimator.speed = _playerAnimatorSpeed;

            if (_frozenRigidbody != null)
            {
                _frozenRigidbody.constraints = _playerRigidbodyConstraints;
                _frozenRigidbody.isKinematic = _playerRigidbodyWasKinematic;
                _frozenRigidbody.useGravity = _playerRigidbodyWasUseGravity;
                _frozenRigidbody.velocity = _playerRigidbodyVelocity;
                _frozenRigidbody.angularVelocity = _playerRigidbodyAngularVelocity;
            }

            if (_frozenController != null)
                _frozenController.enabled = _playerControllerWasEnabled;

            player.enabled = _playerMovementWasEnabled;

            _playerFreezeActive = false;
            _frozenPlayer = null;
            _frozenController = null;
            _frozenRigidbody = null;
            _frozenAnimator = null;
        }

        static void ToggleCursorMode()
        {
            cursorMode = !cursorMode;
            Cursor.lockState = cursorMode ? CursorLockMode.Confined : CursorLockMode.Locked;
            Cursor.visible   = cursorMode;
            if (LevelEditorManager.Instance != null)
                LevelEditorManager.Instance.SetEditorModeActive(cursorMode && flyCamActive);
            if (!cursorMode) LevelEditor.HideGizmo();
        }

        // R key: toggle teleport mode (fly cam without cursor/editor).
        static void ToggleTeleportMode()
        {
            if (!flyCamActive)
            {
                ToggleFlyMode();
                return;
            }

            ToggleFlyMode();
        }

        // BackQuote key: toggle fly+editor mode (fly cam with cursor).
        static void ToggleFlyEditorMode()
        {
            if (!flyCamActive)
            {
                ToggleFlyMode();
                return;
            }

            ToggleCursorMode();
        }

        public static void HandleFlyCamTeleport(FlyCam flyCam)
        {
            if (Menu.me.teleporting || _refreezePending) return;

            // Raycast from screen centre — the surface we hit already has collision loaded
            // (it's visible from the fly cam), so no chunk-loading wait is needed.
            var ray = Camera.main.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f));
            if (!Physics.Raycast(ray, out var hit, 1000f, LayerCache.PropTerrainMask))
                return;

            var player = PlayerMovement.me;
            if (player == null) return;

            // Cast down from just above the hit point to get the clean surface height.
            Vector3 checkPos;
            if (Physics.Raycast(hit.point + Vector3.up * 0.5f, Vector3.down, out var groundHit, 10f, LayerCache.PropTerrainMask))
                checkPos = groundHit.point + Vector3.up;
            else
                checkPos = hit.point + Vector3.up;

            // Unfreeze the player so TeleportCo's SetActive/SwitchToDisabledMode sequence
            // runs on a properly active player — leaving the player frozen causes TeleportCo
            // to crash silently mid-coroutine, locking controls with teleporting stuck true.
            FreezePlayer(player, false);
            player.anim.transform.rotation = Quaternion.Euler(0f, flyCam.transform.eulerAngles.y, 0f);
            player.pm.SwitchToActiveMode();
            player.pm.SwitchModes();

            // Fire the native teleport in the background — fly cam stays active.
            // The fly cam has been streaming this area, so fullyLoaded is already true.
            Menu.me.Teleport(checkPos);

            // OnUpdate will re-freeze once Menu.me.teleporting drops back to false.
            _refreezePending = true;
        }
    }

    // Replaces FlyCam.Update to add right-click look in cursor mode and left-click teleport in fly mode.
    [HarmonyPatch(typeof(FlyCam), "Update")]
    class FlyCamUpdatePatch
    {
        static bool Prefix(FlyCam __instance)
        {
            if (FlyCam.locked) return false;

            bool uiTyping = Core.cursorMode && (LevelEditor.IsTypingInUI || Core.IsKeyboardCaptured);
            var input = Vector3.zero;
            if (!uiTyping)
            {
                if (Input.GetKey(KeyCode.W)) input += Vector3.forward;
                if (Input.GetKey(KeyCode.S)) input += Vector3.back;
                if (Input.GetKey(KeyCode.D)) input += Vector3.right;
                if (Input.GetKey(KeyCode.A)) input += Vector3.left;
                if (Input.GetKey(KeyCode.E)) input += Vector3.up;
                if (Input.GetKey(KeyCode.Q)) input += Vector3.down;
                if (Input.GetKey(KeyCode.LeftShift)) input *= 10.0f;
                if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.LeftAlt)) input *= 30f;
            }

            // Cursor mode: look only while RMB held and not dragging a gizmo (avoids camera drift).
            bool doLook = !Core.cursorMode
                       || (Input.GetMouseButton(1) && !LevelEditor.isDragging);

            if (doLook)
            {
                var eul = __instance.transform.eulerAngles;
                if (eul.x > 180f) eul.x -= 360f;
                eul.x -= Input.GetAxis("Mouse Y");
                eul.x = Mathf.Clamp(eul.x, -85f, 85f);
                eul.y += Input.GetAxis("Mouse X");
                __instance.transform.eulerAngles = eul;
            }

            __instance.transform.position +=
                __instance.transform.rotation * (input * __instance.maxVel) * Time.unscaledDeltaTime;

            if (!Core.cursorMode && Input.GetMouseButtonDown(0) && !Menu.me.paused)
                Core.HandleFlyCamTeleport(__instance);

            return false;
        }
    }

    [HarmonyPatch(typeof(BBConvoStarter), "OnTriggerEnter")]
    class BBConvoStarterTriggerPatch
    {
        static bool Prefix() => !Core.flyCamActive;
    }

    // Makes editor bush props produce grass sounds (rustle + foot-plant impact) by intercepting
    // TractionByteKeeper.GetGrassAt for positions inside any active editor bush sphere.
    [HarmonyPatch(typeof(TractionByteKeeper), "GetGrassAt")]
    class TractionByteKeeperGetGrassAtPatch
    {
        static bool Prefix(Vector3 pos, ref GrassType __result)
        {
            int gt = PropMetadataPanel.BushAudioTracker.GetGrassTypeAtPos(pos);
            if (gt != 0)
            {
                __result = (GrassType)gt;
                return false;
            }
            return true;
        }
    }

}
