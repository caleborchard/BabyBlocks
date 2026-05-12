using UnityEngine;

namespace BabyBlocks
{
    static class PropPalette
    {
        const float Pad    = 8f;
        const float ItemH  = 60f;
        const float PanelW = 110f;
        const float ItemW  = PanelW - Pad * 2f; // 94

        static GUIStyle _itemStyle;
        static GUIStyle _ghostStyle;

        // How many slots fit on screen, clamped to a reasonable range.
        static int VisibleSlots =>
            Mathf.Clamp(Mathf.FloorToInt((Screen.height - 100f) / (ItemH + Pad)), 4, 15);

        static int _scrollOffset  = 0;
        static int _draggingIndex = -1; // index into PropLibrary.FilteredProps

        public static Rect PanelRect { get; private set; }

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
            EnsureStyles();
            if (!PropLibrary.IsInitialized)
            {
                PanelRect = new Rect(10f, 10f, PanelW, 40f);
                GUI.color = new Color(0f, 0f, 0f, 0.65f);
                GUI.Box(PanelRect, "");
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
            PanelRect = panelRect;

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

                    GUI.Box(itemRect, invalid ? prop.displayName + "\n(no mesh)" : prop.displayName, _itemStyle);


                    if (!invalid && e.type == EventType.MouseDown && e.button == 0 && itemRect.Contains(e.mousePosition))
                    {
                        _draggingIndex = propIdx;
                        e.Use();
                    }
                }
                else
                {
                    GUI.color = new Color(0.25f, 0.25f, 0.25f, 0.5f);
                    GUI.Box(itemRect, "", _itemStyle);
                }
            }

            // Hover tooltip showing full asset path.
            if (!IsDragging)
            {
                string hoverPath = null;
                for (int i = 0; i < visible && hoverPath == null; i++)
                {
                    int propIdx = _scrollOffset + i;
                    if (propIdx >= total) break;

                    float y = 10f + Pad + i * (ItemH + Pad);
                    var itemRect = new Rect(10f + Pad, y, ItemW, ItemH);
                    var prop = props[propIdx];
                    if (itemRect.Contains(e.mousePosition))
                        hoverPath = prop.id;
                }

                if (!string.IsNullOrEmpty(hoverPath))
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.9f);
                    var pos = e.mousePosition;
                    var size = GUI.skin.box.CalcSize(new GUIContent(hoverPath));
                    var rect = new Rect(pos.x + 12f, pos.y + 12f, size.x + 8f, size.y + 6f);
                    GUI.Box(rect, hoverPath);
                    GUI.color = Color.white;
                }
            }

            // Page label at bottom of panel.
            int pageCount   = total > 0 ? Mathf.CeilToInt(total / (float)visible) : 0;
            int currentPage = total > 0 ? (_scrollOffset / visible) + 1 : 0;
            GUI.color = new Color(0.75f, 0.75f, 0.75f, 1f);
            GUI.Label(
                new Rect(10f + Pad, 10f + Pad + visible * (ItemH + Pad) + 2f, ItemW, 18f),
                $"{currentPage}/{pageCount}");
            GUI.color = Color.white;

            // Ghost label following cursor while dragging.
            if (IsDragging)
            {
                GUI.color = new Color(1f, 1f, 0.4f, 0.80f);
                GUI.Box(
                    new Rect(e.mousePosition.x + 12f, e.mousePosition.y + 12f,
                             ItemW * 0.75f, ItemH * 0.75f),
                    DraggingProp.displayName, _ghostStyle);
                GUI.color = Color.white;
            }
        }

        static void EnsureStyles()
        {
            if (_itemStyle == null)
            {
                var padding = new RectOffset();
                padding.left = 4;
                padding.right = 4;
                padding.top = 4;
                padding.bottom = 4;
                _itemStyle = new GUIStyle(GUI.skin.box)
                {
                    wordWrap = true,
                    alignment = TextAnchor.MiddleCenter,
                    clipping = TextClipping.Clip,
                    padding = padding
                };
            }

            if (_ghostStyle == null)
            {
                _ghostStyle = new GUIStyle(_itemStyle);
            }
        }
    }
}
