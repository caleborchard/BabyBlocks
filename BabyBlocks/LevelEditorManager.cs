using System;
using System.Collections.Generic;
using System.IO;
using Il2Cpp;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace BabyBlocks
{
    public class LevelEditorManager : MonoBehaviour
    {
        public LevelEditorManager(IntPtr ptr) : base(ptr) { }

        public static LevelEditorManager Instance { get; private set; }

        readonly List<LevelEditorObject> _objects = new();
        public IReadOnlyList<LevelEditorObject> Objects => _objects;

        static string SavePath =>
            Path.Combine(MelonEnvironment.UserDataDirectory, "FlyAndQuickSave_level.json");

        public void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            if (!PropLibrary.IsInitialized) PropLibrary.Init();
        }

        // Spawns a game asset prop.
        //
        // GPUI props with a pool template (rocks, roots, trees, etc.) are spawned by
        // cloning the pre-instantiated pool object, which preserves MicroSplatRockObject
        // and any other component-driven rendering, giving correct materials automatically.
        //
        // Other props (wooden planks, beams, etc.) build child GameObjects from
        // PropMeshPart data as before.  Each part gets its own MeshCollider.
        public LevelEditorObject SpawnFromPropInfo(PropInfo info, Vector3 position)
        {
            PropLibrary.LoadPropData(info);
            if (!info.HasMesh)
            {
                MelonLogger.Warning($"[LevelEditorManager] Cannot spawn {info.displayName}: no mesh.");
                return null;
            }

            // ── Mesh-part path ───────────────────────────────────────────────
            var root = new GameObject($"LEO_{info.displayName}");
            root.transform.position = position;
            root.layer = PropLayer;

            foreach (var part in info.parts)
            {
                var child = new GameObject("Part");
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

            if (PropMetadataPanel.GetUseRenderMeshCollider(info.id))
                ApplyColliderParts(root, info);

            var leo = root.AddComponent<LevelEditorObject>();
            leo.objectType     = "Addressable";
            leo.addressableKey = info.id;
            _objects.Add(leo);
            return leo;
        }

        // Per-source-mesh cache so the GPU readback only happens once per unique mesh.
        static readonly Dictionary<int, Mesh> _physicsMeshCache = new();

        // Reads vertex positions and triangle indices from the GPU vertex/index buffers.
        // Game meshes are shipped without Read/Write enabled, so Mesh.vertices is unavailable.
        // GetVertexBuffer/GetIndexBuffer bypass that; position is assumed Float32×3 at byte-offset 0
        // in stream 0 (standard Unity layout). Returns null and caches null on failure.
        static Mesh BuildPhysicsMesh(Mesh source)
        {
            if (source == null) return null;
            int id = source.GetInstanceID();
            if (_physicsMeshCache.TryGetValue(id, out var hit)) return hit;

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
                int iCount = (int)source.GetIndexCount(0);
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
                ib.Release();

                result = new Mesh { name = source.name + "_phys" };
                result.vertices  = positions;
                result.triangles = tris;
                result.RecalculateBounds();
            }
            catch { result = null; }

            _physicsMeshCache[id] = result;
            return result;
        }

        // Adds PropCollider children to root based on the currently enabled MeshRenderers.
        // For props with pre-cooked prefab colliders those are used instead.
        // Deduplicates LOD variants by bounds so only one collider per unique mesh shape is added.
        public static void ApplyColliderParts(GameObject root, PropInfo info)
        {
            if (root == null || info == null) return;
            int layer = PropLayer;

            if (info.HasColliderParts)
            {
                foreach (var cp in info.colliderParts)
                {
                    var go = new GameObject("PropCollider");
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

            // GPU-readback path: one collider per unique-bounds enabled renderer.
            var seenBounds = new HashSet<string>();
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || !mr.enabled) continue;

                var b   = mf.sharedMesh.bounds;
                string key = $"{b.center.x:F2},{b.center.y:F2},{b.center.z:F2}|{b.size.x:F2},{b.size.y:F2},{b.size.z:F2}";
                if (!seenBounds.Add(key)) continue;

                var go = new GameObject("PropCollider");
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = mf.transform.localPosition;
                go.transform.localRotation = mf.transform.localRotation;
                go.transform.localScale    = mf.transform.localScale;
                go.layer = layer;

                var physMesh = BuildPhysicsMesh(mf.sharedMesh);
                if (physMesh != null)
                {
                    var mc = go.AddComponent<MeshCollider>();
                    mc.sharedMesh = physMesh;
                }
                else
                {
                    var bc = go.AddComponent<BoxCollider>();
                    bc.center = b.center;
                    bc.size   = b.size;
                }
            }
        }

        // Sets the layer on a GameObject and all of its descendants.
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
            UseAccurateCollider(go);
            var leo = go.AddComponent<LevelEditorObject>();
            leo.objectType = type.ToString();
            _objects.Add(leo);
            return leo;
        }

        // First layer in PropTerrainMask so the player collides with placed objects
        // and fly-cam raycasts land on them.
        static int PropLayer
        {
            get
            {
                int mask = LayerCache.PropTerrainMask;
                for (int i = 0; i < 32; i++)
                    if ((mask & (1 << i)) != 0) return i;
                return 0;
            }
        }

        // Replaces the default primitive collider with a MeshCollider for accurate hit shapes.
        static void UseAccurateCollider(GameObject go)
        {
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = go.GetComponent<MeshFilter>().sharedMesh;
        }

        public void Remove(LevelEditorObject obj)
        {
            if (obj == null) return;
            _objects.Remove(obj);
            Destroy(obj.gameObject);
        }

        [Serializable] class SaveFile { public List<ObjData> objects = new(); }

        [Serializable]
        class ObjData
        {
            public string type;
            public float px, py, pz;
            public float rx, ry, rz;
            public float sx, sy, sz;
        }
        /*
        public void Save() { ... }
        public void Load() { ... }
        */
    }
}
