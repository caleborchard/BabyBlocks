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
        public static bool DebugMode = false; // for categorizing props in the library

        static MelonPreferences_Category _prefs;
        static MelonPreferences_Entry<string> _lastSavePath;

        // Multiplayer-mod chat detection
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
            _prefs        = MelonPreferences.CreateCategory("BabyBlocks");
            _lastSavePath = _prefs.CreateEntry("LastSavePath", "");

            ClassInjector.RegisterTypeInIl2Cpp<LevelEditorObject>();
            ClassInjector.RegisterTypeInIl2Cpp<LevelEditorManager>();
            ClassInjector.RegisterTypeInIl2Cpp<GizmoHandle>();
            ClassInjector.RegisterTypeInIl2Cpp<GhostCollisionCutter>();

            new HarmonyLib.Harmony("BabyBlocks.Patches").PatchAll();
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            PropMetadataPanel.InvalidateMaterialCache();
        }

        public override void OnUpdate() => FlyCamController.OnUpdate();

        public override void OnGUI() => FlyCamController.OnGUI();
    }

    // Replaces FlyCam.Update to add right-click look in cursor mode and left-click teleport.
    [HarmonyPatch(typeof(FlyCam), "Update")]
    class FlyCamUpdatePatch
    {
        static bool Prefix(FlyCam __instance)
        {
            if (FlyCam.locked) return false;

            bool uiTyping = FlyCamController.CursorMode && (LevelEditor.IsTypingInUI || Core.IsKeyboardCaptured);
            var input = Vector3.zero;
            if (!uiTyping)
            {
                if (Input.GetKey(KeyCode.W)) input += Vector3.forward;
                if (Input.GetKey(KeyCode.S)) input += Vector3.back;
                if (Input.GetKey(KeyCode.D)) input += Vector3.right;
                if (Input.GetKey(KeyCode.A)) input += Vector3.left;
                if (Input.GetKey(KeyCode.E)) input += Vector3.up;
                if (Input.GetKey(KeyCode.Q)) input += Vector3.down;
                if (Input.GetKey(KeyCode.LeftShift))                                     input *= 10f;
                if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.LeftAlt)) input *= 30f;
            }

            // Cursor mode: look only while RMB held and not dragging a gizmo.
            bool doLook = !FlyCamController.CursorMode
                       || (Input.GetMouseButton(1) && !LevelEditor.isDragging);

            if (doLook)
            {
                var eul = __instance.transform.eulerAngles;
                if (eul.x > 180f) eul.x -= 360f;
                eul.x -= Input.GetAxis("Mouse Y");
                eul.x  = Mathf.Clamp(eul.x, -85f, 85f);
                eul.y += Input.GetAxis("Mouse X");
                __instance.transform.eulerAngles = eul;
            }

            __instance.transform.position +=
                __instance.transform.rotation * (input * __instance.maxVel) * Time.unscaledDeltaTime;

            if (!FlyCamController.CursorMode && Input.GetMouseButtonDown(0) && !Menu.me.paused)
                FlyCamController.HandleTeleport(__instance);

            return false;
        }
    }

    [HarmonyPatch(typeof(BBConvoStarter), "OnTriggerEnter")]
    class BBConvoStarterTriggerPatch
    {
        static bool Prefix() => !FlyCamController.FlyCamActive;
    }

    // Makes editor bush props produce grass sounds by intercepting TractionByteKeeper.GetGrassAt.
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

    // Verbose diagnostic logging
    internal static class BBLog
    {
        internal static bool Verbose = false;
        internal static void Msg(string msg) { if (Verbose) MelonLogger.Msg(msg); }
    }

    // Applies per-prop hat offset overrides on top of the default head placement.
    [HarmonyPatch(typeof(PlayerMovement), "PlaceCurrentHatOnHead")]
    class PlaceCurrentHatOnHeadPatch
    {
        static bool Prefix(PlayerMovement __instance)
        {
            if (__instance.currentHat == null) return true;

            var leo = __instance.currentHat.GetComponent<LevelEditorObject>()
                   ?? __instance.currentHat.GetComponentInChildren<LevelEditorObject>();
            if (leo == null) return true;

            var hatTrans = __instance.currentHat.transform;
            hatTrans.parent        = __instance.inCutscene ? __instance.head : __instance.headRB;
            hatTrans.localPosition = new Vector3(0f, .207f, -.02f) + leo.hatOffsetPos;
            hatTrans.localRotation = Quaternion.Euler(
                -25f + leo.hatOffsetRot.x, leo.hatOffsetRot.y, leo.hatOffsetRot.z);
            if (!__instance.inCutscene) hatTrans.parent = null;
            return false;
        }
    }
}
