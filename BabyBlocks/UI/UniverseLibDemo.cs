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
    static class UniverseLibDemo
    {
        public static bool Ready { get; private set; }

        static UIBase _uiBase;
        static TopBarPanel _topBar;
        static PropLibraryPanel _panel;

        public static void Init()
        {
            Universe.Init(1f, OnReady, null, new UniverseLibConfig
            {
                Disable_EventSystem_Override = false,
                Force_Unlock_Mouse = false,
            });
        }

        static void OnReady()
        {
            _uiBase = UniversalUI.RegisterUI<DemoUIBase>("BabyBlocks.Demo", OnUpdate);
            _topBar = new TopBarPanel(_uiBase);
            _panel = new PropLibraryPanel(_uiBase);
            // UIBase constructor ends with RootObject.SetActive(true); hide immediately.
            // UIBase.Enabled reads RootObject.activeSelf, so this also sets
            // AnyUIShowing=false, stopping UniverseLib's cursor coroutine.
            _uiBase.RootObject.SetActive(false);
            Ready = true;
        }

        // Called from Core.OnUpdate — runs every frame regardless of UIBase state.
        // Must live here rather than in the UIBase OnUpdate callback, because that
        // callback is skipped while RootObject is inactive (deadlock otherwise).
        public static void UpdateVisibility()
        {
            if (!Ready) return;
            bool inEditor = FlyCamController.FlyCamActive && FlyCamController.CursorMode;
            if (_uiBase.RootObject.activeSelf != inEditor)
                _uiBase.RootObject.SetActive(inEditor);
        }

        // UIBase callback — only fires while RootObject is active (editor mode).
        static void OnUpdate()
        {
            _topBar?.Tick();
            _panel?.Tick();
        }

        public static void RestoreCursor()
        {
            if (!Ready) return;
            bool inEditorCursor = FlyCamController.FlyCamActive && FlyCamController.CursorMode;
            if (inEditorCursor) return;
            bool menuOpen = Menu.me != null && Menu.me.paused;
            if (menuOpen) return;
            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Locked;
        }
    }

    class DemoUIBase : UIBase
    {
        public DemoUIBase(string id, Action update) : base(id, update) { }
    }

    //  Top bar — full-width menu bar with File dropdown
    class TopBarPanel : PanelBase
    {
        internal const int BarHeight = 28;

        public override string Name => "TopBar";
        public override int MinWidth => 400;
        public override int MinHeight => BarHeight;
        public override Vector2 DefaultAnchorMin => new(0f, 1f);
        public override Vector2 DefaultAnchorMax => new(1f, 1f);
        public override bool CanDragAndResize => false;

        public override void SetDefaultSizeAndPosition()
        {
            Rect.pivot = new Vector2(0.5f, 1f);
            Rect.anchorMin = new Vector2(0f, 1f);
            Rect.anchorMax = new Vector2(1f, 1f); // full-width, top-anchored
            Rect.anchoredPosition = Vector2.zero;
            Rect.sizeDelta = new Vector2(0f, BarHeight);
        }

        GameObject _fileDropdown;
        bool _fileOpen;

        public TopBarPanel(UIBase owner) : base(owner) { }

        protected override void ConstructPanelContent()
        {
            TitleBar?.SetActive(false);

            // PanelBase already owns ContentRoot's VerticalLayoutGroup; adding a second
            // layout group type on the same object crashes in this Unity version.
            // Create a horizontal row inside ContentRoot instead.
            var barRow = UIFactory.CreateHorizontalGroup(ContentRoot, "BarRow",
                false, false, true, true, spacing: 0, padding: new Vector4(4, 2, 4, 2));
            UIFactory.SetLayoutElement(barRow, flexibleWidth: 9999, minHeight: BarHeight - 4);

            var fileBtn = UIFactory.CreateButton(barRow, "FileBtn", " File ");
            UIFactory.SetLayoutElement(fileBtn.Component.gameObject, minWidth: 54, minHeight: 22);
            fileBtn.OnClick += ToggleFile;

            _fileDropdown = BuildDropdown();
            _fileDropdown.SetActive(false);
        }

        GameObject BuildDropdown()
        {
            // Child of the canvas root so it can float outside the bar's rect.
            var go = UIFactory.CreateUIObject("FileDropdown", Owner.RootObject);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f); // canvas top-left
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(4f, -(BarHeight + 1f));
            rt.sizeDelta = new Vector2(130f, 62f);

            var bg = go.AddComponent<Image>();
            bg.color = new Color(0.13f, 0.13f, 0.16f, 0.97f);

            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(go,
                forceWidth: true, forceHeight: false,
                childControlWidth: true, childControlHeight: true,
                spacing: 1, padTop: 3, padBottom: 3, padLeft: 3, padRight: 3);

            var loadBtn = UIFactory.CreateButton(go, "LoadBtn", "Load");
            UIFactory.SetLayoutElement(loadBtn.Component.gameObject, minHeight: 26, flexibleWidth: 9999);
            loadBtn.OnClick += () => { CloseFile(); OnLoad(); };

            var saveBtn = UIFactory.CreateButton(go, "SaveBtn", "Save");
            UIFactory.SetLayoutElement(saveBtn.Component.gameObject, minHeight: 26, flexibleWidth: 9999);
            saveBtn.OnClick += () => { CloseFile(); OnSave(); };

            return go;
        }

        void ToggleFile()
        {
            _fileOpen = !_fileOpen;
            if (_fileOpen) _fileDropdown.transform.SetAsLastSibling();
            _fileDropdown.SetActive(_fileOpen);
        }

        void CloseFile()
        {
            _fileOpen = false;
            _fileDropdown.SetActive(false);
        }

        static void OnLoad() { }
        static void OnSave() { }

        public void Tick() { }
    }

    //  Prop Library Panel — full-height left sidebar, paged prop cards
    class PropLibraryPanel : PanelBase
    {
        const int SlotCount = 8;
        const int PanelWidth = 220;
        const int CardHeight = 60;
        const int PreviewSize = CardHeight;
        const int LabelFontSz = 16;
        const float ScrollPx = 36f;

        public override string Name => "Prop Library";
        public override int MinWidth => PanelWidth;
        public override int MinHeight => 200;
        public override Vector2 DefaultAnchorMin => new(0f, 1f);
        public override Vector2 DefaultAnchorMax => new(0f, 1f);
        public override bool CanDragAndResize => false;

        public override void SetDefaultSizeAndPosition()
        {
            int h = TopBarPanel.BarHeight;
            Rect.pivot = new Vector2(0f, 1f);
            Rect.anchorMin = new Vector2(0f, 0f);
            Rect.anchorMax = new Vector2(0f, 1f);
            Rect.anchoredPosition = new Vector2(0f, -h);
            Rect.sizeDelta = new Vector2(PanelWidth, -h);
        }

        struct PropCard
        {
            public GameObject Root;
            public RawImage Preview;
            public Text Label;
            public RectTransform LabelRect;
            public RectTransform LabelMask;
            public string DisplayedName;
            public bool IsDoubled;
        }

        readonly PropCard[] _cards = new PropCard[SlotCount];
        ButtonRef _prevBtn, _nextBtn;
        int _offset;
        static Texture2D _debugTex;

        // Panel keeps its own "All" view — always shows all categorized props,
        // ignoring PropPalette.SelectedCategory so it's independent of the ImGui palette.
        readonly List<PropInfo> _panelProps = new();
        int _lastAllCount = -1;

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
            UIFactory.SetLayoutElement(navRow, minHeight: 44, flexibleWidth: 9999);

            _prevBtn = UIFactory.CreateButton(navRow, "PrevBtn", "◄");
            UIFactory.SetLayoutElement(_prevBtn.Component.gameObject, minHeight: 40, flexibleWidth: 9999);
            SetButtonLabel(_prevBtn, "◄", "-");
            _prevBtn.OnClick += () => Scroll(-SlotCount);

            _nextBtn = UIFactory.CreateButton(navRow, "NextBtn", "►");
            UIFactory.SetLayoutElement(_nextBtn.Component.gameObject, minHeight: 40, flexibleWidth: 9999);
            SetButtonLabel(_nextBtn, "►", "=");
            _nextBtn.OnClick += () => Scroll(+SlotCount);
        }

        static Texture2D BuildCheckerTexture()
        {
            const int sz = 8;
            var tex = new Texture2D(sz, sz, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Repeat;
            var pixels = new Color32[sz * sz];
            var dark = new Color32(28, 28, 33, 255);
            var light = new Color32(62, 62, 72, 255);
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
            txt.text = $"{arrow}\n{key}";
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.lineSpacing = 0.8f;
        }

        PropCard BuildCard(GameObject parent, int index)
        {
            // Card: plain UIObject so the VLG controls its height cleanly.
            var cardGO = UIFactory.CreateUIObject($"Card{index}", parent);
            var cardBg = cardGO.AddComponent<Image>();
            cardBg.color = new Color(0.13f, 0.13f, 0.16f);
            UIFactory.SetLayoutElement(cardGO, minHeight: CardHeight, flexibleWidth: 9999, flexibleHeight: 0);

            // Square preview — left-anchored, fills full card height → CardHeight × CardHeight.
            // anchorMin=(0,0), anchorMax=(0,1): left edge fixed, height follows parent.
            // sizeDelta.x = PreviewSize gives the explicit pixel width.
            var previewGO = UIFactory.CreateUIObject($"Preview{index}", cardGO);
            var preview = previewGO.AddComponent<RawImage>();
            preview.texture = _debugTex;
            preview.uvRect = new Rect(0f, 0f, 4f, 4f);
            var previewRect = previewGO.GetComponent<RectTransform>();
            previewRect.anchorMin = new Vector2(0f, 0f);
            previewRect.anchorMax = new Vector2(0f, 1f);
            previewRect.pivot = new Vector2(0f, 0.5f);
            previewRect.anchoredPosition = Vector2.zero;
            previewRect.sizeDelta = new Vector2(PreviewSize, 0f);

            // Mask for the scrolling label — fills the area to the right of the preview.
            // anchorMin=(0,0), anchorMax=(1,1) + pivot=(0,0.5):
            //   offsetMin.x = anchoredPos.x = PreviewSize+gap  → left edge past preview
            //   offsetMax.x = anchoredPos.x + sizeDelta.x = 0  → right edge at parent right
            const float gap = 4f;
            var maskGO = UIFactory.CreateUIObject($"LabelMask{index}", cardGO);
            maskGO.AddComponent<RectMask2D>();
            var maskRect = maskGO.GetComponent<RectTransform>();
            maskRect.anchorMin = new Vector2(0f, 0f);
            maskRect.anchorMax = new Vector2(1f, 1f);
            maskRect.pivot = new Vector2(0f, 0.5f);
            maskRect.anchoredPosition = new Vector2(PreviewSize + gap, 0f);
            maskRect.sizeDelta = new Vector2(-(PreviewSize + gap), 0f);

            var lbl = UIFactory.CreateLabel(maskGO, $"LabelText{index}", "",
                TextAnchor.MiddleLeft, new Color(0.95f, 0.95f, 0.95f),
                supportRichText: false, fontSize: LabelFontSz);
            lbl.horizontalOverflow = HorizontalWrapMode.Overflow;
            lbl.verticalOverflow = VerticalWrapMode.Overflow;

            var lblRect = lbl.GetComponent<RectTransform>();
            lblRect.anchorMin = new Vector2(0f, 0f);
            lblRect.anchorMax = new Vector2(0f, 1f);
            lblRect.pivot = new Vector2(0f, 0.5f);
            lblRect.anchoredPosition = Vector2.zero;
            lblRect.sizeDelta = new Vector2(200f, 0f);

            return new PropCard
            {
                Root = cardGO,
                Preview = preview,
                Label = lbl,
                LabelRect = lblRect,
                LabelMask = maskRect,
            };
        }

        // Returns all categorized props regardless of PropPalette.SelectedCategory.
        // Rebuilds only when AllProps grows (catalog init) or DebugMode changes.
        IReadOnlyList<PropInfo> GetPanelProps()
        {
            var all = PropLibrary.AllProps;
            if (_lastAllCount == all.Count) return _panelProps;
            _lastAllCount = all.Count;
            _panelProps.Clear();
            if (Core.DebugMode)
            {
                foreach (var p in all) _panelProps.Add(p);
            }
            else
            {
                foreach (var p in all)
                {
                    if (PropMetadataStore.HasCategory(p.id))
                        _panelProps.Add(p);
                }
            }
            return _panelProps;
        }

        void Scroll(int delta)
        {
            var props = GetPanelProps();
            int max = Math.Max(0, props.Count - SlotCount);
            _offset = Math.Clamp(_offset + delta, 0, max);
            RefreshCards(props);
        }

        public void Tick()
        {
            PropPreviewRenderer.Update();

            if (!Core.IsKeyboardCaptured)
            {
                if (Input.GetKeyDown(KeyCode.Minus))  Scroll(-SlotCount);
                if (Input.GetKeyDown(KeyCode.Equals)) Scroll(+SlotCount);
            }

            var props = GetPanelProps();
            RefreshCards(props);

            // Load one unloaded visible prop per tick to feed the preview renderer.
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
                        {
                            card.Preview.texture = tex;
                            card.Preview.uvRect = new Rect(0f, 0f, 1f, 1f);
                        }
                        else
                        {
                            card.Preview.texture = _debugTex;
                            card.Preview.uvRect = new Rect(0f, 0f, 4f, 4f);
                        }
                    }
                }

                TickMarquee(i);
            }
        }

        void RefreshCards(IReadOnlyList<PropInfo> props)
        {
            int total = props.Count;

            if (_prevBtn != null) _prevBtn.Component.interactable = _offset > 0;
            if (_nextBtn != null) _nextBtn.Component.interactable = _offset + SlotCount < total;

            for (int i = 0; i < SlotCount; i++)
            {
                int propIdx = _offset + i;
                ref var card = ref _cards[i];
                if (card.Root == null) continue;

                bool hasProp = propIdx < total;
                card.Root.SetActive(hasProp);
                if (!hasProp) continue;

                var info = props[propIdx];
                string name = Core.DebugMode
                    ? info.displayName
                    : (PropMetadataStore.GetDisplayName(info.id) ?? info.displayName);

                if (card.DisplayedName != name)
                {
                    card.DisplayedName = name;
                    card.IsDoubled = false;
                    if (card.Label != null)
                        card.Label.text = name;
                    if (card.LabelRect != null)
                        card.LabelRect.anchoredPosition = Vector2.zero;
                }
            }
        }

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
                card.IsDoubled = true;
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
