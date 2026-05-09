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

                // MeshCollider on every part so raycasts and the player collide with
                // placed props.  Non-convex is fine — no Rigidbody on these objects.
                var mc = child.AddComponent<MeshCollider>();
                mc.sharedMesh = part.mesh;
            }

            var leo = root.AddComponent<LevelEditorObject>();
            leo.objectType     = "Addressable";
            leo.addressableKey = info.id;
            _objects.Add(leo);
            return leo;
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
