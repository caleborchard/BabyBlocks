using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib;
using UniverseLib.Config;
using UniverseLib.UI;
using UniverseLib.UI.Models;
using UniverseLib.UI.Panels;

namespace BabyBlocks.UI
{
    static class PropBrowserUI
    {
        public static bool Ready { get; private set; }

        static UIBase           _uiBase;
        static TopBarPanel      _topBar;
        static PropLibraryPanel _panel;

        public static void Init()
        {
            Universe.Init(1f, OnReady, null, new UniverseLibConfig
            {
                Disable_EventSystem_Override = false,
                Force_Unlock_Mouse           = true,
            });
        }

        static void OnReady()
        {
            _uiBase = UniversalUI.RegisterUI<PropBrowserUIBase>("BabyBlocks.PropBrowser", OnUpdate);
            try
            {
                _topBar = new TopBarPanel(_uiBase);
                _panel  = new PropLibraryPanel(_uiBase);
                Ready = true;
                MelonLogger.Msg("[BabyBlocks] Prop browser UI ready.");
            }
            catch (System.Exception e)
            {
                MelonLogger.Error($"[BabyBlocks] Prop browser UI init failed: {e}");
            }
            finally
            {
                // Always hide on startup — UpdateVisibility shows it when entering the editor.
                _uiBase.Enabled = false;
            }
        }

        public static void UpdateVisibility()
        {
            if (!Ready) return;
            bool inEditor = FlyCamController.FlyCamActive && FlyCamController.CursorMode;
            if (_uiBase.Enabled != inEditor)
                _uiBase.Enabled = inEditor;
        }

        static void OnUpdate()
        {
            // Prevent Space/Enter from re-firing the last clicked button.
            UniversalUI.EventSys?.SetSelectedGameObject(null);
            _topBar?.Tick();
            _panel?.Tick();
        }

        internal static void ApplyButtonColors(ButtonRef btn)
        {
            var c = btn.Component.colors;
            c.normalColor      = new Color(0.22f, 0.22f, 0.26f, 1f);
            c.highlightedColor = new Color(0.32f, 0.32f, 0.38f, 1f);
            c.pressedColor     = new Color(0.18f, 0.45f, 0.75f, 1f);
            c.selectedColor    = new Color(0.22f, 0.22f, 0.26f, 1f);
            c.colorMultiplier  = 1f;
            btn.Component.colors = c;

            // Disable keyboard navigation so Space/Enter can never re-fire this button.
            var nav = btn.Component.navigation;
            nav.mode = Navigation.Mode.None;
            btn.Component.navigation = nav;
        }

        internal static void Deselect() => UniversalUI.EventSys?.SetSelectedGameObject(null);

        public static void RestoreCursor()
        {
            if (!Ready) return;
            bool inEditorCursor = FlyCamController.FlyCamActive && FlyCamController.CursorMode;
            if (inEditorCursor) return;
            bool menuOpen = Menu.me != null && Menu.me.paused;
            if (menuOpen) return;
            Cursor.visible   = false;
            Cursor.lockState = CursorLockMode.Locked;
        }
    }

    class PropBrowserUIBase : UIBase
    {
        public PropBrowserUIBase(string id, Action update) : base(id, update) { }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Top bar — full-width menu bar
    // ─────────────────────────────────────────────────────────────────────────
    class TopBarPanel : PanelBase
    {
        internal const int BarHeight = 28;

        public override string  Name             => "TopBar";
        public override int     MinWidth         => 400;
        public override int     MinHeight        => BarHeight;
        public override Vector2 DefaultAnchorMin => new(0f, 1f);
        public override Vector2 DefaultAnchorMax => new(1f, 1f);
        public override bool    CanDragAndResize => false;

        public override void SetDefaultSizeAndPosition()
        {
            Rect.pivot            = new Vector2(0.5f, 1f);
            Rect.anchorMin        = new Vector2(0f, 1f);
            Rect.anchorMax        = new Vector2(1f, 1f);
            Rect.anchoredPosition = Vector2.zero;
            Rect.sizeDelta        = new Vector2(0f, BarHeight);
        }

        GameObject _fileDropdown;
        GameObject _fileItemsContainer;
        bool       _fileOpen;
        bool       _pendingClear;
        float      _pendingClearTime;
        const float ClearConfirmTimeout = 5f;
        ButtonRef  _clearBtn;

        ButtonRef  _catBtn;
        GameObject _catDropdown;
        GameObject _catItemsContainer;
        bool       _catOpen;

        const float CatDropdownOffsetX = 4f;

        public TopBarPanel(UIBase owner) : base(owner) { }

        protected override void ConstructPanelContent()
        {
            TitleBar?.SetActive(false);

            var barRow = UIFactory.CreateHorizontalGroup(ContentRoot, "BarRow",
                false, false, true, true, spacing: 0, padding: new Vector4(0, 2, 4, 2));
            UIFactory.SetLayoutElement(barRow, flexibleWidth: 9999, minHeight: BarHeight - 4);

            // Category / mode selector — fixed width matching the prop list panel
            _catBtn = UIFactory.CreateButton(barRow, "CatBtn", " All ▼ ");
            UIFactory.SetLayoutElement(_catBtn.Component.gameObject,
                minWidth: PropLibraryPanel.PanelWidth, flexibleWidth: 0, minHeight: 22);
            PropBrowserUI.ApplyButtonColors(_catBtn);
            _catBtn.OnClick += ToggleCat;

            // Gap between cat button and file menu
            var gap = UIFactory.CreateUIObject("Gap", barRow);
            UIFactory.SetLayoutElement(gap, minWidth: 6, flexibleWidth: 0);

            // File
            var fileBtn = UIFactory.CreateButton(barRow, "FileBtn", " File ");
            UIFactory.SetLayoutElement(fileBtn.Component.gameObject, minWidth: 54, minHeight: 22);
            PropBrowserUI.ApplyButtonColors(fileBtn);
            fileBtn.OnClick += ToggleFile;

            _fileDropdown = BuildFileDropdown();
            _fileDropdown.SetActive(false);

            _catDropdown = BuildCatDropdown();
            _catDropdown.SetActive(false);
        }

        // ---- File dropdown ----

        GameObject BuildFileDropdown()
        {
            var go = UIFactory.CreateUIObject("FileDropdown", Owner.RootObject);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin        = new Vector2(0f, 1f);
            rt.anchorMax        = new Vector2(0f, 1f);
            rt.pivot            = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(4f, -(BarHeight + 1f));
            rt.sizeDelta        = new Vector2(150f, 10f); // height set by layout

            go.AddComponent<Image>().color = new Color(0.13f, 0.13f, 0.16f, 0.97f);

            _fileItemsContainer = UIFactory.CreateUIObject("Items", go);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(_fileItemsContainer,
                forceWidth: true, forceHeight: false,
                childControlWidth: true, childControlHeight: true,
                spacing: 1, padTop: 3, padBottom: 3, padLeft: 3, padRight: 3);
            var itemsRT = _fileItemsContainer.GetComponent<RectTransform>();
            itemsRT.anchorMin = Vector2.zero;
            itemsRT.anchorMax = Vector2.one;
            itemsRT.sizeDelta = Vector2.zero;

            return go;
        }

        void RebuildFileItems()
        {
            for (int i = _fileItemsContainer.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_fileItemsContainer.transform.GetChild(i).gameObject);

            const int itemH = 26;
            // Save, Load, Clear = 3 items
            float totalH = 3 + 3 * itemH + 2 * 1 + 3; // pad + items + spacing + pad
            var rt = _fileDropdown.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(rt.sizeDelta.x, totalH);

            var saveBtn = UIFactory.CreateButton(_fileItemsContainer, "SaveBtn", "Save");
            UIFactory.SetLayoutElement(saveBtn.Component.gameObject, minHeight: itemH, flexibleWidth: 9999);
            PropBrowserUI.ApplyButtonColors(saveBtn);
            saveBtn.OnClick += () => { PropBrowserUI.Deselect(); CloseFile(); SaveLoadWindow.TriggerSaveDialog(); };

            var loadBtn = UIFactory.CreateButton(_fileItemsContainer, "LoadBtn", "Load");
            UIFactory.SetLayoutElement(loadBtn.Component.gameObject, minHeight: itemH, flexibleWidth: 9999);
            PropBrowserUI.ApplyButtonColors(loadBtn);
            loadBtn.OnClick += () => { PropBrowserUI.Deselect(); CloseFile(); SaveLoadWindow.TriggerLoadDialog(); };

            string clearLabel = _pendingClear ? "Confirm clear?" : "Clear";
            _clearBtn = UIFactory.CreateButton(_fileItemsContainer, "ClearBtn", clearLabel);
            UIFactory.SetLayoutElement(_clearBtn.Component.gameObject, minHeight: itemH, flexibleWidth: 9999);
            PropBrowserUI.ApplyButtonColors(_clearBtn);
            if (_pendingClear)
            {
                var img = _clearBtn.Component.GetComponent<Image>();
                if (img != null) img.color = new Color(0.7f, 0.2f, 0.2f, 1f);
            }
            _clearBtn.OnClick += () => { PropBrowserUI.Deselect(); OnClearClicked(); };
        }

        void OnClearClicked()
        {
            if (_pendingClear)
            {
                _pendingClear = false;
                CloseFile();
                SaveLoadWindow.TriggerClear();
            }
            else if (!SaveLoadWindow.HasObjects)
            {
                CloseFile(); // nothing to clear
            }
            else
            {
                _pendingClear     = true;
                _pendingClearTime = Time.realtimeSinceStartup;
                RebuildFileItems(); // show "Confirm clear?" with red tint
            }
        }

        void ToggleFile()
        {
            PropBrowserUI.Deselect();
            _fileOpen = !_fileOpen;
            if (_fileOpen)
            {
                CloseCat();
                _pendingClear = false;
                RebuildFileItems();
                _fileDropdown.transform.SetAsLastSibling();
            }
            _fileDropdown.SetActive(_fileOpen);
        }

        void CloseFile()
        {
            _fileOpen     = false;
            _pendingClear = false;
            _fileDropdown.SetActive(false);
        }

        // ---- Category / mode dropdown ----

        GameObject BuildCatDropdown()
        {
            var go = UIFactory.CreateUIObject("CatDropdown", Owner.RootObject);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin        = new Vector2(0f, 1f);
            rt.anchorMax        = new Vector2(0f, 1f);
            rt.pivot            = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(CatDropdownOffsetX, -(BarHeight + 1f));
            rt.sizeDelta        = new Vector2(PropLibraryPanel.PanelWidth, 10f);

            var bg = go.AddComponent<Image>();
            bg.color = new Color(0.13f, 0.13f, 0.16f, 0.97f);

            _catItemsContainer = UIFactory.CreateUIObject("Items", go);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(_catItemsContainer,
                forceWidth: true, forceHeight: false,
                childControlWidth: true, childControlHeight: true,
                spacing: 1, padTop: 3, padBottom: 3, padLeft: 3, padRight: 3);
            var itemsRT = _catItemsContainer.GetComponent<RectTransform>();
            itemsRT.anchorMin = Vector2.zero;
            itemsRT.anchorMax = Vector2.one;
            itemsRT.sizeDelta = Vector2.zero;

            return go;
        }

        void RebuildCatItems()
        {
            for (int i = _catItemsContainer.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_catItemsContainer.transform.GetChild(i).gameObject);

            var cats = PropMetadataStore.GetAllCategories();
            // All + each category + "Materials" at bottom = cats.Count + 2
            int itemCount = cats.Count + 2;
            const int itemH = 26;
            const int pad   = 3;
            float totalH = pad + itemCount * itemH + (itemCount - 1) * 1 + pad;

            var rt = _catDropdown.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(rt.sizeDelta.x, totalH);

            AddFilterItem("All", null, showingMats: false);
            foreach (var cat in cats)
                AddFilterItem(cat, cat, showingMats: false);
            AddFilterItem("Materials", null, showingMats: true);
        }

        void AddFilterItem(string label, string category, bool showingMats)
        {
            var btn = UIFactory.CreateButton(_catItemsContainer, "Item_" + label, label);
            UIFactory.SetLayoutElement(btn.Component.gameObject, minHeight: 26, flexibleWidth: 9999);
            PropBrowserUI.ApplyButtonColors(btn);

            // Highlight current selection
            bool isCurrent = showingMats
                ? PropPalette.ShowingMaterials
                : !PropPalette.ShowingMaterials && string.Equals(category, PropPalette.SelectedCategory, StringComparison.OrdinalIgnoreCase);
            if (isCurrent)
            {
                var img = btn.Component.GetComponent<Image>();
                if (img != null) img.color = new Color(0.3f, 0.5f, 0.8f, 1f);
            }

            string capturedCat = category;
            bool capturedMats  = showingMats;
            btn.OnClick += () =>
            {
                PropBrowserUI.Deselect();
                CloseCat();
                PropPalette.ShowingMaterials = capturedMats;
                if (!capturedMats)
                {
                    PropPalette.SelectedCategory = capturedCat;
                    PropLibrary.RebuildFiltered();
                }
            };
        }

        void ToggleCat()
        {
            PropBrowserUI.Deselect();
            _catOpen = !_catOpen;
            if (_catOpen)
            {
                CloseFile();
                RebuildCatItems();
                _catDropdown.transform.SetAsLastSibling();
            }
            _catDropdown.SetActive(_catOpen);
        }

        void CloseCat()
        {
            _catOpen = false;
            _catDropdown.SetActive(false);
        }

        // ---- Tick ----

        public void Tick()
        {
            if (_catBtn != null)
            {
                var txt = _catBtn.Component.GetComponentInChildren<Text>();
                if (txt != null)
                {
                    string label = PropPalette.ShowingMaterials
                        ? "Materials"
                        : (PropPalette.SelectedCategory ?? "All");
                    txt.text = $" {label} ▼ ";
                }
            }

            // Expire the "Confirm clear?" state if the user doesn't act in time.
            if (_pendingClear && Time.realtimeSinceStartup - _pendingClearTime > ClearConfirmTimeout)
            {
                _pendingClear = false;
                if (_fileOpen) RebuildFileItems(); // revert button to "Clear"
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Prop Library Panel — full-height left sidebar, paged prop/material cards
    // ─────────────────────────────────────────────────────────────────────────
    class PropLibraryPanel : PanelBase
    {
        const int   SlotCount        = 8;
        internal const int PanelWidth = 290;
        const int   CardHeight  = 120;
        const int   PreviewSize = CardHeight;
        const int   LabelFontSz = 18;
        const float ScrollPx   = 36f;
        const float DragThreshold = 8f;

        static readonly Color CardNormalColor = new Color(0.13f, 0.13f, 0.16f);
        static readonly Color CardHoverColor  = new Color(0.24f, 0.24f, 0.29f, 1f);

        public override string  Name             => "Prop Library";
        public override int     MinWidth         => PanelWidth;
        public override int     MinHeight        => 200;
        public override Vector2 DefaultAnchorMin => new(0f, 1f);
        public override Vector2 DefaultAnchorMax => new(0f, 1f);
        public override bool    CanDragAndResize => false;

        public override void SetDefaultSizeAndPosition()
        {
            int h = TopBarPanel.BarHeight;
            Rect.pivot            = new Vector2(0f, 1f);
            Rect.anchorMin        = new Vector2(0f, 0f);
            Rect.anchorMax        = new Vector2(0f, 1f);
            Rect.anchoredPosition = new Vector2(0f, -h);
            Rect.sizeDelta        = new Vector2(PanelWidth, -h);
        }

        struct PropCard
        {
            public GameObject    Root;
            public RectTransform RootRT;
            public RawImage      Preview;
            public Text          Label;
            public RectTransform LabelRect;
            public RectTransform LabelMask;
            public Image         CardBg;
            public string        DisplayedName;
            public bool          IsDoubled;
        }

        readonly PropCard[] _cards = new PropCard[SlotCount];
        ButtonRef           _prevBtn, _nextBtn;
        int                 _offset;
        static Texture2D    _debugTex;

        int  _lastPropCount  = -1;
        int  _lastMatCount   = -1;
        bool _wasShowingMats = false;

        // Drag state
        int                     _mouseDownCard = -1;
        Vector2                 _mouseDownPos;
        MaterialConstructionEntry _matDragEntry;
        Text                    _dragLabel;
        RectTransform           _dragLabelRT;
        RectTransform           _canvasRT;

        Text _pageLabel;

        public PropLibraryPanel(UIBase owner) : base(owner) { }

        protected override void ConstructPanelContent()
        {
            TitleBar?.SetActive(false);
            _debugTex = BuildCheckerTexture();

            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(ContentRoot,
                forceWidth: true, forceHeight: false,
                childControlWidth: true, childControlHeight: true,
                spacing: 2, padTop: 2, padBottom: 2, padLeft: 2, padRight: 2);

            for (int i = 0; i < SlotCount; i++)
                _cards[i] = BuildCard(ContentRoot, i);

            var navRow = UIFactory.CreateHorizontalGroup(ContentRoot, "NavRow",
                false, false, true, true, spacing: 2, padding: new Vector4(2, 2, 2, 2));
            UIFactory.SetLayoutElement(navRow, minHeight: 56, flexibleWidth: 9999);

            _prevBtn = UIFactory.CreateButton(navRow, "PrevBtn", "◄");
            UIFactory.SetLayoutElement(_prevBtn.Component.gameObject, minHeight: 52, flexibleWidth: 9999);
            SetButtonLabel(_prevBtn, "◄", "-");
            PropBrowserUI.ApplyButtonColors(_prevBtn);
            _prevBtn.OnClick += () => { Scroll(-SlotCount); PropBrowserUI.Deselect(); };

            _nextBtn = UIFactory.CreateButton(navRow, "NextBtn", "►");
            UIFactory.SetLayoutElement(_nextBtn.Component.gameObject, minHeight: 52, flexibleWidth: 9999);
            SetButtonLabel(_nextBtn, "►", "=");
            PropBrowserUI.ApplyButtonColors(_nextBtn);
            _nextBtn.OnClick += () => { Scroll(+SlotCount); PropBrowserUI.Deselect(); };

            _pageLabel = UIFactory.CreateLabel(ContentRoot, "PageLabel", "1 / 1",
                TextAnchor.MiddleCenter, new Color(0.6f, 0.6f, 0.6f), fontSize: 14);
            UIFactory.SetLayoutElement(_pageLabel.gameObject, minHeight: 20, flexibleWidth: 9999);

            // Floating drag label — shown while dragging a material.
            // Use UIFactory so font and component setup match UniverseLib conventions.
            var dragBgGO = UIFactory.CreateUIObject("DragLabelBg", Owner.RootObject);
            dragBgGO.AddComponent<Image>().color = new Color(0.1f, 0.1f, 0.15f, 0.85f);
            _dragLabelRT           = dragBgGO.GetComponent<RectTransform>();
            _dragLabelRT.anchorMin = new Vector2(0f, 1f);
            _dragLabelRT.anchorMax = new Vector2(0f, 1f);
            _dragLabelRT.pivot     = new Vector2(0f, 1f);
            _dragLabelRT.sizeDelta = new Vector2(200f, 28f);

            _dragLabel = UIFactory.CreateLabel(dragBgGO, "DragLabelText", "",
                TextAnchor.MiddleCenter, new Color(1f, 1f, 0.4f, 0.9f), fontSize: 15);
            var dlRT       = _dragLabel.GetComponent<RectTransform>();
            dlRT.anchorMin = Vector2.zero;
            dlRT.anchorMax = Vector2.one;
            dlRT.sizeDelta = Vector2.zero;

            dragBgGO.SetActive(false);

            _canvasRT = Owner.RootObject.GetComponent<RectTransform>();
        }

        static Texture2D BuildCheckerTexture()
        {
            const int sz = 8;
            var tex = new Texture2D(sz, sz, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode   = TextureWrapMode.Repeat;
            var pixels = new Color32[sz * sz];
            var dark   = new Color32(28, 28, 33, 255);
            var light  = new Color32(62, 62, 72, 255);
            for (int y = 0; y < sz; y++)
            for (int x = 0; x < sz; x++)
                pixels[y * sz + x] = ((x + y) % 2 == 0) ? light : dark;
            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;
        }

        static void SetButtonLabel(ButtonRef btn, string arrow, string key)
        {
            var txt = btn.Component.GetComponentInChildren<Text>();
            if (txt == null) return;
            txt.text             = $"{arrow}\n{key}";
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.lineSpacing      = 0.8f;
        }

        PropCard BuildCard(GameObject parent, int index)
        {
            var cardGO = UIFactory.CreateUIObject($"Card{index}", parent);
            var cardBg = cardGO.AddComponent<Image>();
            cardBg.color = new Color(0.13f, 0.13f, 0.16f);
            UIFactory.SetLayoutElement(cardGO, minHeight: CardHeight, flexibleWidth: 9999, flexibleHeight: 0);

            var previewGO = UIFactory.CreateUIObject($"Preview{index}", cardGO);
            var preview   = previewGO.AddComponent<RawImage>();
            preview.texture = _debugTex;
            preview.uvRect  = new Rect(0f, 0f, 4f, 4f);
            var previewRect = previewGO.GetComponent<RectTransform>();
            previewRect.anchorMin        = new Vector2(0f, 0f);
            previewRect.anchorMax        = new Vector2(0f, 1f);
            previewRect.pivot            = new Vector2(0f, 0.5f);
            previewRect.anchoredPosition = Vector2.zero;
            previewRect.sizeDelta        = new Vector2(PreviewSize, 0f);

            const float gap = 4f;
            var maskGO   = UIFactory.CreateUIObject($"LabelMask{index}", cardGO);
            maskGO.AddComponent<RectMask2D>();
            var maskRect = maskGO.GetComponent<RectTransform>();
            maskRect.anchorMin        = new Vector2(0f, 0f);
            maskRect.anchorMax        = new Vector2(1f, 1f);
            maskRect.pivot            = new Vector2(0f, 0.5f);
            maskRect.anchoredPosition = new Vector2(PreviewSize + gap, 0f);
            maskRect.sizeDelta        = new Vector2(-(PreviewSize + gap), 0f);

            var lbl = UIFactory.CreateLabel(maskGO, $"LabelText{index}", "",
                TextAnchor.MiddleLeft, new Color(0.95f, 0.95f, 0.95f),
                supportRichText: false, fontSize: LabelFontSz);
            lbl.horizontalOverflow = HorizontalWrapMode.Overflow;
            lbl.verticalOverflow   = VerticalWrapMode.Overflow;

            var lblRect = lbl.GetComponent<RectTransform>();
            lblRect.anchorMin        = new Vector2(0f, 0f);
            lblRect.anchorMax        = new Vector2(0f, 1f);
            lblRect.pivot            = new Vector2(0f, 0.5f);
            lblRect.anchoredPosition = Vector2.zero;
            lblRect.sizeDelta        = new Vector2(200f, 0f);

            return new PropCard
            {
                Root      = cardGO,
                RootRT    = cardGO.GetComponent<RectTransform>(),
                Preview   = preview,
                Label     = lbl,
                LabelRect = lblRect,
                LabelMask = maskRect,
                CardBg    = cardBg,
            };
        }

        // ---- Data sources ----

        IReadOnlyList<PropInfo> GetPanelProps()
        {
            if (PropPalette.ShowingMaterials) return Array.Empty<PropInfo>();
            var filtered = PropLibrary.FilteredProps;
            if (_lastPropCount != filtered.Count)
            {
                _lastPropCount = filtered.Count;
                _offset = Math.Clamp(_offset, 0, Math.Max(0, filtered.Count - SlotCount));
            }
            return filtered;
        }

        IReadOnlyList<MaterialConstructionEntry> GetPanelMaterials()
        {
            PropMetadataStore.EnsureLoaded();
            var mats = MaterialConstructionLibrary.Entries;
            if (_lastMatCount != mats.Count)
            {
                _lastMatCount = mats.Count;
                _offset = Math.Clamp(_offset, 0, Math.Max(0, mats.Count - SlotCount));
            }
            return mats;
        }

        // ---- Wrap-around navigation (same logic as PropPalette.StepPageOffset) ----

        void Scroll(int delta)
        {
            int total = PropPalette.ShowingMaterials
                ? GetPanelMaterials().Count
                : GetPanelProps().Count;
            if (total <= 0) return;
            int maxOffset = ((total - 1) / SlotCount) * SlotCount;
            int next = _offset + delta;
            if (next > maxOffset) _offset = 0;
            else if (next < 0)   _offset = maxOffset;
            else                  _offset = next;
        }

        // ---- Main tick ----

        public void Tick()
        {
            if (!PropLibrary.IsInitialized) LevelEditor.EnsureManager();

            bool showMats = PropPalette.ShowingMaterials;
            if (showMats != _wasShowingMats)
            {
                _offset = 0;
                _wasShowingMats = showMats;
                CancelMatDrag();
                for (int i = 0; i < SlotCount; i++)
                {
                    _cards[i].DisplayedName = null;
                    _cards[i].IsDoubled = false;
                }
            }

            PropPreviewRenderer.Update();

            if (!Core.IsKeyboardCaptured)
            {
                if (Input.GetKeyDown(KeyCode.Minus))  Scroll(-SlotCount);
                if (Input.GetKeyDown(KeyCode.Equals)) Scroll(+SlotCount);
            }

            TickDragDetection();
            TickMatDrag();

            if (showMats)
                TickMaterialsMode();
            else
                TickPropsMode();

            TickCardHover();
            TickPageLabel();
        }

        void TickPageLabel()
        {
            if (_pageLabel == null) return;
            int total = PropPalette.ShowingMaterials ? GetPanelMaterials().Count : GetPanelProps().Count;
            int totalPages  = total <= 0 ? 1 : (total - 1) / SlotCount + 1;
            int currentPage = _offset / SlotCount + 1;
            _pageLabel.text = $"{currentPage} / {totalPages}";
        }

        // ---- Card hover highlight ----

        void TickCardHover()
        {
            for (int i = 0; i < SlotCount; i++)
            {
                ref var card = ref _cards[i];
                if (card.Root == null || !card.Root.activeSelf || card.CardBg == null) continue;
                bool hovered = card.RootRT != null &&
                    RectTransformUtility.RectangleContainsScreenPoint(card.RootRT, Input.mousePosition, null);
                card.CardBg.color = hovered ? CardHoverColor : CardNormalColor;
            }
        }

        // ---- Drag detection ----

        void TickDragDetection()
        {
            // Track mouse-down on cards, convert to drag once threshold is exceeded.
            if (!PropPalette.IsDragging && _matDragEntry == null)
            {
                if (Input.GetMouseButtonDown(0))
                {
                    _mouseDownCard = -1;
                    for (int i = 0; i < SlotCount; i++)
                    {
                        ref var card = ref _cards[i];
                        if (card.Root == null || !card.Root.activeSelf) continue;
                        if (card.RootRT != null && RectTransformUtility.RectangleContainsScreenPoint(card.RootRT, Input.mousePosition, null))
                        {
                            _mouseDownCard = i;
                            _mouseDownPos  = Input.mousePosition;
                            break;
                        }
                    }
                }

                if (_mouseDownCard >= 0)
                {
                    if (!Input.GetMouseButton(0))
                    {
                        _mouseDownCard = -1; // Released without dragging — treat as cancelled
                    }
                    else if (Vector2.Distance(Input.mousePosition, _mouseDownPos) > DragThreshold)
                    {
                        StartCardDrag(_mouseDownCard);
                        _mouseDownCard = -1;
                    }
                }
            }
            else
            {
                // Already in a drag — clear pending card if any
                _mouseDownCard = -1;
            }
        }

        void StartCardDrag(int cardIndex)
        {
            if (PropPalette.ShowingMaterials)
            {
                var mats = GetPanelMaterials();
                int idx  = _offset + cardIndex;
                if (idx < mats.Count)
                {
                    _matDragEntry = mats[idx];
                    if (_dragLabel != null) _dragLabel.text = _matDragEntry.name;
                    if (_dragLabelRT != null) _dragLabelRT.gameObject.SetActive(true);
                }
            }
            else
            {
                var props = GetPanelProps();
                int idx   = _offset + cardIndex;
                if (idx < props.Count)
                    PropPalette.BeginDrag(idx, props[idx]);
            }
        }

        void TickMatDrag()
        {
            if (_matDragEntry == null) return;

            if (Input.GetMouseButton(0))
            {
                if (_dragLabelRT != null && _canvasRT != null)
                {
                    RectTransformUtility.ScreenPointToLocalPointInRectangle(
                        _canvasRT, Input.mousePosition, null, out Vector2 lp);
                    _dragLabelRT.anchoredPosition = lp + new Vector2(12f, 12f);
                }
            }
            else
            {
                ApplyDraggedMaterial();
                CancelMatDrag();
            }
        }

        void ApplyDraggedMaterial()
        {
            if (_matDragEntry == null) return;
            if (Camera.main == null) return;

            var ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            LevelEditorObject foundLeo = null;
            float bestDist = float.MaxValue;
            foreach (var h in Physics.RaycastAll(ray, 2000f, ~GizmoRenderer.Mask, QueryTriggerInteraction.Collide))
            {
                if (h.distance >= bestDist) continue;
                var leo = h.collider.GetComponent<LevelEditorObject>()
                       ?? h.collider.GetComponentInParent<LevelEditorObject>();
                if (leo == null) continue;
                bestDist = h.distance;
                foundLeo = leo;
            }

            if (foundLeo == null) return;
            MaterialConstructionPanel.ApplyToInstance(foundLeo, _matDragEntry);
            if (foundLeo.netId != 0)
                Networking.ModNetworking.SendMaterialApplied(foundLeo.netId, _matDragEntry.id);
        }

        void CancelMatDrag()
        {
            _matDragEntry = null;
            if (_dragLabelRT != null) _dragLabelRT.gameObject.SetActive(false);
        }

        // ---- Props mode tick ----

        void TickPropsMode()
        {
            var props = GetPanelProps();
            RefreshPropCards(props);

            for (int i = 0; i < SlotCount; i++)
            {
                int propIdx = _offset + i;
                if (propIdx >= props.Count) break;
                var checkInfo = props[propIdx];
                if (!checkInfo.isLoaded && !checkInfo.isInvalid)
                {
                    PropLibrary.LoadPropData(checkInfo);
                    break;
                }
            }

            for (int i = 0; i < SlotCount; i++)
            {
                ref var card = ref _cards[i];
                if (card.Root == null || !card.Root.activeSelf) continue;

                int propIdx = _offset + i;
                if (propIdx < props.Count)
                {
                    var info = props[propIdx];
                    PropPreviewRenderer.Request(info);
                    var tex = PropPreviewRenderer.Get(info.id);
                    if (card.Preview != null)
                    {
                        if (tex != null)
                        { card.Preview.texture = tex;  card.Preview.uvRect = new Rect(0f, 0f, 1f, 1f); }
                        else
                        { card.Preview.texture = _debugTex; card.Preview.uvRect = new Rect(0f, 0f, 4f, 4f); }
                    }
                }

                TickMarquee(i);
            }
        }

        // ---- Materials mode tick ----

        void TickMaterialsMode()
        {
            var mats = GetPanelMaterials();
            RefreshMatCards(mats);

            MaterialCatalog.EnsureMaterialList();

            for (int i = 0; i < SlotCount; i++)
            {
                ref var card = ref _cards[i];
                if (card.Root == null || !card.Root.activeSelf) continue;

                int idx = _offset + i;
                if (idx >= mats.Count) continue;

                var entry = mats[idx];
                // Use ResolveMaterialByName so hash-suffixed materials and
                // verified-source materials are found, not just in-scene byName lookup.
                Material mat = null;
                if (!string.IsNullOrEmpty(entry.materialName))
                    mat = MaterialCatalog.ResolveMaterialByName(entry.materialName);

                if (mat != null)
                    PropPreviewRenderer.RequestMaterialSphere(entry.id, mat);

                var tex = PropPreviewRenderer.GetMaterialSphere(entry.id);
                if (card.Preview != null)
                {
                    if (tex != null)
                    { card.Preview.texture = tex;  card.Preview.uvRect = new Rect(0f, 0f, 1f, 1f); }
                    else
                    { card.Preview.texture = _debugTex; card.Preview.uvRect = new Rect(0f, 0f, 4f, 4f); }
                }

                TickMarquee(i);
            }
        }

        // ---- Card refresh ----

        void RefreshPropCards(IReadOnlyList<PropInfo> props)
        {
            int total = props.Count;
            if (_prevBtn != null) _prevBtn.Component.interactable = true;
            if (_nextBtn != null) _nextBtn.Component.interactable = total > SlotCount;

            for (int i = 0; i < SlotCount; i++)
            {
                int propIdx = _offset + i;
                ref var card = ref _cards[i];
                if (card.Root == null) continue;

                bool hasProp = propIdx < total;
                card.Root.SetActive(hasProp);
                if (!hasProp) continue;

                var info = props[propIdx];
                string name = PropMetadataStore.GetDisplayName(info.id) ?? info.displayName;

                if (card.DisplayedName != name)
                {
                    card.DisplayedName = name;
                    card.IsDoubled     = false;
                    if (card.Label != null) card.Label.text = name;
                    if (card.LabelRect != null) card.LabelRect.anchoredPosition = Vector2.zero;
                }
            }
        }

        void RefreshMatCards(IReadOnlyList<MaterialConstructionEntry> mats)
        {
            int total = mats.Count;
            if (_prevBtn != null) _prevBtn.Component.interactable = true;
            if (_nextBtn != null) _nextBtn.Component.interactable = total > SlotCount;

            for (int i = 0; i < SlotCount; i++)
            {
                int idx = _offset + i;
                ref var card = ref _cards[i];
                if (card.Root == null) continue;

                bool has = idx < total;
                card.Root.SetActive(has);
                if (!has) continue;

                string name = mats[idx].name;
                if (card.DisplayedName != name)
                {
                    card.DisplayedName = name;
                    card.IsDoubled     = false;
                    if (card.Label != null) card.Label.text = name;
                    if (card.LabelRect != null) card.LabelRect.anchoredPosition = Vector2.zero;
                }
            }
        }

        // ---- Marquee ----

        void TickMarquee(int i)
        {
            ref var card = ref _cards[i];
            if (card.Label == null || card.LabelMask == null || card.LabelRect == null) return;

            float viewW = card.LabelMask.rect.width;
            if (viewW <= 1f) return;

            if (!card.IsDoubled)
            {
                float singleW = card.Label.preferredWidth;
                if (singleW <= viewW)
                {
                    card.LabelRect.anchoredPosition = Vector2.zero;
                    if (Mathf.Abs(card.LabelRect.sizeDelta.x - singleW) > 0.5f)
                        card.LabelRect.sizeDelta = new Vector2(singleW, card.LabelRect.sizeDelta.y);
                    return;
                }

                string name = card.DisplayedName ?? "";
                card.Label.text = name + "   •   " + name + "   •   ";
                card.IsDoubled  = true;
            }

            float textW = card.Label.preferredWidth;
            float halfW = textW * 0.5f;
            if (halfW <= 1f) return;

            if (Mathf.Abs(card.LabelRect.sizeDelta.x - textW) > 0.5f)
                card.LabelRect.sizeDelta = new Vector2(textW, card.LabelRect.sizeDelta.y);

            float t = Time.unscaledTime * ScrollPx + i * 37f;
            card.LabelRect.anchoredPosition = new Vector2(-Mathf.Repeat(t, halfW), 0f);
        }
    }
}
