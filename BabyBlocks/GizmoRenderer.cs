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

        // Pivot rotations: align local +Y with each world axis.
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

        // Arrow geometry (gizmo-local units, scaled at draw time).
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
        // Outline pipeline — two cameras after the main scene camera:
        //
        //  _outlineRingCam (depth 99,  ClearFlags=Nothing) — inherits the main scene depth
        //    buffer unchanged.  Draws the normal-expanded shell with ZTest=Always so the ring
        //    is always visible over terrain and other scene objects.  ZWrite=Off keeps the
        //    depth buffer at its incoming main-scene values for the cover pass below.
        //
        //  _outlineCoverCam (depth 99.5, ClearFlags=Nothing) — also inherits the main-scene
        //    depth (ring didn't write depth).  Re-draws the prop with its own original
        //    materials using ZTest=LEqual against real scene depth, so the prop is correctly
        //    occluded by other scene objects (doesn't float over terrain).  Because this camera
        //    renders after the ring (depth 99.5 > 99) the prop color overwrites the ring
        //    exactly where the prop surface is visible.  Only the silhouette edge — where no
        //    prop geometry exists — keeps the yellow ring color.
        //
        // Normal-based vertex expansion (each vertex moved LocalThick along its normal) gives
        // correct inner-hole outlines (inner normals point inward → expanded geometry covers
        // the hole edge). Position-based normal averaging in ComputeSmoothedNormals ensures
        // smooth expansion even across UV seams.
        const float LocalThick = 0.04f; // local-space expansion per vertex
        static Material _outlineMat;
        static readonly Dictionary<Mesh, Mesh> _outlineExpandedCache = new();
        static Camera _mainCamRef; // set by Sync(); avoids Camera.main tag dependency

        static GameObject _root, _arrowHandles, _ringHandles;
        static Camera _outlineRingCam, _outlineCoverCam, _overlayCam;
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
            _outlineRingCam  = BuildCam( 99f,  500000f, CameraClearFlags.Nothing); // ring, inherits scene depth
            _outlineCoverCam = BuildCam( 99.5f,500000f, CameraClearFlags.Nothing); // cover, inherits scene depth
            _overlayCam      = BuildCam(100f,  500000f, CameraClearFlags.Depth);   // gizmo arrows
        }

        public static void Sync(IReadOnlyList<LevelEditorObject> selection, LevelEditorObject primary,
            LevelEditor.ToolMode tool, Camera mainCam)
        {
            if (_root == null) return;
            _mainCamRef = mainCam;
            bool visible = selection != null && selection.Count > 0;
            _root.SetActive(visible);
            if (_outlineRingCam  != null) _outlineRingCam.enabled  = visible;
            if (_outlineCoverCam != null) _outlineCoverCam.enabled = visible;
            if (_overlayCam      != null) _overlayCam.enabled      = visible;
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

            if (_outlineRingCam != null)
            {
                _outlineRingCam.transform.SetPositionAndRotation(mainCam.transform.position, mainCam.transform.rotation);
                _outlineRingCam.projectionMatrix = mainCam.projectionMatrix;
            }

            if (_outlineCoverCam != null)
            {
                _outlineCoverCam.transform.SetPositionAndRotation(mainCam.transform.position, mainCam.transform.rotation);
                _outlineCoverCam.projectionMatrix = mainCam.projectionMatrix;
            }

            if (_overlayCam != null)
            {
                _overlayCam.transform.SetPositionAndRotation(mainCam.transform.position, mainCam.transform.rotation);
                _overlayCam.fieldOfView    = mainCam.fieldOfView;
                _overlayCam.aspect         = mainCam.aspect;
                _overlayCam.nearClipPlane  = mainCam.nearClipPlane;
            }
        }

        public static void SetActive(bool on)
        {
            if (_root            != null) _root.SetActive(on);
            if (_outlineRingCam  != null) _outlineRingCam.enabled  = on;
            if (_outlineCoverCam != null) _outlineCoverCam.enabled = on;
            if (_overlayCam      != null) _overlayCam.enabled      = on;
        }

        // Returns the hovered GizmoHandle for a ray, or null. Free sphere (axisIndex=3) wins over arrows.
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

        public static void DrawOutline(IReadOnlyList<LevelEditorObject> selection)
        {
            if (selection == null || selection.Count == 0) return;
            if (_outlineMat == null || _outlineRingCam == null || _outlineCoverCam == null) return;

            var cam = _mainCamRef != null ? _mainCamRef : Camera.main;
            if (cam == null) return;

            for (int s = 0; s < selection.Count; s++)
            {
                var selected = selection[s];
                if (selected == null) continue;

                var rootMf = selected.GetComponent<MeshFilter>();
                if (rootMf != null && rootMf.sharedMesh != null)
                {
                    DrawMeshOutline(rootMf.sharedMesh, selected.transform, cam);
                    continue;
                }

                var childMfs = selected.GetComponentsInChildren<MeshFilter>();
                if (childMfs == null) continue;
                for (int i = 0; i < childMfs.Length; i++)
                {
                    var mf = childMfs[i];
                    if (mf == null || mf.sharedMesh == null) continue;
                    var mr = mf.GetComponent<MeshRenderer>();
                    if (mr == null || !mr.enabled) continue;
                    DrawMeshOutline(mf.sharedMesh, mf.transform, cam);
                }
            }
        }

        static void DrawMeshOutline(Mesh srcMesh, Transform t, Camera cam)
        {
            if (srcMesh == null) return;

            var ws = t.lossyScale;

            // Pass 1 — ring cam (ClearFlags=Nothing, inherits main-scene depth):
            // Draw the normal-expanded shell with ZTest=Always so the ring is always
            // visible over terrain and other scene objects.
            // ZWrite=Off → depth buffer stays at main-scene values for the cover pass.
            if (!_outlineExpandedCache.TryGetValue(srcMesh, out var expanded))
            {
                expanded = BuildExpandedMesh(srcMesh);
                _outlineExpandedCache[srcMesh] = expanded;
            }
            if (expanded != null)
            {
                Graphics.DrawMesh(expanded,
                    Matrix4x4.TRS(t.position, t.rotation, ws),
                    _outlineMat, Layer, _outlineRingCam);
            }

            // Pass 2 — cover cam (ClearFlags=Nothing, inherits main-scene depth):
            // Re-draw the original mesh with its actual material(s) using ZTest=LEqual
            // against the real scene depth.  The prop renders only where D_prop <= D_scene,
            // so it is correctly occluded by other scene objects and does NOT float over
            // terrain.  Because this camera renders after the ring (depth 99.5 > 99) the
            // prop color overwrites ring pixels exactly where the prop surface is visible.
            // Only the silhouette edge — where no prop geometry exists — stays yellow.
            var mr = t.GetComponent<MeshRenderer>();
            if (mr == null || !mr.enabled) return;
            var mats = mr.sharedMaterials;
            if (mats == null) return;
            for (int j = 0; j < mats.Length; j++)
            {
                var mat = mats[j];
                if (mat == null) continue;
                Graphics.DrawMesh(srcMesh, t.localToWorldMatrix, mat, Layer, _outlineCoverCam, j);
            }
        }

        // Builds a version of the mesh where every vertex is offset by its normal × LocalThick.
        // This creates a uniform outline shell on ALL surfaces, including inner holes, because
        // inner normals point inward — the expanded geometry covers the inner edge correctly.
        // Falls back to BuildPhysicsMesh + ComputeSmoothedNormals for non-readable game meshes.
        static Mesh BuildExpandedMesh(Mesh source)
        {
            var verts   = source.vertices;
            var normals = source.normals;
            var tris    = source.triangles;

            if (verts == null || verts.Length == 0 || tris == null || tris.Length == 0)
            {
                var phys = LevelEditorManager.BuildPhysicsMesh(source);
                if (phys == null) return null;
                verts   = phys.vertices;
                tris    = phys.triangles;
                normals = null;
            }

            if (normals == null || normals.Length != verts.Length)
                normals = ComputeSmoothedNormals(verts, tris);

            var expanded = new Vector3[verts.Length];
            for (int i = 0; i < verts.Length; i++)
                expanded[i] = verts[i] + normals[i] * LocalThick;

            var mesh = new Mesh();
            mesh.vertices  = expanded;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            return mesh;
        }

        // Computes smooth normals even across UV/lightmap seams by grouping vertices
        // that share the same quantised world position rather than the same vertex index.
        // This prevents the per-face faceting that appears when a mesh has duplicate
        // vertices at seam edges (common in non-readable game meshes via BuildPhysicsMesh).
        static Vector3[] ComputeSmoothedNormals(Vector3[] verts, int[] tris)
        {
            // Step 1: accumulate area-weighted face normals per vertex index.
            var indexAcc = new Vector3[verts.Length];
            for (int i = 0; i < tris.Length; i += 3)
            {
                int i0 = tris[i], i1 = tris[i + 1], i2 = tris[i + 2];
                var n = Vector3.Cross(verts[i1] - verts[i0], verts[i2] - verts[i0]);
                indexAcc[i0] += n;
                indexAcc[i1] += n;
                indexAcc[i2] += n;
            }

            // Step 2: merge by quantised position so that seam-duplicate vertices
            // (same XYZ, different UV index) share the same smoothed normal.
            const int Q = 8192; // ~0.12 mm precision at unit scale
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

            // Step 3: write back position-averaged normals per vertex index.
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
            const int GizmoQueue = 4000; // above outline ring (3001) so arrows render over it

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
                _planeMats[i] = MakeGizmoMat(shader, planeColors[i], GizmoQueue);
                _planeHoverMats[i] = MakeGizmoMat(shader, Color.Lerp(planeColors[i], Color.white, 0.45f), GizmoQueue);
            }

            var outlineColor = new Color(1f, 0.85f, 0.1f, 1f);

            // Outline ring — ZTest=Always so the ring is visible over all scene objects.
            // ZWrite=Off so the depth buffer stays at its incoming main-scene values, which
            // the cover cam then uses to correctly occlude the prop behind scene objects.
            _outlineMat = new Material(shader);
            _outlineMat.color = outlineColor;
            if (_outlineMat.HasProperty("_BaseColor")) _outlineMat.SetColor("_BaseColor", outlineColor);
            _outlineMat.renderQueue = 3001;
            _outlineMat.SetInt("_ZWrite", 0); // Off — preserve real scene depth for cover pass
            _outlineMat.SetInt("_ZTest",  8); // Always — ring visible over terrain/other objects
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

        // Cone: apex at (0,1,0), base circle at y=0, radius 0.5.
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

        // Flat ring (annulus) in XZ plane with front and back faces.
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
