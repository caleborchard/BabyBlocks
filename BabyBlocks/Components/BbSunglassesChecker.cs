using System;
using Il2Cpp;
using UnityEngine;

namespace BabyBlocks
{
    class BbSunglassesChecker : MonoBehaviour
    {
        public BbSunglassesChecker(IntPtr ptr) : base(ptr) { }

        public static bool IsWearingSunglasses()
        {
            if (FlyCamController.FlyCamActive) return true;
            var pm = PlayerMovement.me;
            return pm != null && pm.currentHat != null && pm.currentHat.isSunglasses && !pm.inCutscene;
        }

        void Update()
        {
            bool visible = IsWearingSunglasses();
            foreach (var r in GetComponentsInChildren<Renderer>(true))
                if (r != null) r.enabled = visible;
        }
    }
}
