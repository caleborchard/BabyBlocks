using System;
using Il2Cpp;
using UnityEngine;

namespace BabyBlocks
{
    public enum PhysicsMode { Static = 0, Rigidbody = 1, Grabable = 2, Hat = 3 }

    public class LevelEditorObject : MonoBehaviour
    {
        public LevelEditorObject(IntPtr ptr) : base(ptr) { }
        public string objectType     = "Cube"; // primitive type name, or "Addressable"
        public string addressableKey = "";     // non-empty for game asset props; stable ID for save/load
        public int chunkIndex = -1;
        public Vector2Int chunkCoord = new Vector2Int(-1, -1);
        public Vector3 loopBasePosition;
        public Quaternion loopBaseRotation = Quaternion.identity;
        public Vector3 loopBaseScale = Vector3.one;
        public bool hasLoopBasePosition;
        public bool hasLoopBaseRotation;
        public bool hasLoopBaseScale;

        // Per-instance physics mode — saved in .bbb, not in metadata
        public PhysicsMode physicsMode = PhysicsMode.Static;
        public float hatHairAmt = 0f;
        public int groupId = 0;
        public int physicsGroupId = 0;
        public bool isPhysicsManaged;

        public bool editorFreezeStateValid;
        public Vector3 editorFreezePosition;
        public Quaternion editorFreezeRotation;
        public Vector3 editorFreezeScale;
        public Vector3 editorFreezeVelocity;
        public Vector3 editorFreezeAngularVelocity;
        public bool editorFreezeIsKinematic;
        public bool editorFreezeUseGravity;
        public RigidbodyConstraints editorFreezeConstraints;

        // Grabable: offset (in hand-local space) where the object sits when held
        public Vector3 grabOffsetPos = Vector3.zero;
        public Vector3 grabOffsetRot = Vector3.zero; // Euler degrees

        // Hat: additive offset on top of the default head placement
        public Vector3 hatOffsetPos = Vector3.zero;
        public Vector3 hatOffsetRot = Vector3.zero; // Euler degrees

        // Per-instance material/surface override applied via MaterialConstructionPanel —
        // an id into MaterialConstructionLibrary.Entries, -1 if none.
        public int materialConstructionId = -1;

        // Non-zero once this object has been synced over the network (placed or
        // received via ModNetworking). Shared between both clients' copies of the
        // same logical prop so transform updates apply in-place instead of
        // respawning a duplicate. 0 = not network-tracked.
        public ulong netId = 0;
    }
}
