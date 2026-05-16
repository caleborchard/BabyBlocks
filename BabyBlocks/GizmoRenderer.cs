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

        // Stencil outline: shell cleared to 0, prop footprint marked 1 (ZTest=Always so occluded
        // props still mark), then shell drawn only where stencil=0 — giving a ring, always on top.
        const float OutlineThickness = 0.03f;
        static Material _stencilClearMat;
        static Material _stencilMarkMat;
        static Material _outlineMat;
        static CommandBuffer _outlineBuffer;
        static Camera _outlineCameraTarget;

        struct MeshData { public Vector3[] verts; public int[] tris; public Vector3[] smoothNormals; }
        static readonly Dictionary<Mesh, MeshData> _meshDataCache = new();

        static GameObject _root, _arrowHandles, _ringHandles;
        static Camera _overlayCam;
        static Vector3 _pivotPos;
        static bool _pivotOverrideActive;
        static Vector3 _pivotOverride;

        public static bool IsReady => _root != null;
        public static Vector3 PivotPosition => _pivotPos;

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
            _overlayCam = BuildCam(100f, 500000f, CameraClearFlags.Depth);
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
            _root.transform.rotation = (tool == LevelEditor.ToolMode.Scale)
                ? (primary != null ? primary.transform.rotation : Quaternion.identity)
                : Quaternion.identity;
            float dist = Vector3.Distance(mainCam.transform.position, _root.transform.position);
            _root.transform.localScale = Vector3.one * Mathf.Max(dist * 0.14f, 0.02f);

            bool rotating = tool == LevelEditor.ToolMode.Rotate;
            if (_arrowHandles != null) _arrowHandles.SetActive(!rotating);
            if (_ringHandles  != null) _ringHandles.SetActive(rotating);

            if (_overlayCam != null)
            {
                _overlayCam.transform.SetPositionAndRotation(mainCam.transform.position, mainCam.transform.rotation);
                _overlayCam.fieldOfView   = mainCam.fieldOfView;
                _overlayCam.aspect        = mainCam.aspect;
                _overlayCam.nearClipPlane = mainCam.nearClipPlane;
            }
        }

        public static void SetActive(bool on)
        {
            if (_root       != null) _root.SetActive(on);
            if (_overlayCam != null) _overlayCam.enabled = on;
            if (!on) DetachOutlineBuffer();
        }

        static void DetachOutlineBuffer()
        {
            if (_outlineCameraTarget != null && _outlineBuffer != null)
            {
                _outlineCameraTarget.RemoveCommandBuffer(CameraEvent.AfterEverything, _outlineBuffer);
                _outlineCameraTarget = null;
            }
        }

        public static GizmoHandle RaycastHandle(Ray ray)
        {
            var hits = Physics.RaycastAll(ray, 2000f, Mask);
            if (hits == null || hits.Length == 0) return null;

            GizmoHandle closest     = null;
            float       closestDist = float.MaxValue;
            foreach (var hit in hits)
            {
                var h = hit.collider.GetComponent<GizmoHandle>();
                if (h == null) continue;
                if (h.axisIndex == 3) return h;
                if (hit.distance < closestDist) { closestDist = hit.distance; closest = h; }
            }
            return closest;
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
                for (int i = 0; i < 3; i++)
                {
                    var mat = (hoveredAxis == i && _hoverMats != null) ? _hoverMats[i] : _mats[i];
                    Graphics.DrawMesh(_ringMesh,
                        Matrix4x4.TRS(origin, RingPivotRots[i], Vector3.one * s),
                        mat, Layer, _overlayCam);
                }
                return;
            }

            bool scaleMode = tool == LevelEditor.ToolMode.Scale;
            var  tipMesh   = scaleMode ? _cubeTipMesh  : _coneTipMesh;
            var  tipPos    = scaleMode ? ScaleTipPos   : TipPos;
            var  tipScale  = scaleMode ? ScaleTipScale : TipScale;
            var  planeSize  = scaleMode ? PlaneSizeScale : PlaneSizeMove;
            var  planePosXY = new Vector3(planeSize * 0.5f, planeSize * 0.5f, 0f);
            var  planePosYZ = new Vector3(0f, planeSize * 0.5f, -planeSize * 0.5f);
            var  planePosXZ = new Vector3(planeSize * 0.5f, 0f, -planeSize * 0.5f);
            var  planeScaleXY = new Vector3(planeSize, planeSize, PlaneThickness);
            var  planeScaleYZ = new Vector3(PlaneThickness, planeSize, planeSize);
            var  planeScaleXZ = new Vector3(planeSize, PlaneThickness, planeSize);

            var rootRot = _root.transform.rotation;
            for (int i = 0; i < 3; i++)
            {
                var rot = rootRot * PivotRots[i];
                var mat = (hoveredAxis == i && _hoverMats != null) ? _hoverMats[i] : _mats[i];

                Graphics.DrawMesh(_shaftMesh,
                    Matrix4x4.TRS(origin + rot * (ShaftPos * s), rot, ShaftScale * s),
                    mat, Layer, _overlayCam);

                Graphics.DrawMesh(tipMesh,
                    Matrix4x4.TRS(origin + rot * (tipPos * s), rot, tipScale * s),
                    mat, Layer, _overlayCam);
            }

            var freeMat = (hoveredAxis == 3 && _freeHoverMat != null) ? _freeHoverMat : _freeMat;
            var freeMesh = scaleMode ? _cubeTipMesh : _sphereMesh;
            var freeRot = scaleMode ? rootRot : Quaternion.identity;
            if (freeMat != null)
                Graphics.DrawMesh(freeMesh,
                    Matrix4x4.TRS(origin, freeRot, Vector3.one * 0.22f * s),
                    freeMat, Layer, _overlayCam);

            if (_planeMats != null && _planeHoverMats != null)
            {
                for (int i = 0; i < 3; i++)
                {
                    int axisIndex = 4 + i;
                    var mat = (hoveredAxis == axisIndex) ? _planeHoverMats[i] : _planeMats[i];
                    if (mat == null) continue;

                    Vector3 localPos  = i == 0 ? planePosXY : i == 1 ? planePosYZ : planePosXZ;
                    Vector3 localSize = i == 0 ? planeScaleXY : i == 1 ? planeScaleYZ : planeScaleXZ;
                    var pos = origin + rootRot * (localPos * s);

                    Graphics.DrawMesh(_cubeTipMesh,
                        Matrix4x4.TRS(pos, rootRot, localSize * s),
                        mat, Layer, _overlayCam);
                }
            }
        }

        public static void DrawOutline(IReadOnlyList<LevelEditorObject> selection, Camera mainCam)
        {
            DetachOutlineBuffer();

            if (selection == null || selection.Count == 0 || mainCam == null) return;
            if (_stencilClearMat == null || _stencilMarkMat == null || _outlineMat == null) return;

            if (_outlineBuffer == null)
                _outlineBuffer = new CommandBuffer { name = "PropOutline" };
            else
                _outlineBuffer.Clear();

            // ZTest=Always on all passes; restored to LEqual at the end so game UI is unaffected.
            _outlineBuffer.SetGlobalFloat("unity_GUIZTestMode", (float)CompareFunction.Always);

            bool anyDraw = false;
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
                    var t      = mf.transform;
                    var matrix = t.localToWorldMatrix;

                    var data = GetOrBuildMeshData(mesh);
                    if (data.verts == null) continue;

                    var shell = BuildShellWorldSpace(data.verts, data.tris, data.smoothNormals, t);
                    if (shell == null) continue;

                    _outlineBuffer.DrawMesh(shell, Matrix4x4.identity, _stencilClearMat);
                    for (int sub = 0; sub < mesh.subMeshCount; sub++)
                        _outlineBuffer.DrawMesh(mesh, matrix, _stencilMarkMat, sub);
                    _outlineBuffer.DrawMesh(shell, Matrix4x4.identity, _outlineMat);

                    anyDraw = true;
                }
            }

            _outlineBuffer.SetGlobalFloat("unity_GUIZTestMode", (float)CompareFunction.LessEqual);

            if (!anyDraw) return;

            mainCam.AddCommandBuffer(CameraEvent.AfterEverything, _outlineBuffer);
            _outlineCameraTarget = mainCam;
        }

        static MeshData GetOrBuildMeshData(Mesh source)
        {
            if (_meshDataCache.TryGetValue(source, out var cached)) return cached;

            var verts = source.vertices;
            var tris  = source.triangles;

            if (verts == null || verts.Length == 0 || tris == null || tris.Length == 0)
            {
                var phys = LevelEditorManager.BuildPhysicsMesh(source);
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

        // World-space expansion so thickness is uniform under non-uniform scale.
        // TransformDirection normalizes the normal, cancelling scale before the offset.
        static Mesh BuildShellWorldSpace(Vector3[] localVerts, int[] tris, Vector3[] smoothNormals, Transform t)
        {
            var worldVerts = new Vector3[localVerts.Length];
            for (int i = 0; i < localVerts.Length; i++)
            {
                var wPos    = t.TransformPoint(localVerts[i]);
                var wNormal = t.TransformDirection(smoothNormals[i]).normalized;
                worldVerts[i] = wPos + wNormal * OutlineThickness;
            }

            var colors = new Color[localVerts.Length];
            for (int i = 0; i < colors.Length; i++) colors[i] = Color.white;

            var mesh = new Mesh();
            mesh.vertices  = worldVerts;
            mesh.triangles = tris;
            mesh.colors    = colors;
            mesh.RecalculateBounds();
            return mesh;
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

        static void InitMeshes()
        {
            _shaftMesh   = BorrowMesh(PrimitiveType.Cylinder);
            _sphereMesh  = BorrowMesh(PrimitiveType.Sphere);
            _cubeTipMesh = BorrowMesh(PrimitiveType.Cube);
            _coneTipMesh = BuildConeMesh(16);
            _ringMesh    = BuildRingMesh(48, outerRadius: 0.65f, innerRadius: 0.50f);
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
            }
        }

        static Material MakeGizmoMat(Shader shader, Color color, int queue)
        {
            var m = new Material(shader);
            m.color = color;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            m.renderQueue = queue;
            m.SetInt("_ZTest", 8); // Always — depth is handled by the overlay camera
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
            }

            _ringHandles = new GameObject("RingHandles");
            _ringHandles.transform.SetParent(_root.transform, false);
            _ringHandles.SetActive(false);

            for (int i = 0; i < 3; i++)
            {
                var pivot = new GameObject($"GizmoRingPivot_{i}");
                pivot.transform.SetParent(_ringHandles.transform, false);
                pivot.transform.localRotation = RingPivotRots[i];

                var col = new GameObject($"GizmoRingCol_{i}");
                col.transform.SetParent(pivot.transform, false);
                col.layer = Layer;
                var mc = col.AddComponent<MeshCollider>();
                mc.sharedMesh = _ringMesh;
                col.AddComponent<GizmoHandle>().axisIndex = i;
            }
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

        public static Vector3 GetSelectionBoundsCenter(IReadOnlyList<LevelEditorObject> selection)
        {
            if (selection == null || selection.Count == 0) return Vector3.zero;
            bool hasBounds = false;
            Bounds b = new Bounds(Vector3.zero, Vector3.zero);

            for (int s = 0; s < selection.Count; s++)
            {
                var selected = selection[s];
                if (selected == null) continue;
                var renderers = selected.GetComponentsInChildren<Renderer>();
                if (renderers == null || renderers.Length == 0)
                {
                    if (!hasBounds)
                    {
                        b = new Bounds(selected.transform.position, Vector3.zero);
                        hasBounds = true;
                    }
                    else
                    {
                        b.Encapsulate(selected.transform.position);
                    }
                    continue;
                }

                for (int i = 0; i < renderers.Length; i++)
                {
                    var r = renderers[i];
                    if (r == null) continue;
                    if (!hasBounds)
                    {
                        b = r.bounds;
                        hasBounds = true;
                    }
                    else
                    {
                        b.Encapsulate(r.bounds);
                    }
                }
            }

            return hasBounds ? b.center : Vector3.zero;
        }
    }
}
