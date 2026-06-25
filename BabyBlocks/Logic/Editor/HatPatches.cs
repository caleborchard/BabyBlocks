using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace BabyBlocks
{
    // Safety-net body-collision patches for custom hat props.
    //
    // Native WearHat accesses hat.colls[0] and casts it to BoxCollider. We prepend a tiny
    // trigger BoxCollider at index 0 in AddGrabableComponent so the cast succeeds. These
    // patches additionally ensure all child colliders on a custom hat are ignored by / restored
    // to body collisions on wear and knock-off, since native IgnoreBodyCollisions only covers
    // the entries in hat.colls.
    //
    // PlaceInHandPatch fixes a scale mismatch in native Grabable.ToWorld / PlaceInHand.
    // Native code stores grabLocPtsR as prop-local (pre-scale) positions and uses them in two ways:
    //   ToWorld  → prop.TransformPoint(-pt)  — applies prop scale, correct for local-space pts
    //   PlaceInHand → hand.TransformPoint(pt) — hand has no scale, expects world-scale pts
    // These are identical when prop scale = (1,1,1), but diverge for custom scaled props.
    // SyncGrabOffset stores grabOffsetPos / localScale (local-space) so ToWorld is correct.
    // PlaceInHandPatch re-multiplies by scale before the hand.TransformPoint call.

    internal static class HatBodyColls
    {
        internal static void SetIgnore(Hat hat, bool ignore)
        {
            try
            {
                var pm = PlayerMovement.me;
                if (pm == null) return;
                var bodyColls = pm.allBodyColliders;
                if (bodyColls == null) return;
                var hatColls = hat.GetComponentsInChildren<Collider>(true);
                foreach (var hc in hatColls)
                    foreach (var bc in bodyColls)
                        Physics.IgnoreCollision(hc, bc, ignore);
            }
            catch { }
        }

        internal static bool IsCustomHat(Hat hat)
        {
            try { return hat != null && hat.GetComponent<LevelEditorObject>() != null; }
            catch { return false; }
        }
    }

    [HarmonyPatch(typeof(Grabable), "PlaceInHand")]
    class PlaceInHandPatch
    {
        static void Postfix(Grabable __instance, Transform hand, int handIndex)
        {
            if (__instance.GetComponent<LevelEditorObject>() == null) return;
            var scale = __instance.transform.localScale;
            if (scale.x == 1f && scale.y == 1f && scale.z == 1f) return;

            var pts  = handIndex == 0 ? __instance.grabLocPtsR  : __instance.grabLocPtsL;
            var rots = handIndex == 0 ? __instance.grabLocRotsR : __instance.grabLocRotsL;
            if (pts == null || pts.Count == 0) return;

            // pts[0] is stored in local (pre-scale) space. Re-apply scale so hand.TransformPoint
            // receives the world-scale offset that positions the grab point at the hand.
            var localPt     = pts[0];
            var worldOffset = new Vector3(localPt.x * scale.x, localPt.y * scale.y, localPt.z * scale.z);
            __instance.transform.position = hand.TransformPoint(worldOffset);
            __instance.transform.rotation = hand.rotation * rots[0];
        }
    }

    [HarmonyPatch(typeof(PlayerMovement), "WearHat")]
    class WearHatPatch
    {
        static void Postfix(PlayerMovement __instance, Hat hat)
        {
            if (hat == null || !HatBodyColls.IsCustomHat(hat)) return;
            if (hat.heldState == HeldState.head)
                HatBodyColls.SetIgnore(hat, true);
        }
    }

    [HarmonyPatch(typeof(PlayerMovement), "KnockOffHat")]
    class KnockOffHatPatch
    {
        static Hat _knockedOffHat;

        static void Prefix(PlayerMovement __instance)
        {
            _knockedOffHat = __instance.currentHat;
        }

        static void Postfix()
        {
            var hat = _knockedOffHat;
            _knockedOffHat = null;
            if (hat == null || !HatBodyColls.IsCustomHat(hat)) return;
            HatBodyColls.SetIgnore(hat, false);
        }
    }
}
