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

        // Spawns a game asset prop. Each mesh part becomes a child GameObject with its own
        // MeshCollider so collision uses exact mesh geometry for every part.
        // Multi-part models (e.g. CreepDoll) are grouped under one root LevelEditorObject.
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

                // MeshCollider on the child so raycasts hit exact mesh geometry.
                // Non-convex MeshColliders are fine here since we have no Rigidbody.
                // Skip collider for GPUI props — visual test only for now.
                if (!info.IsGpui)
                {
                    var mc = child.AddComponent<MeshCollider>();
                    mc.sharedMesh = part.mesh;
                }
            }

            var leo = root.AddComponent<LevelEditorObject>();
            leo.objectType     = "Addressable";
            leo.addressableKey = info.id;
            _objects.Add(leo);
            return leo;
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
