using System.Collections.Generic;
using UnityEngine;

namespace BabyBlocks
{
    public class PropMeshPart
    {
        public Mesh       mesh;
        public Material[] materials;
        public Vector3    localPosition;
        public Quaternion localRotation;
        public Vector3    localScale;
    }

    public class PropColliderPart
    {
        public enum ColliderType { Mesh, Box, Sphere, Capsule }
        public ColliderType type;
        public Mesh  mesh;
        public bool  convex;
        public Vector3 center;
        public Vector3 size;
        public float radius;
        public float height;
        public int   direction;
        public Vector3    localPosition;
        public Quaternion localRotation;
        public Vector3    localScale;
    }

    public class PropInfo
    {
        public readonly string id;
        public          string displayName;

        public List<PropMeshPart>     parts         = new();
        public List<PropColliderPart> colliderParts = new();
        public bool HasColliderParts => colliderParts != null && colliderParts.Count > 0;
        public bool isLoaded;
        public bool isInvalid;

        public int  gpuiIndex = -1;
        public bool IsGpui => gpuiIndex >= 0;
        public string visualPath     = "";
        public string gpuiPrefabName = "";

        public bool HasMesh      => parts != null && parts.Count > 0;
        public bool IsPrimitive  => id.StartsWith("primitive://", System.StringComparison.Ordinal);

        // Holds the Addressables-loaded asset so it can be released when the prop is unloaded.
        internal UnityEngine.Object _addressableAsset;
        // Original prefab/template kept for keepOriginalHierarchy spawning (bones, rigidbodies, etc.).
        internal GameObject sourcePrefab;

        public PropInfo(string key, string name = null)
        {
            id = key;
            displayName = name ?? key;
        }
    }
}
