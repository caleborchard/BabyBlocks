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
        public static bool DebugMode = true; // for categorizing props and materials in the library

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
            ClassInjector.RegisterTypeInIl2Cpp<SpawnPointMarker>();

            new HarmonyLib.Harmony("BabyBlocks.Patches").PatchAll();
        }

        // A "load a different save" triggers a burst of many additive scene loads/unloads in
        // quick succession (streaming chunks, loading screens, etc). Refreshing the MicroSplat
        // layer materials mid-burst grabs an intermediate/loading scene's terrain material,
        // which itself goes stale once the burst finishes loading the final scene. Instead,
        // each scene load pushes this timestamp out; FlyCamController.OnUpdate fires the
        // refresh once no further scene load has happened for MicroSplatRefreshSettleDelay.
        internal static float PendingMicroSplatRefreshTime = -1f;
        internal const float MicroSplatRefreshSettleDelay = 1.5f;

        // Tracks whether FarTeleportCo was active last frame, so OnUpdate can
        // detect the true->false transition and start the post-teleport rescan
        // window (see OnUpdate).
        static bool _wasFarTeleportActive;

        // After FarTeleportCo finishes, BRL keeps streaming in surrounding chunks
        // for a bit as the restored chunkLoadDist/propLoadDists take effect (most
        // were still "loading" — loadedChunk == null — at the single rescan done on
        // the finish frame, so they were missed). Keep brl.off == false and rescan
        // every frame for this many frames afterward, then flip brl.off = true once.
        const int PostTeleportRescanFrames = 90;
        static int _postTeleportRescanFramesRemaining;

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            PropMetadataPanel.InvalidateMaterialCache();
            PendingMicroSplatRefreshTime = Time.realtimeSinceStartup;
        }

        public override void OnUpdate()
        {
            if (Input.GetKeyDown(KeyCode.F3))
                PerfMonitor.Toggle();
            PerfMonitor.OnUpdate();

            if (Input.GetKeyDown(KeyCode.F5))
            {
                var (uniqueMats, totalSlots, rendererCount) = LevelEditorManager.CountPropMaterials();
                MelonLogger.Msg($"[MaterialDiag] props: {uniqueMats} unique materials, " +
                    $"{totalSlots} renderer-material slots, {rendererCount} renderers, " +
                    $"BaseMapEnabled={LevelEditorManager.BaseMapEnabled}");
            }

            FlyCamController.OnUpdate();

            // Suppress any terrain chunks or prop containers that BRL streams in
            // while the base map is hidden. Runs unconditionally so it works even
            // before the fly cam editor has been opened.
            //
            // Skipped entirely while FarTeleportCo is running: that coroutine needs
            // uncontested control of brl.off (it flips it false to stream the
            // destination chunk — forcing it back to true here every frame stalls
            // BestRegionLoader.fullyLoaded forever, leaving Menu.me.teleporting stuck
            // true and the player permanently SetActive(false)/invisible) and of
            // player.gameObject's active state during the ragdoll handoff.
            if (!LevelEditorManager.BaseMapEnabled && !FlyCamController.FarTeleportActive)
            {
                // FarTeleportCo just finished (this frame's the first one back with
                // FarTeleportActive == false). brl.off is still false at this point
                // (FarTeleportCo never restores it) and chunkLoadDist/propLoadDists
                // were just restored to normal — BRL will stream in surrounding
                // chunks over the next several frames. Start the rescan window so
                // those chunks get hidden as they finish loading, instead of just
                // the single chunk that's loaded on this exact frame.
                if (_wasFarTeleportActive)
                {
                    // TEMP DIAGNOSTIC
                    BBLog.Msg($"[BaseMapDiag] Core.OnUpdate: FarTeleportActive->false transition, brl.off={(BestRegionLoader.me != null ? BestRegionLoader.me.off : true)}, starting rescan window");
                    _postTeleportRescanFramesRemaining = PostTeleportRescanFrames;
                }
                _wasFarTeleportActive = false;

                var brl = BestRegionLoader.me;
                if (brl != null)
                {
                    if (_postTeleportRescanFramesRemaining > 0)
                    {
                        LevelEditorManager.RescanLoadedChunksForBaseMapOff();
                        _postTeleportRescanFramesRemaining--;
                        if (_postTeleportRescanFramesRemaining == 0)
                        {
                            BBLog.Msg("[BaseMapDiag] Core.OnUpdate: post-teleport rescan window done");
                        }
                    }

                    if (!brl.off && !LevelEditorManager.DeferBrlOff && _postTeleportRescanFramesRemaining == 0)
                        brl.off = true;

                    // Suppress BRL child renderers (proxies).
                    var cache = LevelEditorManager._brlRendererCache;
                    bool needsRefresh = false;
                    foreach (var r in cache)
                    {
                        if (r == null) { needsRefresh = true; break; }
                        if (r.enabled)
                        {
                            // TEMP DIAGNOSTIC
                            if (LevelEditorManager.IsUnderPlayer(r.transform))
                                BBLog.Msg($"[BaseMapDiag] Core.OnUpdate disabling PLAYER renderer '{r.name}' from _brlRendererCache");
                            r.enabled = false;
                        }
                    }
                    if (needsRefresh)
                        LevelEditorManager._brlRendererCache =
                            brl.GetComponentsInChildren<Renderer>(true);
                }

                // Re-assert hidden lights/colliders that game logic (day-night cycle,
                // quest/cutscene state) may have re-enabled since the last toggle.
                LevelEditorManager.SuppressHiddenWhileBaseMapOff();
            }
            else if (FlyCamController.FarTeleportActive)
            {
                _wasFarTeleportActive = true;
            }
        }

        public override void OnGUI()
        {
            FlyCamController.OnGUI();
            PerfMonitor.OnGUI();
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
            if (LevelEditorManager.BaseMapEnabled) return;
            __instance.ManualUpdate();
        }
    }

    [HarmonyPatch(typeof(BBConvoStarter), "OnTriggerEnter")]
    class BBConvoStarterTriggerPatch
    {
        static bool Prefix(BBConvoStarter __instance)
        {
            bool suppress = FlyCamController.SuppressCutsceneTriggers;
            MelonLogger.Msg($"[Cutscene] OnTriggerEnter on '{__instance?.name}' " +
                $"(used={__instance?.used}, cutscene={__instance?.cutscene}) " +
                $"FlyCamActive={FlyCamController.FlyCamActive} suppress={suppress} time={Time.unscaledTime:F2}");
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
            MelonLogger.Msg($"[Cutscene] PlayCutscene on '{__instance?.name}' " +
                $"(used={__instance?.used}, cutscene={__instance?.cutscene}) " +
                $"FlyCamActive={FlyCamController.FlyCamActive} suppress={suppress} time={Time.unscaledTime:F2}");
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
