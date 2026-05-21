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
        static bool _flyTeleportInProgress;
        static float _flyCamNoiseAmplitude = -1f;

        static MelonPreferences_Category _prefs;
        static MelonPreferences_Entry<string> _lastSavePath;

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
            if (Input.GetKeyDown(KeyCode.BackQuote) && PlayerMovement.me != null)
                ToggleEditorMode();

            // Keep terrain streaming around the fly cam rather than the hidden player.
            // Skipped during active teleports so TeleportCo can pre-load the destination.
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

            if (flyCamActive && cursorMode && !LevelEditor.IsTypingInUI
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
                Cursor.lockState = CursorLockMode.Locked;
                // Hide player before freezing physics so the A-pose from SwitchToDisabledMode is never visible.
                player.gameObject.SetActive(false);
                player.pm.SwitchToDisabledMode();
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
                flyCamActive = false;
                cursorMode   = false;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible   = false;
                player.pm.SwitchToActiveMode();
                player.pm.SwitchModes();
                player.gameObject.SetActive(true);
                player.OnStandUp();
                LevelEditor.HideGizmo();

                if (_flyCamNoiseAmplitude >= 0f)
                {
                    var noise = player.flyCam.GetComponentInChildren<CinemachineBasicMultiChannelPerlin>();
                    if (noise != null) noise.m_AmplitudeGain = _flyCamNoiseAmplitude;
                }
            }
        }

        static void ToggleCursorMode()
        {
            cursorMode = !cursorMode;
            Cursor.lockState = cursorMode ? CursorLockMode.Confined : CursorLockMode.Locked;
            Cursor.visible   = cursorMode;
            if (!cursorMode) LevelEditor.HideGizmo();
        }

        static void ToggleEditorMode()
        {
            if (!flyCamActive)
            {
                ToggleFlyMode();
                ToggleCursorMode();
                return;
            }

            if (cursorMode)
            {
                ToggleCursorMode();
                return;
            }

            ToggleCursorMode();
        }

        public static void HandleFlyCamTeleport(FlyCam flyCam)
        {
            if (_flyTeleportInProgress || Menu.me.teleporting) return;

            var ray = Camera.main.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f));
            if (!Physics.Raycast(ray, out var hit, 1000f, LayerCache.PropTerrainMask))
                return;

            // Set animator yaw before teleport so FindBestFootSpots uses the fly cam's facing.
            var player = PlayerMovement.me;
            if (player != null)
            {
                float yaw = flyCam.transform.eulerAngles.y;
                player.anim.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            }

            _flyTeleportInProgress = true;
            Menu.me.Teleport(hit.point);
            MelonCoroutines.Start(ExitFlyModeAfterTeleport());
        }

        static System.Collections.IEnumerator ExitFlyModeAfterTeleport()
        {
            while (Menu.me.teleporting)
                yield return null;

            yield return null; // One extra frame for TeleportCo's final cleanup.

            if (flyCamActive)
                ToggleFlyMode();

            _flyTeleportInProgress = false;
        }
    }

    // Replaces FlyCam.Update to add right-click look in cursor mode and left-click teleport in fly mode.
    [HarmonyPatch(typeof(FlyCam), "Update")]
    class FlyCamUpdatePatch
    {
        static bool Prefix(FlyCam __instance)
        {
            if (FlyCam.locked) return false;

            bool uiTyping = Core.cursorMode && LevelEditor.IsTypingInUI;
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
}
