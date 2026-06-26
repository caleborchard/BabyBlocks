using System.Collections.Generic;
using UnityEngine;

namespace BabyBlocks.Networking
{
    // Per-peer selection highlights: the props other connected players currently have selected,
    // outlined in that player's suit colour. Rendered through GizmoRenderer's screen-space outline
    // system (the same shader-based outline as the local selection), so remote highlights match
    // the local look exactly — the SelectionMask/SelectionOutline shaders take the outline colour
    // from the mask RGB, so each peer's group just draws into the shared mask in its own colour.
    //
    // Only shown while the local player has the Baby Blocks editor open (FlyCamActive &&
    // CursorMode), mirroring RemoteFreecamManager's visibility rule.
    internal static class RemotePropHighlightManager
    {
        class Highlight
        {
            public readonly List<LevelEditorObject> targets = new();
            public Color color;
        }

        static readonly Dictionary<byte, Highlight> _highlights = new();

        public static void Update()
        {
            // Rebuild GizmoRenderer's remote-highlight list from scratch each frame so removed or
            // disconnected peers — and the editor-closed state — clear immediately.
            GizmoRenderer.ClearRemoteHighlights();
            if (_highlights.Count == 0) return;

            bool editorActive = FlyCamController.FlyCamActive && FlyCamController.CursorMode;

            List<byte> stale = null;
            foreach (var kv in _highlights)
            {
                var h = kv.Value;
                h.targets.RemoveAll(t => t == null);
                if (h.targets.Count == 0)
                {
                    (stale ??= new List<byte>()).Add(kv.Key);
                    continue;
                }
                if (editorActive)
                    GizmoRenderer.AddRemoteHighlight(h.color, h.targets);
            }

            if (stale != null)
                foreach (var uuid in stale) _highlights.Remove(uuid);
        }

        public static void SetHighlights(byte uuid, List<LevelEditorObject> targets, Color color)
        {
            if (targets == null || targets.Count == 0) { Remove(uuid); return; }
            if (!_highlights.TryGetValue(uuid, out var h))
            {
                h = new Highlight();
                _highlights[uuid] = h;
            }
            h.targets.Clear();
            h.targets.AddRange(targets);
            h.color = color;
        }

        public static void Remove(byte uuid) => _highlights.Remove(uuid);

        public static void ClearAll()
        {
            _highlights.Clear();
            GizmoRenderer.ClearRemoteHighlights();
        }
    }
}
