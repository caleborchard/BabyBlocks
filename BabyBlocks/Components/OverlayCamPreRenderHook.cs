using System;
using UnityEngine;

namespace BabyBlocks
{
    // Attached to _overlayCam's GameObject so Unity calls OnPreRender() just before the
    // overlay camera renders. By that point ALL LateUpdate() calls (including Cinemachine's
    // camera update) have already run, so Camera.main.worldToCameraMatrix is current.
    // We use this to re-record the screen-space outline CommandBuffer with fresh matrices,
    // correcting the 1-frame stale drift that occurs when recording in Update().
    class OverlayCamPreRenderHook : MonoBehaviour
    {
        public OverlayCamPreRenderHook(IntPtr ptr) : base(ptr) { }

        public void OnPreRender()
        {
            GizmoRenderer.RefreshSSBufferMatrices();
        }
    }
}
