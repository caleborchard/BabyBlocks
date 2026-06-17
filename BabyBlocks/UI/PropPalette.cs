using System.Collections.Generic;
using UnityEngine;

namespace BabyBlocks
{
    static class PropPalette
    {
        const float Pad    = 8f;
        const float ItemH  = 60f;
        const float PanelW = 110f;
        const float ItemW  = PanelW - Pad * 2f; // 94

        // Category panel (non-debug mode only)
        const float CatPad   = 6f;
        const float CatItemH = 26f;

        static GUIStyle _itemStyle;
        static GUIStyle _ghostStyle;
        static GUIStyle _excludedXStyle;
        static GUIStyle _warningStyle;
        static GUIStyle _catStyle;

        // How many slots fit on screen, clamped to a reasonable range.
        static int VisibleSlots =>
            Mathf.Clamp(Mathf.FloorToInt((Screen.height - 50f) / (ItemH + Pad)), 4, 15);

        static int   _scrollOffset    = 0;
        static int   _draggingIndex   = -1; // index into PropLibrary.FilteredProps
        static int   _dragStartFrame  = -1;
        static int   _lastFilterCount = -1;
        static float _scrollTimer;
        static bool  _scrollActive;
        static bool  _scrollInDelay;

        const float ScrollInitialDelay  = 0.35f;
        const float ScrollRepeatInterval = 0.08f;

        // Advances a paged scroll offset by one page in `dir`, wrapping around to the
        // start/end so paging never gets stuck showing a lone leftover item on its
        // own trailing "page". Shared with MaterialConstructionPanel so every list
        // pages and loops the same way.
        public static int StepPageOffset(int current, int dir, int page, int total)
        {
            if (total <= 0) return 0;
            int maxOffset = ((total - 1) / page) * page;
            int next = current + dir * page;
            if (next > maxOffset) return 0;
            if (next < 0) return maxOffset;
            return next;
        }

        // Exposed so PropLibrary.BuildFiltered can read the selected category.
        public static string SelectedCategory = null;

        // Non-debug mode: when true, the main panel shows the Material Constructions
        // palette (read-only, no "+ New") instead of the prop list.
        public static bool ShowingMaterials = false;

        // Category list cached in non-debug mode; null when stale.
        static List<string> _cachedCategories;

        public static void InvalidateCategories() => _cachedCategories = null;

        public static Rect PanelRect { get; internal set; }

        static PropInfo _overrideDragInfo;

        public static bool     IsDragging     => _draggingIndex >= 0 || _overrideDragInfo != null;
        public static int      DragPropIndex  => _draggingIndex;
        public static PropInfo DraggingProp   => _overrideDragInfo != null ? _overrideDragInfo
                                               : _draggingIndex >= 0 ? PropLibrary.FilteredProps[_draggingIndex] : null;
        public static void     CancelDrag()   { _draggingIndex = -1; _overrideDragInfo = null; }

        // Start a prop drag from an external palette (e.g. the UniverseLib browser).
        // filteredIndex must be a valid index into PropLibrary.FilteredProps.
        public static void BeginDrag(int filteredIndex, PropInfo info)
        {
            if (!info.isLoaded) PropLibrary.LoadPropData(info);
            _draggingIndex  = filteredIndex;
            _overrideDragInfo = null;
            _dragStartFrame = Time.frameCount;
        }

        // Drag a prop that may not be in FilteredProps (e.g. from the History category).
        public static void BeginDragDirect(PropInfo info)
        {
            if (!info.isLoaded) PropLibrary.LoadPropData(info);
            _draggingIndex  = -1;
            _overrideDragInfo = info;
            _dragStartFrame = Time.frameCount;
        }

        // The OnGUI MouseDown that starts a drag and the Update() that checks for
        // mouse-release can land on the same frame, which would otherwise read the
        // button as already-up and drop the prop immediately under the palette.
        // Skip the release check for the frame the drag started on.
        public static bool JustStartedDrag => IsDragging && Time.frameCount == _dragStartFrame;

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
                _scrollOffset  = StepPageOffset(_scrollOffset, dir, page, total);
                return;
            }

            _scrollTimer += Time.unscaledDeltaTime;
            float threshold = _scrollInDelay ? ScrollInitialDelay : ScrollRepeatInterval;
            if (_scrollTimer >= threshold)
            {
                _scrollTimer  -= threshold;
                _scrollInDelay = false;
                _scrollOffset  = StepPageOffset(_scrollOffset, dir, page, total);
            }
        }

        public static void DrawGUI(Event e)
        {
            EnsureStyles();

            if (Core.DebugMode && MaterialConstructionPanel.Active)
            {
                MaterialConstructionPanel.DrawPalette(e);
                return;
            }

            if (!Core.DebugMode && ShowingMaterials)
            {
                MaterialConstructionPanel.DrawPalette(e, showAddButton: false);
                DrawCategoryPanel(e, PanelRect);
                return;
            }

            if (!PropLibrary.IsInitialized)
            {
                PanelRect = new Rect(10f, 10f, PanelW, 40f);
                GUI.color = new Color(0f, 0f, 0f, 0.65f);
                GUI.Box(PanelRect, "");
                GUI.color = Color.white;
                GUI.Label(new Rect(10f + Pad, 18f, ItemW, 20f), "Loading props…");
                return;
            }

            // Block dragging while the material-source scan is spreading its loads across frames —
            // placing a prop before its override material is in memory would apply the override
            // once (at placement time) and never retry, leaving it permanently wrong.
            if (MaterialCatalog.IsLoadingMaterialSources)
            {
                PanelRect = new Rect(10f, 10f, PanelW, 40f);
                GUI.color = new Color(0f, 0f, 0f, 0.65f);
                GUI.Box(PanelRect, "");
                GUI.color = Color.white;
                GUI.Label(new Rect(10f + Pad, 18f, ItemW, 20f), "Loading materials…");
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
                    bool isExcluded = PropMetadataStore.IsExcluded(prop.id);
                    bool hovered    = itemRect.Contains(e.mousePosition) && !IsDragging && !invalid && !isExcluded;

                    // In non-debug mode use the metadata display name; debug mode uses raw name.
                    string label = Core.DebugMode
                        ? prop.displayName
                        : (PropMetadataStore.GetDisplayName(prop.id) ?? prop.displayName);

                    if (invalid)
                    {
                        GUI.color = new Color(0.45f, 0.45f, 0.45f, 0.7f);
                        if (GUI.Button(itemRect, label + "\n(no mesh)", _itemStyle))
                            PropMetadataEditor.SetPaletteSelection(prop.id);
                    }
                    else
                    {
                        GUI.color = isExcluded
                            ? new Color(0.35f, 0.35f, 0.35f, 0.7f)
                            : hovered ? new Color(1f, 1f, 1f, 0.95f) : new Color(0.6f, 0.6f, 0.6f, 0.85f);
                        GUI.Box(itemRect, label, _itemStyle);
                        if (e.type == EventType.MouseDown && e.button == 0 && itemRect.Contains(e.mousePosition))
                        {
                            if (isExcluded)
                                PropMetadataEditor.SetPaletteSelection(prop.id);
                            else
                            {
                                _draggingIndex  = propIdx;
                                _dragStartFrame = Time.frameCount;
                                // Dragging straight out of a focused search field (without
                                // clicking elsewhere first to defocus it) can leave Unity's
                                // input state out of sync for the first frames of the drag,
                                // causing the dragged prop to drop immediately. Clear focus.
                                GUI.FocusControl(null);
                                // Load mesh data now, on click, rather than on the first
                                // ghost-update frame. Loading an unloaded prop can take long
                                // enough that the user's drag-and-release finishes during the
                                // hitch, making the prop appear to drop instantly at the click
                                // point instead of following the cursor.
                                if (!prop.isLoaded) PropLibrary.LoadPropData(prop);
                            }
                            e.Use();
                        }
                    }

                    if (Core.DebugMode)
                    {
                        // Red X overlay for excluded items — drawn after the box so it sits on top.
                        if (isExcluded)
                        {
                            GUI.color = new Color(1f, 0.15f, 0.15f, 0.85f);
                            GUI.Label(itemRect, "✕", _excludedXStyle);
                        }

                        if (PropMetadataStore.IsPartiallyFilled(prop.id))
                        {
                            GUI.color = new Color(1f, 0.85f, 0f, 0.85f);
                            GUI.Label(itemRect, "!", _warningStyle);
                        }

                        if (PropMetadataStore.HasMetadata(prop.id))
                        {
                            GUI.color = new Color(0.4f, 1f, 0.4f, 0.95f);
                            GUI.Label(new Rect(itemRect.xMax - 16f, itemRect.y + 3f, 16f, 16f), "✓");
                        }
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
            {
                int pageCount   = total > 0 ? Mathf.CeilToInt(total / (float)visible) : 0;
                int currentPage = total > 0 ? (_scrollOffset / visible) + 1 : 0;
                GUI.color = new Color(0.75f, 0.75f, 0.75f, 1f);
                var pageLabelRect = new Rect(10f + Pad, 10f + Pad + visible * (ItemH + Pad) + 2f, ItemW, 18f);
                GUI.Label(pageLabelRect, $"{currentPage}/{pageCount}");

                if (Core.DebugMode && total > 0)
                {
                    int pageStart = _scrollOffset;
                    int pageEnd   = Mathf.Min(_scrollOffset + visible, total);
                    bool allChecked = true;
                    for (int i = pageStart; i < pageEnd; i++)
                    {
                        if (!PropMetadataStore.HasMetadata(props[i].id)) { allChecked = false; break; }
                    }
                    if (allChecked)
                    {
                        GUI.color = new Color(0.4f, 1f, 0.4f, 0.95f);
                        GUI.Label(new Rect(pageLabelRect.x + 38f, pageLabelRect.y, 16f, 18f), "✓");
                    }
                }

                GUI.color = Color.white;
            }

            // Ghost label following cursor while dragging — only while still inside the panel.
            // Once outside, the 3D ghost preview in the scene takes over.
            if (IsDragging && panelRect.Contains(e.mousePosition))
            {
                GUI.color = new Color(1f, 1f, 0.4f, 0.80f);
                GUI.Box(
                    new Rect(e.mousePosition.x + 12f, e.mousePosition.y + 12f,
                             ItemW * 0.75f, ItemH * 0.75f),
                    DraggingProp.displayName, _ghostStyle);
                GUI.color = Color.white;
            }

            // Category panel — only in non-debug mode.
            if (!Core.DebugMode)
                DrawCategoryPanel(e, panelRect);
        }

        static void DrawCategoryPanel(Event e, Rect mainPanelRect)
        {
            if (_cachedCategories == null)
                _cachedCategories = PropMetadataStore.GetAllCategories();

            var cats = _cachedCategories;
            // +1 for the "(All)" entry at the top, +1 for "Materials" at the bottom.
            int count = cats.Count + 2;

            float catPanelX = mainPanelRect.xMax + 10f;
            float catItemW  = PanelW - CatPad * 2f;
            float catPanelH = CatPad + count * (CatItemH + CatPad);
            var   catPanel  = new Rect(catPanelX, 10f, PanelW, catPanelH);

            GUI.color = new Color(0f, 0f, 0f, 0.65f);
            GUI.Box(catPanel, "");
            GUI.color = Color.white;

            for (int i = 0; i < count; i++)
            {
                bool   isMaterials = i == count - 1;
                string cat         = (i == 0 || isMaterials) ? null : cats[i - 1];
                string label       = isMaterials ? "Materials" : (cat ?? "(All)");
                bool   sel         = isMaterials
                    ? ShowingMaterials
                    : !ShowingMaterials && ((cat == null && SelectedCategory == null)
                          || (cat != null && string.Equals(cat, SelectedCategory, StringComparison.OrdinalIgnoreCase)));
                float  iy      = 10f + CatPad + i * (CatItemH + CatPad);
                var    itemR   = new Rect(catPanelX + CatPad, iy, catItemW, CatItemH);

                GUI.color = sel
                    ? new Color(1f, 0.85f, 0.3f, 0.95f)
                    : new Color(0.6f, 0.6f, 0.6f, 0.85f);

                if (GUI.Button(itemR, label, _catStyle) && !sel)
                {
                    if (isMaterials)
                    {
                        ShowingMaterials = true;
                    }
                    else
                    {
                        ShowingMaterials = false;
                        SelectedCategory = cat;
                        PropLibrary.RebuildFiltered();
                    }
                    e.Use();
                }

                GUI.color = Color.white;
            }
        }

        static void EnsureStyles()
        {
            GuiStyleHelpers.EnsureItemAndGhostStyles(ref _itemStyle, ref _ghostStyle);

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

            if (_warningStyle == null)
            {
                _warningStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize  = 38,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                };
                _warningStyle.normal.textColor = new Color(1f, 0.85f, 0f, 0.9f);
            }

            if (_catStyle == null)
            {
                var padding = new RectOffset { left = 4, right = 4, top = 2, bottom = 2 };
                _catStyle = new GUIStyle(GUI.skin.button)
                {
                    wordWrap  = false,
                    alignment = TextAnchor.MiddleCenter,
                    clipping  = TextClipping.Clip,
                    padding   = padding,
                    fontSize  = 11,
                };
            }
        }
    }
}
