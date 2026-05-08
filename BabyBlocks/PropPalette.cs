using UnityEngine;

namespace BabyBlocks
{
    static class PropPalette
    {
        const float Pad    = 8f;
        const float ItemH  = 60f;
        const float PanelW = 110f;
        const float ItemW  = PanelW - Pad * 2f; // 94

        // How many slots fit on screen, clamped to a reasonable range.
        static int VisibleSlots =>
            Mathf.Clamp(Mathf.FloorToInt((Screen.height - 100f) / (ItemH + Pad)), 4, 15);

        static int _scrollOffset  = 0;
        static int _draggingIndex = -1; // index into PropLibrary.FilteredProps

        public static bool     IsDragging   => _draggingIndex >= 0;
        public static PropInfo DraggingProp => IsDragging ? PropLibrary.FilteredProps[_draggingIndex] : null;
        public static void     CancelDrag() => _draggingIndex = -1;

        // Call from LevelEditor.Update so - = work even while RMB-orbiting is blocked.
        public static void HandleScrollInput()
        {
            if (!PropLibrary.IsInitialized) return;
            int total = PropLibrary.FilteredProps.Count;
            int page  = VisibleSlots;
            if (total > 0)
            {
                if (Input.GetKeyDown(KeyCode.Minus))
                    _scrollOffset = Mathf.Max(0, _scrollOffset - page);
                if (Input.GetKeyDown(KeyCode.Equals))
                    _scrollOffset = Mathf.Min(Mathf.Max(0, total - 1), _scrollOffset + page);
            }
        }

        public static void DrawGUI(Event e)
        {
            if (!PropLibrary.IsInitialized)
            {
                GUI.color = new Color(0f, 0f, 0f, 0.65f);
                GUI.Box(new Rect(10f, 10f, PanelW, 40f), "");
                GUI.color = Color.white;
                GUI.Label(new Rect(10f + Pad, 18f, ItemW, 20f), "Loading props…");
                return;
            }

            var props   = PropLibrary.FilteredProps;
            int total   = props.Count;
            int visible = VisibleSlots;

            _scrollOffset = Mathf.Clamp(_scrollOffset, 0, Mathf.Max(0, total - 1));

            float panelH    = Pad + visible * (ItemH + Pad) + 22f;
            var   panelRect = new Rect(10f, 10f, PanelW, panelH);

            GUI.color = new Color(0f, 0f, 0f, 0.65f);
            GUI.Box(panelRect, "");
            GUI.color = Color.white;

            for (int i = 0; i < visible; i++)
            {
                int   propIdx  = _scrollOffset + i;
                float y        = 10f + Pad + i * (ItemH + Pad);
                var   itemRect = new Rect(10f + Pad, y, ItemW, ItemH);

                if (propIdx < total)
                {
                    var  prop    = props[propIdx];
                    bool invalid = prop.isLoaded && !prop.HasMesh;
                    bool hovered = itemRect.Contains(e.mousePosition) && !IsDragging && !invalid;

                    if (invalid)
                        GUI.color = new Color(0.35f, 0.35f, 0.35f, 0.55f);
                    else
                        GUI.color = hovered ? new Color(1f, 1f, 1f, 0.95f) : new Color(0.6f, 0.6f, 0.6f, 0.85f);

                    GUI.Box(itemRect, invalid ? prop.displayName + "\n(no mesh)" : prop.displayName);

                    if (!invalid && e.type == EventType.MouseDown && e.button == 0 && itemRect.Contains(e.mousePosition))
                    {
                        _draggingIndex = propIdx;
                        e.Use();
                    }
                }
                else
                {
                    GUI.color = new Color(0.25f, 0.25f, 0.25f, 0.5f);
                    GUI.Box(itemRect, "");
                }
            }

            // Count label at bottom of panel.
            int displayStart = total > 0 ? _scrollOffset + 1 : 0;
            int displayEnd   = Mathf.Min(_scrollOffset + visible, total);
            GUI.color = new Color(0.75f, 0.75f, 0.75f, 1f);
            GUI.Label(
                new Rect(10f + Pad, 10f + Pad + visible * (ItemH + Pad) + 2f, ItemW, 18f),
                $"{displayStart}-{displayEnd} / {total}  (- =)");
            GUI.color = Color.white;

            // Ghost label following cursor while dragging.
            if (IsDragging)
            {
                GUI.color = new Color(1f, 1f, 0.4f, 0.80f);
                GUI.Box(
                    new Rect(e.mousePosition.x + 12f, e.mousePosition.y + 12f,
                             ItemW * 0.75f, ItemH * 0.75f),
                    DraggingProp.displayName);
                GUI.color = Color.white;
            }
        }
    }
}
