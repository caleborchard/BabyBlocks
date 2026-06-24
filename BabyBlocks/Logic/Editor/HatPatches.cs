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
