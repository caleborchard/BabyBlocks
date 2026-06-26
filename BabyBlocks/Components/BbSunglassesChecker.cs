using System;
using Il2Cpp;
using UnityEngine;

namespace BabyBlocks
{
    class BbSunglassesChecker : MonoBehaviour
    {
        public BbSunglassesChecker(IntPtr ptr) : base(ptr) { }

        // Tracks which renderer slots were enabled when this component was first attached, only those are used.
        bool[] _wasEnabled;

        public static bool IsWearingSunglasses()
        {
            if (FlyCamController.FlyCamActive) return true;
            var pm = PlayerMovement.me;
            return pm != null && pm.currentHat != null && pm.currentHat.isSunglasses && !pm.inCutscene;
        }

        void Awake()
        {
            var renderers = GetComponentsInChildren<Renderer>(true);
            _wasEnabled = new bool[renderers.Length];
            for (int i = 0; i < renderers.Length; i++)
                _wasEnabled[i] = renderers[i] != null && renderers[i].enabled;
        }

        void Update()
        {
            if (_wasEnabled == null) return;
            bool visible = IsWearingSunglasses();
            var renderers = GetComponentsInChildren<Renderer>(true);
            int count = Math.Min(renderers.Length, _wasEnabled.Length);
            for (int i = 0; i < count; i++)
            {
                if (renderers[i] == null) continue;
                if (_wasEnabled[i]) renderers[i].enabled = visible;
            }
        }
    }
}
