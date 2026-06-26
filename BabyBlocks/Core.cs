using BabyBlocks.Networking;
using BabyStepsMenuLib;
using HarmonyLib;
using Il2Cpp;
using Il2CppCinemachine;
using Il2CppInterop.Runtime.Injection;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(BabyBlocks.Core), "Baby Blocks", "1.0.1", "Caleb Orchard", null)]
[assembly: MelonGame("DefaultCompany", "BabySteps")]

namespace BabyBlocks
{
    public class Core : MelonMod
    {
        public static bool DebugMode = false; // for categorizing props and materials in the library
        public static MelonLogger.Instance Logger { get; private set; }

        static MelonPreferences_Category _prefs;
        static MelonPreferences_Entry<string> _lastSavePath;

        // reflects into BabyStepsMultiplayerClient to detect chat open; cached after first lookup
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
                    var coreType = asm.GetType("BabyStepsMultiplayerClient.Core");
                    var uiField = coreType?.GetField("uiManager", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    var chatField = uiField?.FieldType.GetField("showChatTab", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (uiField != null && chatField != null)
                    {
                        _mpUiManagerField = uiField;
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

        // if BRL.somebodyLoading gets stuck (Addressables handle invalidated mid-flight) no further chunks can start; force-clear after 6s
        const float SomebodyLoadingStuckTimeout = 6f;
        static bool _somebodyLoadingWasTrue;
        static float _somebodyLoadingSinceTime;

        static void WatchSomebodyLoading()
        {
            if (BestRegionLoader.me == null) return;
            if (FlyCamController.FarTeleportActive) return;

            if (BestRegionLoader.somebodyLoading)
            {
                if (!_somebodyLoadingWasTrue)
                {
                    _somebodyLoadingWasTrue = true;
                    _somebodyLoadingSinceTime = Time.realtimeSinceStartup;
                }
                else if (Time.realtimeSinceStartup - _somebodyLoadingSinceTime > SomebodyLoadingStuckTimeout)
                {
                    MelonLogger.Warning(
                        $"[BabyBlocks] BestRegionLoader.somebodyLoading stuck for {SomebodyLoadingStuckTimeout}s, force-clearing.");
                    BestRegionLoader.somebodyLoading = false;
                    _somebodyLoadingWasTrue = false;
                }
            }
            else { _somebodyLoadingWasTrue = false; }
        }

        // Menu.me.teleporting can get stuck (TeleportCo never completes); force-clear after 8s
        const float MenuTeleportingStuckTimeout = 8f;
        static bool _menuTeleportingWasTrue;
        static float _menuTeleportingSinceTime;

        static void WatchMenuTeleporting()
        {
            if (Menu.me == null) return;

            // skip when our own coroutines are managing the teleport
            if (FlyCamController.FarTeleportActive || FlyCamController.LevelLoadTeleportActive || FlyCamController.NetworkLevelTransferActive)
            {
                _menuTeleportingWasTrue = false;
                return;
            }

            if (Menu.me.teleporting)
            {
                if (!_menuTeleportingWasTrue)
                {
                    _menuTeleportingWasTrue = true;
                    _menuTeleportingSinceTime = Time.realtimeSinceStartup;
                }
                else if (Time.realtimeSinceStartup - _menuTeleportingSinceTime > MenuTeleportingStuckTimeout)
                {
                    MelonLogger.Warning(
                        $"[BabyBlocks] Menu.me.teleporting stuck true for {MenuTeleportingStuckTimeout}s, force-clearing.");
                    Menu.me.teleporting = false;
                    _menuTeleportingWasTrue = false;
                }
            }
            else
            {
                _menuTeleportingWasTrue = false;
            }
        }

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
            Logger = LoggerInstance;

            _prefs = MelonPreferences.CreateCategory("BabyBlocks");
            _lastSavePath = _prefs.CreateEntry("LastSavePath", "");

            ClassInjector.RegisterTypeInIl2Cpp<LevelEditorObject>();
            ClassInjector.RegisterTypeInIl2Cpp<LevelEditorManager>();
            ClassInjector.RegisterTypeInIl2Cpp<GizmoHandle>();
            ClassInjector.RegisterTypeInIl2Cpp<GhostCollisionCutter>();
            ClassInjector.RegisterTypeInIl2Cpp<SpawnPointMarker>();
            ClassInjector.RegisterTypeInIl2Cpp<OverlayCamPreRenderHook>();
            ClassInjector.RegisterTypeInIl2Cpp<BbSunglassesChecker>();

            var harmony = new HarmonyLib.Harmony("BabyBlocks.Patches");
            harmony.PatchAll(System.Reflection.Assembly.GetExecutingAssembly());
            harmony.PatchAll(typeof(MenuInjectionLibrary).Assembly);

            MenuInjectionLibrary.Logger = Logger;
            UI.CustomLevelsMenu.Initialize();

            UI.PropBrowserUI.Init();
        }

        // scene load burst during save-change can grab a stale terrain material mid-burst; settle for 1.5s first
        internal static float PendingMicroSplatRefreshTime = -1f;
        internal const float MicroSplatRefreshSettleDelay = 1.5f;

        static bool _wasFarTeleportActive;

        // after far teleport, BRL keeps streaming; rescan 90 frames so newly-loaded chunks get hidden
        const int PostTeleportRescanFrames = 90;
        static int _postTeleportRescanFramesRemaining;

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            MaterialVariantTracker.InvalidateMaterialCache();
            PendingMicroSplatRefreshTime = Time.realtimeSinceStartup;
        }

        // BabyStepsNetworking.dll may not be present; catch the first failure and disable networking
        static bool _networkingDisabled;

        public override void OnUpdate()
        {
            UI.CustomLevelsMenu.Update();
            WatchSomebodyLoading();
            WatchMenuTeleporting();

            if (!_networkingDisabled)
            {
                try { ModNetworking.Update(); }
                catch (Exception ex)
                {
                    _networkingDisabled = true;
                    MelonLogger.Warning($"[BabyBlocks] Networking unavailable, disabling: {ex.Message}");
                }
            }

            FlyCamController.OnUpdate();
            UI.PropBrowserUI.UpdateVisibility();
            PropInstanceServices.RenderTints();

            BaseMapController.TickWeatherPreset();

            // suppress BRL-streamed terrain chunks while base map is hidden; skipped during far teleport so TeleportCo has uncontested brl.off
            if (!BaseMapController.BaseMapEnabled && !FlyCamController.FarTeleportActive)
            {
                if (_wasFarTeleportActive)
                    _postTeleportRescanFramesRemaining = PostTeleportRescanFrames;
                _wasFarTeleportActive = false;

                var brl = BestRegionLoader.me;
                if (brl != null)
                {
                    if (_postTeleportRescanFramesRemaining > 0)
                    {
                        BaseMapController.RescanLoadedChunksForBaseMapOff();
                        _postTeleportRescanFramesRemaining--;
                    }

                    if (!brl.off && !BaseMapController.DeferBrlOff && _postTeleportRescanFramesRemaining == 0)
                        brl.off = true;

                    var cache = BaseMapController._brlRendererCache;
                    bool needsRefresh = false;
                    foreach (var r in cache)
                    {
                        if (r == null) { needsRefresh = true; break; }
                        if (r.enabled) r.enabled = false;
                    }
                    if (needsRefresh)
                        BaseMapController._brlRendererCache =
                            brl.GetComponentsInChildren<Renderer>(true);
                }

                BaseMapController.SuppressHiddenWhileBaseMapOff();
            }
            else if (FlyCamController.FarTeleportActive)
            {
                _wasFarTeleportActive = true;
            }
        }

        public override void OnLateUpdate()
        {
            UI.PropBrowserUI.RestoreCursor();
        }

        public override void OnGUI()
        {
            FlyCamController.OnGUI();
            UI.HatPreviewRenderer.DrawWindowGUI();
        }
    }

    // replaces FlyCam.Update to add RMB look in cursor mode and LMB far-teleport
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

            bool doLook = !FlyCamController.CursorMode
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

            if (!FlyCamController.CursorMode && Input.GetMouseButtonDown(0) && !Menu.me.paused)
                FlyCamController.HandleFarTeleport();

            return false;
        }
    }

    // when base map is off CinemachineBrain.LateUpdate doesn't drive Camera.main; ManualUpdate finishes it
    [HarmonyPatch(typeof(CinemachineBrain), "LateUpdate")]
    class CinemachineBrainLateUpdatePatch
    {
        static void Postfix(CinemachineBrain __instance)
        {
            if (!BaseMapController.BaseMapEnabled)
                __instance.ManualUpdate();

            GizmoRenderer.RefreshSSBufferMatrices();
        }
    }

    [HarmonyPatch(typeof(BBConvoStarter), "OnTriggerEnter")]
    class BBConvoStarterTriggerPatch
    {
        static bool Prefix(BBConvoStarter __instance)
        {
            bool suppress = FlyCamController.SuppressCutsceneTriggers;
            return !suppress;
        }
    }

    // gates all cutscene starts including queued/scheduled ones that bypass OnTriggerEnter
    [HarmonyPatch(typeof(BBConvoStarter), "PlayCutscene")]
    class BBConvoStarterPlayCutscenePatch
    {
        static bool Prefix(BBConvoStarter __instance)
        {
            bool suppress = FlyCamController.SuppressCutsceneTriggers;
            if (suppress)
                FlyCamController.RegisterSuppressedCutscene(__instance);
            return !suppress;
        }
    }

    // editor bush props produce grass sounds via GetGrassAt intercept
    [HarmonyPatch(typeof(TractionByteKeeper), "GetGrassAt")]
    class TractionByteKeeperGetGrassAtPatch
    {
        static bool Prefix(Vector3 pos, ref GrassType __result)
        {
            int gt = PropInstanceServices.BushAudioTracker.GetGrassTypeAtPos(pos);
            if (gt != 0)
            {
                __result = (GrassType)gt;
                return false;
            }
            return true;
        }
    }

    internal static class BBLog
    {
        internal static bool Verbose = false;
        internal static void Msg(string msg) { if (Verbose) MelonLogger.Msg(msg); }
    }

    // applies per-prop hat offset overrides on top of default head placement
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
            hatTrans.parent = __instance.inCutscene ? __instance.head : __instance.headRB;
            hatTrans.localPosition = new Vector3(0f, .207f, -.02f) + leo.hatOffsetPos;
            hatTrans.localRotation = Quaternion.Euler(
                -25f + leo.hatOffsetRot.x, leo.hatOffsetRot.y, leo.hatOffsetRot.z);
            if (!__instance.inCutscene) hatTrans.parent = null;
            return false;
        }
    }

    // forces native SunglassesChecker props visible in editor; resets on first frame back in game
    [HarmonyPatch(typeof(SunglassesChecker), "Update")]
    class SunglassesCheckerUpdatePatch
    {
        static readonly System.Collections.Generic.HashSet<int> _forcedVisible = new();

        static void Postfix(SunglassesChecker __instance)
        {
            bool editorMode = FlyCamController.FlyCamActive;
            int iid = __instance.GetInstanceID();

            if (editorMode)
            {
                _forcedVisible.Add(iid);
                SetRends(__instance, true);
            }
            else if (_forcedVisible.Remove(iid))
            {
                bool sunglasses = BbSunglassesChecker.IsWearingSunglasses();
                SetRends(__instance, sunglasses);
            }
        }

        static void SetRends(SunglassesChecker instance, bool enabled)
        {
            try
            {
                var field = instance.GetIl2CppType().GetField("rends");
                if (field != null)
                {
                    var arr = field.GetValue(instance.Cast<Il2CppSystem.Object>())
                                  ?.TryCast<Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Renderer>>();
                    if (arr != null)
                    {
                        foreach (var r in arr)
                            if (r != null) r.enabled = enabled;
                        return;
                    }
                }
            }
            catch { }
            foreach (var r in instance.GetComponentsInChildren<Renderer>(true))
                if (r != null) r.enabled = enabled;
        }
    }
}
