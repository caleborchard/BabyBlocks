using System;
using BabyBlocks.Networking;
using Il2Cpp;
using UnityEngine;

namespace BabyBlocks
{
    public enum PhysicsMode { Static = 0, Rigidbody = 1, Grabable = 2, Hat = 3 }

    public class LevelEditorObject : MonoBehaviour
    {
        public LevelEditorObject(IntPtr ptr) : base(ptr) { }
        public string objectType = "Cube"; // primitive type name, or "Addressable"
        public string addressableKey = ""; // non-empty for game asset props, stable ID for save/load
        public int chunkIndex = -1;
        public Vector2Int chunkCoord = new Vector2Int(-1, -1);
        public Vector3 loopBasePosition;
        public Quaternion loopBaseRotation = Quaternion.identity;
        public Vector3 loopBaseScale = Vector3.one;
        public bool hasLoopBasePosition;
        public bool hasLoopBaseRotation;
        public bool hasLoopBaseScale;

        // Per instance physics mode saved in .bbb, not in metadata
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

        // Grabable: offset (in hand local space) where the object sits when held
        public Vector3 grabOffsetPos = Vector3.zero;
        public Vector3 grabOffsetRot = Vector3.zero; // Euler degrees

        // Hat: additive offset on top of the default head placement
        public Vector3 hatOffsetPos = Vector3.zero;
        public Vector3 hatOffsetRot = Vector3.zero; // Euler degrees

        // Per-instance material/surface override applied via MaterialConstructionPanel
        public int materialConstructionId = -1;

        // Per-instance overrides independent of any material construction entry.
        public bool sunglassesNeeded; // adds BbSunglassesChecker (prop invisible without sunglasses hat)
        public bool playerPassthrough; // makes all colliders triggers so player walks through the prop
        public bool freezeUntilHit; // Rigidbody stays kinematic until something collides with it

        // Mirrors the surface tag applied via ApplySurfaceType so the UI can read it back
        // without relying on gameObject.tag (Il2Cpp can return stale cached values).
        public string surfaceTypeTag = "";

        // Material tint. (255,255,255) = no tint (white).
        public Vector3 materialTint = new Vector3(255f, 255f, 255f);

        // Backing store for tint overlay: renderers snapshotted on first tint
        // Per prop Material instance from the BabyBlocks/TintOverlay shader.
        internal Renderer[] _tintRenderers;
        internal Material   _tintMaterial;

        // Non zero once this object has been synced over the network (placed or received via ModNetworking).
        // Shared between both clients' copies of the same logical prop so transform updates apply in-place
        // instead of respawning a duplicate. 0 = not network-tracked.
        public ulong netId = 0;

        // Fired by Unity when this Rigidbody prop is struck by another physics object.
        // Handles the freezeUntilHit gameplay unfreeze, no ops for all other props.
        public void OnCollisionEnter(Collision collision)
        {
            if (!freezeUntilHit || !PhysicsObjectManager.HasFreezeUntilHit(this)) return;
            var otherRb = collision.rigidbody;
            int otherLayer = collision.collider.gameObject.layer;
            bool isPlayer = otherLayer == LayerCache.RagdollLayer;
            bool isActiveProp = otherRb != null
                                && !otherRb.isKinematic
                                && otherRb.GetComponentInParent<LevelEditorObject>(true) != null;
            if (!isPlayer && !isActiveProp) return;
            PhysicsObjectManager.OnFreezeUntilHitTriggered(this);
            PhysicsObjectManager.UnfreezeHitProp(this, collision);
            if (netId != 0) ModNetworking.SendPropFreezeReleased(netId);
        }
    }
}
