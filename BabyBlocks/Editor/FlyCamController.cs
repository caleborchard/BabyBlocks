using System.Collections;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppCinemachine;
using MelonLoader;
using UnityEngine;

namespace BabyBlocks
{
    static class FlyCamController
    {
        public static bool FlyCamActive;
        public static bool CursorMode;

        // true while a file load teleport is running; shows "Teleporting..." overlay without needing _farTeleportActive
        public static bool LevelLoadTeleportActive;

        // true while a network level transfer is in progress. shows "Loading level..." overlay
        public static bool NetworkLevelTransferActive;

        public static void BeginLevelLoadTeleport() => LevelLoadTeleportActive = true;
        public static void EndLevelLoadTeleport() => LevelLoadTeleportActive = false;
        public static void BeginNetworkLevelTransfer() => NetworkLevelTransferActive = true;
        public static void EndNetworkLevelTransfer() => NetworkLevelTransferActive = false;

        // grace period so OnTriggerEnter cutscenes fired on FixedUpdate after FlyCamActive=false are still suppressed
        const float CutsceneSuppressGraceTime = 0.3f;
        static float _cutsceneSuppressUntil = -1f;

        public static bool SuppressCutsceneTriggers =>
            FlyCamActive || !BaseMapController.BaseMapEnabled || Time.unscaledTime < _cutsceneSuppressUntil;

        // OnTriggerEnter is one-shot so we replay once suppression lifts so swallowed cutscenes still play
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
        static bool _editorScanDone;

        static EnviroSkyRendering _enviroSkyRendering;
        static Il2CppBeautifyEffect.Beautify _beautify;

        static void SetEditorPostProcessing(bool enabled)
        {
            if (_enviroSkyRendering == null || _beautify == null)
            {
                var cam = GameObject.Find("BigManagerPrefab")?.transform.Find("Camera");
                if (cam == null) return;
                _enviroSkyRendering = cam.GetComponent<EnviroSkyRendering>();
                _beautify = cam.GetComponent<Il2CppBeautifyEffect.Beautify>();
            }
            if (_enviroSkyRendering != null) _enviroSkyRendering.enabled = enabled;
            if (_beautify != null) _beautify.enabled = enabled;
        }

        // while true, Core.OnUpdate's Base-Map-off block skips brl.off so TeleportCo can drive chunk loading uncontested
        static bool _farTeleportActive;
        internal static bool FarTeleportActive => _farTeleportActive;

        // anchor for loadingTransform during chunk-grid pre-convergence. never rendered, kept alive across scene loads
        static Transform _teleportLoadAnchor;
        static Transform TeleportLoadAnchor
        {
            get
            {
                if (_teleportLoadAnchor == null)
                {
                    var go = new GameObject("BB_TeleportLoadAnchor");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _teleportLoadAnchor = go.transform;
                }
                return _teleportLoadAnchor;
            }
        }

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

            if (Core.PendingMicroSplatRefreshTime >= 0f
                && Time.realtimeSinceStartup - Core.PendingMicroSplatRefreshTime >= Core.MicroSplatRefreshSettleDelay)
            {
                Core.PendingMicroSplatRefreshTime = -1f;
                MaterialCatalog.RefreshMicroSplatLayerMaterials();

                // rebuild material cache after streaming settles. chunk drain can destroy Material instances props were pointing at
                MaterialVariantTracker.InvalidateMaterialCache();
                MaterialCatalog.EnsureMaterialListLoaded();
                MaterialCatalog.ReapplyAllMaterialOverrides();

                // freshly-resolved materials were never snow suppressed, redo suppression now
                if (!BaseMapController.BaseMapEnabled) BaseMapController.SetEditorPropsSnowDisabled(true);

                LevelEditorManager.Instance?.PruneDestroyedObjects();
                LevelEditor.PruneSelection();
                if (GizmoRenderer.IsReady) GizmoRenderer.RefreshAssets();

                // GhostCollisionCutter.BakeAllColliderCarves(); // prop collider carving disabled, bad implementation
            }

            // block fly/editor toggle during cutscene (player.flyCam ends up null mid cutscene causing NREs)
            if (Input.GetKeyDown(KeyCode.R) && PlayerMovement.me != null
                && !Menu.me.teleporting && !_farTeleportActive && !Core.IsKeyboardCaptured
                && !LevelEditor.IsTypingInUI
                && !PropPalette.IsDragging && !LevelEditor.IsSurfaceSnapDragging)
            {
                if (!PlayerMovement.me.inCutscene) ToggleFlyEditorMode();
            }

            if (Input.GetKeyDown(KeyCode.BackQuote) && PlayerMovement.me != null
                && !Menu.me.teleporting && !_farTeleportActive && !Core.IsKeyboardCaptured)
            {
                if (!PlayerMovement.me.inCutscene) ToggleTeleportMode();
            }

            if (FlyCamActive && !CursorMode && Input.GetKeyDown(KeyCode.G)
                && !Menu.me.teleporting && !_farTeleportActive
                && !Core.IsKeyboardCaptured)
                HandleFarTeleport();

            if (FlyCamActive && !Menu.me.teleporting)
            {
                var player = PlayerMovement.me;
                if (player != null) BestRegionLoader.me.loadingTransform = player.flyCam.transform;
            }

            // RMB in cursor mode: lock cursor while held so mouse look deltas are clean
            if (FlyCamActive && CursorMode)
            {
                if (Input.GetMouseButtonDown(1))
                {
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible = false;
                }
                if (Input.GetMouseButtonUp(1))
                {
                    Cursor.lockState = CursorLockMode.Confined;
                    Cursor.visible = true;
                }
            }

            if (FlyCamActive && CursorMode && !LevelEditor.IsTypingInUI && !Core.IsKeyboardCaptured
                && Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.S))
                SaveLoadWindow.TriggerSave();

            if (FlyCamActive && CursorMode && !LevelEditor.IsTypingInUI && !Core.IsKeyboardCaptured
                && Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.A))
                LevelEditor.SelectAll();

            if (FlyCamActive && CursorMode && !LevelEditor.IsTypingInUI && !Core.IsKeyboardCaptured
                && Input.GetKeyDown(KeyCode.M))
                MaterialInspectorPanel.Toggle();

            if (FlyCamActive && CursorMode) LevelEditor.Update();
        }

        static GUIStyle _teleportLabelStyle;

        public static void OnGUI()
        {
            if (FlyCamActive && CursorMode && (!UI.PropBrowserUI.Ready || Core.DebugMode))
                LevelEditor.OnGUI();

            bool isTeleporting = _farTeleportActive || LevelLoadTeleportActive;
            bool isLoadingLevel = NetworkLevelTransferActive;

            if (isTeleporting || isLoadingLevel)
            {
                if (_teleportLabelStyle == null)
                {
                    _teleportLabelStyle = new GUIStyle(GUI.skin.label)
                    {
                        fontSize = 48,
                        fontStyle = FontStyle.Bold,
                    };
                    _teleportLabelStyle.normal.textColor = Color.white;
                }

                string text = isLoadingLevel && !isTeleporting ? "Loading level..." : "Teleporting...";

                const float w = 400f, h = 60f, margin = 12f;
                var shadow = new Rect(margin + 1, Screen.height - margin - h + 1, w, h);
                var front = new Rect(margin, Screen.height - margin - h, w, h);

                var prev = _teleportLabelStyle.normal.textColor;
                _teleportLabelStyle.normal.textColor = Color.black;
                GUI.Label(shadow, text, _teleportLabelStyle);
                _teleportLabelStyle.normal.textColor = prev;
                GUI.Label(front, text, _teleportLabelStyle);
            }
        }

        // BackQuote key: direct bidirectional game to editor toggle (skips bare fly mode)
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

        public static void InvokeRKeyAction()
        {
            if (PlayerMovement.me == null || Menu.me == null) return;
            if (Menu.me.teleporting || _farTeleportActive) return;
            if (PlayerMovement.me.inCutscene) return;
            ToggleFlyEditorMode();
        }

        // R key: game to fly, then cycles fly to editor
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
                FlyCamActive = true;
                CursorMode = false;
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
                CursorMode = false;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                SetEditorPostProcessing(true);
                FreezePlayer(player, false);

                player.pm.SwitchToActiveMode();
                player.pm.SwitchModes();
                player.OnStandUp();
                LevelEditor.HideGizmo();
                LevelEditor.ClearRemoteSelectionBroadcast();

                // cleared after unfreeze/mode switch so cutscene triggers stay suppressed while player collider reactivates
                FlyCamActive = false;
                _cutsceneSuppressUntil = Time.unscaledTime + CutsceneSuppressGraceTime;

                if (_noiseAmplitude >= 0f)
                {
                    var noise = player.flyCam.GetComponentInChildren<CinemachineBasicMultiChannelPerlin>();
                    if (noise != null) noise.m_AmplitudeGain = _noiseAmplitude;
                }

            }
        }

        // spread scan across two frames. single ~400ms frame tripped Linux compositor freeze detection lol, thanks Cthalla
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
            CursorMode = !CursorMode;
            Cursor.lockState = CursorMode ? CursorLockMode.Confined : CursorLockMode.Locked;
            Cursor.visible = CursorMode;
            if (LevelEditorManager.Instance != null)
                PhysicsObjectManager.SetEditorModeActive(CursorMode && FlyCamActive);
            SetEditorPostProcessing(!CursorMode);
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

                _frozenPlayer = player;
                _freezeActive = true;

                _movementWasEnabled = player.enabled;
                player.enabled = false;

                _frozenController = player.GetComponent<CharacterController>();
                if (_frozenController != null)
                {
                    _controllerWasEnabled = _frozenController.enabled;
                    _frozenController.enabled = false;
                }

                _frozenRigidbody = player.GetComponent<Rigidbody>();
                if (_frozenRigidbody != null)
                {
                    _rigidbodyWasKinematic = _frozenRigidbody.isKinematic;
                    _rigidbodyWasUseGravity = _frozenRigidbody.useGravity;
                    _rigidbodyConstraints = _frozenRigidbody.constraints;
                    _rigidbodyVelocity = _frozenRigidbody.velocity;
                    _rigidbodyAngularVelocity = _frozenRigidbody.angularVelocity;

                    _frozenRigidbody.velocity = Vector3.zero;
                    _frozenRigidbody.angularVelocity = Vector3.zero;
                    _frozenRigidbody.isKinematic = true;
                    _frozenRigidbody.useGravity = false;
                    _frozenRigidbody.constraints = RigidbodyConstraints.FreezeAll;
                }

                _frozenAnimator = player.anim;
                if (_frozenAnimator != null)
                {
                    _animatorSpeed = _frozenAnimator.speed;
                    _frozenAnimator.speed = 0f;
                }

                Physics.SyncTransforms();
                return;
            }

            if (!_freezeActive || _frozenPlayer != player) return;

            if (_frozenAnimator != null) _frozenAnimator.speed = _animatorSpeed;

            if (_frozenRigidbody != null)
            {
                _frozenRigidbody.constraints = _rigidbodyConstraints;
                _frozenRigidbody.isKinematic = _rigidbodyWasKinematic;
                _frozenRigidbody.useGravity = _rigidbodyWasUseGravity;
                _frozenRigidbody.velocity = _rigidbodyVelocity;
                _frozenRigidbody.angularVelocity = _rigidbodyAngularVelocity;
            }

            if (_frozenController != null) _frozenController.enabled = _controllerWasEnabled;

            player.enabled = _movementWasEnabled;

            _freezeActive = false;
            _frozenPlayer = null;
            _frozenController = null;
            _frozenRigidbody = null;
            _frozenAnimator = null;
        }

        // LMB / G key: crosshair teleport. raycast from center screen, call Menu.me.Teleport, restore flycam state. This was a nightmare
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
            var brl = BestRegionLoader.me;

            // if Base Map is off, Core.OnUpdate keeps brl.off=true. flip it off here since OnUpdate skips while FarTeleportActive
            if (!BaseMapController.BaseMapEnabled && brl != null && brl.off)
                brl.off = false;

            FreezePlayer(player, false);

            float facingY = player.flyCam.transform.eulerAngles.y;
            player.anim.transform.rotation = Quaternion.Euler(0f, facingY, 0f);

            player.pm.SwitchToActiveMode();
            player.pm.SwitchModes();

            // SetActive(false) before TeleportCo so unfrozen player can't fall through unloaded terrain. TeleportCo's SetActive(true) re-enables
            player.gameObject.SetActive(false);

            // TeleportCo calls OceanRenderer.Instance.RebuildOcean with no null check. when Base Map off the NRE silently kills it
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

            // pre converge chunkPositions[] grid toward target because TeleportCo only calls brl.Update once so a far grid leaves player in void.
            // LoopChunkMapPositions shifts columns/rows independently, compare whole array each pass to detect completion
            if (brl != null && brl.chunkPositions != null && !brl.off)
            {
                var anchor = TeleportLoadAnchor;
                anchor.position = target;
                brl.loadingTransform = anchor;
                var chunkPositions = brl.chunkPositions;
                var prevPositions = new Vector3[chunkPositions.Length];
                for (int i = 0; i < 64; i++)
                {
                    for (int j = 0; j < chunkPositions.Length; j++) prevPositions[j] = chunkPositions[j];
                    brl.Update();
                    bool changed = false;
                    for (int j = 0; j < chunkPositions.Length; j++)
                        if (chunkPositions[j] != prevPositions[j]) { changed = true; break; }
                    if (!changed) break;
                }
            }

            SaveGod.me.stopSaving = true;
            SaveGod.theSave.continuePt = target;
            Menu.me.Teleport(target);

            // poll downward raycast until terrain at target is present, then override fullyLoaded so TeleportCo exits its wait and places player
            {
                float waitStart = Time.unscaledTime;
                bool slWasTrue = false;
                float slStuckSince = 0f;
                bool targetLoaded = false;

                while (Menu.me.teleporting)
                {
                    float elapsed = Time.unscaledTime - waitStart;

                    // somebodyLoading watchdog: 5s threshold so normal ~3-4s chunk loads are never interrupted
                    if (BestRegionLoader.somebodyLoading)
                    {
                        if (!slWasTrue) { slWasTrue = true; slStuckSince = Time.unscaledTime; }
                        else if (Time.unscaledTime - slStuckSince > 5f)
                        {
                            //MelonLogger.Warning($"[FarTeleport] somebodyLoading stuck" + $" {(Time.unscaledTime - slStuckSince):F1}s — force-clearing"); //Reenable if teleporting still sucks
                            BestRegionLoader.somebodyLoading = false;
                            slWasTrue = false;
                        }
                    }
                    else { slWasTrue = false; }

                    if (!targetLoaded && elapsed >= 1f)
                    {
                        var origin = new Vector3(target.x, target.y + 100f, target.z);
                        if (Physics.Raycast(origin, Vector3.down, 300f, LayerCache.PropTerrainMask))
                        {
                            targetLoaded = true;
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

            }

            if (crestWater != null && !BaseMapController.BaseMapEnabled)
                crestWater.SetActive(false);

            SaveGod.me.stopSaving = false;

            if (brl != null) brl.loadingTransform = player.flyCam.transform;
            FreezePlayer(player, true);

            // second-pass landing check: click raycast may have hit unloaded LOD at wrong height, correct after terrain fully loads
            if (player.gameObject.activeInHierarchy
                && player.torsoRbs != null && player.torsoRbs.Length > 0)
            {
                float playerY = player.torsoRbs[0].transform.position.y;
                var camPos = player.flyCam.transform.position;
                var camToTarget = target - camPos;
                float camDist = camToTarget.magnitude;
                if (camDist > 0.1f && Physics.Raycast(camPos, camToTarget / camDist, out var verifyHit, camDist + 100f, LayerCache.PropTerrainMask))
                {
                    float delta = Mathf.Abs(playerY - verifyHit.point.y);
                    if (delta > 3f)
                    {
                        var corrected = verifyHit.point;
                        FreezePlayer(player, false);
                        player.gameObject.SetActive(false);

                        if (!BaseMapController.BaseMapEnabled && crestWater != null) crestWater.SetActive(true);

                        SaveGod.theSave.continuePt = corrected;
                        Menu.me.Teleport(corrected);

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

                        if (!BaseMapController.BaseMapEnabled && crestWater != null) crestWater.SetActive(false);

                        if (brl != null) brl.loadingTransform = player.flyCam.transform;
                        FreezePlayer(player, true);
                    }
                }
            }

            // freshly loaded terrain can invalidate cached material instances so repoint all placed props
            MaterialVariantTracker.InvalidateMaterialCache();
            MaterialCatalog.EnsureMaterialListLoaded();
            MaterialCatalog.ReapplyAllMaterialOverrides();
            if (!BaseMapController.BaseMapEnabled)
                BaseMapController.SetEditorPropsSnowDisabled(true);

            // clear last. Core.OnUpdate detects true→false to start post-teleport rescan window
            _farTeleportActive = false;
        }
    }
}
