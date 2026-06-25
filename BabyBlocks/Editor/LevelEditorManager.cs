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

namespace BabyBlocks
{
    public partial class LevelEditorManager : MonoBehaviour
    {
        public LevelEditorManager(IntPtr ptr) : base(ptr) { }

        const float WorldLoopSize = 512f;
        const float ChunkWorldSize = 64f;
        const int ChunksPerAxis = (int)(WorldLoopSize / ChunkWorldSize);

        public static bool ChunkLoopingEnabled = true;

        public static LevelEditorManager Instance { get; private set; }

        // Used by BaseMapController to identify/skip the editor's own placed props.
        internal static GameObject PropsContainer => Instance != null ? Instance._propsContainer : null;

        readonly List<LevelEditorObject> _objects = new();
        // Keyed by groupId. Using a dictionary avoids the Il2Cpp wrapper-equality pitfall where
        // List<GameObject>.Contains() always returns false because each access returns a new
        // managed wrapper for the same native object.
        internal readonly Dictionary<int, GameObject> _groupRoots = new();
        internal int _nextGroupId = 1;
        internal bool _editorModeActive;
        GameObject _propsContainer;

        [HideFromIl2Cpp]
        internal IReadOnlyList<LevelEditorObject> Objects => _objects;

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

            MaterialCatalog.RefreshMicroSplatLayerMaterials();
        }

        [HideFromIl2Cpp]
        internal LevelEditorObject SpawnFromPropInfo(PropInfo info, Vector3 position)
        {
            PropLibrary.LoadPropData(info);
            bool keepHierarchy = info.sourcePrefab != null && PropMetadataStore.GetKeepOriginalHierarchy(info.id);

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
                // Standard colliders are applied below, after ApplyDisabledRenderersToRoot,
                // so the render-mesh path sees the correct mr.enabled state.
            }

            if (_propsContainer != null)
                root.transform.SetParent(_propsContainer.transform, true);

            string surfaceTag = PropMetadataStore.GetSurfaceType(info.id);
            if (!string.IsNullOrEmpty(surfaceTag))
                PropInstanceServices.ApplySurfaceTypeToRoot(root, surfaceTag);

            MaterialCatalog.ApplyMaterialOverridesToRoot(info.id, root);
            PropInstanceServices.ApplyDisabledRenderersToRoot(info.id, root);

            if (!keepHierarchy && !PropLibrary.IsNegativeCollisionProp(info.id) && !PropLibrary.IsSpawnPointProp(info.id))
                PhysicsObjectManager.ApplyColliderParts(root, info, PropMetadataStore.GetUseRenderMeshCollider(info.id));

            PropInstanceServices.ApplyBushColliderToRoot(info.id, root);

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

            // Mirror the metadata default so the Properties Panel can read it without
            // relying on gameObject.tag (ApplySurfaceTypeToRoot above ran before this LEO existed).
            string metaSurfTag = PropMetadataStore.GetSurfaceType(info.id);
            if (!string.IsNullOrEmpty(metaSurfTag) && metaSurfTag != "Untagged")
                leo.surfaceTypeTag = metaSurfTag;

            // If the native prefab already contains a Rigidbody (e.g. the Toy Wagon),
            // freeze it immediately so it doesn't fall while in editor mode. The normal
            // ExitEditorPhysicsMode path unfreezes it when gameplay starts.
            if (keepHierarchy && root.GetComponent<Rigidbody>() != null)
            {
                leo.physicsMode = PhysicsMode.Rigidbody;
                PhysicsObjectManager.FreezeRigidBodyObject(leo, true);
                // Disable native scripts (Skateboard etc.) so they don't fight our frozen
                // transform while in editor mode. Re-enabled by ExitEditorPhysicsMode.
                PhysicsObjectManager.DisableKeepHierarchyBehaviours(leo);
                leo.isPhysicsManaged = true;
                // AmplifyImpostor LODs have no baked texture in a runtime-placed context
                // and render as invisible at distance. Force the highest LOD at all times.
                foreach (var lg in root.GetComponentsInChildren<LODGroup>(true))
                    lg.enabled = false;
            }

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

        internal const int PropLayer        = 16; // "Props" layer — required for the surface Tag to work
        internal const int PropsDynamicLayer = 24; // "PropsDynamic" layer — used by all native kickable/dynamic props

        public void Remove(LevelEditorObject obj)
        {
            if (obj == null) return;
            // Dissolve the entire group before removing; DissolveGroup moves all
            // siblings back to _propsContainer so they survive the deletion.
            int gid = obj.groupId > 0 ? obj.groupId
                    : obj.physicsGroupId > 0 ? obj.physicsGroupId : 0;
            if (gid > 0) GroupManager.DissolveGroup(gid);
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
            var playerPos = PhysicsObjectManager.GetPlayerReferencePosition(reference);
            bool canLoop = reference != null && ChunkLoopingEnabled
                        && !LevelEditor.isDragging && !PropPalette.IsDragging;

            PhysicsObjectManager.PhysicsControlSeen.Clear();

            for (int i = 0; i < _objects.Count; i++)
            {
                var obj = _objects[i];
                if (obj == null) continue;

                var basePos = GetLoopBasePosition(obj);

                if (obj.isPhysicsManaged)
                {
                    PhysicsObjectManager.UpdatePhysicsObjectState(obj, playerPos);
                    PhysicsObjectManager.MarkPhysicsChunkIndependent(obj);
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

        public void RemoveAll()
        {
            while (_objects.Count > 0)
                Remove(_objects[_objects.Count - 1]);
            foreach (var root in _groupRoots.Values)
                if (root != null) Destroy(root);
            _groupRoots.Clear();
            _nextGroupId = 1;
        }
    }
}
