using System;
using BabyBlocks.Networking;
using UnityEngine;

namespace BabyBlocks
{
    // Attached to a Rigidbody prop whose freezeUntilHit flag is set.  On the first
    // collision the rigidbody is made dynamic and the component destroys itself.
    // Also notifies peers so their copy unfreezes at the same time.
    //
    // IMPORTANT: never use GetComponent<FreezeUntilHitBehaviour>() to find this component.
    // Il2CppInterop's generic GetComponent<T> static constructor crashes for mod-defined
    // types if AddComponent<T> hasn't been called first.  Use PhysicsObjectManager's
    // HasFreezeUntilHit / OnFreezeUntilHitTriggered instead (same pattern as BbHatSunglassesFlag).
    public class FreezeUntilHitBehaviour : MonoBehaviour
    {
        public FreezeUntilHitBehaviour(IntPtr ptr) : base(ptr) { }

        internal LevelEditorObject Leo;

        public void OnCollisionEnter(Collision collision)
        {
            PhysicsObjectManager.OnFreezeUntilHitTriggered(this);
            if (Leo != null)
            {
                PhysicsObjectManager.UnfreezeHitProp(Leo);
                if (Leo.netId != 0)
                    ModNetworking.SendPropFreezeReleased(Leo.netId);
            }
            UnityEngine.Object.Destroy(this);
        }
    }
}
