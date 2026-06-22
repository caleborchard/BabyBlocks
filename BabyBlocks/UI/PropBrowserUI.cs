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
        public static bool IsTypingInUI => (_panel != null && _panel.IsSearchFocused)
                                        || (_fileBrowser != null && _fileBrowser.IsTypingInUI);

        static UIBase              _uiBase;
        static TopBarPanel         _topBar;
        static PropLibraryPanel    _panel;
        static FileBrowserPanel    _fileBrowser;
        static GizmoSettingsPanel  _gizmoSettings;

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
                _topBar        = new TopBarPanel(_uiBase);
                _panel         = new PropLibraryPanel(_uiBase);
                _fileBrowser   = new FileBrowserPanel(_uiBase);
                _gizmoSettings = new GizmoSettingsPanel(_uiBase);
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

        static bool _savedCenterDot;
        static bool _lastFreeCamActive;

        public static void UpdateVisibility()
        {
            if (!Ready) return;
            bool inEditor      = FlyCamController.FlyCamActive && FlyCamController.CursorMode;
            bool freeCamNow    = FlyCamController.FlyCamActive && !FlyCamController.CursorMode;

            if (_uiBase.Enabled != inEditor)
            {
                _uiBase.Enabled = inEditor;
                ApplyCameraViewport(inEditor);
            }

            if (freeCamNow != _lastFreeCamActive)
            {
                _lastFreeCamActive = freeCamNow;
                ApplyCrosshair(freeCamNow);
            }
        }

        static void ApplyCrosshair(bool freeCamActive)
        {
            if (Menu.me == null) return;
            if (freeCamActive)
            {
                _savedCenterDot = Menu.cfg.centerDot;
                Menu.me.ToggleCenterDot(true);
            }
            else
            {
                Menu.me.ToggleCenterDot(_savedCenterDot);
            }
        }

        static void ApplyCameraViewport(bool editorOpen)
        {
            var cam = Camera.main;
            if (cam == null) return;
            if (editorOpen)
            {
                float panelFrac = PropLibraryPanel.PanelWidth / (float)Screen.width;
                cam.rect = new Rect(panelFrac, 0f, 1f - panelFrac, 1f);
            }
            else
            {
                cam.rect = new Rect(0f, 0f, 1f, 1f);
            }
        }

        static int _lastViewportW = -1;

        static void OnUpdate()
        {
            // Prevent Space/Enter from re-firing the last clicked button.
            UniversalUI.EventSys?.SetSelectedGameObject(null);
            _topBar?.Tick();
            _panel?.Tick();
            _fileBrowser?.Tick();

            // Re-apply camera viewport fraction if the screen width changes while editor is open.
            if (_uiBase != null && _uiBase.Enabled && Screen.width != _lastViewportW)
            {
                _lastViewportW = Screen.width;
                ApplyCameraViewport(true);
            }
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

        public static bool IsPointerOverPanel()
        {
            if (!Ready || _uiBase == null || !_uiBase.Enabled) return false;
            var pos = Input.mousePosition;
            if (_topBar?.Rect != null &&
                RectTransformUtility.RectangleContainsScreenPoint(_topBar.Rect, pos, null))
                return true;
            if (_topBar?.IsPointerOverDropdown() == true) return true;
            if (_panel?.Rect != null &&
                RectTransformUtility.RectangleContainsScreenPoint(_panel.Rect, pos, null))
                return true;
            if (_fileBrowser?.UIRoot?.activeSelf == true && _fileBrowser.Rect != null &&
                RectTransformUtility.RectangleContainsScreenPoint(_fileBrowser.Rect, pos, null))
                return true;
            return false;
        }

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

        ButtonRef    _catBtn;
        LayoutElement _catBtnLE;
        GameObject   _catDropdown;
        GameObject   _catItemsContainer;
        bool         _catOpen;

        // Mode/tool quick-buttons in the bar
        ButtonRef _editorBtn;
        ButtonRef _toolBtn;
        ButtonRef _localBtn;
        ButtonRef _smoothBtn;
        ButtonRef _baseMapBtn;
        ButtonRef _weatherBtn;

        const float CatDropdownOffsetX = 4f;
        const float BarSpacing          = 4f;

        // Width for a "Current → Next" cycle button: "longest → longest" label at ~9 px/char + 8 padding.
        static int CycleButtonWidth(params string[] opts)
        {
            int max = 0;
            foreach (var s in opts) if (s.Length > max) max = s.Length;
            return (2 * max + 3) * 9 + 8; // "max → max" with arrow
        }

        public TopBarPanel(UIBase owner) : base(owner) { }

        protected override void ConstructPanelContent()
        {
            TitleBar?.SetActive(false);

            var barRow = UIFactory.CreateHorizontalGroup(ContentRoot, "BarRow",
                false, false, true, true, spacing: (int)BarSpacing, padding: new Vector4(0, 2, 4, 2));
            UIFactory.SetLayoutElement(barRow, flexibleWidth: 9999, minHeight: BarHeight - 4);

            // Category / mode selector — width sized to content in RebuildCatItems
            _catBtn = UIFactory.CreateButton(barRow, "CatBtn", " All ▼ ");
            UIFactory.SetLayoutElement(_catBtn.Component.gameObject,
                minWidth: 60, flexibleWidth: 0, minHeight: 22);
            _catBtnLE = _catBtn.Component.gameObject.GetComponent<LayoutElement>();
            PropBrowserUI.ApplyButtonColors(_catBtn);
            _catBtn.OnClick += ToggleCat;

            // File
            var fileBtn = UIFactory.CreateButton(barRow, "FileBtn", " File ");
            UIFactory.SetLayoutElement(fileBtn.Component.gameObject, minWidth: 70, minHeight: 22);
            PropBrowserUI.ApplyButtonColors(fileBtn);
            fileBtn.OnClick += ToggleFile;

            // Editor / Teleport toggle (R key)
            _editorBtn = UIFactory.CreateButton(barRow, "EditorBtn", "Editor → Teleport");
            UIFactory.SetLayoutElement(_editorBtn.Component.gameObject,
                minWidth: CycleButtonWidth("Editor", "Teleport"), minHeight: 22);
            PropBrowserUI.ApplyButtonColors(_editorBtn);
            _editorBtn.OnClick += () => { PropBrowserUI.Deselect(); CloseFile(); CloseCat(); FlyCamController.InvokeRKeyAction(); };

            // Gizmo tool cycle (Space key)
            _toolBtn = UIFactory.CreateButton(barRow, "ToolBtn", "Translate → Scale");
            UIFactory.SetLayoutElement(_toolBtn.Component.gameObject,
                minWidth: CycleButtonWidth("Translate", "Scale", "Rotate"), minHeight: 22);
            PropBrowserUI.ApplyButtonColors(_toolBtn);
            _toolBtn.OnClick += () =>
            {
                PropBrowserUI.Deselect();
                LevelEditor.currentTool = LevelEditor.currentTool == LevelEditor.ToolMode.Translate ? LevelEditor.ToolMode.Scale
                                        : LevelEditor.currentTool == LevelEditor.ToolMode.Scale     ? LevelEditor.ToolMode.Rotate
                                        : LevelEditor.ToolMode.Translate;
            };

            // Local / Global toggle (T key)
            _localBtn = UIFactory.CreateButton(barRow, "LocalBtn", "Local → Global");
            UIFactory.SetLayoutElement(_localBtn.Component.gameObject,
                minWidth: CycleButtonWidth("Local", "Global"), minHeight: 22);
            PropBrowserUI.ApplyButtonColors(_localBtn);
            _localBtn.OnClick += () => { PropBrowserUI.Deselect(); LevelEditor.LocalMode = !LevelEditor.LocalMode; };

            // Smooth / Snap toggle (Y key)
            _smoothBtn = UIFactory.CreateButton(barRow, "SmoothBtn", "Smooth → Snap");
            UIFactory.SetLayoutElement(_smoothBtn.Component.gameObject,
                minWidth: CycleButtonWidth("Smooth", "Snap"), minHeight: 22);
            PropBrowserUI.ApplyButtonColors(_smoothBtn);
            _smoothBtn.OnClick += () => { PropBrowserUI.Deselect(); LevelEditor.SnapEnabled = !LevelEditor.SnapEnabled; };

            // Base Map toggle
            _baseMapBtn = UIFactory.CreateButton(barRow, "BaseMapBtn", "Base Map: On → Off");
            UIFactory.SetLayoutElement(_baseMapBtn.Component.gameObject,
                minWidth: "Base Map: Off → On".Length * 9 + 8, minHeight: 22);
            PropBrowserUI.ApplyButtonColors(_baseMapBtn);
            _baseMapBtn.OnClick += () =>
            {
                PropBrowserUI.Deselect();
                bool next = !BaseMapController.BaseMapEnabled;
                BaseMapController.SetBaseMapEnabled(next);
                Networking.ModNetworking.SendBaseMapState(next);
            };

            // Weather preset cycle (Default → 0 → 1 → … → N-1 → Default)
            _weatherBtn = UIFactory.CreateButton(barRow, "WeatherBtn", "Weather: Default → 0");
            UIFactory.SetLayoutElement(_weatherBtn.Component.gameObject,
                minWidth: "Weather: Default → 0".Length * 9 + 8, minHeight: 22);
            PropBrowserUI.ApplyButtonColors(_weatherBtn);
            _weatherBtn.OnClick += () =>
            {
                PropBrowserUI.Deselect();
                int count = BaseMapController.DayWeatherPlaylistCount;
                if (count == 0) return;
                int next = BaseMapController.WeatherPreset < 0 ? 0
                         : BaseMapController.WeatherPreset + 1 >= count ? -1
                         : BaseMapController.WeatherPreset + 1;
                BaseMapController.SetWeatherPreset(next);
            };

            _fileDropdown = BuildFileDropdown();
            _fileDropdown.SetActive(false);

            _catDropdown = BuildCatDropdown();
            _catDropdown.SetActive(false);

            // Size cat button and dropdowns immediately so File dropdown is at the right X from the start.
            RebuildCatItems();
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
            rt.anchoredPosition = new Vector2(60f + BarSpacing, -(BarHeight + 1f)); // X updated in RebuildCatItems
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
            // Files, Clear = 2 items
            float totalH = 3 + 2 * itemH + 1 * 1 + 3; // pad + items + spacing + pad
            var rt = _fileDropdown.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(rt.sizeDelta.x, totalH);

            var filesBtn = UIFactory.CreateButton(_fileItemsContainer, "FilesBtn", "Save/Load");
            UIFactory.SetLayoutElement(filesBtn.Component.gameObject, minHeight: itemH, flexibleWidth: 9999);
            PropBrowserUI.ApplyButtonColors(filesBtn);
            filesBtn.OnClick += () => { PropBrowserUI.Deselect(); CloseFile(); FileBrowserPanel.OpenPanel(); };

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
                BaseMapController.SetWeatherPreset(-1);
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
            // DestroyImmediate so children are gone before we add new ones and measure.
            for (int i = _catItemsContainer.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(_catItemsContainer.transform.GetChild(i).gameObject);

            var cats = PropMetadataStore.GetAllCategories();
            cats.Sort(StringComparer.OrdinalIgnoreCase);

            // Width: sized to the longest button label. The button shows " {name} ▼ ", so
            // add 4 chars to the raw name length. ~11px per char fits the default UniverseLib font.
            int maxLen = 9; // "Materials"
            foreach (var cat in cats)
                if (cat.Length > maxLen) maxLen = cat.Length;
            float dropW = Mathf.Max(100f, (maxLen + 4) * 11f + 8f); // +4 for " ▼ " + leading space

            // Keep cat button and file dropdown X in sync with computed width.
            if (_catBtnLE != null) _catBtnLE.minWidth = dropW;
            var fileDDRT = _fileDropdown?.GetComponent<RectTransform>();
            if (fileDDRT != null)
                fileDDRT.anchoredPosition = new Vector2(dropW + BarSpacing, -(BarHeight + 1f));

            AddFilterItem("All", null, showingMats: false);
            foreach (var cat in cats)
                AddFilterItem(cat, cat, showingMats: false);
            AddFilterItem("Materials", null, showingMats: true);

            // History preserves whatever ShowingMaterials mode is active when clicked,
            // so it works as "prop history" or "material history" depending on context.
            {
                var histBtn = UIFactory.CreateButton(_catItemsContainer, "Item_History", "History");
                UIFactory.SetLayoutElement(histBtn.Component.gameObject, minHeight: 26, flexibleWidth: 9999);
                PropBrowserUI.ApplyButtonColors(histBtn);
                bool isHistCurrent = !PropPalette.ShowingMaterials && string.Equals(PropPalette.SelectedCategory, "History", StringComparison.OrdinalIgnoreCase);
                if (isHistCurrent)
                {
                    var img = histBtn.Component.GetComponent<Image>();
                    if (img != null) img.color = new Color(0.3f, 0.5f, 0.8f, 1f);
                }
                histBtn.OnClick += () =>
                {
                    PropBrowserUI.Deselect();
                    CloseCat();
                    PropPalette.ShowingMaterials = false;
                    PropPalette.SelectedCategory = "History";
                    PropLibrary.RebuildFiltered();
                };
            }

            int itemCount = cats.Count + 3; // All + History + cats + Materials
            const int itemH = 26, pad = 3, spacing = 1;
            float totalH = pad + itemCount * itemH + (itemCount - 1) * spacing + pad;

            var rt = _catDropdown.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(dropW, totalH);
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

            // Editor / Teleport button
            if (_editorBtn != null)
            {
                bool inEditor = FlyCamController.FlyCamActive && FlyCamController.CursorMode;
                var txt = _editorBtn.Component.GetComponentInChildren<Text>();
                if (txt != null) txt.text = inEditor ? "Editor → Teleport" : "Teleport → Editor";
            }

            // Gizmo tool button
            if (_toolBtn != null)
            {
                string toolLabel = LevelEditor.currentTool switch
                {
                    LevelEditor.ToolMode.Scale  => "Scale → Rotate",
                    LevelEditor.ToolMode.Rotate => "Rotate → Translate",
                    _                           => "Translate → Scale",
                };
                var txt = _toolBtn.Component.GetComponentInChildren<Text>();
                if (txt != null) txt.text = toolLabel;
            }

            // Local / Global button
            if (_localBtn != null)
            {
                var txt = _localBtn.Component.GetComponentInChildren<Text>();
                if (txt != null) txt.text = LevelEditor.LocalMode ? "Local → Global" : "Global → Local";
            }

            // Smooth / Snap button
            if (_smoothBtn != null)
            {
                var txt = _smoothBtn.Component.GetComponentInChildren<Text>();
                if (txt != null) txt.text = LevelEditor.SnapEnabled ? "Snap → Smooth" : "Smooth → Snap";
            }

            // Base Map button
            if (_baseMapBtn != null)
            {
                var txt = _baseMapBtn.Component.GetComponentInChildren<Text>();
                if (txt != null) txt.text = BaseMapController.BaseMapEnabled
                    ? "Base Map: On → Off"
                    : "Base Map: Off → On";
            }

            // Weather preset button
            if (_weatherBtn != null)
            {
                int count = BaseMapController.DayWeatherPlaylistCount;
                int cur   = BaseMapController.WeatherPreset;
                string label;
                if (count == 0)
                    label = "Weather: ...";
                else if (cur < 0)
                    label = "Weather: Default → 0";
                else
                {
                    string nextStr = (cur + 1 >= count) ? "Default" : (cur + 1).ToString();
                    label = $"Weather: {cur} → {nextStr}";
                }
                var txt = _weatherBtn.Component.GetComponentInChildren<Text>();
                if (txt != null) txt.text = label;
            }

            // Expire the "Confirm clear?" state if the user doesn't act in time.
            if (_pendingClear && Time.realtimeSinceStartup - _pendingClearTime > ClearConfirmTimeout)
            {
                _pendingClear = false;
                if (_fileOpen) RebuildFileItems(); // revert button to "Clear"
            }
        }

        internal bool IsPointerOverDropdown()
        {
            var pos = Input.mousePosition;
            if (_fileOpen && _fileDropdown != null)
            {
                var rt = _fileDropdown.GetComponent<RectTransform>();
                if (rt != null && RectTransformUtility.RectangleContainsScreenPoint(rt, pos, null)) return true;
            }
            if (_catOpen && _catDropdown != null)
            {
                var rt = _catDropdown.GetComponent<RectTransform>();
                if (rt != null && RectTransformUtility.RectangleContainsScreenPoint(rt, pos, null)) return true;
            }
            return false;
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

        internal bool IsSearchFocused => _searchInput?.Component?.isFocused == true;

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
            public GameObject              Root;
            public RectTransform           RootRT;
            public RawImage                Preview;
            public RectTransform           PreviewRT;
            public Text                    Label;
            public RectTransform           LabelRect;
            public RectTransform           LabelMask;
            public Image                   CardBg;
            public LayoutElement           CardLE;
            public string                  DisplayedName;
            public bool                    IsDoubled;
            public MaterialConstructionEntry HistoryMat; // non-null when this history card is a material
        }

        readonly PropCard[] _cards = new PropCard[SlotCount];
        ButtonRef           _prevBtn, _nextBtn;
        int                 _offset;
        static Texture2D    _debugTex;

        int  _lastPropCount    = -1;
        int  _lastMatCount     = -1;
        bool _wasShowingMats   = false;
        int  _lastScreenH      = -1;
        int  _activeSlotCount  = SlotCount;

        // Search
        InputFieldRef _searchInput;
        string        _searchText = "";
        readonly List<PropInfo>                          _propSearchResults = new();
        readonly List<MaterialConstructionEntry>         _matSearchResults  = new();
        readonly List<PropHistory.ResolvedHistoryEntry>  _histSearchResults = new();

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

            // Search bar
            var searchRow = UIFactory.CreateHorizontalGroup(ContentRoot, "SearchRow",
                false, false, true, true, spacing: 4, padding: new Vector4(2, 1, 2, 1));
            UIFactory.SetLayoutElement(searchRow, minHeight: 26, flexibleWidth: 9999);
            _searchInput = UIFactory.CreateInputField(searchRow, "SearchInput", "Search...");
            UIFactory.SetLayoutElement(_searchInput.Component.gameObject, flexibleWidth: 9999, minHeight: 22);
            _searchInput.Component.characterLimit = 64;

            for (int i = 0; i < SlotCount; i++)
                _cards[i] = BuildCard(ContentRoot, i);

            // Flexible spacer: pushes nav row to the bottom when fewer than SlotCount cards are visible.
            var navSpacer = UIFactory.CreateUIObject("NavSpacer", ContentRoot);
            UIFactory.SetLayoutElement(navSpacer, flexibleHeight: 9999, minHeight: 0);

            var navRow = UIFactory.CreateHorizontalGroup(ContentRoot, "NavRow",
                false, false, true, true, spacing: 2, padding: new Vector4(2, 2, 2, 2));
            UIFactory.SetLayoutElement(navRow, minHeight: 56, flexibleWidth: 9999);

            _prevBtn = UIFactory.CreateButton(navRow, "PrevBtn", "◄");
            UIFactory.SetLayoutElement(_prevBtn.Component.gameObject, minHeight: 52, flexibleWidth: 9999);
            SetButtonLabel(_prevBtn, "◄", "-");
            PropBrowserUI.ApplyButtonColors(_prevBtn);
            _prevBtn.OnClick += () => { Scroll(-_activeSlotCount); PropBrowserUI.Deselect(); };

            _nextBtn = UIFactory.CreateButton(navRow, "NextBtn", "►");
            UIFactory.SetLayoutElement(_nextBtn.Component.gameObject, minHeight: 52, flexibleWidth: 9999);
            SetButtonLabel(_nextBtn, "►", "=");
            PropBrowserUI.ApplyButtonColors(_nextBtn);
            _nextBtn.OnClick += () => { Scroll(+_activeSlotCount); PropBrowserUI.Deselect(); };

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
            var cardLE = cardGO.GetComponent<LayoutElement>();

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
                PreviewRT = previewRect,
                Label     = lbl,
                LabelRect = lblRect,
                LabelMask = maskRect,
                CardBg    = cardBg,
                CardLE    = cardLE,
            };
        }

        // ---- Data sources ----

        IReadOnlyList<PropInfo> GetPanelProps()
        {
            if (PropPalette.ShowingMaterials) return Array.Empty<PropInfo>();
            var filtered = PropLibrary.FilteredProps;
            IReadOnlyList<PropInfo> result = filtered;
            if (!string.IsNullOrEmpty(_searchText))
            {
                _propSearchResults.Clear();
                foreach (var p in filtered)
                {
                    var name = PropMetadataStore.GetDisplayName(p.id) ?? p.displayName ?? "";
                    if (name.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                        _propSearchResults.Add(p);
                }
                result = _propSearchResults;
            }
            if (_lastPropCount != result.Count)
            {
                _lastPropCount = result.Count;
                _offset = Math.Clamp(_offset, 0, Math.Max(0, result.Count - _activeSlotCount));
            }
            return result;
        }

        IReadOnlyList<MaterialConstructionEntry> GetPanelMaterials()
        {
            PropMetadataStore.EnsureLoaded();
            var mats = MaterialConstructionLibrary.Entries;
            IReadOnlyList<MaterialConstructionEntry> result = mats;
            if (!string.IsNullOrEmpty(_searchText))
            {
                _matSearchResults.Clear();
                foreach (var m in mats)
                {
                    if ((m.name ?? "").IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                        _matSearchResults.Add(m);
                }
                result = _matSearchResults;
            }
            if (_lastMatCount != result.Count)
            {
                _lastMatCount = result.Count;
                _offset = Math.Clamp(_offset, 0, Math.Max(0, result.Count - _activeSlotCount));
            }
            return result;
        }

        // ---- Wrap-around navigation (same logic as PropPalette.StepPageOffset) ----

        bool IsHistoryMode => string.Equals(PropPalette.SelectedCategory, "History", StringComparison.OrdinalIgnoreCase);

        IReadOnlyList<PropHistory.ResolvedHistoryEntry> GetHistoryFiltered()
        {
            var all = PropHistory.GetAllResolved();
            if (string.IsNullOrEmpty(_searchText)) return all;
            _histSearchResults.Clear();
            foreach (var e in all)
            {
                string name = e.IsMat
                    ? (e.Mat?.name ?? "")
                    : (PropMetadataStore.GetDisplayName(e.Prop?.id) ?? e.Prop?.displayName ?? "");
                if (name.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                    _histSearchResults.Add(e);
            }
            return _histSearchResults;
        }

        int GetCurrentCount()
        {
            if (PropPalette.ShowingMaterials) return GetPanelMaterials().Count;
            if (IsHistoryMode) return GetHistoryFiltered().Count;
            return GetPanelProps().Count;
        }

        void Scroll(int delta)
        {
            int total = GetCurrentCount();
            if (total <= 0) return;
            int maxOffset = ((total - 1) / _activeSlotCount) * _activeSlotCount;
            int next = _offset + delta;
            if (next > maxOffset) _offset = 0;
            else if (next < 0)   _offset = maxOffset;
            else                  _offset = next;
        }

        // ---- Main tick ----

        public void Tick()
        {
            if (!PropLibrary.IsInitialized) LevelEditor.EnsureManager();

            if (Screen.height != _lastScreenH)
            {
                _lastScreenH = Screen.height;
                UpdateCardLayout();
            }

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

            if (_searchInput?.Component != null)
            {
                // Deselect if user clicks outside the search field
                if (_searchInput.Component.isFocused && (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1)))
                {
                    var rt = _searchInput.Component.GetComponent<RectTransform>();
                    if (rt == null || !RectTransformUtility.RectangleContainsScreenPoint(rt, Input.mousePosition, null))
                        _searchInput.Component.DeactivateInputField();
                }

                string cur = _searchInput.Component.text ?? "";
                if (cur != _searchText)
                {
                    _searchText = cur;
                    _offset = 0;
                }
            }

            PropPreviewRenderer.Update();

            if (!Core.IsKeyboardCaptured)
            {
                if (Input.GetKeyDown(KeyCode.Minus))  Scroll(-_activeSlotCount);
                if (Input.GetKeyDown(KeyCode.Equals)) Scroll(+_activeSlotCount);
            }

            TickDragDetection();
            TickMatDrag();

            if (showMats)
                TickMaterialsMode();
            else if (IsHistoryMode)
                TickHistoryMode();
            else
                TickPropsMode();

            TickCardHover();
            TickPageLabel();
        }

        void TickPageLabel()
        {
            if (_pageLabel == null) return;
            int total = GetCurrentCount();
            int totalPages  = total <= 0 ? 1 : (total - 1) / _activeSlotCount + 1;
            int currentPage = _offset / _activeSlotCount + 1;
            _pageLabel.text = $"{currentPage} / {totalPages}";
        }

        void UpdateCardLayout()
        {
            // Fixed overhead: navRow(56) + pageLabel(20) + searchBar(26) + padTop(2) + padBottom(2) + spacing(2).
            const int FixedBase   = 56 + 20 + 26 + 2 + 2 + 2; // 108
            const int TargetCardH = 80;
            const int MinLabelW   = 100; // always leave this many px for the name label
            int available = Screen.height - TopBarPanel.BarHeight;

            int n = Mathf.Clamp(
                Mathf.FloorToInt((available - FixedBase) / (float)(TargetCardH + 2)),
                3, SlotCount);
            int cardH = Mathf.Max(50, (available - FixedBase - (n + 1) * 2) / n);

            // Cap the preview square so the label area never collapses on large/4K screens.
            int previewSize = Mathf.Min(cardH, PanelWidth - MinLabelW);

            _activeSlotCount = n;
            const float gap = 4f;

            for (int i = 0; i < SlotCount; i++)
            {
                bool active = i < _activeSlotCount;
                ref var card = ref _cards[i];
                if (!active && card.Root != null) card.Root.SetActive(false);
                if (!active) continue;
                if (card.CardLE    != null) card.CardLE.minHeight             = cardH;
                if (card.PreviewRT != null) card.PreviewRT.sizeDelta          = new Vector2(previewSize, 0f);
                if (card.LabelMask != null)
                {
                    card.LabelMask.anchoredPosition = new Vector2(previewSize + gap, 0f);
                    card.LabelMask.sizeDelta        = new Vector2(-(previewSize + gap), 0f);
                }
            }
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
            else if (IsHistoryMode)
            {
                ref var card = ref _cards[cardIndex];
                if (card.HistoryMat != null)
                {
                    _matDragEntry = card.HistoryMat;
                    if (_dragLabel != null) _dragLabel.text = card.HistoryMat.name;
                    if (_dragLabelRT != null) _dragLabelRT.gameObject.SetActive(true);
                }
                else
                {
                    var history = PropHistory.GetAllResolved();
                    int idx = _offset + cardIndex;
                    if (idx < history.Count && !history[idx].IsMat)
                    {
                        var info = history[idx].Prop;
                        PropHistory.RecordUse(info.id);
                        PropPalette.BeginDragDirect(info);
                    }
                }
            }
            else
            {
                var props = GetPanelProps();
                int idx   = _offset + cardIndex;
                if (idx < props.Count)
                {
                    var dragInfo = props[idx];
                    PropHistory.RecordUse(dragInfo.id);
                    // When search is active, idx is an index into search results, not FilteredProps,
                    // so BeginDrag's filteredIndex would point at the wrong prop. Use direct instead.
                    if (!string.IsNullOrEmpty(_searchText))
                        PropPalette.BeginDragDirect(dragInfo);
                    else
                        PropPalette.BeginDrag(idx, dragInfo);
                }
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

        // ---- History mode tick (unified props + materials) ----

        void TickHistoryMode()
        {
            var history = GetHistoryFiltered();
            int total = history.Count;

            if (_lastPropCount != total)
            {
                _lastPropCount = total;
                _offset = Math.Clamp(_offset, 0, Math.Max(0, total - _activeSlotCount));
            }

            if (_prevBtn != null) _prevBtn.Component.interactable = true;
            if (_nextBtn != null) _nextBtn.Component.interactable = total > _activeSlotCount;

            for (int i = 0; i < SlotCount; i++)
            {
                int histIdx = _offset + i;
                ref var card = ref _cards[i];
                if (card.Root == null) continue;

                bool hasEntry = i < _activeSlotCount && histIdx < total;
                card.Root.SetActive(hasEntry);
                card.HistoryMat = null;
                if (!hasEntry) continue;

                var entry = history[histIdx];

                if (entry.IsMat)
                {
                    card.HistoryMat = entry.Mat;
                    MaterialCatalog.EnsureMaterialList();
                    Material mat = null;
                    if (!string.IsNullOrEmpty(entry.Mat.materialName))
                        mat = MaterialCatalog.ResolveMaterialByName(entry.Mat.materialName);
                    if (mat != null)
                        PropPreviewRenderer.RequestMaterialSphere(entry.Mat.id, mat);
                    var tex = PropPreviewRenderer.GetMaterialSphere(entry.Mat.id);
                    if (card.Preview != null)
                    {
                        card.Preview.texture = tex ?? _debugTex;
                        card.Preview.uvRect  = tex != null ? new Rect(0f, 0f, 1f, 1f) : new Rect(0f, 0f, 4f, 4f);
                    }
                    string matName = entry.Mat.name;
                    if (card.DisplayedName != matName)
                    {
                        card.DisplayedName = matName;
                        if (card.Label != null) card.Label.text = matName;
                    }
                }
                else
                {
                    var info = entry.Prop;
                    PropPreviewRenderer.Request(info);
                    var tex = PropPreviewRenderer.Get(info.id);
                    if (card.Preview != null)
                    {
                        card.Preview.texture = tex ?? _debugTex;
                        card.Preview.uvRect  = tex != null ? new Rect(0f, 0f, 1f, 1f) : new Rect(0f, 0f, 4f, 4f);
                    }
                    string displayName = PropMetadataStore.GetDisplayName(info.id) ?? info.displayName;
                    if (card.DisplayedName != displayName)
                    {
                        card.DisplayedName = displayName;
                        if (card.Label != null) card.Label.text = displayName;
                    }
                    if (!info.isLoaded && !info.isInvalid)
                        PropLibrary.LoadPropData(info);
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
            if (_nextBtn != null) _nextBtn.Component.interactable = total > _activeSlotCount;

            for (int i = 0; i < SlotCount; i++)
            {
                int propIdx = _offset + i;
                ref var card = ref _cards[i];
                if (card.Root == null) continue;

                bool hasProp = i < _activeSlotCount && propIdx < total;
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
            if (_nextBtn != null) _nextBtn.Component.interactable = total > _activeSlotCount;

            for (int i = 0; i < SlotCount; i++)
            {
                int idx = _offset + i;
                ref var card = ref _cards[i];
                if (card.Root == null) continue;

                bool has = i < _activeSlotCount && idx < total;
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
