using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
//using static Il2CppConstGenConstants._SHADERPROPS;

namespace BabyBlocks
{
    // Static helpers for constructing and configuring ghost cube GameObjects.
    static class GhostCubeConfig
    {
        static Material _frameMaterial;

        public static void Configure(GameObject go)
        {
            if (go == null) return;

            foreach (var renderer in go.GetComponentsInChildren<MeshRenderer>(true))
            {
                renderer.forceRenderingOff = true;
                renderer.enabled = false;
            }

            BuildFrame(go);

            var box = go.GetComponent<BoxCollider>();
            if (box == null) box = go.AddComponent<BoxCollider>();
            box.center    = Vector3.zero;
            box.size      = Vector3.one;
            box.isTrigger = true;

            if (go.GetComponent<GhostCollisionCutter>() == null)
                go.AddComponent<GhostCollisionCutter>();
        }

        internal static void BuildFrame(GameObject root)
        {
            if (root == null) return;

            var mat = GetFrameMaterial();
            if (mat == null) return;

            var edges = new (Vector3 a, Vector3 b)[]
            {
                (new Vector3(-0.5f, -0.5f, -0.5f), new Vector3( 0.5f, -0.5f, -0.5f)),
                (new Vector3( 0.5f, -0.5f, -0.5f), new Vector3( 0.5f, -0.5f,  0.5f)),
                (new Vector3( 0.5f, -0.5f,  0.5f), new Vector3(-0.5f, -0.5f,  0.5f)),
                (new Vector3(-0.5f, -0.5f,  0.5f), new Vector3(-0.5f, -0.5f, -0.5f)),
                (new Vector3(-0.5f,  0.5f, -0.5f), new Vector3( 0.5f,  0.5f, -0.5f)),
                (new Vector3( 0.5f,  0.5f, -0.5f), new Vector3( 0.5f,  0.5f,  0.5f)),
                (new Vector3( 0.5f,  0.5f,  0.5f), new Vector3(-0.5f,  0.5f,  0.5f)),
                (new Vector3(-0.5f,  0.5f,  0.5f), new Vector3(-0.5f,  0.5f, -0.5f)),
                (new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(-0.5f,  0.5f, -0.5f)),
                (new Vector3( 0.5f, -0.5f, -0.5f), new Vector3( 0.5f,  0.5f, -0.5f)),
                (new Vector3( 0.5f, -0.5f,  0.5f), new Vector3( 0.5f,  0.5f,  0.5f)),
                (new Vector3(-0.5f, -0.5f,  0.5f), new Vector3(-0.5f,  0.5f,  0.5f)),
            };

            var existing = root.transform.Find("GhostFrame");
            if (existing != null) UnityEngine.Object.Destroy(existing.gameObject);

            var frameRoot = new GameObject("GhostFrame");
            frameRoot.transform.SetParent(root.transform, false);
            frameRoot.layer = root.layer;

            for (int i = 0; i < edges.Length; i++)
            {
                var edgeGo = new GameObject($"Edge_{i}");
                edgeGo.transform.SetParent(frameRoot.transform, false);
                edgeGo.layer = root.layer;

                var line = edgeGo.AddComponent<LineRenderer>();
                line.useWorldSpace     = false;
                line.positionCount     = 2;
                line.SetPosition(0, edges[i].a);
                line.SetPosition(1, edges[i].b);
                line.startWidth        = 0.01f;
                line.endWidth          = 0.01f;
                line.numCapVertices    = 0;
                line.numCornerVertices = 0;
                line.alignment         = LineAlignment.View;
                line.material          = mat;
                line.startColor        = new Color(1f, 0.95f, 0.3f, 0.9f);
                line.endColor          = new Color(1f, 0.95f, 0.3f, 0.9f);
            }
        }

        static Material GetFrameMaterial()
        {
            if (_frameMaterial != null) return _frameMaterial;
            var shader = Shader.Find("Sprites/Default")
                      ?? Shader.Find("Unlit/Color")
                      ?? Shader.Find("Standard");
            _frameMaterial = new Material(shader) { name = "BabyBlocks_GhostFrame", renderQueue = 5000 };
            return _frameMaterial;
        }
    }

    public class GhostCollisionCutter : MonoBehaviour
    {
        public GhostCollisionCutter(IntPtr ptr) : base(ptr) { }

        /*
        struct CarvedMeshState
        {
            public Mesh   originalMesh;
            public Mesh   carvedMesh;
            public string carveKey;
            public bool   originalConvex;
            public bool   IsValid => originalMesh != null;
        }
        */

        struct CarvedTerrainState
        {
            public TerrainData originalData;
            public Il2CppSystem.Array originalHoles;
            public int         xBase;
            public int         yBase;
            public int         width;
            public int         height;
            public string      carveKey;
            public bool        IsValid => originalData != null && originalHoles != null;
        }

        // static readonly Dictionary<MeshCollider, CarvedMeshState> _carvedColliders = new();
        // static readonly Dictionary<MeshFilter,   CarvedMeshState> _carvedFilters   = new();
        static readonly Dictionary<UnityEngine.Terrain,      CarvedTerrainState> _carvedTerrains = new();

        // All enabled cutters, so the editor-mode-transition / post-load hooks can
        // drive a one-shot collider carve pass across every ghost cube at once.
        static readonly List<GhostCollisionCutter> _instances = new();

        // readonly HashSet<Transform> _activeRoots = new();
        BoxCollider _volume;

        // Extra margin added to the overlap-detection box (but NOT the carve box)
        // to stabilize Physics.OverlapBox results for colliders near the boundary.
        // const float DetectionPadding = 0.05f;

        // Cached cutter pose used to debounce carve rebuilds — tiny per-frame
        // float jitter in the transform must not trigger a restore+reapply
        // cycle (which manifests as the terrain holes flickering on/off).
        const float PositionTolerance = 0.01f;
        const float AngleTolerance    = 0.05f;

        bool       _hasLastCarvePose;
        Vector3    _lastCenter;
        Vector3    _lastHalfExtents;
        Quaternion _lastRotation;
        string     _lastCarveKey;

        Transform _frameRoot;

        void Awake()
        {
            _volume    = GetComponent<BoxCollider>();
            if (_volume != null) _volume.isTrigger = true;

            _frameRoot = transform.Find("GhostFrame");
        }

        void OnEnable()
        {
            _instances.Add(this);
            Refresh();
        }

        void FixedUpdate() => Refresh();
        void Update()      => UpdateFrameVisibility();

        void OnDisable()
        {
            _instances.Remove(this);
            ReleaseAll();
        }

        void OnDestroy()
        {
            _instances.Remove(this);
            ReleaseAll();
        }

        // The yellow wireframe is only useful while editing - hide it during
        // normal gameplay so it doesn't show up around hole props in-game.
        void UpdateFrameVisibility()
        {
            if (_frameRoot == null) _frameRoot = transform.Find("GhostFrame");
            if (_frameRoot == null) return;

            bool editorActive = FlyCamController.FlyCamActive && FlyCamController.CursorMode;
            if (_frameRoot.gameObject.activeSelf != editorActive)
                _frameRoot.gameObject.SetActive(editorActive);
        }

        void Refresh()
        {
            if (_volume == null) _volume = GetComponent<BoxCollider>();
            if (_volume == null || !gameObject.activeInHierarchy) return;

            var scale       = transform.lossyScale;
            var worldCenter = transform.TransformPoint(_volume.center);
            var halfExtents = Vector3.Scale(_volume.size * 0.5f,
                new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            var rotation    = transform.rotation;

            string carveKey = GetDebouncedCarveKey(worldCenter, halfExtents, rotation);
            RefreshTerrains(carveKey, worldCenter, rotation, halfExtents);
        }

        /*
        // One-shot mesh-collider carve pass: finds every prop currently overlapping
        // this cutter and carves its MeshColliders, restoring any previously-carved
        // props that no longer overlap. Call via BakeAllColliderCarves.
        void BakeColliderCarve()
        {
            if (_volume == null) _volume = GetComponent<BoxCollider>();
            if (_volume == null || !gameObject.activeInHierarchy) return;

            var scale       = transform.lossyScale;
            var worldCenter = transform.TransformPoint(_volume.center);
            var halfExtents = Vector3.Scale(_volume.size * 0.5f,
                new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            var rotation    = transform.rotation;
            string carveKey = BuildCarveKey(worldCenter, halfExtents, rotation);

            // Pad the detection volume slightly beyond the visual/carve box so props
            // sitting almost exactly on the boundary are still picked up.
            var detectHalfExtents = halfExtents + Vector3.one * DetectionPadding;

            var nextRoots = new HashSet<Transform>();
            var hits = Physics.OverlapBox(worldCenter, detectHalfExtents, rotation, ~0, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < hits.Length; i++)
            {
                var hit = hits[i];
                if (hit == null || hit.isTrigger) continue;
                if (IsOwnCollider(hit) || IsPlayerCollider(hit)) continue;

                var root = hit.attachedRigidbody != null ? hit.attachedRigidbody.transform : hit.transform.root;
                if (root == null || IsOwnTransform(root) || IsPlayerTransform(root)) continue;
                nextRoots.Add(root);
            }

            foreach (var root in _activeRoots)
                if (!nextRoots.Contains(root)) RestoreRoot(root);

            _activeRoots.Clear();
            foreach (var root in nextRoots)
            {
                _activeRoots.Add(root);
                UpdateRootCarve(root, carveKey);
            }
        }

        // Restores every prop this cutter has carved, without recomputing overlap —
        // used when entering editor mode so carved-away geometry doesn't get in the
        // way of moving/selecting props.
        void RestoreColliderCarve()
        {
            if (_activeRoots.Count == 0) return;
            foreach (var root in _activeRoots) RestoreRoot(root);
            _activeRoots.Clear();
        }

        // Runs BakeColliderCarve for every enabled ghost cube. Called when leaving
        // editor mode and after a save load settles.
        internal static void BakeAllColliderCarves()
        {
            foreach (var inst in _instances) inst?.BakeColliderCarve();
        }

        // Runs RestoreColliderCarve for every enabled ghost cube. Called when
        // entering editor mode.
        internal static void RestoreAllColliderCarves()
        {
            foreach (var inst in _instances) inst?.RestoreColliderCarve();
        }
        */

        // Terrains can't be discovered through Physics.OverlapBox like other roots:
        // carving calls TerrainData.SetHoles, which makes Unity rebuild the
        // TerrainCollider's physics representation over the next few frames. While
        // that rebuild is in flight, OverlapBox stops reporting the collider, which
        // (with the old physics-driven logic) looked like the cutter moved away —
        // triggering a restore, which triggers another SetHoles, which triggers
        // another rebuild... a self-sustaining flicker loop entirely of our own
        // making. Instead, find/track terrains via Terrain.activeTerrains and decide
        // overlap with a plain geometric bounds test that doesn't depend on collider
        // state at all.
        void RefreshTerrains(string carveKey, Vector3 cutterCenter, Quaternion cutterRotation, Vector3 cutterHalfExtents)
        {
            var terrains = Terrain.activeTerrains;
            if (terrains == null) return;

            var overlapping = new HashSet<Terrain>();
            for (int i = 0; i < terrains.Length; i++)
            {
                var terrain = terrains[i];
                if (terrain == null || terrain.terrainData == null) continue;
                if (!CutterOverlapsTerrainBounds(terrain, cutterCenter, cutterRotation, cutterHalfExtents)) continue;

                overlapping.Add(terrain);
                ApplyTerrainCarve(terrain, carveKey, cutterCenter, cutterRotation, cutterHalfExtents);
            }

            if (_carvedTerrains.Count == 0) return;

            var toRestore = new List<Terrain>();
            foreach (var kv in _carvedTerrains)
                if (!overlapping.Contains(kv.Key)) toRestore.Add(kv.Key);

            foreach (var terrain in toRestore) RestoreTerrain(terrain);
        }

        // Cheap world-space AABB-vs-OBB overlap test (terrains are axis-aligned and
        // unrotated, so this is just the cutter corners projected into terrain-local
        // space — the same bounds check TryBuildTerrainHolePatch performs before
        // building a patch, factored out so RefreshTerrains can use it standalone).
        static bool CutterOverlapsTerrainBounds(Terrain terrain, Vector3 cutterCenter, Quaternion cutterRotation, Vector3 cutterHalfExtents)
        {
            var terrainTransform = terrain.transform;
            var size = terrain.terrainData.size;

            var corners = new Vector3[8];
            int index = 0;
            var half = cutterHalfExtents;
            for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                    {
                        var offset = new Vector3(half.x * x, half.y * y, half.z * z);
                        var worldCorner = cutterCenter + cutterRotation * offset;
                        corners[index++] = terrainTransform.InverseTransformPoint(worldCorner);
                    }

            float minX = corners[0].x, maxX = corners[0].x;
            float minZ = corners[0].z, maxZ = corners[0].z;
            for (int i = 1; i < corners.Length; i++)
            {
                minX = Mathf.Min(minX, corners[i].x);
                maxX = Mathf.Max(maxX, corners[i].x);
                minZ = Mathf.Min(minZ, corners[i].z);
                maxZ = Mathf.Max(maxZ, corners[i].z);
            }

            return maxX >= 0f && maxZ >= 0f && minX <= size.x && minZ <= size.z;
        }

        void ReleaseAll()
        {
            _hasLastCarvePose = false;

            // RestoreColliderCarve();

            // Terrains are tracked independently of _activeRoots (see RefreshTerrains),
            // so they need to be restored here too — otherwise deleting/disabling the
            // ghost cube would leave its carved holes behind permanently.
            if (_carvedTerrains.Count > 0)
            {
                var terrains = new List<Terrain>(_carvedTerrains.Keys);
                foreach (var terrain in terrains) RestoreTerrain(terrain);
            }
        }

        /*
        void UpdateRootCarve(Transform root, string carveKey)
        {
            if (root == null) return;

            var meshColliders = root.GetComponentsInChildren<MeshCollider>(true);

            for (int i = 0; meshColliders != null && i < meshColliders.Length; i++)
            {
                var mc = meshColliders[i];
                if (mc == null || mc.isTrigger || mc == _volume || mc.sharedMesh == null) continue;
                ApplyCarve(mc, carveKey);
            }

            // Terrains are handled separately via RefreshTerrains — see its comment
            // for why they can't be driven off Physics.OverlapBox roots.

            // Visual (render-mesh) carving disabled — it doesn't produce a usable result,
            // so only the collision mesh is carved. Leave renderers untouched.
        }

        void RestoreRoot(Transform root)
        {
            if (root == null) return;

            var meshColliders = root.GetComponentsInChildren<MeshCollider>(true);
            if (meshColliders != null)
            {
                for (int i = 0; i < meshColliders.Length; i++)
                {
                    var mc = meshColliders[i];
                    if (mc == null) continue;
                    if (!_carvedColliders.TryGetValue(mc, out var st) || !st.IsValid) continue;
                    mc.sharedMesh = st.originalMesh;
                    mc.convex     = st.originalConvex;
                    if (st.carvedMesh != null) UnityEngine.Object.Destroy(st.carvedMesh);
                    _carvedColliders.Remove(mc);
                }
            }

            // Terrains are restored by RefreshTerrains, not here — see its comment.

            var meshFilters = root.GetComponentsInChildren<MeshFilter>(true);
            if (meshFilters == null) return;
            for (int i = 0; i < meshFilters.Length; i++)
            {
                var mf = meshFilters[i];
                if (mf == null) continue;
                if (!_carvedFilters.TryGetValue(mf, out var st) || !st.IsValid) continue;
                mf.sharedMesh = st.originalMesh;
                if (st.carvedMesh != null) UnityEngine.Object.Destroy(st.carvedMesh);
                _carvedFilters.Remove(mf);
            }
        }

        // Use the BoxCollider's LOCAL half-extents (matching original behaviour).
        // OverlapBox uses scale-adjusted extents for detection, but the mesh-space
        // cut uses the collider's own size so the carved boundary stays consistent
        // with the box regardless of object scale.
        Vector3 CutterCenter    => transform.TransformPoint(_volume.center);
        Vector3 CutterHalfSize  => _volume.size * 0.5f;

        void ApplyCarve(MeshCollider collider, string carveKey)
        {
            if (collider == null || collider.sharedMesh == null) return;

            if (!_carvedColliders.TryGetValue(collider, out var state) || !state.IsValid)
                state = new CarvedMeshState { originalMesh = collider.sharedMesh, originalConvex = collider.convex };

            if (!string.IsNullOrEmpty(state.carveKey)
             && string.Equals(state.carveKey, carveKey, StringComparison.Ordinal)
             && state.carvedMesh != null)
                return;

            if (state.carvedMesh != null) { UnityEngine.Object.Destroy(state.carvedMesh); state.carvedMesh = null; }

            var carved = BuildCarvedMeshPhysics(state.originalMesh, collider.transform, transform, CutterHalfSize);
            if (carved == null) return;

            collider.sharedMesh        = carved;
            collider.convex            = false;
            state.carvedMesh           = carved;
            state.carveKey             = carveKey;
            _carvedColliders[collider] = state;
        }

        void ApplyVisualCarve(MeshFilter filter, string carveKey)
        {
            if (filter == null || filter.sharedMesh == null) return;

            if (!_carvedFilters.TryGetValue(filter, out var state) || !state.IsValid)
                state = new CarvedMeshState { originalMesh = filter.sharedMesh };

            if (!string.IsNullOrEmpty(state.carveKey)
             && string.Equals(state.carveKey, carveKey, StringComparison.Ordinal)
             && state.carvedMesh != null)
                return;

            if (state.carvedMesh != null) { UnityEngine.Object.Destroy(state.carvedMesh); state.carvedMesh = null; }

            var carved = BuildCarvedMeshVisual(state.originalMesh, filter.transform, CutterCenter, transform.rotation, CutterHalfSize);
            if (carved == null) return;

            filter.sharedMesh       = carved;
            state.carvedMesh        = carved;
            state.carveKey          = carveKey;
            _carvedFilters[filter]  = state;
        }
        */

        void ApplyTerrainCarve(Terrain terrain, string carveKey, Vector3 cutterCenter, Quaternion cutterRotation, Vector3 cutterHalfExtents)
        {
            if (terrain == null || terrain.terrainData == null) return;

            var data = terrain.terrainData;
            int resolution = data.holesResolution;
            if (resolution <= 0) return;

            bool hadState   = _carvedTerrains.TryGetValue(terrain, out var state);
            bool sameNative = hadState && IsSameNativeObject(state.originalData, data);

            if (!hadState || !state.IsValid || !sameNative)
            {
                state = new CarvedTerrainState
                {
                    originalData  = data,
                    originalHoles = data.GetHoles(0, 0, resolution, resolution).Cast<Il2CppSystem.Array>()
                };
            }

            if (!string.IsNullOrEmpty(state.carveKey)
             && string.Equals(state.carveKey, carveKey, StringComparison.Ordinal))
                return;

            if (!string.IsNullOrEmpty(state.carveKey))
                RestoreTerrain(terrain, state);

            if (!TryBuildTerrainHolePatch(terrain, data, state.originalHoles, cutterCenter, cutterRotation, cutterHalfExtents,
                out int xBase, out int yBase, out Il2CppSystem.Array holes))
            {
                // Nothing to carve (e.g. the box is hovering above the terrain
                // surface) — clear carveKey so we don't keep calling RestoreTerrain
                // every frame for a region that's already restored.
                // Only keep the dict entry if we had one before (originalHoles/originalData
                // cached). A brand-new entry with nothing carved would have width=height=0,
                // which makes ExtractTerrainPatch return null and SetHoles crash on release.
                if (hadState)
                {
                    state.carveKey = null;
                    state.xBase = state.yBase = state.width = state.height = 0;
                    _carvedTerrains[terrain] = state;
                }
                return;
            }

            data.SetHoles(xBase, yBase, holes);

            state.xBase   = xBase;
            state.yBase   = yBase;
            state.width   = holes.GetLength(1);
            state.height  = holes.GetLength(0);
            state.carveKey = carveKey;
            _carvedTerrains[terrain] = state;
        }

        void RestoreTerrain(Terrain terrain)
        {
            if (terrain == null) return;
            if (!_carvedTerrains.TryGetValue(terrain, out var state) || !state.IsValid) return;

            RestoreTerrain(terrain, state);
            _carvedTerrains.Remove(terrain);
        }

        static void RestoreTerrain(Terrain terrain, CarvedTerrainState state)
        {
            if (terrain == null || !state.IsValid) return;
            var patch = ExtractTerrainPatch(state.originalHoles, state.xBase, state.yBase, state.width, state.height);
            if (patch == null) return;
            state.originalData.SetHoles(state.xBase, state.yBase, patch);
        }

        static bool TryBuildTerrainHolePatch(Terrain terrain, TerrainData data, Il2CppSystem.Array originalHoles,
            Vector3 cutterCenter, Quaternion cutterRotation, Vector3 cutterHalfExtents,
            out int xBase, out int yBase, out Il2CppSystem.Array holes)
        {
            xBase = 0;
            yBase = 0;
            holes = null;

            if (terrain == null || data == null || originalHoles == null) return false;

            int resolution = data.holesResolution;
            if (resolution <= 0) return false;

            var terrainTransform = terrain.transform;
            var size = data.size;

            var corners = new Vector3[8];
            int index = 0;
            var half = cutterHalfExtents;
            for (int x = -1; x <= 1; x += 2)
            {
                for (int y = -1; y <= 1; y += 2)
                {
                    for (int z = -1; z <= 1; z += 2)
                    {
                        var offset = new Vector3(half.x * x, half.y * y, half.z * z);
                        var worldCorner = cutterCenter + cutterRotation * offset;
                        corners[index++] = terrainTransform.InverseTransformPoint(worldCorner);
                    }
                }
            }

            float minX = corners[0].x, maxX = corners[0].x;
            float minZ = corners[0].z, maxZ = corners[0].z;
            for (int i = 1; i < corners.Length; i++)
            {
                minX = Mathf.Min(minX, corners[i].x);
                maxX = Mathf.Max(maxX, corners[i].x);
                minZ = Mathf.Min(minZ, corners[i].z);
                maxZ = Mathf.Max(maxZ, corners[i].z);
            }

            if (maxX < 0f || maxZ < 0f || minX > size.x || minZ > size.z) return false;

            minX = Mathf.Clamp(minX, 0f, size.x);
            maxX = Mathf.Clamp(maxX, 0f, size.x);
            minZ = Mathf.Clamp(minZ, 0f, size.z);
            maxZ = Mathf.Clamp(maxZ, 0f, size.z);

            int x0 = Mathf.Clamp(Mathf.FloorToInt((minX / size.x) * (resolution - 1)), 0, resolution - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((maxX / size.x) * (resolution - 1)), 0, resolution - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt((minZ / size.z) * (resolution - 1)), 0, resolution - 1);
            int y1 = Mathf.Clamp(Mathf.CeilToInt((maxZ / size.z) * (resolution - 1)), 0, resolution - 1);

            int width  = x1 - x0 + 1;
            int height = y1 - y0 + 1;
            if (width <= 0 || height <= 0) return false;

            xBase = x0;
            yBase = y0;
            holes = Il2CppSystem.Array.CreateInstance(
            Il2CppType.Of<bool>(),
            new Il2CppStructArray<long>(new long[] { height, width })
            );

            // Only punch a hole where the cutter box actually intersects the terrain
            // surface — sample the terrain height at each grid point and test it
            // against the cutter's 3D bounds (in cutter-local space, so rotation is
            // respected). Without this, every cell within the XZ footprint became a
            // hole regardless of how high above (or below) the terrain the cube was.
            var invRot      = Quaternion.Inverse(cutterRotation);
            int resMinus1   = Mathf.Max(resolution - 1, 1);
            bool anyHole    = false;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int gx = x0 + x;
                    int gy = y0 + y;

                    float normX = (float)gx / resMinus1;
                    float normZ = (float)gy / resMinus1;
                    float terrainHeight = data.GetInterpolatedHeight(normX, normZ);

                    var localPos = new Vector3(normX * size.x, terrainHeight, normZ * size.z);
                    var worldPos = terrainTransform.TransformPoint(localPos);
                    var cutterLocal = invRot * (worldPos - cutterCenter);

                    bool isHole = PointInsideBox(cutterLocal, cutterHalfExtents);
                    if (isHole) anyHole = true;

                    holes.SetValue(isHole ? false : originalHoles.GetValue(gy, gx), y, x);
                }
            }

            if (!anyHole)
            {
                holes = null;
                return false;
            }

            return true;
        }

        static Il2CppSystem.Array ExtractTerrainPatch(
    Il2CppSystem.Array source,
    int xBase,
    int yBase,
    int width,
    int height)
        {
            if (source == null || width <= 0 || height <= 0)
                return null;

            var patch = Il2CppSystem.Array.CreateInstance(
                Il2CppType.Of<bool>(),
                new Il2CppStructArray<long>(new long[] { height, width })
            );

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    patch.SetValue(
                        source.GetValue(yBase + y, xBase + x),
                        y,
                        x
                    );
                }
            }

            return patch;
        }

        // Il2Cpp interop returns a fresh managed wrapper on every property access,
        // so reference/`!=` comparison on Il2Cpp objects (e.g. terrain.terrainData)
        // is always "different" even when it's the same native object — comparing
        // by native pointer is the correct way to detect an actual change. Without
        // this, CarvedTerrainState was rebuilt from scratch every frame, discarding
        // the cached carve key (and re-snapshotting already-carved holes as
        // "original"), which produced the continuous restore/reapply flicker.
        static bool IsSameNativeObject(Il2CppObjectBase a, Il2CppObjectBase b)
        {
            if (a == null || b == null) return false;
            return a.Pointer == b.Pointer;
        }

        static string BuildCarveKey(Vector3 center, Vector3 half, Quaternion rot)
            => $"{center.x:F3},{center.y:F3},{center.z:F3}|{half.x:F3},{half.y:F3},{half.z:F3}|{rot.x:F3},{rot.y:F3},{rot.z:F3},{rot.w:F3}";

        // Returns the carve key to use this frame, only recomputing it (and thus
        // only triggering a carve rebuild) when the cutter's position, size, or
        // rotation has actually moved beyond a small tolerance. Without this,
        // sub-millimeter float jitter changes the key every frame and causes the
        // carved holes to be restored and reapplied continuously (visible as flicker).
        string GetDebouncedCarveKey(Vector3 center, Vector3 halfExtents, Quaternion rotation)
        {
            if (_hasLastCarvePose
             && Vector3.Distance(center, _lastCenter) <= PositionTolerance
             && Vector3.Distance(halfExtents, _lastHalfExtents) <= PositionTolerance
             && Quaternion.Angle(rotation, _lastRotation) <= AngleTolerance)
            {
                return _lastCarveKey;
            }

            string newKey = BuildCarveKey(center, halfExtents, rotation);

            _lastCenter       = center;
            _lastHalfExtents  = halfExtents;
            _lastRotation     = rotation;
            _lastCarveKey     = newKey;
            _hasLastCarvePose = true;
            return _lastCarveKey;
        }

        /*
        // ── Physics carve ────────────────────────────────────────────────────────
        // Exact original algorithm: flat triangle loop, centroid+vertex inside test,
        // single-submesh output. Signature mirrors the original to preserve behaviour.
        static Mesh BuildCarvedMeshPhysics(Mesh source, Transform sourceTransform,
            Transform cutterTransform, Vector3 cutterHalfExtents)
        {
            if (source == null || sourceTransform == null || cutterTransform == null)
                return null;

            try
            {
                bool posOk = false;
                foreach (var a in source.GetVertexAttributes())
                {
                    if (a.attribute == VertexAttribute.Position
                     && a.format    == VertexAttributeFormat.Float32
                     && a.dimension == 3
                     && a.stream    == 0)
                    { posOk = true; break; }
                }
                if (!posOk) return null;

                var vb        = source.GetVertexBuffer(0);
                int floatsPerV = vb.stride / 4;
                int vCount     = source.vertexCount;
                var floatBuf   = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<float>(vCount * floatsPerV);
                vb.GetData(floatBuf.Cast<Il2CppSystem.Array>());
                vb.Release();

                var positions = new Vector3[vCount];
                for (int i = 0; i < vCount; i++)
                {
                    int b = i * floatsPerV;
                    positions[i] = new Vector3(floatBuf[b], floatBuf[b + 1], floatBuf[b + 2]);
                }

                var ib     = source.GetIndexBuffer();
                int iCount = ib.count;
                int[] tris = new int[iCount];
                if (ib.stride == 2)
                {
                    var buf = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ushort>(iCount);
                    ib.GetData(buf.Cast<Il2CppSystem.Array>());
                    for (int i = 0; i < iCount; i++) tris[i] = buf[i];
                }
                else
                {
                    var buf = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(iCount);
                    ib.GetData(buf.Cast<Il2CppSystem.Array>());
                    for (int i = 0; i < iCount; i++) tris[i] = buf[i];
                }

                var kept           = new List<int>(tris.Length);
                var cutterCenter   = cutterTransform.position;
                var cutterRotation = cutterTransform.rotation;
                var half           = cutterHalfExtents;

                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    Vector3 a = sourceTransform.TransformPoint(positions[tris[i]]);
                    Vector3 b = sourceTransform.TransformPoint(positions[tris[i + 1]]);
                    Vector3 c = sourceTransform.TransformPoint(positions[tris[i + 2]]);

                    var invRot = Quaternion.Inverse(cutterRotation);
                    Vector3 la = invRot * (a - cutterCenter);
                    Vector3 lb = invRot * (b - cutterCenter);
                    Vector3 lc = invRot * (c - cutterCenter);

                    Vector3 centroid = (la + lb + lc) * (1f / 3f);
                    bool centroidInside = PointInsideBox(centroid, half);
                    bool anyVertexInside = PointInsideBox(la, half)
                                        || PointInsideBox(lb, half)
                                        || PointInsideBox(lc, half);

                    if (centroidInside || anyVertexInside)
                        continue;

                    kept.Add(tris[i]);
                    kept.Add(tris[i + 1]);
                    kept.Add(tris[i + 2]);
                }

                if (kept.Count == 0)
                {
                    ib.Release();
                    return null;
                }

                var carved = new Mesh { name = source.name + "_ghostcarve" };
                carved.vertices  = positions;
                carved.triangles = kept.ToArray();
                carved.RecalculateNormals();
                carved.RecalculateBounds();
                ib.Release();
                return carved;
            }
            catch
            {
                return null;
            }
        }

        // ── Visual carve ─────────────────────────────────────────────────────────
        // Per-submesh filtering preserves material slot assignments.
        // Normals and UV0 are copied from the raw vertex buffer (bypasses read-write
        // restrictions) so textures and lighting remain correct on carved geometry.
        static Mesh BuildCarvedMeshVisual(Mesh source, Transform sourceTransform,
            Vector3 cutterCenter, Quaternion cutterRotation, Vector3 cutterHalfExtents)
        {
            if (source == null || sourceTransform == null) return null;

            try
            {
                bool posOk       = false;
                int  normalFIdx  = -1;
                int  uv0FIdx     = -1;
                int  floatCursor = 0;

                foreach (var a in source.GetVertexAttributes())
                {
                    if (a.stream != 0) { floatCursor += AttrFloatStride(a); continue; }

                    if (a.attribute == VertexAttribute.Position
                     && a.format    == VertexAttributeFormat.Float32
                     && a.dimension == 3)
                        posOk = true;

                    if (a.format == VertexAttributeFormat.Float32)
                    {
                        if (a.attribute == VertexAttribute.Normal && a.dimension == 3)
                            normalFIdx = floatCursor;
                        else if (a.attribute == VertexAttribute.TexCoord0 && a.dimension == 2)
                            uv0FIdx = floatCursor;
                    }

                    floatCursor += AttrFloatStride(a);
                }

                if (!posOk) return null;

                var vb        = source.GetVertexBuffer(0);
                int floatsPerV = vb.stride / 4;
                int vCount     = source.vertexCount;
                var floatBuf   = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<float>(vCount * floatsPerV);
                vb.GetData(floatBuf.Cast<Il2CppSystem.Array>());
                vb.Release();

                var positions = new Vector3[vCount];
                for (int i = 0; i < vCount; i++)
                {
                    int b = i * floatsPerV;
                    positions[i] = new Vector3(floatBuf[b], floatBuf[b + 1], floatBuf[b + 2]);
                }

                var ib     = source.GetIndexBuffer();
                int iCount = ib.count;
                int[] tris = new int[iCount];
                if (ib.stride == 2)
                {
                    var buf = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ushort>(iCount);
                    ib.GetData(buf.Cast<Il2CppSystem.Array>());
                    for (int i = 0; i < iCount; i++) tris[i] = buf[i];
                }
                else
                {
                    var buf = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(iCount);
                    ib.GetData(buf.Cast<Il2CppSystem.Array>());
                    for (int i = 0; i < iCount; i++) tris[i] = buf[i];
                }
                ib.Release();

                var invRot   = Quaternion.Inverse(cutterRotation);
                int subCount = source.subMeshCount;
                var keptPerSub = new List<int>[subCount];
                for (int s = 0; s < subCount; s++) keptPerSub[s] = new List<int>();

                for (int s = 0; s < subCount; s++)
                {
                    var sm   = source.GetSubMesh(s);
                    int end  = sm.indexStart + sm.indexCount;
                    var kept = keptPerSub[s];

                    for (int i = sm.indexStart; i + 2 < end; i += 3)
                    {
                        Vector3 wA = sourceTransform.TransformPoint(positions[tris[i]]);
                        Vector3 wB = sourceTransform.TransformPoint(positions[tris[i + 1]]);
                        Vector3 wC = sourceTransform.TransformPoint(positions[tris[i + 2]]);

                        if (PointInsideBox(invRot * (wA - cutterCenter), cutterHalfExtents) ||
                            PointInsideBox(invRot * (wB - cutterCenter), cutterHalfExtents) ||
                            PointInsideBox(invRot * (wC - cutterCenter), cutterHalfExtents))
                            continue;

                        kept.Add(tris[i]);
                        kept.Add(tris[i + 1]);
                        kept.Add(tris[i + 2]);
                    }
                }

                bool anyKept = false;
                for (int s = 0; s < subCount; s++)
                    if (keptPerSub[s].Count > 0) { anyKept = true; break; }
                if (!anyKept) return null;

                var carved = new Mesh { name = source.name + "_ghostcarve" };
                carved.vertices = positions;

                if (normalFIdx >= 0)
                {
                    var normals = new Vector3[vCount];
                    for (int i = 0; i < vCount; i++)
                    {
                        int b = i * floatsPerV + normalFIdx;
                        normals[i] = new Vector3(floatBuf[b], floatBuf[b + 1], floatBuf[b + 2]);
                    }
                    carved.normals = normals;
                }

                if (uv0FIdx >= 0)
                {
                    var uvs = new Vector2[vCount];
                    for (int i = 0; i < vCount; i++)
                    {
                        int b = i * floatsPerV + uv0FIdx;
                        uvs[i] = new Vector2(floatBuf[b], floatBuf[b + 1]);
                    }
                    carved.uv = uvs;
                }

                carved.subMeshCount = subCount;
                for (int s = 0; s < subCount; s++)
                    carved.SetTriangles(keptPerSub[s].ToArray(), s);

                if (normalFIdx < 0) carved.RecalculateNormals();
                carved.RecalculateBounds();
                return carved;
            }
            catch
            {
                return null;
            }
        }

        // Returns how many 4-byte (float32) words |attr| occupies in the vertex buffer.
        // This lets us walk stream-0 as a flat float[] regardless of mixed attribute formats.
        static int AttrFloatStride(VertexAttributeDescriptor attr)
        {
            int byteSize;
            switch (attr.format)
            {
                case VertexAttributeFormat.Float16:
                case VertexAttributeFormat.UNorm16:
                case VertexAttributeFormat.SNorm16:
                case VertexAttributeFormat.UInt16:
                case VertexAttributeFormat.SInt16:
                    byteSize = attr.dimension * 2; break;
                case VertexAttributeFormat.UNorm8:
                case VertexAttributeFormat.SNorm8:
                case VertexAttributeFormat.UInt8:
                case VertexAttributeFormat.SInt8:
                    byteSize = attr.dimension; break;
                default: // Float32, UInt32, SInt32
                    byteSize = attr.dimension * 4; break;
            }
            return (byteSize + 3) / 4;
        }
        */

        static bool PointInsideBox(Vector3 local, Vector3 half)
            => Mathf.Abs(local.x) <= half.x
            && Mathf.Abs(local.y) <= half.y
            && Mathf.Abs(local.z) <= half.z;

        /*
        bool IsOwnCollider(Collider c)  => c != null && IsOwnTransform(c.transform);
        bool IsOwnTransform(Transform t) => t != null && (t == transform || t.IsChildOf(transform));

        bool IsPlayerCollider(Collider c)  => c != null && PlayerMovement.me != null && IsPlayerTransform(c.transform);
        bool IsPlayerTransform(Transform t) => t != null && PlayerMovement.me != null
            && (t == PlayerMovement.me.transform || t.IsChildOf(PlayerMovement.me.transform));
        */
    }
}
