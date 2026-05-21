using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace BabyBlocks
{
    public class LevelEditorManager : MonoBehaviour
    {
        public LevelEditorManager(IntPtr ptr) : base(ptr) { }

        const float WorldLoopSize = 512f;
        const float ChunkWorldSize = 64f;
        const int ChunksPerAxis = (int)(WorldLoopSize / ChunkWorldSize);

        public static bool ChunkLoopingEnabled = true;

        public static LevelEditorManager Instance { get; private set; }

        readonly List<LevelEditorObject> _objects = new();
        public IReadOnlyList<LevelEditorObject> Objects => _objects;

        public void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            if (!PropLibrary.IsInitialized) PropLibrary.Init();
        }

        public LevelEditorObject SpawnFromPropInfo(PropInfo info, Vector3 position)
        {
            PropLibrary.LoadPropData(info);
            if (!info.HasMesh)
            {
                MelonLogger.Warning($"[LevelEditorManager] Cannot spawn {info.displayName}: no mesh.");
                return null;
            }

            var root = new GameObject($"LEO_{info.displayName}");
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

            ApplyColliderParts(root, info, PropMetadataPanel.GetUseRenderMeshCollider(info.id));

            string surfaceTag = PropMetadataPanel.GetSurfaceType(info.id);
            if (!string.IsNullOrEmpty(surfaceTag))
                PropMetadataPanel.ApplySurfaceTypeToRoot(root, surfaceTag);

            PropMetadataPanel.ApplyMaterialOverridesToRoot(info.id, root);
            PropMetadataPanel.ApplyDisabledRenderersToRoot(info.id, root);

            var leo = root.AddComponent<LevelEditorObject>();
            leo.objectType     = "Addressable";
            leo.addressableKey = info.id;
            _objects.Add(leo);
            InitializeLoopBase(leo, position);
            PropLibrary.AddRef(info.id);
            return leo;
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
            if (_physicsMeshCache.TryGetValue(id, out var physMap)
                && physMap.TryGetValue(cacheKey, out var hit))
                return hit;

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
                            mc.sharedMesh = cp.mesh;
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
            _objects.Remove(obj);
            if (!string.IsNullOrEmpty(obj.addressableKey))
                PropLibrary.RemoveRef(obj.addressableKey);
            Destroy(obj.gameObject);
        }

        public static Vector2Int GetChunkCoord(Vector3 position)
        {
            float wrappedX = Mathf.Repeat(position.x + WorldLoopSize * 0.5f, WorldLoopSize);
            float wrappedZ = Mathf.Repeat(position.z + WorldLoopSize * 0.5f, WorldLoopSize);

            int chunkX = Mathf.Clamp(Mathf.FloorToInt(wrappedX / ChunkWorldSize), 0, ChunksPerAxis - 1);
            int chunkZ = Mathf.Clamp(Mathf.FloorToInt(wrappedZ / ChunkWorldSize), 0, ChunksPerAxis - 1);
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
            obj.hasLoopBasePosition = true;
            UpdateChunkData(obj, obj.loopBasePosition);
        }

        public void SyncLoopBases(IEnumerable<LevelEditorObject> objects)
        {
            if (objects == null) return;
            foreach (var obj in objects)
                SyncLoopBase(obj);
        }

        void Update()
        {
            RefreshChunkMemberships();
            ApplyChunkLooping();
        }

        void RefreshChunkMemberships()
        {
            for (int i = 0; i < _objects.Count; i++)
            {
                var obj = _objects[i];
                if (obj == null) continue;
                UpdateChunkData(obj, GetChunkSourcePosition(obj));
            }
        }

        void UpdateChunkData(LevelEditorObject obj, Vector3 position)
        {
            if (obj == null) return;

            var coord = GetChunkCoord(position);
            int index = GetChunkIndex(coord);
            obj.chunkCoord = coord;
            obj.chunkIndex = index;
        }

        void ApplyChunkLooping()
        {
            var reference = GetRenderReference();
            if (reference == null || !ChunkLoopingEnabled || LevelEditor.isDragging || PropPalette.IsDragging)
                return;

            for (int i = 0; i < _objects.Count; i++)
            {
                var obj = _objects[i];
                if (obj == null) continue;

                var basePos = GetLoopBasePosition(obj);
                var loopedPos = GetLoopedPosition(basePos, reference.position);
                if (obj.transform.position != loopedPos)
                    obj.transform.position = loopedPos;

                UpdateChunkData(obj, basePos);
            }
        }

        Vector3 GetChunkSourcePosition(LevelEditorObject obj)
            => GetLoopBasePosition(obj);

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
            obj.hasLoopBasePosition = true;
            UpdateChunkData(obj, position);
        }

        Vector3 GetLoopedPosition(Vector3 basePosition, Vector3 referencePosition)
        {
            float shiftX = Mathf.Round((referencePosition.x - basePosition.x) / WorldLoopSize) * WorldLoopSize;
            float shiftZ = Mathf.Round((referencePosition.z - basePosition.z) / WorldLoopSize) * WorldLoopSize;
            return new Vector3(basePosition.x + shiftX, basePosition.y, basePosition.z + shiftZ);
        }

        Transform GetRenderReference()
        {
            var mainCam = Camera.main;
            if (mainCam != null)
                return mainCam.transform;

            var player = PlayerMovement.me;
            if (player == null) return null;

            if (player.flyCam != null && (Core.flyCamActive || player.flyCam.gameObject.activeInHierarchy))
                return player.flyCam.transform;

            return player.transform;
        }

        static int WrappedChunkDelta(int a, int b)
        {
            int delta = Mathf.Abs(a - b);
            return Mathf.Min(delta, ChunksPerAxis - delta);
        }

        public void RemoveAll()
        {
            while (_objects.Count > 0)
                Remove(_objects[_objects.Count - 1]);
        }
    }
}
