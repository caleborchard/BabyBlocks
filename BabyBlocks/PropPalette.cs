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
        static GUIStyle _excludedXStyle;

        // How many slots fit on screen, clamped to a reasonable range.
        static int VisibleSlots =>
            Mathf.Clamp(Mathf.FloorToInt((Screen.height - 100f) / (ItemH + Pad)), 4, 15);

        static int   _scrollOffset    = 0;
        static int   _draggingIndex   = -1; // index into PropLibrary.FilteredProps
        static int   _lastFilterCount = -1;
        static float _scrollTimer;
        static bool  _scrollActive;
        static bool  _scrollInDelay;

        const float ScrollInitialDelay  = 0.35f;
        const float ScrollRepeatInterval = 0.08f;

        public static Rect PanelRect { get; private set; }

        public static bool     IsDragging   => _draggingIndex >= 0;
        public static PropInfo DraggingProp => IsDragging ? PropLibrary.FilteredProps[_draggingIndex] : null;
        public static void     CancelDrag() => _draggingIndex = -1;

        // Call from LevelEditor.Update so - = work even while RMB-orbiting is blocked.
        public static void HandleScrollInput()
        {
            if (!PropLibrary.IsInitialized) return;
            int total = PropLibrary.FilteredProps.Count;
            if (total == 0) return;

            bool minusHeld  = Input.GetKey(KeyCode.Minus);
            bool equalsHeld = Input.GetKey(KeyCode.Equals);

            if (!minusHeld && !equalsHeld)
            {
                _scrollActive = false;
                _scrollTimer  = 0f;
                return;
            }

            int dir = minusHeld ? -1 : 1;

            int page = VisibleSlots;

            if (!_scrollActive)
            {
                _scrollActive  = true;
                _scrollInDelay = true;
                _scrollTimer   = 0f;
                _scrollOffset  = Mathf.Clamp(_scrollOffset + dir * page, 0, total - 1);
                return;
            }

            _scrollTimer += Time.unscaledDeltaTime;
            float threshold = _scrollInDelay ? ScrollInitialDelay : ScrollRepeatInterval;
            if (_scrollTimer >= threshold)
            {
                _scrollTimer  -= threshold;
                _scrollInDelay = false;
                _scrollOffset  = Mathf.Clamp(_scrollOffset + dir * page, 0, total - 1);
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

            if (total != _lastFilterCount)
            {
                _scrollOffset    = 0;
                _lastFilterCount = total;
            }

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
                    var  prop       = props[propIdx];
                    bool invalid    = prop.isLoaded && !prop.HasMesh;
                    bool isExcluded = PropMetadataPanel.IsExcluded(prop.id);
                    bool hovered    = itemRect.Contains(e.mousePosition) && !IsDragging && !invalid && !isExcluded;

                    bool hasMetadata = PropMetadataPanel.HasMetadata(prop.id);
                    if (invalid)
                    {
                        GUI.color = new Color(0.45f, 0.45f, 0.45f, 0.7f);
                        if (GUI.Button(itemRect, prop.displayName + "\n(no mesh)", _itemStyle))
                            PropMetadataPanel.SetPaletteSelection(prop.id);
                    }
                    else
                    {
                        GUI.color = isExcluded
                            ? new Color(0.35f, 0.35f, 0.35f, 0.7f)
                            : hovered ? new Color(1f, 1f, 1f, 0.95f) : new Color(0.6f, 0.6f, 0.6f, 0.85f);
                        GUI.Box(itemRect, prop.displayName, _itemStyle);
                        if (e.type == EventType.MouseDown && e.button == 0 && itemRect.Contains(e.mousePosition))
                        {
                            if (isExcluded)
                                PropMetadataPanel.SetPaletteSelection(prop.id);
                            else
                                _draggingIndex = propIdx;
                            e.Use();
                        }
                    }

                    // Red X overlay for excluded items — drawn after the box so it sits on top.
                    if (isExcluded)
                    {
                        GUI.color = new Color(1f, 0.15f, 0.15f, 0.85f);
                        GUI.Label(itemRect, "✕", _excludedXStyle);
                    }

                    if (hasMetadata)
                    {
                        GUI.color = new Color(0.4f, 1f, 0.4f, 0.95f);
                        GUI.Label(new Rect(itemRect.xMax - 16f, itemRect.y + 3f, 16f, 16f), "✓");
                    }

                    GUI.color = Color.white;
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

            if (_excludedXStyle == null)
            {
                _excludedXStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize  = 38,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                };
                _excludedXStyle.normal.textColor = new Color(1f, 0.15f, 0.15f, 0.9f);
            }
        }
    }
}
