using BabyBlocks.Networking;
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
        public static bool DebugMode = false; // for categorizing props and materials in the library

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

        // BestRegionLoader.LoadProp/UnloadProp are async UniTaskVoid methods that set
        // somebodyLoading = true, await an Addressables handle, then clear it. If the
        // handle gets invalidated mid-flight (e.g. concurrent chunk/scene unload),
        // somebodyLoading stays true forever and no further chunk loads can start.
        // Normal chunk loads take 3-4s; use 6s so we never interrupt a healthy load.
        // Skipped during far teleports — FlyCamTeleportCo has its own scoped watchdog.
        const float SomebodyLoadingStuckTimeout = 6f;
        static bool  _somebodyLoadingWasTrue;
        static float _somebodyLoadingSinceTime;

        static void WatchSomebodyLoading()
        {
            if (BestRegionLoader.me == null) return;
            if (FlyCamController.FarTeleportActive) return;

            if (BestRegionLoader.somebodyLoading)
            {
                if (!_somebodyLoadingWasTrue)
                {
                    _somebodyLoadingWasTrue   = true;
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

        // Menu.me.teleporting can get stuck true (e.g. if a level-transfer load
        // called Menu.me.Teleport in a context where the native TeleportCo never
        // completes). While stuck, the R / BackQuote guards block editor exit,
        // leaving the player permanently trapped. Force-clear after a generous timeout.
        const float MenuTeleportingStuckTimeout = 8f;
        static bool  _menuTeleportingWasTrue;
        static float _menuTeleportingSinceTime;

        static void WatchMenuTeleporting()
        {
            if (Menu.me == null) return;

            // Never interfere while one of our own coroutines is managing the teleport —
            // they deliberately loop on Menu.me.teleporting and must let it finish naturally.
            // Only watch for unmanaged stuck states (e.g. from a peer-join side-effect).
            if (FlyCamController.FarTeleportActive
                || FlyCamController.LevelLoadTeleportActive
                || FlyCamController.NetworkLevelTransferActive)
            {
                _menuTeleportingWasTrue = false;
                return;
            }

            if (Menu.me.teleporting)
            {
                if (!_menuTeleportingWasTrue)
                {
                    _menuTeleportingWasTrue  = true;
                    _menuTeleportingSinceTime = Time.realtimeSinceStartup;
                }
                else if (Time.realtimeSinceStartup - _menuTeleportingSinceTime > MenuTeleportingStuckTimeout)
                {
                    MelonLogger.Warning(
                        $"[BabyBlocks] Menu.me.teleporting stuck true for {MenuTeleportingStuckTimeout}s, force-clearing.");
                    Menu.me.teleporting  = false;
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
            _prefs        = MelonPreferences.CreateCategory("BabyBlocks");
            _lastSavePath = _prefs.CreateEntry("LastSavePath", "");

            ClassInjector.RegisterTypeInIl2Cpp<LevelEditorObject>();
            ClassInjector.RegisterTypeInIl2Cpp<LevelEditorManager>();
            ClassInjector.RegisterTypeInIl2Cpp<GizmoHandle>();
            ClassInjector.RegisterTypeInIl2Cpp<GhostCollisionCutter>();
            ClassInjector.RegisterTypeInIl2Cpp<SpawnPointMarker>();
            ClassInjector.RegisterTypeInIl2Cpp<OverlayCamPreRenderHook>();
            ClassInjector.RegisterTypeInIl2Cpp<BbSunglassesChecker>();

            new HarmonyLib.Harmony("BabyBlocks.Patches").PatchAll();
            UI.PropBrowserUI.Init();
        }

        // A "load a different save" triggers a burst of many additive scene loads/unloads in
        // quick succession (streaming chunks, loading screens, etc). Refreshing the MicroSplat
        // layer materials mid-burst grabs an intermediate/loading scene's terrain material,
        // which itself goes stale once the burst finishes loading the final scene. Instead,
        // each scene load pushes this timestamp out; FlyCamController.OnUpdate fires the
        // refresh once no further scene load has happened for MicroSplatRefreshSettleDelay.
        internal static float PendingMicroSplatRefreshTime = -1f;
        internal const float MicroSplatRefreshSettleDelay = 1.5f;

        // Tracks whether FlyCamTeleportCo was active last frame, so OnUpdate can
        // detect the true->false transition and start the post-teleport rescan
        // window (see OnUpdate).
        static bool _wasFarTeleportActive;

        // After FlyCamTeleportCo finishes, BRL keeps streaming in surrounding chunks.
        // Keep brl.off == false and rescan every frame for this many frames so newly
        // loaded chunks get hidden, then flip brl.off = true once.
        const int PostTeleportRescanFrames = 90;
        static int _postTeleportRescanFramesRemaining;

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            MaterialVariantTracker.InvalidateMaterialCache();
            PendingMicroSplatRefreshTime = Time.realtimeSinceStartup;
        }

        // ModNetworking.Update touches types from BabyStepsNetworking.dll, which BabyBlocks
        // references directly but which may not be present if no other mod has installed it.
        // Resolving those types fails the first time this is JIT'd/called in that case;
        // catch it once and stop calling in so the rest of the editor keeps working.
        static bool _networkingDisabled;

        public override void OnUpdate()
        {
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
            BaseMapController.TickWeatherPreset();

            // Suppress any terrain chunks or prop containers that BRL streams in
            // while the base map is hidden. Runs unconditionally so it works even
            // before the fly cam editor has been opened.
            //
            // Skipped while FlyCamTeleportCo is running so the native TeleportCo has
            // uncontested control of brl.off; forcing it back to true here every frame
            // would stall fullyLoaded forever.
            if (!BaseMapController.BaseMapEnabled && !FlyCamController.FarTeleportActive)
            {
                // FlyCamTeleportCo just finished (first frame back with FarTeleportActive
                // == false). brl.off is still false — BRL will stream surrounding chunks
                // over the next several frames. Start the rescan window so those chunks
                // get hidden as they load.
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

                    // Suppress BRL child renderers (proxies).
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

                // Re-assert hidden lights/colliders that game logic (day-night cycle,
                // quest/cutscene state) may have re-enabled since the last toggle.
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
        }
    }

    // Replaces FlyCam.Update to add right-click look in cursor mode and left-click far-teleport.
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
                FlyCamController.HandleFarTeleport();

            return false;
        }
    }

    // While base map is disabled, the FlyCam/GameCam vcams' transforms update
    // correctly each frame, but CinemachineBrain.LateUpdate stops applying that
    // state to Camera.main on its own. Forcing a manual re-update keeps the
    // output camera synced.
    [HarmonyPatch(typeof(CinemachineBrain), "LateUpdate")]
    class CinemachineBrainLateUpdatePatch
    {
        static void Postfix(CinemachineBrain __instance)
        {
            // In base-map-disabled mode LateUpdate alone doesn't drive Camera.main;
            // ManualUpdate finishes the job. Either way, by the time we reach here
            // the camera transform for this frame is final.
            if (!BaseMapController.BaseMapEnabled)
                __instance.ManualUpdate();

            // Re-record the screen-space outline CommandBuffer with the now-current
            // Camera.main matrices so the mask aligns with the scene render.
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

    // PlayCutscene is the common choke point for every way a cutscene can start — not just
    // OnTriggerEnter, but also Menu.qedCutscene, morning/evening checkpoint cutscenes, and
    // sequelCutscene chaining (BBConvoStarter.OnEnd). Gating here catches scheduled/queued
    // cutscenes that never go through OnTriggerEnter at all.
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

    // Makes editor bush props produce grass sounds by intercepting TractionByteKeeper.GetGrassAt.
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

    // Force native SunglassesChecker props visible while in editor cursor mode.
    // The native type controls visibility via a Renderer[] field named "rends" and uses
    // change-detection, so it won't re-hide on its own when we exit editor mode — we
    // must actively reset it on the first frame after leaving editor.
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
                // First frame back in game — native's cached state is stale; force-correct it.
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
