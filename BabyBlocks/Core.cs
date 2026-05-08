using HarmonyLib;
using Il2Cpp;
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

        public override void OnInitializeMelon()
        {
            // Custom MonoBehaviours must be registered before any AddComponent call.
            ClassInjector.RegisterTypeInIl2Cpp<LevelEditorObject>();
            ClassInjector.RegisterTypeInIl2Cpp<LevelEditorManager>();
            ClassInjector.RegisterTypeInIl2Cpp<GizmoHandle>();

            new HarmonyLib.Harmony("BabyBlocks.Patches").PatchAll();

            SceneHierarchyScanner.Init();
        }

        public override void OnUpdate()
        {
            if (Input.GetKeyDown(KeyCode.F1) && PlayerMovement.me != null)
                ToggleFlyMode();

            if (Input.GetKeyDown(KeyCode.Tab) && flyCamActive)
                ToggleCursorMode();

            // Re-assert the loading transform on the fly cam every frame while
            // flying.  TeleportCo resets it to the player's torso at the end of
            // every teleport; this reclaims it within one frame so terrain keeps
            // streaming around the fly cam rather than the hidden player.
            // We skip only during active teleports so TeleportCo can still use
            // its own loadingTargetTransform to pre-load the destination area.
            if (flyCamActive && !Menu.me.teleporting)
            {
                var player = PlayerMovement.me;
                if (player != null)
                    BestRegionLoader.me.loadingTransform = player.flyCam.transform;
            }

            // Right-click in cursor mode: lock the cursor while the button is held
            // so mouse-look deltas are clean (no cursor wandering off screen).
            if (flyCamActive && cursorMode)
            {
                if (Input.GetMouseButtonDown(1))
                {
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible   = false;
                }
                if (Input.GetMouseButtonUp(1))
                {
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible   = true;
                }
            }

            // Level editor input — only runs in cursor mode.
            if (flyCamActive && cursorMode)
                LevelEditor.Update();

            // Scene hierarchy scanner — available any time the freecam is active.
            if (flyCamActive)
                SceneHierarchyScanner.OnUpdate();
        }

        public override void OnGUI()
        {
            if (flyCamActive && cursorMode)
                LevelEditor.OnGUI();
        }

        static void ToggleFlyMode()
        {
            var player = PlayerMovement.me;

            // flyCam.transform.parent != null means it's currently parented/inactive,
            // so we're about to enable — same condition ToggleFlyCam uses internally.
            bool activating = player.flyCam.transform.parent != null;

            player.ToggleFlyCam();

            if (activating)
            {
                flyCamActive = true;
                cursorMode   = false;
                Cursor.lockState = CursorLockMode.Locked;
                // Hide the player before freezing physics so the A-pose that
                // SwitchToDisabledMode causes is never visible.
                player.gameObject.SetActive(false);
                player.pm.SwitchToDisabledMode();
                LevelEditor.EnsureManager();
                PropLibrary.ScanGpuiProps();
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
            }
        }

        static void ToggleCursorMode()
        {
            cursorMode = !cursorMode;
            Cursor.lockState = cursorMode ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible   = cursorMode;
        }

        public static void HandleFlyCamTeleport(FlyCam flyCam)
        {
            var ray = Camera.main.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f));
            if (!Physics.Raycast(ray, out var hit, 1000f, LayerCache.PropTerrainMask))
                return;

            // Set the animator rotation to the fly cam's yaw before the teleport.
            // TeleportCo calls FindBestFootSpots(checkPos, player.anim.transform.rotation),
            // so this is the hook point for controlling player facing after landing.
            var player = PlayerMovement.me;
            if (player != null)
            {
                float yaw = flyCam.transform.eulerAngles.y;
                player.anim.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            }

            Menu.me.Teleport(hit.point);

            // TeleportCo re-enables the player object and calls OnStandUp() at the
            // end of every teleport — undo that to keep the player hidden while flying.
            MelonCoroutines.Start(ReApplyFlyState());
        }

        static System.Collections.IEnumerator ReApplyFlyState()
        {
            while (Menu.me.teleporting)
                yield return null;

            // One extra frame for TeleportCo's final cleanup to complete.
            yield return null;

            if (!flyCamActive) yield break;

            var player = PlayerMovement.me;
            if (player == null) yield break;

            // TeleportCo called SetActive(true) + OnStandUp() — undo both.
            player.gameObject.SetActive(false);
            player.pm.SwitchToDisabledMode();
            // loadingTransform re-assertion is handled by OnUpdate next frame.
        }
    }

    // Replace FlyCam.Update entirely so we can add right-click look in cursor mode
    // and suppress the native left-click teleport when the level editor is active.
    [HarmonyPatch(typeof(FlyCam), "Update")]
    class FlyCamUpdatePatch
    {
        static bool Prefix(FlyCam __instance)
        {
            if (FlyCam.locked) return false;

            // Movement — identical to native FlyCam, always active.
            var input = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) input += Vector3.forward;
            if (Input.GetKey(KeyCode.S)) input += Vector3.back;
            if (Input.GetKey(KeyCode.D)) input += Vector3.right;
            if (Input.GetKey(KeyCode.A)) input += Vector3.left;
            if (Input.GetKey(KeyCode.E)) input += Vector3.up;
            if (Input.GetKey(KeyCode.Q)) input += Vector3.down;
            if (Input.GetKey(KeyCode.LeftShift)) input *= 10.0f;
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.LeftAlt)) input *= 30f;

            // Mouse look:
            //   • Normal fly mode  → always active (cursor locked).
            //   • Cursor mode      → only while right mouse button is held,
            //                        and not while dragging a gizmo handle
            //                        (avoids camera drift during axis drags).
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

            // Left-click teleport: only in normal fly mode (not cursor / editor mode).
            if (!Core.cursorMode && Input.GetMouseButtonDown(0) && !Menu.me.paused)
                Core.HandleFlyCamTeleport(__instance);

            return false; // Skip original.
        }
    }
}
