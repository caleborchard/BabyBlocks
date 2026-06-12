using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppNWH.DWP2.WaterObjects;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace BabyBlocks
{
    public class LevelEditorManager : MonoBehaviour
    {
        public LevelEditorManager(IntPtr ptr) : base(ptr) { }

        const float WorldLoopSize = 512f;
        const float ChunkWorldSize = 64f;
        const int ChunksPerAxis = (int)(WorldLoopSize / ChunkWorldSize);

        public static bool ChunkLoopingEnabled = true;
        public static bool BaseMapEnabled = true;

        // Selected WeatherMan "Day Weather Playlist" (Menu.curChapter index) applied
        // while the base map is hidden. Edited via the Save/Load window dropdown,
        // which is only enabled while BaseMapEnabled == false.
        public static int DayWeatherPlaylist;
        // Menu.curChapter value to restore once the base map is shown again.
        public static int RestoreDayWeatherPlaylist;
        static bool _weatherPlaylistOverridden;
        const float PhysicsActiveRadius    = 25f;
        const float PhysicsActiveRadiusSqr = PhysicsActiveRadius * PhysicsActiveRadius;

        public static LevelEditorManager Instance { get; private set; }

        readonly List<LevelEditorObject> _objects = new();
        // Keyed by groupId. Using a dictionary avoids the Il2Cpp wrapper-equality pitfall where
        // List<GameObject>.Contains() always returns false because each access returns a new
        // managed wrapper for the same native object.
        readonly Dictionary<int, GameObject> _groupRoots = new();
        int _nextGroupId = 1;
        bool _editorModeActive;
        GameObject _propsContainer;
        static readonly HashSet<int> _physicsControlSeen = new();
        readonly List<LevelEditorObject> _heldObjectsToRestore = new();
        readonly List<Vector3> _heldScalesToRestore = new();

        [HideFromIl2Cpp]
        internal IReadOnlyList<LevelEditorObject> Objects => _objects;

        // Group-root helpers
        GameObject GetGroupRoot(int groupId)
        {
            if (groupId <= 0) return null;
            _groupRoots.TryGetValue(groupId, out var r);
            return r != null ? r : null;
        }
        void SetGroupRoot(int groupId, GameObject root)  { if (groupId > 0 && root != null) _groupRoots[groupId] = root; }
        void RemoveGroupRoot(int groupId) => _groupRoots.Remove(groupId);

        public void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            _propsContainer = new GameObject("Baby Blocks");

            if (!PropLibrary.IsInitialized) PropLibrary.Init();
        }

        // Physics-managed props (Rigidbody/Grabable/Hat — see LevelEditorObject.isPhysicsManaged)
        // get moved by native game code into a scene that's wiped and rebuilt on a save load,
        // so their GameObjects are destroyed out from under us even though LevelEditorManager
        // itself is DontDestroyOnLoad. _objects/_groupRoots/selection then hold dangling
        // references to destroyed Il2Cpp objects, which breaks gizmo/selection updates. Drop
        // those entries and reset selection so the editor recovers cleanly.
        [HideFromIl2Cpp]
        internal void PruneDestroyedObjects()
        {
            int removedObjs = 0;
            for (int i = _objects.Count - 1; i >= 0; i--)
            {
                if (_objects[i] == null)
                {
                    _objects.RemoveAt(i);
                    removedObjs++;
                }
            }

            var deadGroups = new List<int>();
            foreach (var kvp in _groupRoots)
                if (kvp.Value == null) deadGroups.Add(kvp.Key);
            foreach (var gid in deadGroups) _groupRoots.Remove(gid);

            if (removedObjs == 0 && deadGroups.Count == 0) return;

            BBLog.Msg($"[LevelEditorManager] Pruned {removedObjs} destroyed object(s) and " +
                $"{deadGroups.Count} dead group root(s) after a native save load.");

            LevelEditor.Select(null);
        }

        // _propsContainer (and every prop parented under it) isn't DontDestroyOnLoad, so a
        // native save load that reloads the scene destroys them out from under us. _objects
        // and _groupRoots then hold dangling references to already-destroyed Il2Cpp objects,
        // which breaks selection/gizmo updates. Detect that and reset to a clean state.
        [HideFromIl2Cpp]
        internal void EnsurePropsContainer()
        {
            if (_propsContainer != null) return;

            BBLog.Msg("[LevelEditorManager] Props container was destroyed (scene reload?) — resetting editor state.");

            LevelEditor.Select(null);
            _objects.Clear();
            _groupRoots.Clear();
            _nextGroupId = 1;
            _propsContainer = new GameObject("Baby Blocks");

            PropMetadataPanel.RefreshMicroSplatLayerMaterials();
        }

        [HideFromIl2Cpp]
        internal LevelEditorObject SpawnFromPropInfo(PropInfo info, Vector3 position)
        {
            PropLibrary.LoadPropData(info);
            bool keepHierarchy = info.sourcePrefab != null && PropMetadataPanel.GetKeepOriginalHierarchy(info.id);

            if (!keepHierarchy && !info.HasMesh)
            {
                MelonLogger.Warning($"[LevelEditorManager] Cannot spawn {info.displayName}: no mesh.");
                return null;
            }

            GameObject root;
            if (keepHierarchy)
            {
                root = UnityEngine.Object.Instantiate(info.sourcePrefab, position, Quaternion.identity);
                root.name = $"LEO_{info.displayName}";
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    t.gameObject.layer = PropLayer;
            }
            else
            {
                root = new GameObject($"LEO_{info.displayName}");
                root.transform.position = position;
                root.layer = PropLayer;
                for (int i = 0; i < info.parts.Count; i++)
                {
                    var part  = info.parts[i];
                    var child = new GameObject($"Part_{i}");
                    child.transform.SetParent(root.transform, false);
                    child.transform.localPosition = part.localPosition;
                    child.transform.localRotation = part.localRotation;
                    child.transform.localScale    = part.localScale;
                    child.layer = PropLayer;

                    var mf = child.AddComponent<MeshFilter>();
                    mf.sharedMesh = part.mesh;

                    var mr = child.AddComponent<MeshRenderer>();
                    if (part.materials != null) mr.sharedMaterials = part.materials;
                }
                if (PropLibrary.IsNegativeCollisionProp(info.id))
                {
                    // The ghost cube provides its own trigger volume — a real mesh/render
                    // collider on the placeholder cylinder would solidly block the player.
                    GhostCubeConfig.Configure(root);
                }
                else if (PropLibrary.IsSpawnPointProp(info.id))
                {
                    // The spawn point marker provides its own trigger volume — the
                    // placeholder capsule mesh is hidden and never needs collision.
                    SpawnPointConfig.Configure(root);
                }
                else
                {
                    ApplyColliderParts(root, info, PropMetadataPanel.GetUseRenderMeshCollider(info.id));
                }
            }

            if (_propsContainer != null)
                root.transform.SetParent(_propsContainer.transform, true);

            string surfaceTag = PropMetadataPanel.GetSurfaceType(info.id);
            if (!string.IsNullOrEmpty(surfaceTag))
                PropMetadataPanel.ApplySurfaceTypeToRoot(root, surfaceTag);

            PropMetadataPanel.ApplyMaterialOverridesToRoot(info.id, root);
            PropMetadataPanel.ApplyDisabledRenderersToRoot(info.id, root);
            PropMetadataPanel.ApplyBushColliderToRoot(info.id, root);

            // Only one Spawn Point may exist at a time — placing a new one replaces
            // any existing one.
            if (PropLibrary.IsSpawnPointProp(info.id))
            {
                for (int i = _objects.Count - 1; i >= 0; i--)
                {
                    if (_objects[i] != null && PropLibrary.IsSpawnPointProp(_objects[i].addressableKey))
                        Remove(_objects[i]);
                }
            }

            var leo = root.AddComponent<LevelEditorObject>();
            leo.objectType     = "Addressable";
            leo.addressableKey = info.id;
            _objects.Add(leo);
            InitializeLoopBase(leo, position);
            PropLibrary.AddRef(info.id);
            return leo;
        }

        [HideFromIl2Cpp]
        internal IReadOnlyList<LevelEditorObject> GetLogicalGroupMembers(int groupId)
        {
            if (groupId <= 0) return Array.Empty<LevelEditorObject>();
            var members = new List<LevelEditorObject>();
            foreach (var obj in _objects)
                if (obj != null && obj.groupId == groupId) members.Add(obj);
            return members;
        }

        internal static readonly Dictionary<int, Dictionary<string, Mesh>> _physicsMeshCache = new();

        internal static void ReleasePhysicsMeshes(PropInfo info)
        {
            if (info == null) return;
            foreach (var part in info.parts)
            {
                if (part?.mesh == null) continue;
                int id = part.mesh.GetInstanceID();
                if (_physicsMeshCache.TryGetValue(id, out var physMap))
                {
                    _physicsMeshCache.Remove(id);
                    foreach (var phys in physMap.Values)
                    {
                        if (phys != null) Destroy(phys);
                    }
                }
            }
            foreach (var cp in info.colliderParts)
            {
                if (cp?.mesh == null) continue;
                int id = cp.mesh.GetInstanceID();
                if (_physicsMeshCache.TryGetValue(id, out var physMap))
                {
                    _physicsMeshCache.Remove(id);
                    foreach (var phys in physMap.Values)
                    {
                        if (phys != null) Destroy(phys);
                    }
                }
            }
        }

        // Game meshes ship without Read/Write enabled; GetVertexBuffer/GetIndexBuffer bypass that.
        // Position is assumed Float32×3 at byte-offset 0 in stream 0 (standard Unity layout).
        internal static Mesh BuildPhysicsMesh(Mesh source, HashSet<int> ignoredSubmeshes = null)
        {
            if (source == null) return null;
            int id = source.GetInstanceID();
            string cacheKey = ignoredSubmeshes == null || ignoredSubmeshes.Count == 0
                ? string.Empty
                : string.Join(",", ignoredSubmeshes.OrderBy(v => v));
            if (_physicsMeshCache.TryGetValue(id, out var physMap))
            {
                if (physMap == null) return null; // known-bad mesh; don't retry
                if (physMap.TryGetValue(cacheKey, out var hit))
                    return hit;
            }

            Mesh result = null;
            try
            {
                bool posOk = false;
                foreach (var a in source.GetVertexAttributes())
                {
                    if (a.attribute == UnityEngine.Rendering.VertexAttribute.Position
                        && a.format    == UnityEngine.Rendering.VertexAttributeFormat.Float32
                        && a.dimension == 3
                        && a.stream    == 0)
                    { posOk = true; break; }
                }
                if (!posOk) { _physicsMeshCache[id] = null; return null; }

                var vb         = source.GetVertexBuffer(0);
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
                    var idxBuf = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ushort>(iCount);
                    ib.GetData(idxBuf.Cast<Il2CppSystem.Array>());
                    for (int i = 0; i < iCount; i++) tris[i] = idxBuf[i];
                }
                else
                {
                    var idxBuf = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(iCount);
                    ib.GetData(idxBuf.Cast<Il2CppSystem.Array>());
                    for (int i = 0; i < iCount; i++) tris[i] = idxBuf[i];
                }

                int[] filteredTris = tris;
                if (ignoredSubmeshes != null && ignoredSubmeshes.Count > 0 && source.subMeshCount > 0)
                {
                    var combined = new List<int>();
                    for (int sub = 0; sub < source.subMeshCount; sub++)
                    {
                        if (ignoredSubmeshes.Contains(sub)) continue;

                        var sm = source.GetSubMesh(sub);
                        int start = sm.indexStart;
                        int count = sm.indexCount;
                        if (start < 0 || count <= 0 || start + count > tris.Length) continue;

                        for (int i = start; i < start + count; i++)
                            combined.Add(tris[i]);
                    }

                    if (combined.Count > 0)
                        filteredTris = combined.ToArray();
                }

                ib.Release();

                result = new Mesh { name = source.name + "_phys" };
                result.vertices  = positions;
                result.triangles = filteredTris;
                result.RecalculateNormals();
                result.RecalculateBounds();

                if (!_physicsMeshCache.TryGetValue(id, out physMap))
                {
                    physMap = new Dictionary<string, Mesh>();
                    _physicsMeshCache[id] = physMap;
                }
                physMap[cacheKey] = result;
            }
            catch { result = null; }
            return result;
        }

        public static void ApplyColliderParts(GameObject root, PropInfo info, bool applyRenderMesh = false)
        {
            if (root == null || info == null) return;
            int layer = PropLayer;

            // Render mesh override takes priority when explicitly requested.
            // Falls through to pre-cooked colliders otherwise.
            if (!applyRenderMesh)
            {
                if (!info.HasColliderParts) return;

                for (int i = 0; i < info.colliderParts.Count; i++)
                {
                    var cp = info.colliderParts[i];
                    var go = new GameObject($"PropCollider_{i}");
                    go.transform.SetParent(root.transform, false);
                    go.transform.localPosition = cp.localPosition;
                    go.transform.localRotation = cp.localRotation;
                    go.transform.localScale    = cp.localScale;
                    go.layer = layer;
                    switch (cp.type)
                    {
                        case PropColliderPart.ColliderType.Mesh:
                            var mc = go.AddComponent<MeshCollider>();
                            // Use an owned physics mesh so scene unloads can't evict the CPU mesh data.
                            var physMesh = BuildPhysicsMesh(cp.mesh);
                            mc.sharedMesh = physMesh ?? cp.mesh;
                            mc.convex     = cp.convex;
                            break;
                        case PropColliderPart.ColliderType.Box:
                            var bc2 = go.AddComponent<BoxCollider>();
                            bc2.center = cp.center;
                            bc2.size   = cp.size;
                            break;
                        case PropColliderPart.ColliderType.Sphere:
                            var sc = go.AddComponent<SphereCollider>();
                            sc.center = cp.center;
                            sc.radius = cp.radius;
                            break;
                        case PropColliderPart.ColliderType.Capsule:
                            var cc = go.AddComponent<CapsuleCollider>();
                            cc.center    = cp.center;
                            cc.radius    = cp.radius;
                            cc.height    = cp.height;
                            cc.direction = cp.direction;
                            break;
                    }
                }
                return;
            }

            var seenBounds = new HashSet<string>();
            int colIdx = 0;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || !mr.enabled) continue;

                var b   = mf.sharedMesh.bounds;
                string key = $"{b.center.x:F2},{b.center.y:F2},{b.center.z:F2}|{b.size.x:F2},{b.size.y:F2},{b.size.z:F2}";
                if (!seenBounds.Add(key)) continue;

                var go = new GameObject($"PropCollider_{colIdx++}");
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = mf.transform.localPosition;
                go.transform.localRotation = mf.transform.localRotation;
                go.transform.localScale    = mf.transform.localScale;
                go.layer = layer;

                var ignoredSubs = PropMetadataPanel.GetColliderIgnoredSubmeshes(info.id);
                var physMesh = BuildPhysicsMesh(mf.sharedMesh, ignoredSubs);
                var mc = go.AddComponent<MeshCollider>();
                if (physMesh != null)
                    mc.sharedMesh = physMesh;
                else if (ignoredSubs == null || ignoredSubs.Count == 0)
                    mc.sharedMesh = mf.sharedMesh;
                else
                    UnityEngine.Object.Destroy(go);
            }
        }

        static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            for (int i = 0; i < go.transform.childCount; i++)
                SetLayerRecursive(go.transform.GetChild(i).gameObject, layer);
        }

        public LevelEditorObject SpawnPrimitive(PrimitiveType type, Vector3 position)
        {
            var go = GameObject.CreatePrimitive(type);
            go.transform.position = position;
            go.name  = $"LEO_{type}";
            go.layer = PropLayer;
            if (_propsContainer != null)
                go.transform.SetParent(_propsContainer.transform, true);
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = go.GetComponent<MeshFilter>().sharedMesh;
            var leo = go.AddComponent<LevelEditorObject>();
            leo.objectType = type.ToString();
            _objects.Add(leo);
            InitializeLoopBase(leo, position);
            return leo;
        }

        const int PropLayer = 16; // "Props" layer — required for the surface Tag to work

        public void Remove(LevelEditorObject obj)
        {
            if (obj == null) return;
            // Dissolve the entire group before removing; DissolveGroup moves all
            // siblings back to _propsContainer so they survive the deletion.
            int gid = obj.groupId > 0 ? obj.groupId
                    : obj.physicsGroupId > 0 ? obj.physicsGroupId : 0;
            if (gid > 0) DissolveGroup(gid);
            _objects.Remove(obj);
            if (!string.IsNullOrEmpty(obj.addressableKey))
                PropLibrary.RemoveRef(obj.addressableKey);
            Destroy(obj.gameObject);
        }

        public static Vector2Int GetChunkCoord(Vector3 position)
        {
            // X is periodic — wrap into [0, WorldLoopSize).
            // Z is the forward/progress axis and is not periodic; clamp to chunk grid.
            float wrappedX = Mathf.Repeat(position.x + WorldLoopSize * 0.5f, WorldLoopSize);
            int chunkX = Mathf.Clamp(Mathf.FloorToInt(wrappedX / ChunkWorldSize), 0, ChunksPerAxis - 1);
            int chunkZ = Mathf.Clamp(Mathf.FloorToInt(position.z / ChunkWorldSize), 0, ChunksPerAxis - 1);
            return new Vector2Int(chunkX, chunkZ);
        }

        public static int GetChunkIndex(Vector3 position)
            => GetChunkIndex(GetChunkCoord(position));

        public static int GetChunkIndex(Vector2Int chunkCoord)
            => chunkCoord.y * ChunksPerAxis + chunkCoord.x;

        public static void NotifyVisualStateChanged(GameObject root)
        {
        }

        public void SyncLoopBase(LevelEditorObject obj)
        {
            if (obj == null) return;
            obj.loopBasePosition = obj.transform.position;
            obj.loopBaseRotation = obj.transform.rotation;
            obj.loopBaseScale    = obj.transform.localScale;
            obj.hasLoopBasePosition = true;
            obj.hasLoopBaseRotation = true;
            obj.hasLoopBaseScale    = true;
            UpdateChunkData(obj, obj.loopBasePosition);
        }

        [HideFromIl2Cpp]
        internal void SyncLoopBases(IEnumerable<LevelEditorObject> objects)
        {
            if (objects == null) return;
            foreach (var obj in objects)
                SyncLoopBase(obj);
        }

        // Dead-zone around each loop boundary so the ~512-unit snap doesn't fire
        // repeatedly when the reference oscillates right at the ±256 edge.
        const float LoopHysteresis = 16f;

        void Update()
        {
            var reference = GetRenderReference();
            var playerPos = GetPlayerReferencePosition(reference);
            bool canLoop = reference != null && ChunkLoopingEnabled
                        && !LevelEditor.isDragging && !PropPalette.IsDragging;

            _physicsControlSeen.Clear();

            for (int i = 0; i < _objects.Count; i++)
            {
                var obj = _objects[i];
                if (obj == null) continue;

                var basePos = GetLoopBasePosition(obj);

                if (obj.isPhysicsManaged)
                {
                    UpdatePhysicsObjectState(obj, playerPos);
                    MarkPhysicsChunkIndependent(obj);
                    continue;
                }

                if (canLoop)
                {
                    var loopedPos = GetLoopedPosition(basePos, reference.position);
                    if (obj.transform.position != loopedPos)
                    {
                        float dx = loopedPos.x - obj.transform.position.x;
                        bool crossesBoundary = Mathf.Abs(dx) > WorldLoopSize * 0.5f;
                        if (!crossesBoundary || LoopBoundaryDist(basePos.x, reference.position.x) >= LoopHysteresis)
                            obj.transform.position = loopedPos;
                    }
                }

                UpdateChunkData(obj, basePos);
            }
        }

        // Distance from refX to the nearest loop boundary for an object based at baseX.
        float LoopBoundaryDist(float baseX, float refX)
            => Mathf.Min(Mathf.Abs(refX - (baseX + WorldLoopSize * 0.5f)),
                         Mathf.Abs(refX - (baseX - WorldLoopSize * 0.5f)));

        void UpdateChunkData(LevelEditorObject obj, Vector3 position)
        {
            if (obj == null) return;
            var coord = GetChunkCoord(position);
            obj.chunkCoord = coord;
            obj.chunkIndex = GetChunkIndex(coord);
        }

        Vector3 GetLoopBasePosition(LevelEditorObject obj)
        {
            if (obj == null) return Vector3.zero;
            if (!obj.hasLoopBasePosition)
            {
                obj.loopBasePosition = obj.transform.position;
                obj.hasLoopBasePosition = true;
            }
            return obj.loopBasePosition;
        }

        void InitializeLoopBase(LevelEditorObject obj, Vector3 position)
        {
            if (obj == null) return;
            obj.loopBasePosition = position;
            obj.loopBaseRotation = obj.transform.rotation;
            obj.loopBaseScale    = obj.transform.localScale;
            obj.hasLoopBasePosition = true;
            obj.hasLoopBaseRotation = true;
            obj.hasLoopBaseScale    = true;
            UpdateChunkData(obj, position);
        }

        Vector3 GetLoopedPosition(Vector3 basePosition, Vector3 referencePosition)
        {
            // Snap to the nearest integer multiple of WorldLoopSize so the result is always
            // exactly basePosition.x + n*WorldLoopSize (n ∈ ℤ). This gives the same float
            // every frame regardless of camera micro-movement, preventing sub-unit position
            // jitter that causes position-based material shaders to flicker.
            float n = Mathf.Round((referencePosition.x - basePosition.x) / WorldLoopSize);
            return new Vector3(basePosition.x + n * WorldLoopSize, basePosition.y, basePosition.z);
        }

        Transform GetRenderReference()
        {
            // When the fly cam is active, use its virtual camera transform directly.
            // Camera.main in fly-cam mode is the physical Cinemachine camera which can sit
            // near the player (z≈289) while the virtual fly cam is at the edit site (z≈801),
            // causing every object to ghost-loop to the wrong position.
            var player = PlayerMovement.me;
            if (player != null && FlyCamController.FlyCamActive && player.flyCam != null)
                return player.flyCam.transform;

            var mainCam = Camera.main;
            return mainCam != null ? mainCam.transform : null;
        }

        // Physics-state helpers

        Vector3 GetPlayerReferencePosition(Transform reference)
        {
            var player = PlayerMovement.me;
            if (player != null)
                return player.head != null ? player.head.position : player.transform.position;
            return reference != null ? reference.position : Vector3.zero;
        }

        void UpdatePhysicsObjectState(LevelEditorObject obj, Vector3 playerPos)
        {
            if (_editorModeActive) return;
            var control = GetPhysicsControlObject(obj);
            if (control == null) return;

            int controlId = control.GetInstanceID();
            if (!_physicsControlSeen.Add(controlId)) return;

            var rb = control.GetComponent<Rigidbody>();
            if (rb == null) return;
            if (control.GetComponent<Grabable>() != null) return; // grabables manage their own kinematic state

            bool active = (control.transform.position - playerPos).sqrMagnitude <= PhysicsActiveRadiusSqr;
            if (active)
            {
                if (rb.isKinematic) rb.isKinematic = false;
                rb.useGravity = true;
            }
            else if (!rb.isKinematic)
            {
                rb.velocity        = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic     = true;
                rb.useGravity      = false;
            }
        }

        GameObject GetPhysicsControlObject(LevelEditorObject obj)
        {
            if (obj == null) return null;
            var grabable = obj.GetComponentInParent<Grabable>(true);
            if (grabable != null) return grabable.gameObject;
            var rb = obj.GetComponentInParent<Rigidbody>(true);
            if (rb != null) return rb.gameObject;
            return obj.gameObject;
        }

        void MarkPhysicsChunkIndependent(LevelEditorObject obj)
        {
            if (obj == null) return;
            obj.chunkIndex = 255;
            obj.chunkCoord = new Vector2Int(-1, -1);
        }

        // Physics group support

        public int AllocateGroupId() => _nextGroupId++;

        public void SetEditorModeActive(bool active)
        {
            if (_editorModeActive == active) return;
            _editorModeActive = active;
            if (active)
            {
                ReleasePlayerHeldObjects();
                RestoreHeldObjectScales();
                EnterEditorPhysicsMode();

                // Undo any baked ghost-cube collider carves so moving/selecting
                // props in the editor isn't affected by holes cut for gameplay.
                GhostCollisionCutter.RestoreAllColliderCarves();
            }
            else
            {
                ExitEditorPhysicsMode();

                // Bake collider carves for every ghost cube now that editing is
                // done — this is the only point props' MeshColliders get cut, so
                // anything moved into a hole while editing is picked up here.
                GhostCollisionCutter.BakeAllColliderCarves();
            }
        }

        void ReleasePlayerHeldObjects()
        {
            var player = PlayerMovement.me;
            _heldObjectsToRestore.Clear();
            _heldScalesToRestore.Clear();
            if (player == null) return;

            if (player.currentHat != null)
            {
                var hatObj = player.currentHat.GetComponent<LevelEditorObject>();
                if (hatObj != null)
                {
                    _heldObjectsToRestore.Add(hatObj);
                    _heldScalesToRestore.Add(hatObj.transform.localScale);
                }
                player.KnockOffHat();
            }

            for (int i = 0; i < player.handItems.Length; i++)
            {
                if (player.handItems[i] != null)
                {
                    var handObj = player.handItems[i].GetComponent<LevelEditorObject>();
                    if (handObj != null)
                    {
                        _heldObjectsToRestore.Add(handObj);
                        _heldScalesToRestore.Add(handObj.transform.localScale);
                    }
                    player.DropHandItem(i);
                }
            }
        }

        void RestoreHeldObjectScales()
        {
            int count = Mathf.Min(_heldObjectsToRestore.Count, _heldScalesToRestore.Count);
            for (int i = 0; i < count; i++)
            {
                var obj = _heldObjectsToRestore[i];
                if (obj != null) obj.transform.localScale = _heldScalesToRestore[i];
            }
            _heldObjectsToRestore.Clear();
            _heldScalesToRestore.Clear();
        }

        void EnterEditorPhysicsMode()
        {
            var seenPhysicsGroups = new HashSet<int>();
            foreach (var obj in _objects)
            {
                if (obj == null || obj.physicsMode == PhysicsMode.Static) continue;

                if (obj.physicsMode == PhysicsMode.Rigidbody)
                {
                    if (obj.physicsGroupId > 0)
                    {
                        if (!seenPhysicsGroups.Add(obj.physicsGroupId)) continue;
                        FreezeRigidBodyGroupForEditor(obj.physicsGroupId);
                    }
                    else
                    {
                        RestoreBasePose(obj);
                        FreezeRigidBodyObject(obj, true);
                        obj.isPhysicsManaged = true;
                    }
                    continue;
                }

                if (obj.physicsGroupId > 0)
                {
                    if (!seenPhysicsGroups.Add(obj.physicsGroupId)) continue;
                    DeactivatePhysicsGroupForEditor(obj.physicsGroupId);
                }
                else
                {
                    DeactivateSoloWearableForEditor(obj);
                }
            }
        }

        void ExitEditorPhysicsMode()
        {
            var seenPhysicsGroups = new HashSet<int>();
            foreach (var obj in _objects)
            {
                if (obj == null || obj.physicsMode != PhysicsMode.Rigidbody) continue;

                if (obj.physicsGroupId > 0)
                {
                    if (!seenPhysicsGroups.Add(obj.physicsGroupId)) continue;
                    UnfreezeRigidBodyGroup(obj.physicsGroupId);
                }
                else
                {
                    FreezeRigidBodyObject(obj, false);
                }
                obj.isPhysicsManaged = true;
            }
            ApplyPhysicsGroups();
        }

        void FreezeRigidBodyObject(LevelEditorObject obj, bool freeze)
        {
            if (obj == null) return;
            var rb = obj.GetComponent<Rigidbody>();
            if (rb == null) return;

            if (freeze)
            {
                if (!obj.editorFreezeStateValid)
                {
                    obj.editorFreezeVelocity        = Vector3.zero;
                    obj.editorFreezeAngularVelocity  = Vector3.zero;
                    obj.editorFreezeIsKinematic      = rb.isKinematic;
                    obj.editorFreezeUseGravity       = rb.useGravity;
                    obj.editorFreezeConstraints      = rb.constraints;
                    obj.editorFreezeStateValid       = true;
                }
                rb.velocity        = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic     = true;
                rb.useGravity      = false;
                rb.constraints     = RigidbodyConstraints.FreezeAll;
            }
            else if (obj.editorFreezeStateValid)
            {
                rb.constraints     = obj.editorFreezeConstraints;
                rb.isKinematic     = obj.editorFreezeIsKinematic;
                rb.useGravity      = obj.editorFreezeUseGravity;
                rb.velocity        = obj.editorFreezeVelocity;
                rb.angularVelocity = obj.editorFreezeAngularVelocity;
                obj.editorFreezeStateValid = false;
            }
        }

        void FreezeRigidBodyGroupForEditor(int physicsGroupId)
        {
            var members = _objects.Where(o => o != null && o.physicsMode == PhysicsMode.Rigidbody && o.physicsGroupId == physicsGroupId).ToList();
            if (members.Count == 0) return;

            var root = FindPhysicsRoot(members) ?? ActivateRigidbodyGroup(members);
            if (root == null) return;

            var centroid = Vector3.zero;
            foreach (var m in members)
                centroid += m.hasLoopBasePosition ? m.loopBasePosition : m.transform.position;
            centroid /= members.Count;

            SetHierarchyCollisions(root, false);
            root.transform.position = centroid;
            foreach (var m in members) RestoreBasePose(m);
            FreezeRigidBodyGameObject(root, true);
            SetHierarchyCollisions(root, true);
            foreach (var m in members) m.isPhysicsManaged = true;
        }

        void UnfreezeRigidBodyGroup(int physicsGroupId)
        {
            var members = _objects.Where(o => o != null && o.physicsMode == PhysicsMode.Rigidbody && o.physicsGroupId == physicsGroupId).ToList();
            if (members.Count == 0) return;
            var root = FindPhysicsRoot(members);
            if (root == null) return;
            FreezeRigidBodyGameObject(root, false);
            foreach (var m in members) m.isPhysicsManaged = true;
        }

        static void FreezeRigidBodyGameObject(GameObject go, bool freeze)
        {
            if (go == null) return;
            var rb = go.GetComponent<Rigidbody>();
            if (rb == null) return;
            if (freeze)
            {
                rb.velocity        = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic     = true;
                rb.useGravity      = false;
                rb.constraints     = RigidbodyConstraints.FreezeAll;
            }
            else
            {
                rb.constraints = RigidbodyConstraints.None;
                rb.isKinematic = false;
                rb.useGravity  = true;
            }
        }

        static void SetHierarchyCollisions(GameObject go, bool enabled)
        {
            if (go == null) return;
            foreach (var col in go.GetComponentsInChildren<Collider>(true))
                col.enabled = enabled;
        }

        void CleanupGroupRoot(int groupId)
        {
            if (groupId <= 0) return;
            var root = GetGroupRoot(groupId);
            if (root == null) { _groupRoots.Remove(groupId); return; }
            for (int i = 0; i < root.transform.childCount; i++)
                if (root.transform.GetChild(i).GetComponent<LevelEditorObject>() != null) return;
            _groupRoots.Remove(groupId);
            Destroy(root);
        }

        [HideFromIl2Cpp]
        internal void CleanupPhysicsRoot(int groupId) => CleanupGroupRoot(groupId);

        [HideFromIl2Cpp]
        GameObject FindPhysicsRoot(List<LevelEditorObject> members)
        {
            if (members == null || members.Count == 0) return null;
            int gid = members[0].physicsGroupId > 0 ? members[0].physicsGroupId : members[0].groupId;
            return GetGroupRoot(gid);
        }

        void DeactivateSoloWearableForEditor(LevelEditorObject obj)
        {
            SetHierarchyCollisions(obj.gameObject, false);
            RemoveGrabableComponents(obj.gameObject);
            RestoreBasePose(obj);
            SetHierarchyCollisions(obj.gameObject, true);
            obj.isPhysicsManaged = false;
        }

        void DeactivatePhysicsGroupForEditor(int physicsGroupId)
        {
            var members = _objects.Where(o => o != null && o.physicsGroupId == physicsGroupId).ToList();
            if (members.Count == 0) return;

            var root = GetGroupRoot(physicsGroupId);
            if (root != null)
            {
                SetHierarchyCollisions(root, false);
                while (root.transform.childCount > 0)
                {
                    var child = root.transform.GetChild(0);
                    child.SetParent(_propsContainer != null ? _propsContainer.transform : null, true);
                    SetHierarchyCollisions(child.gameObject, true);
                    var childLeo = child.GetComponent<LevelEditorObject>();
                    if (childLeo != null) { RestoreBasePose(childLeo); childLeo.isPhysicsManaged = false; }
                }
                RemoveGroupRoot(physicsGroupId);
                Destroy(root);
                return;
            }

            foreach (var member in members)
            {
                RemoveGrabableComponents(member.gameObject);
                RestoreBasePose(member);
                member.isPhysicsManaged = false;
            }
        }

        void RestoreBasePose(LevelEditorObject obj)
        {
            if (obj == null) return;
            if (obj.hasLoopBasePosition) obj.transform.position   = obj.loopBasePosition;
            if (obj.hasLoopBaseRotation) obj.transform.rotation   = obj.loopBaseRotation;
            if (obj.hasLoopBaseScale)    obj.transform.localScale = obj.loopBaseScale;
        }

        public void ApplyPhysicsGroups()
        {
            var rigidbodySolos   = new List<LevelEditorObject>();
            var rigidbodyGroups  = new Dictionary<int, List<LevelEditorObject>>();
            var wearableSolos    = new List<LevelEditorObject>();
            var wearableGroups   = new Dictionary<int, List<LevelEditorObject>>();
            int maxGroupId = 0;

            foreach (var leo in _objects)
            {
                if (leo == null || leo.physicsMode == PhysicsMode.Static) continue;
                if (leo.groupId       > maxGroupId) maxGroupId = leo.groupId;
                if (leo.physicsGroupId > maxGroupId) maxGroupId = leo.physicsGroupId;

                if (leo.physicsMode == PhysicsMode.Rigidbody)
                {
                    if (leo.physicsGroupId <= 0) { rigidbodySolos.Add(leo); }
                    else
                    {
                        if (!rigidbodyGroups.TryGetValue(leo.physicsGroupId, out var list)) { list = new List<LevelEditorObject>(); rigidbodyGroups[leo.physicsGroupId] = list; }
                        list.Add(leo);
                    }
                    continue;
                }

                if (leo.physicsGroupId <= 0) { wearableSolos.Add(leo); }
                else
                {
                    if (!wearableGroups.TryGetValue(leo.physicsGroupId, out var list)) { list = new List<LevelEditorObject>(); wearableGroups[leo.physicsGroupId] = list; }
                    list.Add(leo);
                }
            }

            foreach (var leo in rigidbodySolos)
            {
                var colls = leo.gameObject.GetComponentsInChildren<Collider>(true);
                AddRigidBodyComponent(leo.gameObject, colls, leo.addressableKey);
                FreezeRigidBodyObject(leo, _editorModeActive);
                MarkPhysicsChunkIndependent(leo);
                leo.isPhysicsManaged = true;
            }

            foreach (var kvp in rigidbodyGroups)
            {
                var members = kvp.Value;
                if (members.Count == 0) continue;
                var root = FindPhysicsRoot(members) ?? ActivateRigidbodyGroup(members);
                if (root != null) FreezeRigidBodyGameObject(root, _editorModeActive);
                foreach (var m in members) { MarkPhysicsChunkIndependent(m); m.isPhysicsManaged = true; }
            }

            foreach (var leo in wearableSolos)
            {
                var colls = leo.gameObject.GetComponentsInChildren<Collider>(true);
                AddGrabableComponent(leo.gameObject, leo.physicsMode == PhysicsMode.Hat, colls, leo.addressableKey);
                SyncHatHairAmount(leo);
                if (leo.physicsMode == PhysicsMode.Grabable) SyncGrabOffset(leo);
                MarkPhysicsChunkIndependent(leo);
                leo.isPhysicsManaged = true;
            }

            foreach (var kvp in wearableGroups)
            {
                var members = kvp.Value;
                if (members.Count == 0) continue;
                ActivatePhysicsGroup(members, members[0].physicsMode);
            }

            if (maxGroupId >= _nextGroupId) _nextGroupId = maxGroupId + 1;
        }

        public void ActivatePhysics(LevelEditorObject leo)
        {
            var colls = leo.gameObject.GetComponentsInChildren<Collider>(true);
            if (leo.physicsMode == PhysicsMode.Rigidbody)
            {
                AddRigidBodyComponent(leo.gameObject, colls, leo.addressableKey);
                FreezeRigidBodyObject(leo, true);
            }
            else
            {
                AddGrabableComponent(leo.gameObject, leo.physicsMode == PhysicsMode.Hat, colls, leo.addressableKey);
                SyncHatHairAmount(leo);
                if (leo.physicsMode == PhysicsMode.Grabable) SyncGrabOffset(leo);
            }
            MarkPhysicsChunkIndependent(leo);
            leo.isPhysicsManaged = true;
        }

        [HideFromIl2Cpp]
        internal GameObject ActivateRigidbodyGroup(List<LevelEditorObject> members)
        {
            if (members == null || members.Count == 0) return null;
            int gid = members[0].physicsGroupId > 0 ? members[0].physicsGroupId : members[0].groupId;

            var existingRoot = FindPhysicsRoot(members);
            if (existingRoot != null)
            {
                existingRoot.name = "PhysicsGroup";
                RemoveGrabableComponents(existingRoot);
                var colls2 = new List<Collider>();
                foreach (var m in members)
                {
                    var p = m.transform.parent?.gameObject;
                    if (p == null || p.GetInstanceID() != existingRoot.GetInstanceID())
                        m.transform.SetParent(existingRoot.transform, true);
                    foreach (var c in m.gameObject.GetComponentsInChildren<Collider>(true)) colls2.Add(c);
                    m.isPhysicsManaged = true;
                }
                AddRigidBodyComponent(existingRoot, colls2.ToArray());
                FreezeRigidBodyGameObject(existingRoot, _editorModeActive);
                foreach (var m in members) MarkPhysicsChunkIndependent(m);
                return existingRoot;
            }

            var centroid = Vector3.zero;
            foreach (var m in members) centroid += m.hasLoopBasePosition ? m.loopBasePosition : m.transform.position;
            centroid /= members.Count;

            var root = new GameObject("PhysicsGroup");
            root.transform.position = centroid;
            if (_propsContainer != null) root.transform.SetParent(_propsContainer.transform, true);

            var colls = new List<Collider>();
            foreach (var m in members)
            {
                foreach (var c in m.gameObject.GetComponentsInChildren<Collider>(true)) colls.Add(c);
                m.gameObject.transform.SetParent(root.transform, true);
                m.isPhysicsManaged = true;
            }
            AddRigidBodyComponent(root, colls.ToArray());
            FreezeRigidBodyGameObject(root, _editorModeActive);
            foreach (var m in members) MarkPhysicsChunkIndependent(m);
            if (gid > 0) SetGroupRoot(gid, root);
            return root;
        }

        [HideFromIl2Cpp]
        internal void ActivatePhysicsGroup(List<LevelEditorObject> members, PhysicsMode mode)
        {
            if (members == null || members.Count == 0) return;
            int gid = members[0].physicsGroupId > 0 ? members[0].physicsGroupId : members[0].groupId;

            var existingRoot = FindPhysicsRoot(members);
            if (existingRoot != null)
            {
                existingRoot.name = "PhysicsGroup";
                RemoveGrabableComponents(existingRoot);
                var colls2 = new List<Collider>();
                foreach (var m in members)
                {
                    var p = m.transform.parent?.gameObject;
                    if (p == null || p.GetInstanceID() != existingRoot.GetInstanceID())
                        m.transform.SetParent(existingRoot.transform, true);
                    foreach (var c in m.gameObject.GetComponentsInChildren<Collider>(true)) colls2.Add(c);
                    m.isPhysicsManaged = true;
                }
                AddGrabableComponent(existingRoot, mode == PhysicsMode.Hat, colls2.ToArray());
                if (mode == PhysicsMode.Hat && members.Count > 0) SyncHatHairAmount(members[0]);
                if (mode == PhysicsMode.Grabable && members.Count > 0) SyncGrabOffset(members[0]);
                foreach (var m in members) MarkPhysicsChunkIndependent(m);
                return;
            }

            var centroid = Vector3.zero;
            foreach (var m in members) centroid += m.hasLoopBasePosition ? m.loopBasePosition : m.transform.position;
            centroid /= members.Count;

            var root = new GameObject("PhysicsGroup");
            root.transform.position = centroid;
            if (_propsContainer != null) root.transform.SetParent(_propsContainer.transform, true);

            var colls = new List<Collider>();
            foreach (var m in members)
            {
                foreach (var c in m.gameObject.GetComponentsInChildren<Collider>(true)) colls.Add(c);
                m.gameObject.transform.SetParent(root.transform, true);
                m.isPhysicsManaged = true;
            }
            AddGrabableComponent(root, mode == PhysicsMode.Hat, colls.ToArray());
            if (mode == PhysicsMode.Hat && members.Count > 0) SyncHatHairAmount(members[0]);
            if (mode == PhysicsMode.Grabable && members.Count > 0) SyncGrabOffset(members[0]);
            foreach (var m in members) MarkPhysicsChunkIndependent(m);
            if (gid > 0) SetGroupRoot(gid, root);
        }

        [HideFromIl2Cpp]
        internal void SyncHatHairAmount(LevelEditorObject leo)
        {
            if (leo == null) return;
            Hat hat = leo.GetComponent<Hat>();
            if (hat == null)
            {
                var p = leo.transform.parent;
                while (p != null && hat == null) { hat = p.GetComponent<Hat>(); p = p.parent; }
            }
            if (hat != null) hat.hairAmt = Mathf.Clamp01(leo.hatHairAmt);
        }

        [HideFromIl2Cpp]
        internal void SyncGrabOffset(LevelEditorObject leo)
        {
            if (leo == null) return;
            Grabable g = leo.GetComponent<Grabable>();
            if (g == null)
            {
                var p = leo.transform.parent;
                while (p != null && g == null) { g = p.GetComponent<Grabable>(); p = p.parent; }
            }
            if (g == null) return;
            var ptsR  = new Il2CppSystem.Collections.Generic.List<Vector3>();
            var rotsR = new Il2CppSystem.Collections.Generic.List<Quaternion>();
            var ptsL  = new Il2CppSystem.Collections.Generic.List<Vector3>();
            var rotsL = new Il2CppSystem.Collections.Generic.List<Quaternion>();
            ptsR.Add(leo.grabOffsetPos); rotsR.Add(Quaternion.Euler(leo.grabOffsetRot));
            ptsL.Add(leo.grabOffsetPos); rotsL.Add(Quaternion.Euler(leo.grabOffsetRot));
            g.grabLocPtsR = ptsR; g.grabLocRotsR = rotsR;
            g.grabLocPtsL = ptsL; g.grabLocRotsL = rotsL;
        }

        [HideFromIl2Cpp]
        internal void SyncLoadedHatHairValues()
        {
            foreach (var leo in _objects)
            {
                if (leo == null || leo.physicsMode != PhysicsMode.Hat) continue;
                SyncHatHairAmount(leo);
            }
        }

        // Re-applies baking (or the lack of it) to every placed instance of `propId` that
        // currently has physics, after the user toggles PropMetadataPanel's per-prop
        // "disable baking" setting in the Physics window. Restoring first undoes any
        // existing bake (no-op if it was never baked), then Bake() re-runs and either
        // re-bakes (using the disk cache when available) or, if disabled, leaves the
        // restored plain/native materials in place.
        internal void RefreshBakingForProp(string propId)
        {
            if (string.IsNullOrEmpty(propId)) return;
            foreach (var leo in _objects)
            {
                if (leo == null || leo.physicsMode == PhysicsMode.Static) continue;
                if (!string.Equals(leo.addressableKey, propId, StringComparison.Ordinal)) continue;

                MaterialBaker.RestoreOriginal(leo.gameObject);
                MaterialBaker.Bake(leo.gameObject, propId);
            }
        }

        // restoreMaterial=false is used when switching directly between physics modes
        // (Rigidbody/Grabable/Hat <-> each other) - the baked mesh/material is left in
        // place (and its _bakeStash entry kept) so MaterialBaker.Bake's "already baked"
        // guard skips recapturing it, instead of restoring the original then re-baking
        // from scratch. Only a transition to Static restores the original mesh/materials.
        public void ClearPhysics(LevelEditorObject leo, bool restoreMaterial = true)
        {
            if (leo == null || leo.physicsMode == PhysicsMode.Static) return;

            if (leo.physicsGroupId <= 0)
            {
                if (restoreMaterial) MaterialBaker.RestoreOriginal(leo.gameObject);
                RemoveGrabableComponents(leo.gameObject);
                leo.isPhysicsManaged = false;
                leo.physicsMode      = PhysicsMode.Static;
                leo.physicsGroupId   = 0;
                SyncLoopBase(leo);
            }
            else
            {
                int gid = leo.physicsGroupId;
                var root = GetGroupRoot(gid);
                if (root != null) { RemoveGrabableComponents(root); root.name = "Group"; }
                foreach (var obj in _objects)
                {
                    if (obj == null || obj.physicsGroupId != gid) continue;
                    if (restoreMaterial) MaterialBaker.RestoreOriginal(obj.gameObject);
                    obj.physicsMode      = PhysicsMode.Static;
                    obj.physicsGroupId   = 0;
                    obj.isPhysicsManaged = false;
                    SyncLoopBase(obj);
                }
            }
        }

        [HideFromIl2Cpp]
        internal void DissolveGroup(int groupId)
        {
            if (groupId <= 0) return;
            var root = GetGroupRoot(groupId);
            if (root != null)
            {
                RemoveGrabableComponents(root);
                while (root.transform.childCount > 0)
                {
                    var child = root.transform.GetChild(0);
                    child.SetParent(_propsContainer != null ? _propsContainer.transform : null, true);
                    var childLeo = child.GetComponent<LevelEditorObject>();
                    if (childLeo != null)
                    {
                        if (childLeo.physicsMode != PhysicsMode.Static)
                            MaterialBaker.RestoreOriginal(childLeo.gameObject);
                        SyncLoopBase(childLeo);
                        childLeo.physicsMode      = PhysicsMode.Static;
                        childLeo.physicsGroupId   = 0;
                        childLeo.groupId          = 0;
                        childLeo.isPhysicsManaged = false;
                    }
                }
                RemoveGroupRoot(groupId);
                Destroy(root);
            }
            else
            {
                foreach (var obj in _objects)
                {
                    if (obj == null || obj.groupId != groupId) continue;
                    if (obj.physicsMode != PhysicsMode.Static)
                        MaterialBaker.RestoreOriginal(obj.gameObject);
                    RemoveGrabableComponents(obj.gameObject);
                    obj.physicsMode      = PhysicsMode.Static;
                    obj.physicsGroupId   = 0;
                    obj.groupId          = 0;
                    obj.isPhysicsManaged = false;
                }
            }
        }

        [HideFromIl2Cpp]
        internal void EnsureStaticGroupRoot(int groupId, List<LevelEditorObject> members)
        {
            if (groupId <= 0 || members == null || members.Count == 0) return;
            var root = GetGroupRoot(groupId);
            if (root == null)
            {
                var centroid = Vector3.zero;
                foreach (var m in members) centroid += m.hasLoopBasePosition ? m.loopBasePosition : m.transform.position;
                centroid /= members.Count;
                root = new GameObject("Group");
                root.transform.position = centroid;
                if (_propsContainer != null) root.transform.SetParent(_propsContainer.transform, true);
                SetGroupRoot(groupId, root);
            }
            foreach (var m in members)
            {
                if (m == null) continue;
                var p = m.transform.parent?.gameObject;
                if (p == null || p.GetInstanceID() != root.GetInstanceID())
                    m.transform.SetParent(root.transform, true);
            }
        }

        static void AddRigidBodyComponent(GameObject go, Collider[] colls, string propId = null)
        {
            MaterialBaker.Bake(go, propId);
            foreach (var mc in go.GetComponentsInChildren<MeshCollider>(true)) mc.convex = true;
            var rb = go.GetComponent<Rigidbody>() ?? go.AddComponent<Rigidbody>();
            rb.mass                   = 5f;
            rb.isKinematic            = false;
            rb.useGravity             = true;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.interpolation          = RigidbodyInterpolation.Interpolate;
        }

        static void AddGrabableComponent(GameObject go, bool isHat, Collider[] colls, string propId = null)
        {
            if (go == null) return;
            MaterialBaker.Bake(go, propId);
            if (colls == null) colls = Array.Empty<Collider>();

            foreach (var mc in go.GetComponentsInChildren<MeshCollider>(true)) mc.convex = true;

            var existingHat      = go.GetComponent<Hat>();
            var existingGrabable = go.GetComponent<Grabable>();
            Grabable g = null;

            if (isHat)
            {
                if (existingGrabable != null && existingHat == null) DestroyImmediate(existingGrabable);
                g = existingHat != null ? existingHat : go.AddComponent<Hat>();
            }
            else
            {
                if (existingHat != null) DestroyImmediate(existingHat);
                g = existingGrabable != null ? existingGrabable : go.AddComponent<Grabable>();
            }

            if (g == null) return;

            var rb = go.GetComponent<Rigidbody>() ?? go.AddComponent<Rigidbody>();
            rb.mass                   = 5f;
            rb.isKinematic            = true;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.interpolation          = RigidbodyInterpolation.Interpolate;

            var crusher = go.transform.Find("Crusher");
            if (crusher == null)
            {
                var co = new GameObject("Crusher");
                co.transform.SetParent(go.transform, false);
                crusher = co.transform;
            }

            g.rb    = rb;
            g.rbs   = new Il2CppReferenceArray<Rigidbody>(1); g.rbs[0] = rb;
            g.colls = new Il2CppReferenceArray<Collider>(colls.Length);
            for (int i = 0; i < colls.Length; i++) if (colls[i] != null) g.colls[i] = colls[i];
            g.crusher = crusher;
            g.type    = isHat ? GrabableType.hat : GrabableType.questItem;

            if (isHat && g is Hat hat)
            {
                hat.enableOnWear  = Array.Empty<GameObject>();
                hat.disableOnWear = Array.Empty<GameObject>();
            }

            var ptsR  = new Il2CppSystem.Collections.Generic.List<Vector3>();
            var rotsR = new Il2CppSystem.Collections.Generic.List<Quaternion>();
            var ptsL  = new Il2CppSystem.Collections.Generic.List<Vector3>();
            var rotsL = new Il2CppSystem.Collections.Generic.List<Quaternion>();
            ptsR.Add(Vector3.zero);  rotsR.Add(Quaternion.identity);
            ptsL.Add(Vector3.zero);  rotsL.Add(Quaternion.identity);
            g.grabLocPtsR  = ptsR;  g.grabLocRotsR = rotsR;
            g.grabLocPtsL  = ptsL;  g.grabLocRotsL = rotsL;

            AddFloater(go);
        }

        static void AddFloater(GameObject go)
        {
            if (go == null) return;

            var existing = go.transform.Find("Floater");
            if (existing != null) DestroyImmediate(existing.gameObject);

            Mesh readableMesh = null;
            var sourceMf = go.GetComponentInChildren<MeshFilter>();
            if (sourceMf?.sharedMesh != null)
                readableMesh = BuildPhysicsMesh(sourceMf.sharedMesh);

            if (readableMesh == null)
            {
                MelonLogger.Warning("[BabyBlocks] AddFloater: could not build readable mesh, skipping Floater.");
                return;
            }

            var floater = new GameObject("Floater");
            floater.SetActive(false);
            floater.transform.SetParent(go.transform, false);
            floater.layer = go.layer;

            var mf = floater.AddComponent<MeshFilter>();
            mf.sharedMesh = readableMesh;

            var wo = floater.AddComponent<WaterObject>();
            wo.convexifyMesh         = true;
            wo.simplifyMesh          = true;
            wo.targetTriangleCount   = 16;
            wo.calculateWaterNormals = true;
            wo.GenerateSimMesh();

            floater.SetActive(true);
        }

        static void RemoveGrabableComponents(GameObject go)
        {
            var hat = go.GetComponent<Hat>();
            if (hat != null) DestroyImmediate(hat);
            else
            {
                var grabable = go.GetComponent<Grabable>();
                if (grabable != null) DestroyImmediate(grabable);
            }
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null) DestroyImmediate(rb);
            var crusher = go.transform.Find("Crusher");
            if (crusher != null) DestroyImmediate(crusher.gameObject);
            var floater = go.transform.Find("Floater");
            if (floater != null) DestroyImmediate(floater.gameObject);
        }


        public void RemoveAll()
        {
            while (_objects.Count > 0)
                Remove(_objects[_objects.Count - 1]);
            foreach (var root in _groupRoots.Values)
                if (root != null) Destroy(root);
            _groupRoots.Clear();
            _nextGroupId = 1;
        }

        static readonly List<GameObject> _hiddenAudioObjects = new();
        // Third-party rendering objects (TVE, GPUI) disabled by type-name search.
        static readonly List<GameObject> _hiddenRenderObjects = new();
        // Renderers hidden on small physics props (rocks, debris). These often carry
        // Rigidbody/WaterObject(DWP2) components whose OnDisable touches native
        // jobs/NativeArrays — SetActive(false) on them can crash, so we only ever
        // toggle Renderer.enabled and leave the GameObject active.
        static readonly List<Renderer> _hiddenPropRenderers = new();
        // Cached BRL child renderers (MegaProxy LODs, GPUI pool objects) so OnUpdate
        // can suppress newly-activated ones cheaply without calling GetComponentsInChildren.
        internal static Renderer[] _brlRendererCache = Array.Empty<Renderer>();
        static readonly List<MonoBehaviour> _disabledTerrainComponents = new();
        static readonly List<Collider> _disabledTerrainColliders = new();
        // Player WaterObjects (torso + feet) frozen below sea level while Base
        // Map is off, so the player doesn't keep "swimming" in the hidden ocean.
        static readonly List<WaterObject> _suppressedWaterObjects = new();
        // Non-terrain colliders found inside loaded chunks (trash, rocks, candles,
        // etc. placed directly in chunk scenes rather than GPUI-instanced).
        static readonly List<Collider> _disabledPropColliders = new();

        static IEnumerable<GameObject> AllSceneRoots()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
                foreach (var root in SceneManager.GetSceneAt(i).GetRootGameObjects())
                    yield return root;
        }

        static bool HasCameraComponent(GameObject go)
        {
            foreach (var comp in go.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue;
                try
                {
                    var name = comp.GetIl2CppType().Name;
                    if (name.Contains("Camera") || name.Contains("Cinemachine")
                        || name.Contains("FlyCam") || name.Contains("PlayerMovement")) return true;
                }
                catch { }
            }
            return false;
        }

        // Hide a GameObject visually without touching SetActive/OnDisable — safe for
        // props with Rigidbody/WaterObject components. Tracks hidden renderers so
        // they can be restored exactly. Also disables non-terrain colliders on the
        // same hierarchy (Collider.enabled = false, unlike SetActive, doesn't fire
        // OnDisable so it's safe for WaterObject/Rigidbody props) so the player
        // can't collide with now-invisible props.
        static void HidePropRenderers(GameObject go)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r != null && r.enabled)
                {
                    r.enabled = false;
                    _hiddenPropRenderers.Add(r);
                }
            }

            foreach (var col in go.GetComponentsInChildren<Collider>(true))
            {
                if (col == null || !col.enabled) continue;
                try
                {
                    if (col.GetIl2CppType().Name == "TerrainCollider") continue;
                }
                catch { }
                col.enabled = false;
                _disabledPropColliders.Add(col);
            }
        }

        // Disable GameObjects whose Il2Cpp type name matches any fragment.
        // Uses SetActive on the whole GO so OnDisable fires and Camera callbacks unregister.
        static void SetRenderObjectsActive(bool enabled, List<GameObject> tracked,
                                           params string[] typeFragments)
        {
            if (!enabled)
            {
                tracked.Clear();
                foreach (var root in AllSceneRoots())
                {
                    foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (mb == null) continue;
                        try
                        {
                            var typeName = mb.GetIl2CppType().Name;
                            foreach (var frag in typeFragments)
                            {
                                if (typeName.Contains(frag))
                                {
                                    var go = mb.gameObject;
                                    if (go.activeSelf && !tracked.Contains(go) && !HasCameraComponent(go))
                                    {
                                        BBLog.Msg($"[BaseMap] SetActive(false) on '{go.name}' ({typeName})");
                                        go.SetActive(false);
                                        tracked.Add(go);
                                    }
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            else
            {
                foreach (var go in tracked)
                    if (go != null) go.SetActive(true);
                tracked.Clear();
            }
        }

        // Toggles the base map (terrain, props, vegetation, water/audio) for an empty
        // canvas to build on. The fly cam and player are left untouched throughout.
        //
        // IMPORTANT: brl.off must be flipped to true LAST (when hiding) and to false
        // FIRST (when restoring). Calling GetComponentsInChildren on BRL or its
        // children (or on scene roots that include BRL) while brl.off == true causes
        // a native Il2Cpp crash with no managed exception/log.
        public static void SetBaseMapEnabled(bool enabled, bool captureRestoreChapter = true)
        {
            BBLog.Msg($"[BaseMap] SetBaseMapEnabled({enabled}) — start");
            BaseMapEnabled = enabled;
            ApplyDayWeatherPlaylistOverride(enabled, captureRestoreChapter);

            var brl = BestRegionLoader.me;
            BBLog.Msg($"[BaseMap] brl={(brl != null ? "found" : "null")}");

            // Clear here (before any HidePropRenderers calls below) so hiding
            // performed during the chunk-scan isn't wiped by the prop-container
            // section's scan further down.
            if (!enabled) _hiddenPropRenderers.Clear();

            if (enabled)
            {
                // Restore the BRL world-position reference before touching anything else.
                if (brl != null) brl.off = false;
                BBLog.Msg("[BaseMap] brl.off=false done");

                foreach (var mb in _disabledTerrainComponents)
                    if (mb != null) mb.enabled = true;
                _disabledTerrainComponents.Clear();

                foreach (var col in _disabledTerrainColliders)
                    if (col != null) col.enabled = true;
                _disabledTerrainColliders.Clear();

                foreach (var col in _disabledPropColliders)
                    if (col != null) col.enabled = true;
                _disabledPropColliders.Clear();

                foreach (var r in _brlRendererCache)
                    if (r != null) r.enabled = true;
                _brlRendererCache = Array.Empty<Renderer>();
                BBLog.Msg("[BaseMap] terrain/renderer restore done");
            }
            else if (brl != null)
            {
                // Gather everything we need via GetComponentsInChildren BEFORE
                // setting brl.off, to avoid the native crash described above.
                _brlRendererCache = brl.GetComponentsInChildren<Renderer>(true);
                BBLog.Msg($"[BaseMap] brl renderer cache gathered: {_brlRendererCache.Length}");

                _disabledTerrainComponents.Clear();
                _disabledTerrainColliders.Clear();
                _disabledPropColliders.Clear();
                if (brl.chunkMap != null)
                {
                    BBLog.Msg($"[BaseMap] chunkMap count: {brl.chunkMap.Length}");
                    foreach (var cell in brl.chunkMap)
                    {
                        if (cell?.loadedChunk == null) continue;
                        foreach (var mb in cell.loadedChunk.GetComponentsInChildren<MonoBehaviour>(true))
                        {
                            if (mb == null || !mb.enabled) continue;
                            try
                            {
                                var n = mb.GetIl2CppType().Name;
                                if (n == "Terrain" || n.Contains("MicroSplat") || n.Contains("Terrain"))
                                    _disabledTerrainComponents.Add(mb);
                            }
                            catch { }
                        }

                        foreach (var col in cell.loadedChunk.GetComponentsInChildren<Collider>(true))
                        {
                            if (col == null || !col.enabled) continue;
                            try
                            {
                                if (col.GetIl2CppType().Name == "TerrainCollider")
                                    _disabledTerrainColliders.Add(col);
                            }
                            catch { }
                        }

                        // Decoration props (trash, rocks, candles, etc.) placed directly
                        // in the chunk scene rather than GPUI-instanced. Hide via
                        // Renderer.enabled only — same reasoning as HidePropRenderers.
                        HidePropRenderers(cell.loadedChunk);
                    }
                }
                BBLog.Msg($"[BaseMap] terrain components gathered: {_disabledTerrainComponents.Count}, terrain colliders: {_disabledTerrainColliders.Count}, prop colliders: {_disabledPropColliders.Count}");
            }

            // Non-GPUI prop instance containers (unnamed root GameObjects) and any
            // DynamicPropSpitter-spawned props (small rocks/debris with Rigidbody +
            // WaterObject). These are hidden via Renderer.enabled only — never
            // SetActive — because SetActive(false) on a WaterObject (DWP2) can crash
            // natively mid-simulation. Skip any root that carries a camera so the fly
            // cam stays alive. Done before brl.off is flipped so this scan is crash-safe.
            if (!enabled)
            {
                int rootCount = 0;
                foreach (var root in AllSceneRoots())
                {
                    rootCount++;
                    BBLog.Msg($"[BaseMap] scanning root #{rootCount} '{root.name}'");
                    if (HasCameraComponent(root)) continue;

                    if (root.name == "New Game Object" || root.name == "")
                    {
                        int before = _hiddenPropRenderers.Count;
                        HidePropRenderers(root);
                        BBLog.Msg($"[BaseMap]   hid {_hiddenPropRenderers.Count - before} renderers on '{root.name}' (prop container)");
                        continue;
                    }

                    foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (mb == null) continue;
                        try
                        {
                            if (mb.GetIl2CppType().Name.Contains("DynamicPropSpitter"))
                            {
                                int before = _hiddenPropRenderers.Count;
                                HidePropRenderers(mb.gameObject);
                                BBLog.Msg($"[BaseMap]   hid {_hiddenPropRenderers.Count - before} renderers on '{mb.gameObject.name}' (DynamicPropSpitter)");
                            }
                        }
                        catch { }
                    }
                }
                BBLog.Msg($"[BaseMap] hid {_hiddenPropRenderers.Count} prop renderers across {rootCount} scene roots");
            }
            else
            {
                foreach (var r in _hiddenPropRenderers)
                    if (r != null) r.enabled = true;
                _hiddenPropRenderers.Clear();
                BBLog.Msg("[BaseMap] restored prop renderers");
            }

            // TVE (The Vegetation Engine) and GPUI managers/pool objects.
            SetRenderObjectsActive(enabled, _hiddenRenderObjects,
                                   "TVE", "GPUInstancer");
            BBLog.Msg("[BaseMap] SetRenderObjectsActive done");

            // GlobalObjectParent: water, fog, NPCs, etc. Hidden via renderer toggling
            // only (never SetActive) — water bodies carry DWP2 WaterObject components
            // whose OnDisable crashes natively mid-simulation, same as small props above.
            var gop = GameObject.Find("GlobalObjectParent");
            if (gop != null)
            {
                if (!enabled)
                {
                    for (int i = 0; i < gop.transform.childCount; i++)
                    {
                        var child = gop.transform.GetChild(i).gameObject;
                        if (HasCameraComponent(child)) continue;
                        int before = _hiddenPropRenderers.Count;
                        HidePropRenderers(child);
                        BBLog.Msg($"[BaseMap] GOP child '{child.name}' hid {_hiddenPropRenderers.Count - before} renderers");
                    }
                }
                // Restoration of these renderers is handled by the prop-renderer
                // restore block above (shared _hiddenPropRenderers list).
            }
            BBLog.Msg("[BaseMap] GlobalObjectParent done");

            // Player swim/buoyancy: Crest's water data provider keeps WaterHeights
            // at its last computed value once the ocean is hidden (GetWaterHeights
            // is skipped while calculateWaterHeights == false, rather than reset),
            // so the player would keep "swimming" in the now-invisible water.
            // Freeze each of the player's WaterObjects (torso + feet) at a height
            // far below any terrain so ResultForce/ResultStates read as "not in
            // water"; re-enabling calculateWaterHeights lets Crest repopulate real
            // heights again.
            var player = PlayerMovement.me;
            if (player != null)
            {
                if (!enabled)
                {
                    _suppressedWaterObjects.Clear();
                    var allWaterObjs = (player.waterObjects ?? Array.Empty<WaterObject>())
                        .Concat(player.footWaterObjs ?? Array.Empty<WaterObject>());
                    foreach (var wo in allWaterObjs)
                    {
                        if (wo == null || !wo.calculateWaterHeights) continue;
                        wo.calculateWaterHeights = false;
                        if (wo.WaterHeights.IsCreated)
                            for (int i = 0; i < wo.WaterHeights.Length; i++)
                                wo.WaterHeights[i] = -10000f;
                        _suppressedWaterObjects.Add(wo);
                    }
                    BBLog.Msg($"[BaseMap] suppressed {_suppressedWaterObjects.Count} player water objects");
                }
                else
                {
                    foreach (var wo in _suppressedWaterObjects)
                        if (wo != null) wo.calculateWaterHeights = true;
                    _suppressedWaterObjects.Clear();
                    BBLog.Msg("[BaseMap] restored player water objects");
                }
            }

            // Crest ocean visuals: toggle the whole CrestWaterRenderer object.
            var crestWater = GameObject.Find("BigManagerPrefab/CrestWaterRenderer");
            if (crestWater != null)
            {
                crestWater.SetActive(enabled);
                BBLog.Msg($"[BaseMap] CrestWaterRenderer SetActive({enabled})");
            }

            // Audio
            if (!enabled)
            {
                _hiddenAudioObjects.Clear();
                foreach (string audioName in new[] { "Arranger", "Gimbal" })
                {
                    var go = GameObject.Find(audioName);
                    if (go != null && !HasCameraComponent(go))
                    {
                        BBLog.Msg($"[BaseMap] SetActive(false) on audio '{audioName}'");
                        go.SetActive(false);
                        _hiddenAudioObjects.Add(go);
                    }
                }
            }
            else
            {
                foreach (var go in _hiddenAudioObjects)
                    if (go != null) go.SetActive(true);
                _hiddenAudioObjects.Clear();
            }
            BBLog.Msg("[BaseMap] audio done");

            if (!enabled)
            {
                // Now actually disable the gathered terrain components/colliders/renderers,
                // and finally tell BRL to stop streaming. This must be last.
                foreach (var mb in _disabledTerrainComponents)
                    if (mb != null) mb.enabled = false;
                foreach (var col in _disabledTerrainColliders)
                    if (col != null) col.enabled = false;
                foreach (var col in _disabledPropColliders)
                    if (col != null) col.enabled = false;
                foreach (var r in _brlRendererCache)
                    if (r != null) r.enabled = false;
                BBLog.Msg("[BaseMap] terrain/renderer disable done");

                // brl.off stops UpdateDirtyGpuis → GPUI instance buffers go stale → rocks fade.
                // Chunks must stay ACTIVE (not SetActive false) so OnPreCull keeps its
                // world-position terrain reference and the camera doesn't break.
                if (brl != null) brl.off = true;
                BBLog.Msg("[BaseMap] brl.off=true done");
            }

            BBLog.Msg($"[BaseMap] SetBaseMapEnabled({enabled}) — end");
        }

        // Applies/restores the Day Weather Playlist override that goes with the base
        // map toggle. Disabling the base map captures Menu.curChapter (so it can be
        // restored later) and switches to DayWeatherPlaylist; re-enabling restores
        // the captured chapter. captureRestoreChapter=false is used when applying a
        // loaded level that already specifies the chapter to restore to.
        static void ApplyDayWeatherPlaylistOverride(bool baseMapEnabled, bool captureRestoreChapter)
        {
            if (Menu.me == null || Menu.me.campfireDatas == null || Menu.me.campfireDatas.Length == 0) return;

            if (!baseMapEnabled)
            {
                if (captureRestoreChapter && !_weatherPlaylistOverridden)
                    RestoreDayWeatherPlaylist = Menu.me.curChapter;
                _weatherPlaylistOverridden = true;
                SetCurChapter(DayWeatherPlaylist);
            }
            else if (_weatherPlaylistOverridden)
            {
                _weatherPlaylistOverridden = false;
                SetCurChapter(RestoreDayWeatherPlaylist);
            }
        }

        // Only drives the live Menu.curChapter (which WeatherMan reads each frame for
        // weather/time-of-day) — deliberately does NOT touch SaveGod.theSave.lastCampfire,
        // which is the persisted "current chapter" written to the player's actual save
        // file. Writing it here would let this editor-only preview leak into the real
        // save if the player saves & quits while the override is active.
        static void SetCurChapter(int index)
        {
            index = Mathf.Clamp(index, 0, Menu.me.campfireDatas.Length - 1);
            Menu.me.curChapter = index;
        }

        // Number of WeatherMan "Day Weather Playlist" options (Menu.campfireDatas
        // entries). 0 if Menu isn't ready yet.
        public static int DayWeatherPlaylistCount =>
            Menu.me != null && Menu.me.campfireDatas != null ? Menu.me.campfireDatas.Length : 0;

        // Sets the Day Weather Playlist dropdown selection. Applies it immediately
        // if the base map is currently hidden (the override is active).
        public static void SetDayWeatherPlaylist(int index)
        {
            DayWeatherPlaylist = index;
            if (!BaseMapEnabled && Menu.me != null && Menu.me.campfireDatas != null && Menu.me.campfireDatas.Length > 0)
                SetCurChapter(index);
        }

        // Applies a loaded level's saved base-map/weather state. restoreChapter comes
        // from the save file rather than being captured from the live Menu.curChapter.
        public static void ApplyLoadedBaseMapState(bool baseMapOff, int dayWeatherPlaylist, int restoreChapter)
        {
            DayWeatherPlaylist = dayWeatherPlaylist;
            RestoreDayWeatherPlaylist = restoreChapter;
            _weatherPlaylistOverridden = false;
            SetBaseMapEnabled(!baseMapOff, captureRestoreChapter: false);
        }

        // Defers ApplyLoadedBaseMapState until Menu.me.Teleport's coroutine (started by
        // TeleportToSpawnPoint) finishes. Menu.TeleportCo waits across many frames for
        // BestRegionLoader.fullyLoaded while the player is mid-teleport; setting
        // brl.off = true (from SetBaseMapEnabled(false)) during that window stalls BRL
        // forever, leaving Menu.me.teleporting stuck true — which breaks the spawn-point
        // teleport, leaves chunks around the destination unloaded ("missing" props), and
        // blocks input that's gated on !Menu.me.teleporting (e.g. tool-mode/edit-mode keys).
        [HideFromIl2Cpp]
        internal static IEnumerator ApplyLoadedBaseMapStateDelayed(bool baseMapOff, int dayWeatherPlaylist, int restoreChapter)
        {
            while (Menu.me != null && Menu.me.teleporting) yield return null;
            ApplyLoadedBaseMapState(baseMapOff, dayWeatherPlaylist, restoreChapter);
        }
    }
}
