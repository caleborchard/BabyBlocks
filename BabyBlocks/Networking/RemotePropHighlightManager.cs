using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BabyBlocks.Networking
{
    // Editor-only stencil outlines (reusing GizmoRenderer's outline shell pipeline) drawn
    // around props that other connected players currently have selected, colored with that
    // player's suit color. Mirrors RemoteFreecamManager's per-peer dictionary and visibility
    // rule (only shown while the local player has the Baby Blocks editor open).
    internal static class RemotePropHighlightManager
    {
        class Highlight
        {
            public LevelEditorObject target;
            public Color color;
            public Material outlineMat;
            public CommandBuffer buffer;
            public Camera attachedCam;
        }

        static readonly Dictionary<byte, Highlight> _highlights = new();

        public static void Update()
        {
            if (_highlights.Count == 0) return;

            bool editorActive = FlyCamController.FlyCamActive && FlyCamController.CursorMode;
            var cam = editorActive ? Camera.main : null;
            if (editorActive) GizmoRenderer.EnsureStencilMaterials();

            List<byte> stale = null;
            foreach (var kv in _highlights)
            {
                var h = kv.Value;
                if (h.target == null)
                {
                    (stale ??= new List<byte>()).Add(kv.Key);
                    continue;
                }

                Detach(h);

                if (cam == null || !GizmoRenderer.StencilMaterialsReady) continue;

                h.buffer.Clear();
                GizmoRenderer.DrawRemoteOutline(h.target, h.outlineMat, h.buffer);
                cam.AddCommandBuffer(CameraEvent.AfterEverything, h.buffer);
                h.attachedCam = cam;
            }

            if (stale != null)
                foreach (var uuid in stale) Remove(uuid);
        }

        public static void SetHighlight(byte uuid, LevelEditorObject target, Color color)
        {
            if (target == null) return;
            if (!_highlights.TryGetValue(uuid, out var h))
            {
                h = new Highlight
                {
                    buffer = new CommandBuffer { name = "BabyBlocks_RemotePropHighlight" },
                    outlineMat = GizmoRenderer.CreateOutlineMaterial(color),
                    color = color,
                };
                _highlights[uuid] = h;
            }

            h.target = target;
            if (h.color != color)
            {
                h.color = color;
                if (h.outlineMat != null) h.outlineMat.SetColor("_Color", color);
            }
        }

        public static void Remove(byte uuid)
        {
            if (_highlights.TryGetValue(uuid, out var h))
            {
                Detach(h);
                _highlights.Remove(uuid);
            }
        }

        public static void ClearAll()
        {
            foreach (var h in _highlights.Values)
                Detach(h);
            _highlights.Clear();
        }

        static void Detach(Highlight h)
        {
            if (h.attachedCam != null && h.buffer != null)
                h.attachedCam.RemoveCommandBuffer(CameraEvent.AfterEverything, h.buffer);
            h.attachedCam = null;
        }
    }
}
