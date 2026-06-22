using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;

namespace BabyBlocks
{
    static class GizmoRenderer
    {
        public const  int Layer = 31;
        public static readonly int Mask = 1 << Layer;

        public const  int   FreeRotateAxis        = 7;
        public const  float FreeRotSphereDrawScale = 0.85f;  // fraction of gizmo scale; must stay inside rings

        const float RingOuterRadius = 0.65f;
        const float RingInnerRadius = 0.50f;
        const float RingPickPadding = 0.05f; // extra click tolerance on either side of the ring band

        public static readonly Quaternion[] PivotRots =
        {
            Quaternion.Euler(  0f, 0f, -90f),  // X
            Quaternion.identity,                // Y
            Quaternion.Euler(-90f, 0f,   0f),  // Z
        };

        static readonly Quaternion[] RingPivotRots =
        {
            Quaternion.Euler(0f,  0f, -90f),  // X: ring in YZ plane
            Quaternion.identity,              // Y: ring in XZ plane
            Quaternion.Euler(90f, 0f,   0f),  // Z: ring in XY plane
        };

        static readonly Vector3 ShaftPos      = new(0f, 0.25f, 0f);
        static readonly Vector3 ShaftScale    = new(0.05f, 0.25f, 0.05f);
        static readonly Vector3 TipPos        = new(0f, 0.50f, 0f);
        static readonly Vector3 TipScale      = new(0.14f, 0.20f, 0.14f);
        static readonly Vector3 ScaleTipPos   = new(0f, 0.55f, 0f);
        static readonly Vector3 ScaleTipScale = new(0.16f, 0.16f, 0.16f);
        static readonly float PlaneSizeMove   = 0.2625f;
        static readonly float PlaneSizeScale  = 0.30f;
        static readonly float PlaneThickness  = 0.03f;

        static Mesh _shaftMesh, _coneTipMesh, _cubeTipMesh, _sphereMesh, _ringMesh;
        static Material[] _mats, _hoverMats;
        static Material[] _planeMats, _planeHoverMats;
        static Material _freeMat, _freeHoverMat;
        static Material _freeRotMat, _freeRotHoverMat;
        // Occluded (checker) pass for each gizmo handle — drawn at queue 4001, after the solid
        // queue-4000 pass. Created in LoadScreenSpaceShaders() once the bundle is available.
        static Material[] _occMats, _planeOccMats;
        static Material   _freeOccMat;

        // Stencil outline: shell cleared to 0, prop footprint marked 1, then shell drawn in two
        // depth passes — LessEqual (solid visible ring) and Greater (semi-transparent through-wall
        // ring) — by toggling unity_GUIZTestMode between draws. No custom shader needed.
        public static float OutlineThickness    = 0.03f;
        public static float OutlineOccludedAlpha = 0.35f; // 0 = invisible through walls, 1 = full color
        public static float OutlineWidth         = 2.0f;  // pixels; controls SS outline border thickness
        public static int   OutlineDebugMode     = 0;     // 0=normal  1=force-dark  2=sceneD grayscale
        static Material _stencilClearMat;
        static Material _stencilMarkMat;
        static Material _outlineMat;     // visible ring (ZTest LessEqual)
        static Material _outlineOccMat;  // through-wall ring (ZTest Greater, low alpha)
        static CommandBuffer _outlineBuffer;
        static Camera _outlineCameraTarget;
        static CommandBuffer _gizmoZTestBuffer; // sets unity_GUIZTestMode=Always before overlay cam geometry pass

        // Screen-space outline: 1px post-process edge, UE4-style checker for occluded regions.
        // Requires babyblocks_shaders.bundle embedded in the assembly (built via ShaderProject).
        // Falls back to the 3D stencil shell above if the bundle is missing or fails to load.
        static Material _maskMat;         // Hidden/BabyBlocks/SelectionMask  — renders objects into mask RT
        static Material _ppMat;           // Hidden/BabyBlocks/SelectionOutline — post-process composite
        static Material _depthCaptureMat; // Hidden/BabyBlocks/DepthCapture — copies depth to plain RFloat RT
        static RenderTexture _depthCopyRT; // plain RFloat copy of scene depth; set on _ppMat once created
        static CommandBuffer _depthCaptureBuffer; // attached to mainCam at AfterEverything
        static Camera _depthCaptureCameraTarget;
        static readonly int _maskRTId         = Shader.PropertyToID("_BabyBlocks_SelectionMaskRT");
        static readonly int _depthCopyTexId   = Shader.PropertyToID("_BabyBlocksDepthCopy");
        static CommandBuffer _ssBuffer;
        static Camera _ssCameraTarget;
        // Cached selection — RefreshSSBufferMatrices() re-records _ssBuffer with
        // fresh Camera.main matrices after Cinemachine LateUpdate has run.
        static IReadOnlyList<LevelEditorObject> _ssLastSelection;

        static bool  _depthDiagPending;

        public static bool ScreenSpaceShadersLoaded => _maskMat != null && _ppMat != null;

        static bool    _outlineTranslateDrag;
        static Vector3 _outlineTranslateDelta;

        public static void SetTranslateDragDelta(Vector3 delta)
        {
            _outlineTranslateDrag  = true;
            _outlineTranslateDelta = delta;
        }

        public static void ClearDragDelta() => _outlineTranslateDrag = false;

        struct MeshData { public Vector3[] verts; public int[] tris; public Vector3[] smoothNormals; }
        struct OutlineShellData
        {
            public int meshId;
            public Vector3 position;
            public Quaternion rotation;
            public Vector3 scale;      // world (lossy) scale — invalidates when parent scales
            public float thickness;
            public Vector3[] worldVerts;
            public int[] tris;
            public Mesh mesh;
        }
        static readonly Dictionary<Mesh, MeshData> _meshDataCache = new();
        static readonly Dictionary<int, OutlineShellData> _outlineShellCache = new();
        static Mesh _combinedOutlineShell;
        static int _combinedOutlineSignature;
        static readonly List<CombineInstance> _combinedOutlineCombines = new();
        static readonly List<(Mesh mesh, Matrix4x4 matrix, int subMeshCount)> _outlineMarks = new();
        static readonly List<Mesh> _remoteOutlineShells = new();
        static readonly List<(Mesh mesh, Matrix4x4 matrix, int subMeshCount)> _remoteOutlineMarks = new();
        static GameObject _root, _arrowHandles, _ringHandles;
        // Single overlay camera (depth=100, ClearFlags.Depth). It is the final camera, so it
        // writes to the real backbuffer. ClearFlags.Depth clears only depth (keeps mainCam's
        // composited scene color), giving the gizmo its own fresh depth buffer: the whole gizmo
        // renders on top of the scene while still depth-testing among its own parts. The SS
        // outline blits at BeforeForwardOpaque (over the scene, under the gizmo geometry pass).
        static Camera _overlayCam;
        static Vector3 _pivotPos;
        static bool _pivotOverrideActive;
        static Vector3 _pivotOverride;

        // Per-axis camera-side flipping: arrow pivots and plane handles always face the camera.
        static readonly Quaternion FlipArrowQ   = Quaternion.Euler(180f, 0f, 0f); // flips +Y→−Y in pivot-local space
        static Transform[]  _axisPivots   = new Transform[3];   // GizmoPivot_i transforms (own arrow collider as child)
        static Transform[]  _planeHandles = new Transform[3];   // plane-handle collider transforms
        static bool[]       _axisFlipped  = new bool[3];        // current per-axis flip state
        static GizmoHandle[] _ringHandleRefs = new GizmoHandle[3]; // GizmoRingCol_i handle components (ring picking is math-based, see RaycastRing)

        public static bool IsReady => _root != null && _overlayCam != null;
        public static Vector3 PivotPosition => _pivotPos;

        // Returns the effective pivot rotation for axis i, accounting for the camera-side flip.
        // PivotRots[i] * FlipArrowQ reverses the arrow direction (used by scale drag logic).
        public static Quaternion GetEffectivePivotRot(int i) =>
            i >= 0 && i < 3 && _axisFlipped[i] ? PivotRots[i] * FlipArrowQ : PivotRots[i];

        public static void SetPivotOverride(Vector3 pivot)
        {
            _pivotOverrideActive = true;
            _pivotOverride = pivot;
        }

        public static void ClearPivotOverride()
        {
            _pivotOverrideActive = false;
        }

        public static void Init()
        {
            InitMeshes();
            InitMaterials();
            BuildColliders();
            // Render order (top → bottom): gizmo, outline, props — all on the one overlay camera.
            // ClearFlags.Depth: keep mainCam's composited color, clear depth so the gizmo sits on
            // top of the scene with correct internal depth. The SS outline blit (BeforeForwardOpaque)
            // draws over the scene color before the gizmo geometry pass draws on top of it.
            _overlayCam = BuildCam(100f, 500000f, CameraClearFlags.Depth);
            AttachGizmoZTestBuffer(_overlayCam);
            LoadScreenSpaceShaders();
        }

        public static void Sync(IReadOnlyList<LevelEditorObject> selection, LevelEditorObject primary,
            LevelEditor.ToolMode tool, Camera mainCam)
        {
            if (_root == null) return;
            bool visible = selection != null && selection.Count > 0;
            _root.SetActive(visible);
            if (_overlayCam != null) _overlayCam.enabled = visible;
            if (!visible) return;

            _pivotPos = _pivotOverrideActive ? _pivotOverride : GetSelectionBoundsCenter(selection);
            _root.transform.position = _pivotPos;
            bool useObjRot = (LevelEditor.LocalMode || tool == LevelEditor.ToolMode.Scale) && primary != null;
            _root.transform.rotation = useObjRot ? primary.transform.rotation : Quaternion.identity;
            float dist = Vector3.Distance(mainCam.transform.position, _root.transform.position);
            _root.transform.localScale = Vector3.one * Mathf.Max(dist * 0.14f, 0.02f);

            bool rotating = tool == LevelEditor.ToolMode.Rotate;
            if (_arrowHandles != null) _arrowHandles.SetActive(!rotating);
            if (_ringHandles  != null) _ringHandles.SetActive(rotating);

            // Per-axis camera-side flip
            // For each arrow axis: if the arrow points away from the camera, flip it
            // 180° so the handle is always on the camera-facing side.
            // Rotate mode uses world-aligned rings and is never flipped.
            for (int i = 0; i < 3; i++)
            {
                if (!rotating && _axisPivots[i] != null)
                {
                    var arrowWorldDir = _root.transform.rotation * (PivotRots[i] * Vector3.up);
                    _axisFlipped[i]   = Vector3.Dot(arrowWorldDir, mainCam.transform.position - _pivotPos) < 0;
                    _axisPivots[i].localRotation = GetEffectivePivotRot(i);
                }
                else
                {
                    _axisFlipped[i] = false;
                    if (_axisPivots[i] != null) _axisPivots[i].localRotation = PivotRots[i];
                }
            }
            // Move each plane handle to the diagonal of its two (possibly flipped) axes.
            for (int pi = 0; pi < 3; pi++)
            {
                if (_planeHandles[pi] == null) continue;
                int aIdx  = pi == 0 ? 0 : pi == 1 ? 1 : 0;
                int bIdx  = pi == 0 ? 1 : pi == 1 ? 2 : 2;
                var signA = _axisFlipped[aIdx] ? -1f : 1f;
                var signB = _axisFlipped[bIdx] ? -1f : 1f;
                var dirA  = (PivotRots[aIdx] * Vector3.up) * signA;
                var dirB  = (PivotRots[bIdx] * Vector3.up) * signB;
                _planeHandles[pi].localPosition = (dirA + dirB) * PlaneSizeMove * 0.5f;
            }

            SyncCamToMain(_overlayCam, mainCam);
        }

        static void SyncCamToMain(Camera cam, Camera mainCam)
        {
            if (cam == null) return;
            cam.transform.SetPositionAndRotation(mainCam.transform.position, mainCam.transform.rotation);
            cam.fieldOfView   = mainCam.fieldOfView;
            cam.aspect        = mainCam.aspect;
            cam.nearClipPlane = mainCam.nearClipPlane;
            cam.rect          = mainCam.rect;
        }

        // WARNING: Camera recovery — modify with caution.
        // In the past, attempting to rebuild or reconfigure the overlay camera
        // inside Sync() (called every frame) caused erratic behaviour when
        // teleporting long distances: native objects stopped loading in and the
        // player was flung far away. The safe pattern is:
        //   • Only rebuild when _overlayCam is actually null (destroyed externally).
        //   • Do NOT touch cullingMask, depth, or farClipPlane every frame.
        //   • Keep this call outside of Sync() — LevelEditor.Update() calls it
        //     once per frame before Sync() runs.
        public static void EnsureCamera()
        {
            if (_root != null && _overlayCam == null)
            {
                _overlayCam = BuildCam(100f, 500000f, CameraClearFlags.Depth);
                _gizmoZTestBuffer = null; // old buffer was on the destroyed cam; rebuild below
                AttachGizmoZTestBuffer(_overlayCam);
            }
        }

        static void AttachGizmoZTestBuffer(Camera cam)
        {
            if (cam == null || _gizmoZTestBuffer != null) return;
            _gizmoZTestBuffer = new CommandBuffer { name = "BabyBlocks_GizmoZTest" };
            // UI/Default shaders (the freeRot sphere) use ZTest [unity_GUIZTestMode], which is
            // otherwise uninitialized on a custom camera.  The gizmo cam clears its depth buffer,
            // so LessEqual gives correct depth-testing among gizmo parts while the whole gizmo
            // still sits on top of the scene (its depth starts fresh).
            _gizmoZTestBuffer.SetGlobalFloat("unity_GUIZTestMode", (float)CompareFunction.LessEqual);
            cam.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, _gizmoZTestBuffer);
            MelonLoader.MelonLogger.Msg($"[GizmoRenderer] Attached GizmoZTest buffer to {cam.name}");
        }

        // Some cached gizmo meshes/materials (_mats, _freeMat, _coneTipMesh, the outline
        // stencil/outline materials, etc.) are plain assets held alive only by these static
        // fields — no GameObject/scene reference keeps them around. The scene-load burst
        // triggered by "load a different save" runs Unity's unused-asset cleanup, which can
        // destroy these even though a managed reference to them still exists, leaving
        // Graphics.DrawMesh / the outline CommandBuffer silently drawing with destroyed
        // materials/meshes. _root/_overlayCam/colliders are DontDestroyOnLoad GameObjects and
        // survive that cleanup, so only the meshes/materials need regenerating.
        internal static void RefreshAssets()
        {
            InitMeshes();
            InitMaterials();
            // Re-create screen-space materials if they were destroyed by asset cleanup.
            // The shaders survive (Unload(false)), so just recreate the materials.
            if (_maskMat == null || _ppMat == null)
                LoadScreenSpaceShaders();
            // Re-create gizmo occluded materials if they were separately destroyed.
            if (_occMats == null && _maskMat != null)
            {
                var occShader = Shader.Find("Hidden/BabyBlocks/GizmoOccluded");
                if (occShader != null) BuildGizmoOccMats(occShader);
            }
        }

        public static void SetActive(bool on)
        {
            if (_root       != null) _root.SetActive(on);
            if (_overlayCam != null) _overlayCam.enabled = on;
            if (!on)
            {
                DetachOutlineBuffer();
                DetachSSBuffer();
            }
        }

        static void DetachOutlineBuffer()
        {
            if (_outlineCameraTarget != null && _outlineBuffer != null)
            {
                _outlineCameraTarget.RemoveCommandBuffer(CameraEvent.AfterEverything, _outlineBuffer);
                _outlineCameraTarget = null;
            }
        }

        static void DetachSSBuffer()
        {
            if (_ssCameraTarget != null && _ssBuffer != null)
            {
                _ssCameraTarget.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, _ssBuffer);
                _ssCameraTarget = null;
            }
            _ssLastSelection = null;
        }


        static void LoadScreenSpaceShaders()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                // Resource name: RootNamespace + "." + path with backslashes replaced by dots
                using var stream = asm.GetManifestResourceStream("BabyBlocks.Shaders.babyblocks_shaders.bundle");
                if (stream == null)
                {
                    // Log all available resources so we can diagnose naming mismatches
                    var names = asm.GetManifestResourceNames();
                    MelonLoader.MelonLogger.Warning(
                        $"[BabyBlocks] babyblocks_shaders.bundle not in embedded resources. Found: {string.Join(", ", names)}");
                    return;
                }

                var bytes = new byte[stream.Length];
                stream.Read(bytes, 0, bytes.Length);
                var bundle = AssetBundle.LoadFromMemory(bytes);
                if (bundle == null)
                {
                    MelonLoader.MelonLogger.Warning("[BabyBlocks] AssetBundle.LoadFromMemory returned null.");
                    return;
                }

                Shader maskShader = null, ppShader = null, depthCaptureShader = null;
                Shader gizmoOccShader = null;
                var allAssets = bundle.LoadAllAssets();
                if (allAssets != null)
                {
                    foreach (var asset in allAssets)
                    {
                        if (asset == null) continue;
                        var s = asset.TryCast<Shader>();
                        if (s == null) continue;
                        if (s.name.Contains("SelectionMask"))    maskShader         = s;
                        if (s.name.Contains("SelectionOutline")) ppShader           = s;
                        if (s.name.Contains("DepthCapture"))     depthCaptureShader = s;
                        if (s.name.Contains("GizmoOccluded"))    gizmoOccShader     = s;
                    }
                }
                bundle.Unload(false);

                if (maskShader == null || ppShader == null || depthCaptureShader == null)
                {
                    MelonLoader.MelonLogger.Warning(
                        $"[BabyBlocks] Could not find shaders in bundle. " +
                        $"mask={maskShader?.name ?? "null"} pp={ppShader?.name ?? "null"} " +
                        $"depthCapture={depthCaptureShader?.name ?? "null"}");
                    return;
                }

                _maskMat         = new Material(maskShader)         { name = "BabyBlocks_SelectionMask" };
                _ppMat           = new Material(ppShader)           { name = "BabyBlocks_SelectionOutline" };
                _depthCaptureMat = new Material(depthCaptureShader) { name = "BabyBlocks_DepthCapture" };
                _ppMat.SetColor("_OutlineColor",  new Color(1f, 0.85f, 0.1f, 1f));
                _ppMat.SetFloat("_OccludedAlpha", OutlineOccludedAlpha);

                // Create the plain RFloat depth copy RT (viewport-sized; recreated in
                // AttachDepthCaptureBuffer if the camera pixel size differs).
                _depthCopyRT = new RenderTexture(1630, 1080, 0, RenderTextureFormat.RFloat)
                {
                    name = "BabyBlocks_DepthCopy",
                    filterMode = FilterMode.Point,
                };
                _depthCopyRT.Create();
                _ppMat.SetTexture(_depthCopyTexId, _depthCopyRT);
                // Expose depth copy globally so GizmoOccluded materials can all sample it
                // without needing SetTexture on each one individually.
                Shader.SetGlobalTexture("_BabyBlocksDepthCopy", _depthCopyRT);

                if (gizmoOccShader != null)
                    BuildGizmoOccMats(gizmoOccShader);
                else
                    MelonLoader.MelonLogger.Warning("[BabyBlocks] GizmoOccluded shader not found in bundle.");

                MelonLoader.MelonLogger.Msg($"[BabyBlocks] Screen-space outline shaders loaded. " +
                    $"maskMat={_maskMat != null} ppMat={_ppMat != null} " +
                    $"depthCaptureMat={_depthCaptureMat != null} gizmoOccMats={_occMats != null}");
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Warning($"[BabyBlocks] Failed to load screen-space shaders: {ex}");
            }
        }

        static void BuildGizmoOccMats(Shader shader)
        {
            const int OccQueue = 4001; // one above the solid gizmo pass (4000)

            var axisColors = new Color[]
            {
                new Color(0.90f, 0.20f, 0.20f),
                new Color(0.20f, 0.80f, 0.20f),
                new Color(0.20f, 0.45f, 1.00f),
            };
            var planeColors = new Color[]
            {
                axisColors[2], // XY
                axisColors[0], // YZ
                axisColors[1], // XZ
            };

            _occMats = new Material[3];
            for (int i = 0; i < 3; i++)
                _occMats[i] = MakeOccMat(shader, axisColors[i], OccQueue);

            _freeOccMat = MakeOccMat(shader, new Color(0.72f, 0.72f, 0.72f), OccQueue);

            _planeOccMats = new Material[3];
            for (int i = 0; i < 3; i++)
                _planeOccMats[i] = MakeOccMat(shader, planeColors[i], OccQueue);
        }

        static Material MakeOccMat(Shader shader, Color color, int queue)
        {
            var m = new Material(shader) { renderQueue = queue };
            m.SetColor("_Color", color);
            return m;
        }

        // Call from input handler (F5) to dump the full gizmo/outline pipeline snapshot to the log
        // on the next recorded SS frame (see DumpDiagnostics). Requires an active selection.
        internal static void LogDepthDiag() => _depthDiagPending = true;

        static void DetachDepthCaptureBuffer()
        {
            if (_depthCaptureCameraTarget != null && _depthCaptureBuffer != null)
            {
                _depthCaptureCameraTarget.RemoveCommandBuffer(CameraEvent.AfterEverything, _depthCaptureBuffer);
                _depthCaptureCameraTarget = null;
            }
        }

        // Attaches a CommandBuffer to mainCam that copies the scene depth into _depthCopyRT.
        // Runs at AfterEverything so _CameraDepthTexture is correctly bound by Unity's pipeline.
        // The capture mat uses UNITY_DECLARE_DEPTH_TEXTURE which resolves the right DX11 sampler
        // type — this is why we can't just do Material.SetTexture with the deferred depth directly.
        static void AttachDepthCaptureBuffer(Camera mainCam)
        {
            DetachDepthCaptureBuffer();
            if (_depthCaptureMat == null || _depthCopyRT == null) return;

            // Recreate RT if camera pixel size changed
            if (_depthCopyRT.width != mainCam.pixelWidth || _depthCopyRT.height != mainCam.pixelHeight)
            {
                _depthCopyRT.Release();
                _depthCopyRT.width  = mainCam.pixelWidth;
                _depthCopyRT.height = mainCam.pixelHeight;
                _depthCopyRT.Create();
                _ppMat.SetTexture(_depthCopyTexId, _depthCopyRT);
                Shader.SetGlobalTexture("_BabyBlocksDepthCopy", _depthCopyRT);
            }

            if (_depthCaptureBuffer == null)
                _depthCaptureBuffer = new CommandBuffer { name = "BabyBlocks_DepthCapture" };
            else
                _depthCaptureBuffer.Clear();

            _depthCaptureBuffer.Blit(BuiltinRenderTextureType.None, _depthCopyRT, _depthCaptureMat);

            mainCam.AddCommandBuffer(CameraEvent.AfterEverything, _depthCaptureBuffer);
            _depthCaptureCameraTarget = mainCam;
        }

        static void DrawOutlineScreenSpace(IReadOnlyList<LevelEditorObject> selection, Camera mainCam)
        {
            DetachSSBuffer();

            if (selection == null || selection.Count == 0 || mainCam == null) return;
            if (!ScreenSpaceShadersLoaded) return;
            if (_overlayCam == null) return;

            // Request depth texture from main camera so _CameraDepthTexture is populated.
            mainCam.depthTextureMode |= DepthTextureMode.Depth;

            // Depth capture buffer: runs at AfterEverything on mainCam, copies scene depth into
            // _depthCopyRT (plain RFloat) via the DepthCapture shader.  Must run before the SS
            // outline buffer (which runs on _overlayCam, depth=100, after mainCam).
            AttachDepthCaptureBuffer(mainCam);

            _ssLastSelection = selection;

            if (_ssBuffer == null)
                _ssBuffer = new CommandBuffer { name = "BabyBlocks_SSOutline" };

            RecordSSBuffer(selection, mainCam);

            // Attach at BeforeForwardAlpha on _overlayCam (deferred pipeline).
            // The gizmo handle materials are renderQueue 4000 (transparent), so they render in the
            // camera's forward-alpha pass. BeforeForwardAlpha runs right before that pass but AFTER
            // the deferred opaque+skybox composite — so the blit lands on a valid composited color
            // buffer (unlike BeforeForwardOpaque, which in deferred is too early / off-target), and
            // the gizmo's forward-alpha draws land on top of the outline. Top→bottom: gizmo,
            // outline, props. RecordSSBuffer restores camera matrices at its end so the gizmo pass
            // isn't left with Blit's fullscreen-ortho state.
            _overlayCam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, _ssBuffer);
            _ssCameraTarget = _overlayCam;

            if (_depthDiagPending)
            {
                _depthDiagPending = false;
                DumpDiagnostics(mainCam, selection);
            }
        }

        // Called by OverlayCamPreRenderHook right before _overlayCam renders.
        // Called from CinemachineBrainLateUpdatePatch after Cinemachine LateUpdate completes,
        // so Camera.main.worldToCameraMatrix is current for this frame.
        // Re-recording here eliminates the 1-frame stale drift seen when recording in Update().
        internal static void RefreshSSBufferMatrices()
        {
            if (_ssBuffer == null || _ssCameraTarget == null) return;
            if (!ScreenSpaceShadersLoaded) return;
            if (_ssLastSelection == null || _ssLastSelection.Count == 0) return;

            var mainCam = Camera.main;
            if (mainCam == null) return;

            RecordSSBuffer(_ssLastSelection, mainCam);
        }

        // One-shot pipeline dump (triggered by F5 → LogDepthDiag). Prints a compact, copy-pasteable
        // snapshot of the gizmo/outline render setup: cameras, clear flags, render path, attached
        // command buffers, and a scene-depth readback. Designed to diagnose outline visibility and
        // gizmo layering without per-frame log spam.
        static void DumpDiagnostics(Camera mainCam, IReadOnlyList<LevelEditorObject> selection)
        {
            void L(string s) => MelonLoader.MelonLogger.Msg("[GIZMO-DIAG] " + s);

            L("================ Gizmo / Outline pipeline ================");
            L($"shaders: mask={_maskMat != null} pp={_ppMat != null} depthCapture={_depthCaptureMat != null}  " +
              $"selection={selection?.Count ?? 0}");

            // Cameras, sorted by render order (depth ascending = renders first).
            try
            {
                var cams = Camera.allCameras;
                var list = new List<Camera>();
                if (cams != null) foreach (var c in cams) if (c != null) list.Add(c);
                list.Sort((a, b) => a.depth.CompareTo(b.depth));
                L($"camera render order ({list.Count} enabled), first→last:");
                foreach (var c in list)
                {
                    bool isMain    = c == mainCam;
                    bool isOverlay = c == _overlayCam;
                    string tag = isOverlay ? " <-- OVERLAY" : isMain ? " <-- MAIN" : "";
                    L($"  depth={c.depth,6:0.#}  '{c.name}'  clear={c.clearFlags}  " +
                      $"path={c.actualRenderingPath}  HDR={c.allowHDR}  " +
                      $"target={(c.targetTexture != null ? c.targetTexture.name : "backbuffer")}{tag}");
                }
            }
            catch (Exception ex) { L($"camera enumerate failed: {ex.Message}"); }

            // Command buffers attached to the overlay cam (where outline + gizmo ztest live).
            DumpCamBuffers(L, _overlayCam, "overlayCam", CameraEvent.BeforeForwardOpaque);
            DumpCamBuffers(L, _overlayCam, "overlayCam", CameraEvent.BeforeForwardAlpha);
            DumpCamBuffers(L, _overlayCam, "overlayCam", CameraEvent.AfterEverything);
            // Depth capture lives on mainCam.
            DumpCamBuffers(L, mainCam, "mainCam", CameraEvent.AfterEverything);

            L($"ssTarget={(_ssCameraTarget != null ? _ssCameraTarget.name : "null")}  " +
              $"depthCaptureTarget={(_depthCaptureCameraTarget != null ? _depthCaptureCameraTarget.name : "null")}");
            L($"depthCopyRT={(_depthCopyRT != null ? $"{_depthCopyRT.width}x{_depthCopyRT.height} created={_depthCopyRT.IsCreated()}" : "null")}  " +
              $"mainCam.depthMode={mainCam.depthTextureMode}");

            var globalDepth = Shader.GetGlobalTexture("_CameraDepthTexture");
            L($"_CameraDepthTexture global={(globalDepth != null ? $"{globalDepth.width}x{globalDepth.height}" : "null")}");

            // Scene-depth readback at the first selected object (verifies depth capture works).
            if (selection != null && selection.Count > 0 && selection[0] != null && _depthCopyRT != null && _depthCopyRT.IsCreated())
            {
                try
                {
                    var r = mainCam.rect;
                    var sp = mainCam.WorldToScreenPoint(selection[0].transform.position);
                    float depUvX = (sp.x / Screen.width  - r.x) / r.width;
                    float depUvY = (sp.y / Screen.height - r.y) / r.height;
                    int px = Mathf.Clamp(Mathf.RoundToInt(depUvX * _depthCopyRT.width),  0, _depthCopyRT.width  - 1);
                    int py = Mathf.Clamp(Mathf.RoundToInt(depUvY * _depthCopyRT.height), 0, _depthCopyRT.height - 1);
                    var prev = RenderTexture.active;
                    RenderTexture.active = _depthCopyRT;
                    var t2d = new Texture2D(1, 1, TextureFormat.RFloat, false);
                    t2d.ReadPixels(new Rect(px, py, 1, 1), 0, 0);
                    t2d.Apply();
                    RenderTexture.active = prev;
                    float d = t2d.GetPixel(0, 0).r;
                    UnityEngine.Object.Destroy(t2d);
                    L($"sel[0] depthCopyRT[{px},{py}]={d:F4} (reversed-Z: near=1 far=0; 0=capture broken)");
                }
                catch (Exception ex) { L($"depth readback failed: {ex.Message}"); }
            }
            L("=========================================================");
        }

        static void DumpCamBuffers(Action<string> L, Camera cam, string label, CameraEvent evt)
        {
            if (cam == null) { L($"{label}.{evt}: <cam null>"); return; }
            try
            {
                var bufs = cam.GetCommandBuffers(evt);
                int n = bufs?.Length ?? 0;
                string names = "";
                if (n > 0) foreach (var b in bufs) names += (names.Length > 0 ? ", " : "") + b.name;
                L($"{label}.{evt}: {n} buffer(s){(n > 0 ? " [" + names + "]" : "")}");
            }
            catch (Exception ex) { L($"{label}.{evt}: query failed: {ex.Message}"); }
        }

        // Records (or re-records) _ssBuffer with the given camera's matrices.
        // Caller must ensure _ssBuffer is not null.
        static void RecordSSBuffer(IReadOnlyList<LevelEditorObject> selection, Camera mainCam)
        {
            _ssBuffer.Clear();
            _ppMat.SetFloat("_OccludedAlpha", OutlineOccludedAlpha);
            _ppMat.SetFloat("_OutlineWidth",  OutlineWidth);
            _ppMat.SetFloat("_DebugMode",     OutlineDebugMode);
            // Eye-depth occlusion: shaders convert hw_depth → eye_dist via near/hwDepth.
            // Set globally so GizmoOccluded materials (not managed by _ppMat) also get it.
            Shader.SetGlobalFloat("_BabyBlocksCamNear", mainCam.nearClipPlane);
            // Mask and depth copy are both camera-pixel-sized (mainCam.pixelWidth × pixelHeight).
            // The HDR intermediate buffer that CameraTarget resolves to in deferred+HDR mode is
            // ALSO camera-pixel-sized, so the Blit maps 1:1 with no scale or offset shift.
            // Using Screen.width/Height would create a larger RT that gets scaled down by the
            // Blit, causing an alignment shift proportional to (1 - pixelWidth/Screen.width).
            // Since mask UV and depth UV are now in the same camera-pixel coordinate space,
            // _ViewportRect is identity — no remapping needed.
            _ppMat.SetVector("_ViewportRect", new Vector4(0f, 0f, 1f, 1f));
            // Scene depth is read via _depthCopyRT (plain RFloat), populated by
            // _depthCaptureBuffer running at AfterEverything on mainCam before us.
            // No per-frame snapshot needed here.

            // Pass 1: render selected objects into a camera-sized mask RT.
            // Using mainCam.pixelWidth/Height so the RT matches the camera HDR buffer exactly.
            // SetViewport covers the full RT (no screen-offset) because the camera projection
            // matrix already maps the viewport frustum to NDC [-1,1]; the GPU viewport then
            // maps that to mask pixels 0→pixelWidth/Height with no left-margin offset.
            // Using mainCam.projectionMatrix (not GL.GetGPUProjectionMatrix) lets DX render
            // Y-flipped into the RT; the SelectionOutline shader corrects via TexelSize.y < 0.
            _ssBuffer.GetTemporaryRT(_maskRTId, mainCam.pixelWidth, mainCam.pixelHeight, 24, FilterMode.Bilinear, RenderTextureFormat.ARGBFloat);
            _ssBuffer.SetRenderTarget(_maskRTId);
            _ssBuffer.ClearRenderTarget(true, true, Color.clear);
            _ssBuffer.SetViewport(new Rect(0, 0, mainCam.pixelWidth, mainCam.pixelHeight));
            _ssBuffer.SetViewProjectionMatrices(mainCam.worldToCameraMatrix, mainCam.projectionMatrix);

            _maskMat.SetColor("_Color", new Color(1f, 0.85f, 0.1f, 1f));
            for (int s = 0; s < selection.Count; s++)
            {
                var sel = selection[s];
                if (sel == null) continue;
                var mfs = sel.GetComponentsInChildren<MeshFilter>();
                if (mfs == null) continue;
                for (int i = 0; i < mfs.Length; i++)
                {
                    var mf = mfs[i];
                    if (mf == null || mf.sharedMesh == null) continue;
                    var mr = mf.GetComponent<MeshRenderer>();
                    if (mr == null || !mr.enabled) continue;
                    var mesh   = mf.sharedMesh;
                    var matrix = mf.transform.localToWorldMatrix;
                    for (int sub = 0; sub < mesh.subMeshCount; sub++)
                        _ssBuffer.DrawMesh(mesh, matrix, _maskMat, sub);
                }
            }

            // Pass 2: Blit mask onto CameraTarget.
            // Blit maps the source RT dimensions 1:1 to screen (ignores camera viewport rect),
            // so the mask must be full-screen-sized (Screen.width × Screen.height) for the Blit
            // UV to map correctly to screen pixels without any scale or offset shift.
            _ssBuffer.Blit(_maskRTId, BuiltinRenderTextureType.CameraTarget, _ppMat);
            _ssBuffer.ReleaseTemporaryRT(_maskRTId);

            // Restore the camera's view/projection (Blit leaves a fullscreen-ortho matrix in the
            // command-buffer state). The gizmo geometry pass (queue 4000, forward-alpha) renders
            // right after BeforeForwardAlpha — without this it would draw with the leftover ortho
            // matrix. _overlayCam mirrors mainCam, so mainCam's matrices are the correct ones.
            _ssBuffer.SetViewProjectionMatrices(mainCam.worldToCameraMatrix, mainCam.projectionMatrix);
        }

        public static GizmoHandle RaycastHandle(Ray ray)
        {
            GizmoHandle closest   = null;
            GizmoHandle freeRot   = null;   // axis 7: only wins if no ring is hit
            float       closestDist = float.MaxValue;

            // Rotation rings are picked via plane/annulus math rather than their
            // MeshColliders. PhysX needs to re-cook a MeshCollider whenever its transform
            // scale changes, and _root is rescaled every frame to keep the gizmo a constant
            // screen size — that re-cook intermittently lags a frame or more behind, making
            // RaycastAll randomly miss the rings (the free-rotate sphere and the move/scale
            // arrows use Sphere/BoxColliders, which don't need re-cooking, so they stay
            // reliable). Doing the math directly sidesteps the lag entirely.
            if (_ringHandles != null && _ringHandles.activeSelf)
            {
                var origin = _root.transform.position;
                var rot    = _root.transform.rotation;
                float s    = _root.transform.localScale.x;

                for (int i = 0; i < 3; i++)
                {
                    if (RaycastRing(ray, origin, rot * RingPivotRots[i], s, out float dist) && dist < closestDist)
                    {
                        closestDist = dist;
                        closest = _ringHandleRefs[i];
                    }
                }
            }

            var hits = Physics.RaycastAll(ray, 2000f, Mask);
            if (hits != null)
            {
                foreach (var hit in hits)
                {
                    var h = hit.collider.GetComponent<GizmoHandle>();
                    if (h == null) continue;
                    if (h.axisIndex == 3)           return h;               // center free: always wins
                    if (h.axisIndex == FreeRotateAxis) { freeRot = h; continue; } // defer; rings take priority
                    if (hit.distance < closestDist) { closestDist = hit.distance; closest = h; }
                }
            }
            return closest ?? freeRot;
        }

        // Ray-vs-annulus test for a rotation ring lying in the plane through `center` with
        // normal = ringRot * Vector3.up, matching the geometry built by BuildRingMesh and
        // drawn in Draw(). `scale` is the gizmo's uniform world scale (_root.localScale.x).
        static bool RaycastRing(Ray ray, Vector3 center, Quaternion ringRot, float scale, out float dist)
        {
            dist = 0f;
            var normal = ringRot * Vector3.up;
            float denom = Vector3.Dot(ray.direction, normal);
            if (Mathf.Abs(denom) < 1e-5f) return false; // ray parallel to ring plane

            float t = Vector3.Dot(center - ray.origin, normal) / denom;
            if (t < 0f) return false;

            var hitPoint   = ray.origin + ray.direction * t;
            float hitRadius = Vector3.Distance(hitPoint, center);

            float outer = (RingOuterRadius + RingPickPadding) * scale;
            float inner = (RingInnerRadius - RingPickPadding) * scale;
            if (hitRadius < inner || hitRadius > outer) return false;

            dist = t;
            return true;
        }


        public static void Draw(int hoveredAxis, LevelEditor.ToolMode tool)
        {
            if (_root == null || !_root.activeSelf || _overlayCam == null) return;
            if (_shaftMesh == null || _coneTipMesh == null || _cubeTipMesh == null) return;
            if (_sphereMesh == null || _ringMesh == null) return;

            var   origin = _root.transform.position;
            float s      = _root.transform.localScale.x;

            if (tool == LevelEditor.ToolMode.Rotate)
            {
                // Free-rotate sphere drawn first (lower queue) so the rings render on top.
                if (_freeRotMat != null)
                {
                    var frMat = (hoveredAxis == FreeRotateAxis && _freeRotHoverMat != null)
                        ? _freeRotHoverMat : _freeRotMat;
                    Graphics.DrawMesh(_sphereMesh,
                        Matrix4x4.TRS(origin, Quaternion.identity, Vector3.one * FreeRotSphereDrawScale * s),
                        frMat, Layer, _overlayCam);
                }
                var ringBaseRot = _root.transform.rotation;
                for (int i = 0; i < 3; i++)
                {
                    var mat = (hoveredAxis == i && _hoverMats != null) ? _hoverMats[i] : _mats[i];
                    var ringMatrix = Matrix4x4.TRS(origin, ringBaseRot * RingPivotRots[i], Vector3.one * s);
                    Graphics.DrawMesh(_ringMesh, ringMatrix, mat, Layer, _overlayCam);
                    if (_occMats != null)
                        Graphics.DrawMesh(_ringMesh, ringMatrix, _occMats[i], Layer, _overlayCam);
                }
                return;
            }

            bool scaleMode = tool == LevelEditor.ToolMode.Scale;
            var  tipMesh   = scaleMode ? _cubeTipMesh  : _coneTipMesh;
            var  tipPos    = scaleMode ? ScaleTipPos   : TipPos;
            var  tipScale  = scaleMode ? ScaleTipScale : TipScale;
            var  planeSize  = scaleMode ? PlaneSizeScale : PlaneSizeMove;
            var  planeScaleXY = new Vector3(planeSize, planeSize, PlaneThickness);
            var  planeScaleYZ = new Vector3(PlaneThickness, planeSize, planeSize);
            var  planeScaleXZ = new Vector3(planeSize, PlaneThickness, planeSize);

            var rootRot = _root.transform.rotation;
            for (int i = 0; i < 3; i++)
            {
                var rot = rootRot * GetEffectivePivotRot(i);   // flips toward camera when needed
                var mat = (hoveredAxis == i && _hoverMats != null) ? _hoverMats[i] : _mats[i];
                var shaftMatrix = Matrix4x4.TRS(origin + rot * (ShaftPos * s), rot, ShaftScale * s);
                var tipMatrix   = Matrix4x4.TRS(origin + rot * (tipPos   * s), rot, tipScale   * s);

                Graphics.DrawMesh(_shaftMesh, shaftMatrix, mat, Layer, _overlayCam);
                Graphics.DrawMesh(tipMesh,   tipMatrix,   mat, Layer, _overlayCam);
                if (_occMats != null)
                {
                    Graphics.DrawMesh(_shaftMesh, shaftMatrix, _occMats[i], Layer, _overlayCam);
                    Graphics.DrawMesh(tipMesh,   tipMatrix,   _occMats[i], Layer, _overlayCam);
                }
            }

            // Center free handle drawn after the axes so it depth-tests against them (an axis
            // passing in front correctly occludes the sphere/cube).  queue 4000, same as axes.
            var freeMat = (hoveredAxis == 3 && _freeHoverMat != null) ? _freeHoverMat : _freeMat;
            var freeMesh = scaleMode ? _cubeTipMesh : _sphereMesh;
            var freeRot = scaleMode ? rootRot : Quaternion.identity;
            var freeMatrix = Matrix4x4.TRS(origin, freeRot, Vector3.one * 0.22f * s);
            if (freeMat != null)
            {
                Graphics.DrawMesh(freeMesh, freeMatrix, freeMat, Layer, _overlayCam);
                if (_freeOccMat != null)
                    Graphics.DrawMesh(freeMesh, freeMatrix, _freeOccMat, Layer, _overlayCam);
            }

            if (_planeMats != null && _planeHoverMats != null)
            {
                for (int i = 0; i < 3; i++)
                {
                    int axisIndex = 4 + i;
                    var mat = (hoveredAxis == axisIndex) ? _planeHoverMats[i] : _planeMats[i];
                    if (mat == null) continue;

                    // Compute plane handle position as the diagonal of its two (possibly flipped) axes.
                    int aIdx = i == 0 ? 0 : i == 1 ? 1 : 0;
                    int bIdx = i == 0 ? 1 : i == 1 ? 2 : 2;
                    var dirA      = GetEffectivePivotRot(aIdx) * Vector3.up;
                    var dirB      = GetEffectivePivotRot(bIdx) * Vector3.up;
                    Vector3 localPos  = (dirA + dirB) * planeSize * 0.5f;
                    Vector3 localSize = i == 0 ? planeScaleXY : i == 1 ? planeScaleYZ : planeScaleXZ;
                    var planeMatrix = Matrix4x4.TRS(origin + rootRot * (localPos * s), rootRot, localSize * s);

                    Graphics.DrawMesh(_cubeTipMesh, planeMatrix, mat, Layer, _overlayCam);
                    if (_planeOccMats != null)
                        Graphics.DrawMesh(_cubeTipMesh, planeMatrix, _planeOccMats[i], Layer, _overlayCam);
                }
            }
        }

        // Legacy 3D-shell outline: single ZTest=Always pass, no through-wall distinction.
        // Kept as fallback reference; active path is DrawOutline below.
        public static void DrawOutline_Legacy(IReadOnlyList<LevelEditorObject> selection, Camera mainCam)
        {
            DetachOutlineBuffer();

            if (selection == null || selection.Count == 0 || mainCam == null) return;
            if (_stencilClearMat == null || _stencilMarkMat == null || _outlineMat == null) return;

            if (_outlineBuffer == null)
                _outlineBuffer = new CommandBuffer { name = "PropOutline" };
            else
                _outlineBuffer.Clear();

            _outlineBuffer.SetGlobalFloat("unity_GUIZTestMode", (float)CompareFunction.Always);

            // Translate drag fast path
            // Reuse the cached combined shell drawn at a translation offset.
            // Stencil marks are refreshed (cheap component lookups, no mesh work).
            if (_outlineTranslateDrag
                && _combinedOutlineShell != null && _combinedOutlineShell.vertexCount > 0)
            {
                _outlineMarks.Clear();
                CollectOutlineMarks(selection);
                if (_outlineMarks.Count > 0)
                {
                    var tMat = Matrix4x4.Translate(_outlineTranslateDelta);
                    _outlineBuffer.DrawMesh(_combinedOutlineShell, tMat, _stencilClearMat);
                    for (int i = 0; i < _outlineMarks.Count; i++)
                    {
                        var mark = _outlineMarks[i];
                        for (int sub = 0; sub < mark.subMeshCount; sub++)
                            _outlineBuffer.DrawMesh(mark.mesh, mark.matrix, _stencilMarkMat, sub);
                    }
                    _outlineBuffer.DrawMesh(_combinedOutlineShell, tMat, _outlineMat);
                }
                _outlineBuffer.SetGlobalFloat("unity_GUIZTestMode", (float)CompareFunction.LessEqual);
                mainCam.AddCommandBuffer(CameraEvent.AfterEverything, _outlineBuffer);
                _outlineCameraTarget = mainCam;
                return;
            }

            // Normal path
            _combinedOutlineCombines.Clear();
            _outlineMarks.Clear();
            int outlineSignature = 17;

            for (int s = 0; s < selection.Count; s++)
            {
                var sel = selection[s];
                if (sel == null) continue;

                var mfs = sel.GetComponentsInChildren<MeshFilter>();
                if (mfs == null) continue;

                outlineSignature = unchecked(outlineSignature * 31 + sel.GetInstanceID());

                for (int i = 0; i < mfs.Length; i++)
                {
                    var mf = mfs[i];
                    if (mf == null || mf.sharedMesh == null) continue;
                    var mr = mf.GetComponent<MeshRenderer>();
                    if (mr == null || !mr.enabled) continue;

                    var mesh = mf.sharedMesh;
                    var t = mf.transform;
                    var shell = GetOrBuildOutlineShell(mesh, t);
                    if (shell.mesh == null) continue;

                    outlineSignature = unchecked(outlineSignature * 31 + mf.GetInstanceID());
                    outlineSignature = unchecked(outlineSignature * 31 + mesh.GetInstanceID());
                    outlineSignature = unchecked(outlineSignature * 31 + t.position.GetHashCode());
                    outlineSignature = unchecked(outlineSignature * 31 + t.rotation.GetHashCode());
                    outlineSignature = unchecked(outlineSignature * 31 + t.lossyScale.GetHashCode());

                    _combinedOutlineCombines.Add(new CombineInstance
                    {
                        mesh = shell.mesh,
                        transform = Matrix4x4.identity,
                    });

                    _outlineMarks.Add((mesh, t.localToWorldMatrix, mesh.subMeshCount));
                }
            }

            if (_combinedOutlineCombines.Count == 0)
            {
                _outlineBuffer.SetGlobalFloat("unity_GUIZTestMode", (float)CompareFunction.LessEqual);
                return;
            }

            bool isDragging = LevelEditor.IsDragging;

            if (!isDragging)
            {
                // Static selection: combine all shells into one mesh for minimal draw calls.
                if (_combinedOutlineShell == null)
                    _combinedOutlineShell = new Mesh { name = "PropOutlineCombinedShell" };

                // vertexCount == 0 also catches a freshly-recreated shell above (the prior
                // instance was destroyed by Unity's asset cleanup) whose signature happens
                // to still match - without this it would stay empty and invisible forever.
                if (_combinedOutlineSignature != outlineSignature || _combinedOutlineShell.vertexCount == 0)
                {
                    _combinedOutlineSignature = outlineSignature;
                    _combinedOutlineShell.Clear(false);
                    _combinedOutlineShell.indexFormat = IndexFormat.UInt32;
                    _combinedOutlineShell.CombineMeshes(_combinedOutlineCombines.ToArray(), true, false, false);
                    _combinedOutlineShell.RecalculateBounds();
                }

                _outlineBuffer.DrawMesh(_combinedOutlineShell, Matrix4x4.identity, _stencilClearMat);
                for (int i = 0; i < _outlineMarks.Count; i++)
                {
                    var mark = _outlineMarks[i];
                    for (int sub = 0; sub < mark.subMeshCount; sub++)
                        _outlineBuffer.DrawMesh(mark.mesh, mark.matrix, _stencilMarkMat, sub);
                }
                _outlineBuffer.DrawMesh(_combinedOutlineShell, Matrix4x4.identity, _outlineMat);
            }
            else
            {
                // Active drag (rotate / scale): draw shells per-mesh to skip CombineMeshes.
                // _combinedOutlineSignature is left stale so the first non-drag frame triggers
                // a full rebuild of the combined mesh.
                for (int i = 0; i < _combinedOutlineCombines.Count; i++)
                    _outlineBuffer.DrawMesh(_combinedOutlineCombines[i].mesh, Matrix4x4.identity, _stencilClearMat);
                for (int i = 0; i < _outlineMarks.Count; i++)
                {
                    var mark = _outlineMarks[i];
                    for (int sub = 0; sub < mark.subMeshCount; sub++)
                        _outlineBuffer.DrawMesh(mark.mesh, mark.matrix, _stencilMarkMat, sub);
                }
                for (int i = 0; i < _combinedOutlineCombines.Count; i++)
                    _outlineBuffer.DrawMesh(_combinedOutlineCombines[i].mesh, Matrix4x4.identity, _outlineMat);
            }

            _outlineBuffer.SetGlobalFloat("unity_GUIZTestMode", (float)CompareFunction.LessEqual);

            mainCam.AddCommandBuffer(CameraEvent.AfterEverything, _outlineBuffer);
            _outlineCameraTarget = mainCam;
        }

        // Two-pass outline: solid ring where visible, semi-transparent ring through occluding
        // geometry. Stencil ops still use ZTest=Always so the interior is correctly excluded
        // even for occluded objects; the outline shell draws switch to LessEqual/Greater.
        // When screen-space shaders are loaded (babyblocks_shaders.bundle present), delegates
        // to DrawOutlineScreenSpace for a 1px UE4-style post-process outline instead.
        public static void DrawOutline(IReadOnlyList<LevelEditorObject> selection, Camera mainCam)
        {
            if (ScreenSpaceShadersLoaded)
            {
                DetachOutlineBuffer();
                DrawOutlineScreenSpace(selection, mainCam);
                return;
            }

            DetachOutlineBuffer();

            if (selection == null || selection.Count == 0 || mainCam == null) return;
            if (_stencilClearMat == null || _stencilMarkMat == null || _outlineMat == null) return;

            if (_outlineBuffer == null)
                _outlineBuffer = new CommandBuffer { name = "PropOutline" };
            else
                _outlineBuffer.Clear();

            SyncOccludedColor();
            bool drawOcc = OutlineOccludedAlpha > 0.01f && _outlineOccMat != null;

            // ── Translate-drag fast path ─────────────────────────────────────────────
            if (_outlineTranslateDrag
                && _combinedOutlineShell != null && _combinedOutlineShell.vertexCount > 0)
            {
                _outlineMarks.Clear();
                CollectOutlineMarks(selection);
                if (_outlineMarks.Count > 0)
                {
                    var tMat = Matrix4x4.Translate(_outlineTranslateDelta);
                    _outlineBuffer.SetGlobalFloat("unity_GUIZTestMode", (float)CompareFunction.Always);
                    _outlineBuffer.DrawMesh(_combinedOutlineShell, tMat, _stencilClearMat);
                    for (int i = 0; i < _outlineMarks.Count; i++)
                    {
                        var mark = _outlineMarks[i];
                        for (int sub = 0; sub < mark.subMeshCount; sub++)
                            _outlineBuffer.DrawMesh(mark.mesh, mark.matrix, _stencilMarkMat, sub);
                    }
                    _outlineBuffer.SetGlobalFloat("unity_GUIZTestMode", (float)CompareFunction.LessEqual);
                    _outlineBuffer.DrawMesh(_combinedOutlineShell, tMat, _outlineMat);
                    if (drawOcc)
                    {
                        _outlineBuffer.SetGlobalFloat("unity_GUIZTestMode", (float)CompareFunction.Greater);
                        _outlineBuffer.DrawMesh(_combinedOutlineShell, tMat, _outlineOccMat);
                    }
                }
                _outlineBuffer.SetGlobalFloat("unity_GUIZTestMode", (float)CompareFunction.LessEqual);
                mainCam.AddCommandBuffer(CameraEvent.AfterEverything, _outlineBuffer);
                _outlineCameraTarget = mainCam;
                return;
            }

            // ── Normal path ──────────────────────────────────────────────────────────
            _combinedOutlineCombines.Clear();
            _outlineMarks.Clear();
            int outlineSignature = 17;

            for (int s = 0; s < selection.Count; s++)
            {
                var sel = selection[s];
                if (sel == null) continue;

                var mfs = sel.GetComponentsInChildren<MeshFilter>();
                if (mfs == null) continue;

                outlineSignature = unchecked(outlineSignature * 31 + sel.GetInstanceID());

                for (int i = 0; i < mfs.Length; i++)
                {
                    var mf = mfs[i];
                    if (mf == null || mf.sharedMesh == null) continue;
                    var mr = mf.GetComponent<MeshRenderer>();
                    if (mr == null || !mr.enabled) continue;

                    var mesh = mf.sharedMesh;
                    var t    = mf.transform;
                    var shell = GetOrBuildOutlineShell(mesh, t);
                    if (shell.mesh == null) continue;

                    outlineSignature = unchecked(outlineSignature * 31 + mf.GetInstanceID());
                    outlineSignature = unchecked(outlineSignature * 31 + mesh.GetInstanceID());
                    outlineSignature = unchecked(outlineSignature * 31 + t.position.GetHashCode());
                    outlineSignature = unchecked(outlineSignature * 31 + t.rotation.GetHashCode());
                    outlineSignature = unchecked(outlineSignature * 31 + t.lossyScale.GetHashCode());

                    _combinedOutlineCombines.Add(new CombineInstance
                    {
                        mesh      = shell.mesh,
                        transform = Matrix4x4.identity,
                    });

                    _outlineMarks.Add((mesh, t.localToWorldMatrix, mesh.subMeshCount));
                }
            }

            if (_combinedOutlineCombines.Count == 0)
            {
                _outlineBuffer.SetGlobalFloat("unity_GUIZTestMode", (float)CompareFunction.LessEqual);
                return;
            }

            bool isDragging = LevelEditor.IsDragging;

            // Stencil ops need ZTest=Always so the interior is marked even for occluded objects.
            _outlineBuffer.SetGlobalFloat("unity_GUIZTestMode", (float)CompareFunction.Always);

            if (!isDragging)
            {
                if (_combinedOutlineShell == null)
                    _combinedOutlineShell = new Mesh { name = "PropOutlineCombinedShell" };

                if (_combinedOutlineSignature != outlineSignature || _combinedOutlineShell.vertexCount == 0)
                {
                    _combinedOutlineSignature = outlineSignature;
                    _combinedOutlineShell.Clear(false);
                    _combinedOutlineShell.indexFormat = IndexFormat.UInt32;
                    _combinedOutlineShell.CombineMeshes(_combinedOutlineCombines.ToArray(), true, false, false);
                    _combinedOutlineShell.RecalculateBounds();
                }

                _outlineBuffer.DrawMesh(_combinedOutlineShell, Matrix4x4.identity, _stencilClearMat);
                for (int i = 0; i < _outlineMarks.Count; i++)
                {
                    var mark = _outlineMarks[i];
                    for (int sub = 0; sub < mark.subMeshCount; sub++)
                        _outlineBuffer.DrawMesh(mark.mesh, mark.matrix, _stencilMarkMat, sub);
                }
                _outlineBuffer.SetGlobalFloat("unity_GUIZTestMode", (float)CompareFunction.LessEqual);
                _outlineBuffer.DrawMesh(_combinedOutlineShell, Matrix4x4.identity, _outlineMat);
                if (drawOcc)
                {
                    _outlineBuffer.SetGlobalFloat("unity_GUIZTestMode", (float)CompareFunction.Greater);
                    _outlineBuffer.DrawMesh(_combinedOutlineShell, Matrix4x4.identity, _outlineOccMat);
                }
            }
            else
            {
                for (int i = 0; i < _combinedOutlineCombines.Count; i++)
                    _outlineBuffer.DrawMesh(_combinedOutlineCombines[i].mesh, Matrix4x4.identity, _stencilClearMat);
                for (int i = 0; i < _outlineMarks.Count; i++)
                {
                    var mark = _outlineMarks[i];
                    for (int sub = 0; sub < mark.subMeshCount; sub++)
                        _outlineBuffer.DrawMesh(mark.mesh, mark.matrix, _stencilMarkMat, sub);
                }
                _outlineBuffer.SetGlobalFloat("unity_GUIZTestMode", (float)CompareFunction.LessEqual);
                for (int i = 0; i < _combinedOutlineCombines.Count; i++)
                    _outlineBuffer.DrawMesh(_combinedOutlineCombines[i].mesh, Matrix4x4.identity, _outlineMat);
                if (drawOcc)
                {
                    _outlineBuffer.SetGlobalFloat("unity_GUIZTestMode", (float)CompareFunction.Greater);
                    for (int i = 0; i < _combinedOutlineCombines.Count; i++)
                        _outlineBuffer.DrawMesh(_combinedOutlineCombines[i].mesh, Matrix4x4.identity, _outlineOccMat);
                }
            }

            _outlineBuffer.SetGlobalFloat("unity_GUIZTestMode", (float)CompareFunction.LessEqual);

            mainCam.AddCommandBuffer(CameraEvent.AfterEverything, _outlineBuffer);
            _outlineCameraTarget = mainCam;
        }

        // True once the stencil clear/mark materials (built by InitMaterials) are available.
        public static bool StencilMaterialsReady => _stencilClearMat != null && _stencilMarkMat != null;

        // Lazily creates the stencil clear/mark materials without doing the rest of Init()'s
        // (heavier, GameObject-creating) setup. Safe to call every frame.
        public static void EnsureStencilMaterials()
        {
            if (!StencilMaterialsReady) InitMaterials();
        }

        // Keeps _outlineOccMat in sync with the current visible color at the current occluded alpha.
        static void SyncOccludedColor()
        {
            if (_outlineMat == null || _outlineOccMat == null) return;
            var c = _outlineMat.GetColor("_Color");
            c.a = OutlineOccludedAlpha;
            _outlineOccMat.SetColor("_Color", c);
        }

        // Builds an outline material matching _outlineMat's stencil setup but with a custom
        // color, for use with DrawRemoteOutline.
        public static Material CreateOutlineMaterial(Color color)
        {
            var uiShader = Shader.Find("UI/Default");
            if (uiShader == null) return null;

            var mat = new Material(uiShader);
            mat.SetColor("_Color", color);
            mat.SetInt("_Stencil",          1);
            mat.SetInt("_StencilComp",      (int)CompareFunction.NotEqual);
            mat.SetInt("_StencilOp",        (int)StencilOp.Keep);
            mat.SetInt("_StencilWriteMask", 255);
            mat.SetInt("_StencilReadMask",  255);
            mat.SetInt("_ColorMask",        15);
            mat.renderQueue = 3000;
            return mat;
        }

        // Creates the matching through-wall (Greater ZTest) material for a remote player color.
        public static Material CreateOccludedOutlineMaterial(Color color)
        {
            return CreateOutlineMaterial(new Color(color.r, color.g, color.b, OutlineOccludedAlpha));
        }

        // Draws a single combined stencil-based outline around all of targets using outlineMat
        // instead of the local selection's hardcoded yellow, into a caller-owned CommandBuffer.
        // Mirrors DrawOutline's per-mesh "active drag" path (no combined-mesh caching) since the
        // targets may be moved every frame by a remote peer's drag. All clears happen before all
        // marks before all outline draws, so a multi-object selection renders as ONE outline
        // around the combined silhouette rather than separate outlines per object. Used by
        // RemotePropHighlightManager to show a peer's current selection in their suit color.
        public static void DrawRemoteOutline(IReadOnlyList<LevelEditorObject> targets, Material outlineMat, Material outlineOccMat, CommandBuffer buffer)
        {
            if (targets == null || targets.Count == 0 || outlineMat == null || buffer == null) return;
            if (!StencilMaterialsReady) return;

            // Keep occ mat alpha in sync with the global setting each frame.
            if (outlineOccMat != null)
            {
                var c = outlineMat.GetColor("_Color");
                c.a = OutlineOccludedAlpha;
                outlineOccMat.SetColor("_Color", c);
            }
            bool drawOcc = OutlineOccludedAlpha > 0.01f && outlineOccMat != null;

            _remoteOutlineShells.Clear();
            _remoteOutlineMarks.Clear();

            for (int t = 0; t < targets.Count; t++)
            {
                var obj = targets[t];
                if (obj == null) continue;

                var mfs = obj.GetComponentsInChildren<MeshFilter>();
                if (mfs == null) continue;

                for (int i = 0; i < mfs.Length; i++)
                {
                    var mf = mfs[i];
                    if (mf == null || mf.sharedMesh == null) continue;
                    var mr = mf.GetComponent<MeshRenderer>();
                    if (mr == null || !mr.enabled) continue;

                    var shell = GetOrBuildOutlineShell(mf.sharedMesh, mf.transform);
                    if (shell.mesh == null) continue;

                    _remoteOutlineShells.Add(shell.mesh);
                    _remoteOutlineMarks.Add((mf.sharedMesh, mf.transform.localToWorldMatrix, mf.sharedMesh.subMeshCount));
                }
            }

            if (_remoteOutlineShells.Count == 0) return;

            buffer.SetGlobalFloat("unity_GUIZTestMode", (float)CompareFunction.Always);
            for (int i = 0; i < _remoteOutlineShells.Count; i++)
                buffer.DrawMesh(_remoteOutlineShells[i], Matrix4x4.identity, _stencilClearMat);
            for (int i = 0; i < _remoteOutlineMarks.Count; i++)
            {
                var mark = _remoteOutlineMarks[i];
                for (int sub = 0; sub < mark.subMeshCount; sub++)
                    buffer.DrawMesh(mark.mesh, mark.matrix, _stencilMarkMat, sub);
            }
            buffer.SetGlobalFloat("unity_GUIZTestMode", (float)CompareFunction.LessEqual);
            for (int i = 0; i < _remoteOutlineShells.Count; i++)
                buffer.DrawMesh(_remoteOutlineShells[i], Matrix4x4.identity, outlineMat);
            if (drawOcc)
            {
                buffer.SetGlobalFloat("unity_GUIZTestMode", (float)CompareFunction.Greater);
                for (int i = 0; i < _remoteOutlineShells.Count; i++)
                    buffer.DrawMesh(_remoteOutlineShells[i], Matrix4x4.identity, outlineOccMat);
            }
            buffer.SetGlobalFloat("unity_GUIZTestMode", (float)CompareFunction.LessEqual);
        }

        // Collects stencil-mark entries for the current selection using current transform matrices.
        static void CollectOutlineMarks(IReadOnlyList<LevelEditorObject> selection)
        {
            for (int s = 0; s < selection.Count; s++)
            {
                var sel = selection[s];
                if (sel == null) continue;
                var mfs = sel.GetComponentsInChildren<MeshFilter>();
                if (mfs == null) continue;
                for (int i = 0; i < mfs.Length; i++)
                {
                    var mf = mfs[i];
                    if (mf == null || mf.sharedMesh == null) continue;
                    var mr = mf.GetComponent<MeshRenderer>();
                    if (mr == null || !mr.enabled) continue;
                    _outlineMarks.Add((mf.sharedMesh, mf.transform.localToWorldMatrix, mf.sharedMesh.subMeshCount));
                }
            }
        }

        static MeshData GetOrBuildMeshData(Mesh source)
        {
            if (_meshDataCache.TryGetValue(source, out var cached)) return cached;

            var verts = source.vertices;
            var tris  = source.triangles;

            if (verts == null || verts.Length == 0 || tris == null || tris.Length == 0)
            {
                var phys = PhysicsObjectManager.BuildPhysicsMesh(source);
                if (phys == null) return default;
                verts = phys.vertices;
                tris  = phys.triangles;
            }

            var data = new MeshData
            {
                verts         = verts,
                tris          = tris,
                smoothNormals = ComputeSmoothedNormals(verts, tris),
            };
            _meshDataCache[source] = data;
            return data;
        }

        static OutlineShellData GetOrBuildOutlineShell(Mesh source, Transform t)
        {
            if (source == null || t == null) return default;

            int key = t.GetInstanceID();
            int sourceId = source.GetInstanceID();

            if (_outlineShellCache.TryGetValue(key, out var cached)
                && cached.mesh != null
                && cached.meshId == sourceId
                && cached.position == t.position
                && cached.rotation == t.rotation
                && cached.scale == t.lossyScale
                && cached.thickness == OutlineThickness)
            {
                return cached;
            }

            var data = GetOrBuildMeshData(source);
            if (data.verts == null || data.tris == null || data.smoothNormals == null) return default;

            var worldVerts = new Vector3[data.verts.Length];
            for (int i = 0; i < data.verts.Length; i++)
            {
                var wPos    = t.TransformPoint(data.verts[i]);
                var wNormal = t.TransformDirection(data.smoothNormals[i]).normalized;
                worldVerts[i] = wPos + wNormal * OutlineThickness;
            }

            // Note: cached.mesh ?? new Mesh(...) would NOT work here — ?? does a raw reference
            // null check, but a destroyed UnityEngine.Object is a non-null reference that only
            // compares equal to null via the overridden == operator (Unity's "fake null").
            var mesh = cached.mesh != null ? cached.mesh : new Mesh { name = "PropOutlineShell" };
            mesh.Clear(false);
            mesh.vertices = worldVerts;
            mesh.triangles = data.tris;
            var colors = new Color[worldVerts.Length];
            for (int i = 0; i < colors.Length; i++) colors[i] = Color.white;
            mesh.colors = colors;
            mesh.RecalculateBounds();

            cached.mesh = mesh;
            cached.meshId = sourceId;
            cached.position = t.position;
            cached.rotation = t.rotation;
            cached.scale = t.lossyScale;
            cached.thickness = OutlineThickness;
            cached.worldVerts = worldVerts;
            cached.tris = data.tris;
            _outlineShellCache[key] = cached;
            return cached;
        }

        static Vector3[] ComputeSmoothedNormals(Vector3[] verts, int[] tris)
        {
            var indexAcc = new Vector3[verts.Length];
            for (int i = 0; i < tris.Length; i += 3)
            {
                int i0 = tris[i], i1 = tris[i + 1], i2 = tris[i + 2];
                var n = Vector3.Cross(verts[i1] - verts[i0], verts[i2] - verts[i0]);
                indexAcc[i0] += n;
                indexAcc[i1] += n;
                indexAcc[i2] += n;
            }

            // Quantize positions to merge UV-seam duplicates before averaging normals.
            const int Q = 8192;
            var posAcc = new Dictionary<(int, int, int), Vector3>(verts.Length);
            for (int i = 0; i < verts.Length; i++)
            {
                var key = (Mathf.RoundToInt(verts[i].x * Q),
                           Mathf.RoundToInt(verts[i].y * Q),
                           Mathf.RoundToInt(verts[i].z * Q));
                posAcc[key] = posAcc.TryGetValue(key, out var existing)
                    ? existing + indexAcc[i]
                    : indexAcc[i];
            }

            var result = new Vector3[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                var key = (Mathf.RoundToInt(verts[i].x * Q),
                           Mathf.RoundToInt(verts[i].y * Q),
                           Mathf.RoundToInt(verts[i].z * Q));
                var n = posAcc.TryGetValue(key, out var v) ? v : indexAcc[i];
                result[i] = n == Vector3.zero ? Vector3.up : n.normalized;
            }
            return result;
        }

        // Each mesh is regenerated only if it's missing. BorrowMesh briefly creates and destroys
        // a primitive GameObject in the active scene to grab its mesh — calling it when the mesh
        // is already alive would needlessly spawn/destroy primitives during gameplay (e.g. when
        // RefreshAssets re-runs this after a save load to replace assets the engine unloaded).
        static void InitMeshes()
        {
            if (_shaftMesh   == null) _shaftMesh   = BorrowMesh(PrimitiveType.Cylinder);
            if (_sphereMesh  == null) _sphereMesh  = BorrowMesh(PrimitiveType.Sphere);
            if (_cubeTipMesh == null) _cubeTipMesh = BorrowMesh(PrimitiveType.Cube);
            if (_coneTipMesh == null) _coneTipMesh = BuildConeMesh(16);
            if (_ringMesh    == null) _ringMesh    = BuildRingMesh(48, outerRadius: RingOuterRadius, innerRadius: RingInnerRadius);
        }

        static void InitMaterials()
        {
            var shader = Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            const int GizmoQueue = 4000;

            var axisColors = new Color[]
            {
                new Color(0.90f, 0.20f, 0.20f),  // X red
                new Color(0.20f, 0.80f, 0.20f),  // Y green
                new Color(0.20f, 0.45f, 1.00f),  // Z blue
            };

            _mats      = new Material[3];
            _hoverMats = new Material[3];
            for (int i = 0; i < 3; i++)
            {
                _mats[i]      = MakeGizmoMat(shader, axisColors[i], GizmoQueue);
                _hoverMats[i] = MakeGizmoMat(shader, Color.Lerp(axisColors[i], Color.white, 0.45f), GizmoQueue);
            }

            // Free handle sphere/cube: plain depth-tested material like the axes.  The gizmo cam
            // clears its own depth buffer, so the whole gizmo sits on top of the scene while the
            // axes (drawn first, queue 4000) correctly occlude the center handle where they pass
            // in front of it.  queue 4000 keeps it in the same depth-tested batch as the axes.
            _freeMat      = MakeGizmoMat(shader, new Color(0.72f, 0.72f, 0.72f), GizmoQueue);
            _freeHoverMat = MakeGizmoMat(shader, Color.white, GizmoQueue);

            var planeColors = new Color[]
            {
                axisColors[2], // XY -> blue (Z)
                axisColors[0], // YZ -> red (X)
                axisColors[1], // XZ -> green (Y)
            };
            _planeMats = new Material[3];
            _planeHoverMats = new Material[3];
            for (int i = 0; i < 3; i++)
            {
                _planeMats[i]      = MakeGizmoMat(shader, planeColors[i], GizmoQueue);
                _planeHoverMats[i] = MakeGizmoMat(shader, Color.Lerp(planeColors[i], Color.white, 0.45f), GizmoQueue);
            }

            // Free-rotate sphere: semi-transparent overlay inside the rotation rings.
            // UI/Default supports alpha blending without needing Standard's transparency setup.
            var freeRotShader = Shader.Find("UI/Default") ?? shader;
            _freeRotMat = new Material(freeRotShader);
            _freeRotMat.color = new Color(1f, 1f, 1f, 0.14f);
            _freeRotMat.SetInt("_ZTest", 8); // Always
            _freeRotMat.renderQueue = GizmoQueue - 1; // draw before rings so rings render on top

            _freeRotHoverMat = new Material(freeRotShader);
            _freeRotHoverMat.color = new Color(0.8f, 0.9f, 1f, 0.28f);
            _freeRotHoverMat.SetInt("_ZTest", 8);
            _freeRotHoverMat.renderQueue = GizmoQueue - 1;

            // UI/Default exposes stencil control as material properties; ZTest is driven by
            // the unity_GUIZTestMode global set in the CommandBuffer before each draw group.
            var uiShader = Shader.Find("UI/Default");
            if (uiShader != null)
            {
                _stencilClearMat = new Material(uiShader);
                _stencilClearMat.SetInt("_Stencil",          0);
                _stencilClearMat.SetInt("_StencilComp",      (int)CompareFunction.Always);
                _stencilClearMat.SetInt("_StencilOp",        (int)StencilOp.Replace);
                _stencilClearMat.SetInt("_StencilWriteMask", 255);
                _stencilClearMat.SetInt("_StencilReadMask",  255);
                _stencilClearMat.SetInt("_ColorMask",        0);

                _stencilMarkMat = new Material(uiShader);
                _stencilMarkMat.SetInt("_Stencil",           1);
                _stencilMarkMat.SetInt("_StencilComp",       (int)CompareFunction.Always);
                _stencilMarkMat.SetInt("_StencilOp",         (int)StencilOp.Replace);
                _stencilMarkMat.SetInt("_StencilWriteMask",  255);
                _stencilMarkMat.SetInt("_StencilReadMask",   255);
                _stencilMarkMat.SetInt("_ColorMask",         0);

                var yellow  = new Color(1f, 0.85f, 0.1f, 1f);
                _outlineMat = new Material(uiShader);
                _outlineMat.SetColor("_Color",               yellow);
                _outlineMat.SetInt("_Stencil",               1);
                _outlineMat.SetInt("_StencilComp",           (int)CompareFunction.NotEqual);
                _outlineMat.SetInt("_StencilOp",             (int)StencilOp.Keep);
                _outlineMat.SetInt("_StencilWriteMask",      255);
                _outlineMat.SetInt("_StencilReadMask",       255);
                _outlineMat.SetInt("_ColorMask",             15);
                _outlineMat.renderQueue = 3000;

                var yellowOcc = new Color(yellow.r, yellow.g, yellow.b, OutlineOccludedAlpha);
                _outlineOccMat = new Material(uiShader);
                _outlineOccMat.SetColor("_Color",            yellowOcc);
                _outlineOccMat.SetInt("_Stencil",            1);
                _outlineOccMat.SetInt("_StencilComp",        (int)CompareFunction.NotEqual);
                _outlineOccMat.SetInt("_StencilOp",          (int)StencilOp.Keep);
                _outlineOccMat.SetInt("_StencilWriteMask",   255);
                _outlineOccMat.SetInt("_StencilReadMask",    255);
                _outlineOccMat.SetInt("_ColorMask",          15);
                _outlineOccMat.renderQueue = 3000;
            }
        }

        static Material MakeGizmoMat(Shader shader, Color color, int queue)
        {
            var m = new Material(shader);
            m.color = color;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            m.renderQueue = queue;
            // Unlit/Color hardcodes ZTest LEqual and ZWrite On — correct for depth-testing among
            // gizmo parts. GizmoOccluded (queue 4001) uses ZTest LessEqual against the depth
            // written here, so only the frontmost gizmo part at each pixel can draw its checker.
            m.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            return m;
        }

        static Camera BuildCam(float depth, float farClip = 500000f,
            CameraClearFlags clearFlags = CameraClearFlags.Depth)
        {
            var go  = new GameObject($"GizmoOverlayCam_{depth}");
            UnityEngine.Object.DontDestroyOnLoad(go);
            var cam = go.AddComponent<Camera>();
            cam.clearFlags    = clearFlags;
            cam.cullingMask   = Mask;
            cam.depth         = depth;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane  = farClip;
            cam.enabled       = false;
            // OnPreRender hook re-records the SS buffer with the freshest Camera.main
            // matrices, after Cinemachine LateUpdate has run.
            go.AddComponent<OverlayCamPreRenderHook>();
            return cam;
        }

        static void BuildColliders()
        {
            _root = new GameObject("LevelEditorGizmo");
            UnityEngine.Object.DontDestroyOnLoad(_root);

            _arrowHandles = new GameObject("ArrowHandles");
            _arrowHandles.transform.SetParent(_root.transform, false);

            for (int i = 0; i < 3; i++)
            {
                var pivot = new GameObject($"GizmoPivot_{i}");
                pivot.transform.SetParent(_arrowHandles.transform, false);
                pivot.transform.localRotation = PivotRots[i];
                _axisPivots[i] = pivot.transform;   // stored for per-frame flip updates

                var col = new GameObject($"GizmoCol_{i}");
                col.transform.SetParent(pivot.transform, false);
                col.transform.localPosition = new Vector3(0f, 0.35f, 0f);
                col.transform.localScale    = new Vector3(0.16f, 0.70f, 0.16f);
                col.layer = Layer;
                col.AddComponent<BoxCollider>();
                col.AddComponent<GizmoHandle>().axisIndex = i;
            }

            var freeCol = new GameObject("GizmoCol_Free");
            freeCol.transform.SetParent(_arrowHandles.transform, false);
            freeCol.transform.localScale = Vector3.one * 0.22f;
            freeCol.layer = Layer;
            freeCol.AddComponent<SphereCollider>();
            freeCol.AddComponent<GizmoHandle>().axisIndex = 3;

            var planePosXY = new Vector3(PlaneSizeMove * 0.5f, PlaneSizeMove * 0.5f, 0f);
            var planePosYZ = new Vector3(0f, PlaneSizeMove * 0.5f, -PlaneSizeMove * 0.5f);
            var planePosXZ = new Vector3(PlaneSizeMove * 0.5f, 0f, -PlaneSizeMove * 0.5f);
            var planeScaleXY = new Vector3(PlaneSizeMove, PlaneSizeMove, PlaneThickness);
            var planeScaleYZ = new Vector3(PlaneThickness, PlaneSizeMove, PlaneSizeMove);
            var planeScaleXZ = new Vector3(PlaneSizeMove, PlaneThickness, PlaneSizeMove);
            for (int i = 0; i < 3; i++)
            {
                var col = new GameObject($"GizmoPlaneCol_{i}");
                col.transform.SetParent(_arrowHandles.transform, false);
                col.transform.localPosition = i == 0 ? planePosXY : i == 1 ? planePosYZ : planePosXZ;
                col.transform.localScale = i == 0 ? planeScaleXY : i == 1 ? planeScaleYZ : planeScaleXZ;
                col.layer = Layer;
                col.AddComponent<BoxCollider>();
                col.AddComponent<GizmoHandle>().axisIndex = 4 + i;
                _planeHandles[i] = col.transform;   // stored for per-frame flip updates
            }

            _ringHandles = new GameObject("RingHandles");
            _ringHandles.transform.SetParent(_root.transform, false);
            _ringHandles.SetActive(false);

            for (int i = 0; i < 3; i++)
            {
                var pivot = new GameObject($"GizmoRingPivot_{i}");
                pivot.transform.SetParent(_ringHandles.transform, false);
                pivot.transform.localRotation = RingPivotRots[i];

                // No collider here — ring picking is done via math in RaycastRing (see
                // RaycastHandle for why MeshColliders are unreliable on a rescaling transform).
                // This GameObject just hosts the GizmoHandle returned for axis i.
                var col = new GameObject($"GizmoRingCol_{i}");
                col.transform.SetParent(pivot.transform, false);
                col.layer = Layer;
                var handle = col.AddComponent<GizmoHandle>();
                handle.axisIndex = i;
                _ringHandleRefs[i] = handle;
            }

            // Free-rotate sphere: collider slightly smaller than inner ring radius (0.50)
            // so clicking directly on a ring always hits the ring, not this sphere.
            var freeRotCol = new GameObject("GizmoCol_FreeRot");
            freeRotCol.transform.SetParent(_ringHandles.transform, false);
            freeRotCol.transform.localScale = Vector3.one * 0.88f; // radius ≈ 0.44, ring inner = 0.50
            freeRotCol.layer = Layer;
            freeRotCol.AddComponent<SphereCollider>();
            freeRotCol.AddComponent<GizmoHandle>().axisIndex = FreeRotateAxis;
        }

        static Mesh BorrowMesh(PrimitiveType type)
        {
            var go = GameObject.CreatePrimitive(type);
            var m  = go.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.Destroy(go);
            return m;
        }

        static Mesh BuildConeMesh(int segments)
        {
            var verts = new Vector3[segments + 2];
            var tris  = new int[segments * 6];

            verts[0] = new Vector3(0f, 1f, 0f);
            verts[1] = Vector3.zero;
            for (int i = 0; i < segments; i++)
            {
                float a = Mathf.PI * 2f * i / segments;
                verts[2 + i] = new Vector3(Mathf.Cos(a) * 0.5f, 0f, Mathf.Sin(a) * 0.5f);
            }
            for (int i = 0; i < segments; i++)
            {
                int cur  = 2 + i;
                int next = 2 + (i + 1) % segments;
                tris[i * 3]     = 0;    tris[i * 3 + 1]     = cur;  tris[i * 3 + 2]     = next;
                tris[segments * 3 + i * 3]     = 1;
                tris[segments * 3 + i * 3 + 1] = next;
                tris[segments * 3 + i * 3 + 2] = cur;
            }

            var mesh = new Mesh();
            mesh.vertices  = verts;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static Mesh BuildRingMesh(int segments, float outerRadius, float innerRadius)
        {
            var verts = new Vector3[segments * 2];
            var tris  = new int[segments * 12];

            for (int i = 0; i < segments; i++)
            {
                float a = Mathf.PI * 2f * i / segments;
                float c = Mathf.Cos(a), s = Mathf.Sin(a);
                verts[i * 2]     = new Vector3(c * outerRadius, 0f, s * outerRadius);
                verts[i * 2 + 1] = new Vector3(c * innerRadius, 0f, s * innerRadius);
            }
            for (int i = 0; i < segments; i++)
            {
                int a = i * 2,     b = i * 2 + 1;
                int c = ((i + 1) % segments) * 2, d = ((i + 1) % segments) * 2 + 1;
                tris[i * 6]     = a; tris[i * 6 + 1] = c; tris[i * 6 + 2] = b;
                tris[i * 6 + 3] = b; tris[i * 6 + 4] = c; tris[i * 6 + 5] = d;
                int off = segments * 6 + i * 6;
                tris[off]     = a; tris[off + 1] = b; tris[off + 2] = c;
                tris[off + 3] = b; tris[off + 4] = d; tris[off + 5] = c;
            }

            var mesh = new Mesh();
            mesh.vertices  = verts;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // Props whose Renderer.bounds exceed this size on any axis have broken export data
        // (e.g. geometry authored far from the mesh origin). Skip them and fall back to
        // transform.position so the gizmo stays at the object's intended pivot.
        const float MaxPlausibleBoundsExtent = 100f;

        public static Bounds GetSelectionBounds(IReadOnlyList<LevelEditorObject> selection)
        {
            var result = new Bounds();
            bool initialized = false;

            if (selection == null) return result;
            for (int s = 0; s < selection.Count; s++)
            {
                var sel = selection[s];
                if (sel == null) continue;

                // The spawn point marker's wireframe (ring/pole/arrow) is intentionally
                // asymmetric around the object's origin, which would otherwise skew the
                // gizmo bounds forward/up. Pin it to a fixed unit bounds at the pivot.
                if (PropLibrary.IsSpawnPointProp(sel.addressableKey))
                {
                    var spawnBounds = new Bounds(sel.transform.position, Vector3.one);
                    if (!initialized) { result = spawnBounds; initialized = true; }
                    else result.Encapsulate(spawnBounds);
                    continue;
                }

                var renderers = sel.GetComponentsInChildren<Renderer>();
                if (renderers != null)
                {
                    for (int i = 0; i < renderers.Length; i++)
                    {
                        var r = renderers[i];
                        if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                        var bs = r.bounds.size;
                        if (bs.x > MaxPlausibleBoundsExtent || bs.y > MaxPlausibleBoundsExtent || bs.z > MaxPlausibleBoundsExtent) continue;
                        if (!initialized) { result = r.bounds; initialized = true; }
                        else result.Encapsulate(r.bounds);
                    }
                }
                if (!initialized)
                {
                    result = new Bounds(sel.transform.position, Vector3.one);
                    initialized = true;
                }
            }
            return result;
        }

        public static Vector3 GetSelectionBoundsCenter(IReadOnlyList<LevelEditorObject> selection)
        {
            if (selection == null || selection.Count == 0) return Vector3.zero;

            var sum = Vector3.zero;
            int count = 0;

            for (int s = 0; s < selection.Count; s++)
            {
                var sel = selection[s];
                if (sel == null) continue;

                // The spawn point marker's wireframe is asymmetric around the object's
                // origin (arrow points forward, pole rises up) — pin its pivot to the
                // actual spawn position rather than the averaged renderer centers.
                if (PropLibrary.IsSpawnPointProp(sel.addressableKey))
                {
                    sum += sel.transform.position;
                    count++;
                    continue;
                }

                // Average enabled-renderer centers, skipping any with implausibly large bounds
                // (bad mesh export). Fall back to transform.position if none pass.
                var renderers = sel.GetComponentsInChildren<Renderer>();
                Vector3 rendSum = Vector3.zero;
                int rendCount = 0;
                if (renderers != null)
                {
                    for (int i = 0; i < renderers.Length; i++)
                    {
                        var r = renderers[i];
                        if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                        var bs = r.bounds.size;
                        if (bs.x > MaxPlausibleBoundsExtent || bs.y > MaxPlausibleBoundsExtent || bs.z > MaxPlausibleBoundsExtent) continue;
                        rendSum += r.bounds.center;
                        rendCount++;
                    }
                }

                sum += rendCount > 0 ? rendSum / rendCount : sel.transform.position;
                count++;
            }

            return count > 0 ? sum / count : Vector3.zero;
        }
    }
}
