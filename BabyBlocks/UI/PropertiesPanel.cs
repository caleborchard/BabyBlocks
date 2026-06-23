using System;
using System.Globalization;
using Il2Cpp;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib;
using UniverseLib.UI;
using UniverseLib.UI.Models;
using UniverseLib.UI.Panels;

namespace BabyBlocks.UI
{
    class PropertiesPanel : PanelBase
    {
        public static PropertiesPanel Instance { get; private set; }

        public override string  Name             => "Properties";
        public override int     MinWidth         => 280;
        public override int     MinHeight        => 200;
        public override bool    CanDragAndResize => true;
        public override Vector2 DefaultAnchorMin => new(0.5f, 0.5f);
        public override Vector2 DefaultAnchorMax => new(0.5f, 0.5f);

        LevelEditorObject _target;

        // Sentinel entry: id = int.MinValue triggers Reset-to-Default logic in ApplyToInstance
        static readonly MaterialConstructionEntry _resetEntry = new MaterialConstructionEntry
        {
            id   = int.MinValue,
            name = "Reset to Default",
        };

        // Transform fields 0-8: pos X/Y/Z, scale X/Y/Z, rot X/Y/Z
        // Offset fields 9-14:  offsetPos X/Y/Z, offsetRot X/Y/Z  (Hat / Grabable only)
        readonly InputFieldRef[] _fields = new InputFieldRef[15];

        // Drag-to-edit state
        int        _dragId     = -1;
        int        _editId     = -1;   // field currently in text-entry mode (-1 = none)
        string     _editOriginalText = "";
        bool       _editWasFocused;    // tracks isFocused frame-over-frame to detect Enter-to-commit via focus loss
        bool       _wasHeld;           // LMB state last frame, used to synthesize 'up' (GetMouseButtonUp unreliable in IL2CPP)
        float      _dragStartMx, _dragStartVal;
        Vector3    _dragPos0;
        Vector3    _dragScale0;
        Quaternion _dragRot0;
        Vector3    _dragRot0Euler;   // eulerAngles captured at drag start (stable base for rotation drag)
        float      _lastClickTime  = -1f;
        int        _lastClickField = -1;

        // Physics dropdown
        ButtonRef  _physBtn;
        Text       _physLabel;
        GameObject _physDDGO;
        bool       _physOpen;

        // Material override searchable dropdown
        ButtonRef     _matBtn;
        Text          _matLabel;
        GameObject    _matDDGO;
        bool          _matOpen;
        InputFieldRef _matSearch;
        GameObject    _matListContent;
        string        _matQuery = "";

        // Surface type dropdown
        ButtonRef  _surfBtn;
        Text       _surfLabel;
        GameObject _surfDDGO;
        bool       _surfOpen;

        // Flag toggles
        ButtonRef _sunglassesBtn;
        Text      _sunglassesLabel;
        ButtonRef _passthroughBtn;
        Text      _passthroughLabel;

        // Offsets section (Hat / Grabable)
        HatPreviewRenderer _preview;
        GameObject         _offsetRoot;
        Text               _offsetHdrText;
        GameObject         _hatModeRow;
        ButtonRef          _hatModeBtn;
        Text               _hatModeLabel;
        bool               _hatModeIsHead = true;
        GameObject         _hairSection;
        Text               _hairLabel;
        Slider             _hairSlider;
        GameObject         _hatSunglassesRow;
        ButtonRef          _hatSunglassesBtn;
        Text               _hatSunglassesLabel;

        // Connector line (sibling of panel in canvas root)
        RectTransform _lineRT;

        // Right-click tracking: only open panel when released on the same prop as pressed.
        LevelEditorObject _rmbDownLeo;

        // Raw catalog material tracking: per-instance-ID name of last raw material applied.
        static readonly Dictionary<int, string> _rawMatNames = new();

        // Horizontal drag sensitivity per field group
        const float SensPos       = 0.05f;
        const float SensOffsetPos = 0.003f;
        const float SensScale     = 0.005f;
        const float SensRot       = 0.5f;

        // Used by PropBrowserUI.IsTypingInUI
        public bool IsMatSearchFocused => _matOpen && (_matSearch?.Component?.isFocused == true);

        public PropertiesPanel(UIBase owner) : base(owner)
        {
            Instance = this;
            UIRoot.SetActive(false);
        }

        public override void SetDefaultSizeAndPosition()
        {
            Rect.pivot            = new Vector2(0.5f, 0.5f);
            Rect.anchorMin        = new Vector2(0.5f, 0.5f);
            Rect.anchorMax        = new Vector2(0.5f, 0.5f);
            Rect.sizeDelta        = new Vector2(300f, 860f);
            Rect.anchoredPosition = new Vector2(250f, 0f);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Panel construction
        // ─────────────────────────────────────────────────────────────────────

        protected override void ConstructPanelContent()
        {
            // Connector line lives outside the panel so it can span the scene view.
            var lineGO = UIFactory.CreateUIObject("PropConnectorLine", Owner.RootObject);
            lineGO.transform.SetSiblingIndex(0);
            _lineRT = lineGO.GetComponent<RectTransform>();
            _lineRT.anchorMin = new Vector2(0.5f, 0.5f);
            _lineRT.anchorMax = new Vector2(0.5f, 0.5f);
            _lineRT.pivot     = new Vector2(0.5f, 0.5f);
            _lineRT.sizeDelta = new Vector2(100f, 2f);
            lineGO.AddComponent<Image>().color = new Color(0.85f, 0.85f, 0.95f, 0.35f);
            lineGO.SetActive(false);

            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(ContentRoot,
                forceWidth: true, forceHeight: false,
                childControlWidth: true, childControlHeight: true,
                spacing: 4, padTop: 6, padBottom: 8, padLeft: 8, padRight: 8);

            // ── Transform ─────────────────────────────────────────────────────
            SectionHdr("Transform");
            Vec3Row("Position", 0);
            Vec3Row("Scale",    3);
            Vec3Row("Rotation", 6);

            // ── Physics ───────────────────────────────────────────────────────
            SectionHdr("Physics");
            _physBtn = UIFactory.CreateButton(ContentRoot, "PhysBtn", "Static");
            UIFactory.SetLayoutElement(_physBtn.Component.gameObject, minHeight: 24, flexibleWidth: 9999);
            PropBrowserUI.ApplyButtonColors(_physBtn);
            _physLabel = _physBtn.Component.GetComponentInChildren<Text>();
            _physBtn.OnClick += TogglePhysDd;
            BuildPhysDd();

            // ── Material Override ──────────────────────────────────────────────
            SectionHdr("Material Override");
            _matBtn = UIFactory.CreateButton(ContentRoot, "MatBtn", "None");
            UIFactory.SetLayoutElement(_matBtn.Component.gameObject, minHeight: 24, flexibleWidth: 9999);
            PropBrowserUI.ApplyButtonColors(_matBtn);
            _matLabel = _matBtn.Component.GetComponentInChildren<Text>();
            _matBtn.OnClick += ToggleMatDd;
            BuildMatDd();

            // ── Surface Type ───────────────────────────────────────────────────
            SectionHdr("Surface Type");
            _surfBtn = UIFactory.CreateButton(ContentRoot, "SurfBtn", "(none)");
            UIFactory.SetLayoutElement(_surfBtn.Component.gameObject, minHeight: 24, flexibleWidth: 9999);
            PropBrowserUI.ApplyButtonColors(_surfBtn);
            _surfLabel = _surfBtn.Component.GetComponentInChildren<Text>();
            _surfBtn.OnClick += ToggleSurfDd;
            BuildSurfDd();

            // ── Flags ─────────────────────────────────────────────────────────
            SectionHdr("Flags");

            _sunglassesBtn = UIFactory.CreateButton(ContentRoot, "SunglassesBtn", "");
            UIFactory.SetLayoutElement(_sunglassesBtn.Component.gameObject, minHeight: 24, flexibleWidth: 9999);
            PropBrowserUI.ApplyButtonColors(_sunglassesBtn);
            _sunglassesLabel = _sunglassesBtn.Component.GetComponentInChildren<Text>();
            _sunglassesBtn.OnClick += ToggleSunglasses;

            _passthroughBtn = UIFactory.CreateButton(ContentRoot, "PassthroughBtn", "");
            UIFactory.SetLayoutElement(_passthroughBtn.Component.gameObject, minHeight: 24, flexibleWidth: 9999);
            PropBrowserUI.ApplyButtonColors(_passthroughBtn);
            _passthroughLabel = _passthroughBtn.Component.GetComponentInChildren<Text>();
            _passthroughBtn.OnClick += TogglePassthrough;

            // ── Offsets (Hat / Grabable) ──────────────────────────────────────
            _offsetRoot = UIFactory.CreateUIObject("OffsetSection", ContentRoot);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(_offsetRoot,
                forceWidth: true, forceHeight: false,
                childControlWidth: true, childControlHeight: true, spacing: 4);
            UIFactory.SetLayoutElement(_offsetRoot, flexibleWidth: 9999, minHeight: 0);

            var offsetHdrGO = UIFactory.CreateLabel(_offsetRoot, "OffsetHdr", "─ Hat Offsets",
                TextAnchor.MiddleLeft, new Color(0.55f, 0.55f, 0.65f), fontSize: 12);
            UIFactory.SetLayoutElement(offsetHdrGO.gameObject, minHeight: 16, flexibleWidth: 9999);
            _offsetHdrText = offsetHdrGO;

            // Head / Hand mode toggle (Hat-mode only).
            _hatModeRow = UIFactory.CreateHorizontalGroup(_offsetRoot, "HatModeRow",
                false, false, true, true, spacing: 4);
            UIFactory.SetLayoutElement(_hatModeRow, minHeight: 24, flexibleWidth: 9999);
            var modeLbl = UIFactory.CreateLabel(_hatModeRow, "ModeLbl", "Mode",
                TextAnchor.MiddleLeft, Color.white, fontSize: 13);
            UIFactory.SetLayoutElement(modeLbl.gameObject, minWidth: 64, preferredWidth: 64, flexibleWidth: 0);
            _hatModeBtn = UIFactory.CreateButton(_hatModeRow, "HatModeBtn", "Head");
            UIFactory.SetLayoutElement(_hatModeBtn.Component.gameObject, minHeight: 22, flexibleWidth: 9999);
            PropBrowserUI.ApplyButtonColors(_hatModeBtn);
            _hatModeLabel = _hatModeBtn.Component.GetComponentInChildren<Text>();
            _hatModeBtn.OnClick += () =>
            {
                _hatModeIsHead = !_hatModeIsHead;
                RefreshHatModeLabel();
                RefreshTransform();
            };

            // Act As Sunglasses toggle (Hat mode only) — direct child of _offsetRoot so it
            // stretches to the same width as Pos/Rot rows.
            _hatSunglassesBtn = UIFactory.CreateButton(_offsetRoot, "HatSunglassesBtn", "");
            UIFactory.SetLayoutElement(_hatSunglassesBtn.Component.gameObject, minHeight: 24, flexibleWidth: 9999);
            PropBrowserUI.ApplyButtonColors(_hatSunglassesBtn);
            _hatSunglassesLabel = _hatSunglassesBtn.Component.GetComponentInChildren<Text>();
            _hatSunglassesBtn.OnClick += ToggleHatSunglasses;
            _hatSunglassesRow = _hatSunglassesBtn.Component.gameObject;
            _hatSunglassesRow.SetActive(false);

            Vec3Row("Pos", 9,  _offsetRoot);
            Vec3Row("Rot", 12, _offsetRoot);

            // Hair cut section (Hat mode only).
            _hairSection = UIFactory.CreateUIObject("HairSection", _offsetRoot);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(_hairSection,
                forceWidth: true, forceHeight: false,
                childControlWidth: true, childControlHeight: true, spacing: 2);
            UIFactory.SetLayoutElement(_hairSection, flexibleWidth: 9999, minHeight: 0);

            var hairLblGO = UIFactory.CreateLabel(_hairSection, "HairLbl", "Hair cut: 0%",
                TextAnchor.MiddleLeft, Color.white, fontSize: 13);
            UIFactory.SetLayoutElement(hairLblGO.gameObject, minHeight: 18, flexibleWidth: 9999);
            _hairLabel = hairLblGO;

            var hairSliderGO = UIFactory.CreateUIObject("HairSlider", _hairSection);
            UIFactory.SetLayoutElement(hairSliderGO, minHeight: 20, flexibleWidth: 9999);
            hairSliderGO.AddComponent<Image>().color = new Color(0.2f, 0.2f, 0.25f, 1f);

            var fillArea = UIFactory.CreateUIObject("Fill Area", hairSliderGO);
            var fillAreaRT = fillArea.GetComponent<RectTransform>();
            fillAreaRT.anchorMin = new Vector2(0f, 0.2f);
            fillAreaRT.anchorMax = new Vector2(1f, 0.8f);
            fillAreaRT.offsetMin = new Vector2(5f, 0f);
            fillAreaRT.offsetMax = new Vector2(-15f, 0f);
            var fill = UIFactory.CreateUIObject("Fill", fillArea);
            fill.AddComponent<Image>().color = new Color(0.35f, 0.55f, 0.85f, 1f);
            var fillRT = fill.GetComponent<RectTransform>();
            fillRT.anchorMin = Vector2.zero; fillRT.anchorMax = Vector2.one; fillRT.sizeDelta = Vector2.zero;

            var handleArea = UIFactory.CreateUIObject("Handle Slide Area", hairSliderGO);
            var handleAreaRT = handleArea.GetComponent<RectTransform>();
            handleAreaRT.anchorMin = Vector2.zero; handleAreaRT.anchorMax = Vector2.one;
            handleAreaRT.offsetMin = new Vector2(10f, 0f); handleAreaRT.offsetMax = new Vector2(-10f, 0f);
            var handle = UIFactory.CreateUIObject("Handle", handleArea);
            var handleImg = handle.AddComponent<Image>();
            handleImg.color = new Color(0.65f, 0.75f, 0.95f, 1f);
            var handleRT = handle.GetComponent<RectTransform>();
            handleRT.anchorMin = new Vector2(0f, 0f); handleRT.anchorMax = new Vector2(0f, 1f);
            handleRT.sizeDelta = new Vector2(20f, 0f);

            _hairSlider = hairSliderGO.AddComponent<Slider>();
            _hairSlider.fillRect     = fillRT;
            _hairSlider.handleRect   = handleRT;
            _hairSlider.targetGraphic = handleImg;
            _hairSlider.direction    = Slider.Direction.LeftToRight;
            _hairSlider.minValue     = 0f;
            _hairSlider.maxValue     = 1f;
            _hairSlider.value        = 0f;

            _hairSlider.onValueChanged.AddListener((float v) =>
            {
                if (_target == null || _target.physicsMode != PhysicsMode.Hat) return;
                // Store the game-ready value (1=full hair, 0=none) so SyncHatHairAmount
                // and in-game hat.hairAmt are correct without any extra inversion.
                float gameVal = 1f - v;
                _target.hatHairAmt = gameVal;
                var hat = _target.GetComponent<Hat>()
                       ?? _target.GetComponentInChildren<Hat>(true);
                if (hat != null) hat.hairAmt = gameVal;
                else PhysicsObjectManager.SyncHatHairAmount(_target);
                _preview?.ApplyHairShader(gameVal, hat);
                if (_hairLabel != null)
                    _hairLabel.text = $"Hair: {Mathf.RoundToInt(v * 100f)}%";
            });

            _hairSection.SetActive(false);

            // Zero-height anchor — HatPreviewRenderer reads its screen position each
            // OnGUI frame to know where to draw the IMGUI preview texture.
            var previewAnchor = UIFactory.CreateUIObject("HatPreviewAnchor", _offsetRoot);
            UIFactory.SetLayoutElement(previewAnchor, minHeight: 0, flexibleWidth: 9999);
            HatPreviewRenderer.SetAnchor(previewAnchor.GetComponent<RectTransform>());

            _offsetRoot.SetActive(false);
        }

        void SectionHdr(string text)
        {
            var lbl = UIFactory.CreateLabel(ContentRoot, $"H_{text.Replace(" ", "_")}",
                $"─ {text}", TextAnchor.MiddleLeft, new Color(0.55f, 0.55f, 0.65f), fontSize: 12);
            UIFactory.SetLayoutElement(lbl.gameObject, minHeight: 16, flexibleWidth: 9999);
        }

        void Vec3Row(string label, int baseIdx, GameObject parent = null)
        {
            parent ??= ContentRoot;
            var row = UIFactory.CreateHorizontalGroup(parent, $"Row{label}",
                false, false, true, true, spacing: 2);
            UIFactory.SetLayoutElement(row, minHeight: 24, flexibleWidth: 9999);

            var lbl = UIFactory.CreateLabel(row, "Lbl", label,
                TextAnchor.MiddleLeft, Color.white, fontSize: 13);
            UIFactory.SetLayoutElement(lbl.gameObject, minWidth: 64, preferredWidth: 64, flexibleWidth: 0);

            Color[] axCol = {
                new Color(0.95f, 0.35f, 0.35f),
                new Color(0.35f, 0.92f, 0.35f),
                new Color(0.35f, 0.55f, 0.95f),
            };
            string[] axes = { "X", "Y", "Z" };
            for (int i = 0; i < 3; i++)
            {
                var al = UIFactory.CreateLabel(row, $"A{axes[i]}", axes[i],
                    TextAnchor.MiddleCenter, axCol[i], fontSize: 11);
                UIFactory.SetLayoutElement(al.gameObject, minWidth: 12, minHeight: 22, flexibleWidth: 0);

                var f = UIFactory.CreateInputField(row, $"F{label}{axes[i]}", "0");
                UIFactory.SetLayoutElement(f.Component.gameObject,
                    flexibleWidth: 1, minHeight: 22, minWidth: 44, preferredWidth: 55);
                f.Component.characterLimit = 14;
                f.Component.contentType    = InputField.ContentType.DecimalNumber;
                // Prevent content-driven resizing so all fields stay the same width.
                var csf = f.Component.GetComponent<ContentSizeFitter>();
                if (csf != null) csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                _fields[baseIdx + i] = f;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Physics dropdown
        // ─────────────────────────────────────────────────────────────────────

        void BuildPhysDd()
        {
            _physDDGO = UIFactory.CreateUIObject("PhysDD", Owner.RootObject);
            _physDDGO.AddComponent<Image>().color = new Color(0.13f, 0.13f, 0.17f, 0.97f);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(_physDDGO,
                forceWidth: true, forceHeight: false,
                childControlWidth: true, childControlHeight: true,
                spacing: 1, padTop: 3, padBottom: 3, padLeft: 3, padRight: 3);

            PhysDdItem("Static",    PhysicsMode.Static);
            PhysDdItem("Rigidbody", PhysicsMode.Rigidbody);
            PhysDdItem("Grabable",  PhysicsMode.Grabable);
            PhysDdItem("Hat",       PhysicsMode.Hat);
            _physDDGO.SetActive(false);
        }

        void PhysDdItem(string label, PhysicsMode mode)
        {
            var btn = UIFactory.CreateButton(_physDDGO, $"PI_{label}", label);
            UIFactory.SetLayoutElement(btn.Component.gameObject, minHeight: 24, flexibleWidth: 9999);
            PropBrowserUI.ApplyButtonColors(btn);
            btn.OnClick += () => { ApplyPhysicsMode(mode); CloseAllDds(); };
        }

        void TogglePhysDd()
        {
            bool want = !_physOpen;
            CloseAllDds();
            if (!want) return;
            _physOpen = true;
            _physDDGO.SetActive(true);
            // 4 items × 24px + 3 gaps × 1px + 6px padding ≈ 105px
            PositionDd(_physDDGO, _physBtn.Component.GetComponent<RectTransform>(), 108f);
            _physDDGO.transform.SetAsLastSibling();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Material override dropdown
        // ─────────────────────────────────────────────────────────────────────

        void BuildMatDd()
        {
            _matDDGO = UIFactory.CreateUIObject("MatDD", Owner.RootObject);
            _matDDGO.AddComponent<Image>().color = new Color(0.11f, 0.11f, 0.15f, 0.97f);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(_matDDGO,
                forceWidth: true, forceHeight: false,
                childControlWidth: true, childControlHeight: true,
                spacing: 2, padTop: 4, padBottom: 4, padLeft: 4, padRight: 4);

            _matSearch = UIFactory.CreateInputField(_matDDGO, "Search", "Search...");
            UIFactory.SetLayoutElement(_matSearch.Component.gameObject, minHeight: 24, flexibleWidth: 9999);
            // onValueChanged fires on every keystroke, fixing the "first character lost" issue.
            _matSearch.Component.onValueChanged.AddListener((string q) => { _matQuery = q ?? ""; RebuildMatList(); });

            var scroll = UIFactory.CreateScrollView(_matDDGO, "List",
                out _matListContent, out _, new Color(0.09f, 0.09f, 0.11f));
            UIFactory.SetLayoutElement(scroll, minHeight: 180, flexibleWidth: 9999);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(_matListContent,
                forceWidth: true, forceHeight: false,
                childControlWidth: true, childControlHeight: true,
                spacing: 1, padTop: 2, padBottom: 2, padLeft: 4, padRight: 4);

            _matDDGO.SetActive(false);
        }

        void ToggleMatDd()
        {
            bool want = !_matOpen;
            CloseAllDds();
            if (!want) return;
            _matOpen  = true;
            _matQuery = "";
            if (_matSearch != null) _matSearch.Component.text = "";
            MaterialCatalog.EnsureMaterialList();
            RebuildMatList();
            _matDDGO.SetActive(true);
            PositionDd(_matDDGO, _matBtn.Component.GetComponent<RectTransform>(), 218f);
            _matDDGO.transform.SetAsLastSibling();
            // Activate the search field immediately so the first keystroke registers.
            if (_matSearch != null) _matSearch.Component.ActivateInputField();
        }

        void RebuildMatList()
        {
            if (_matListContent == null) return;
            for (int i = _matListContent.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_matListContent.transform.GetChild(i).gameObject);

            // Reset to Default is always first.
            var resetBtn = UIFactory.CreateButton(_matListContent, "MI_Reset", "Reset to Default");
            UIFactory.SetLayoutElement(resetBtn.Component.gameObject, minHeight: 24, flexibleWidth: 9999);
            PropBrowserUI.ApplyButtonColors(resetBtn);
            resetBtn.OnClick += () => { ApplyRawMaterial(null); CloseAllDds(); };

            // Raw catalog materials — index 0 is the NoOverride sentinel, skip it.
            for (int i = 1; i < MaterialCatalog.MaterialLabels.Count; i++)
            {
                string label = MaterialCatalog.MaterialLabels[i];
                string name  = MaterialCatalog.MaterialNames[i];
                if (!string.IsNullOrEmpty(_matQuery) &&
                    label.IndexOf(_matQuery, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                string capName = name;
                var btn = UIFactory.CreateButton(_matListContent, $"MI_{i}", label);
                UIFactory.SetLayoutElement(btn.Component.gameObject, minHeight: 24, flexibleWidth: 9999);
                PropBrowserUI.ApplyButtonColors(btn);
                btn.OnClick += () => { ApplyRawMaterial(capName); CloseAllDds(); };
            }
        }

        void ApplyRawMaterial(string matName)
        {
            if (_target == null) return;
            int key = _target.gameObject.GetInstanceID();

            if (string.IsNullOrEmpty(matName))
            {
                MelonLoader.MelonLogger.Msg($"[PP Reset] key={key} matConstructId={_target.materialConstructionId} rawHasKey={_rawMatNames.ContainsKey(key)} rawVal={(_rawMatNames.TryGetValue(key, out var dbgV) ? dbgV : "<none>")}");
                _rawMatNames.Remove(key);
                MelonLoader.MelonLogger.Msg($"[PP Reset] after Remove: rawHasKey={_rawMatNames.ContainsKey(key)}");
                MaterialConstructionPanel.ApplyToInstance(_target, _resetEntry);
                MelonLoader.MelonLogger.Msg($"[PP Reset] after ApplyToInstance: matConstructId={_target.materialConstructionId} rawHasKey={_rawMatNames.ContainsKey(key)}");
                _target.materialConstructionId = -1;
                RefreshMatLabel();
                return;
            }

            var renderers = _target.GetComponentsInChildren<Renderer>(true);
            if (renderers == null || renderers.Length == 0) return;

            // Ensure MaterialConstructionPanel's cache has the TRUE originals before we overwrite
            // renderers, so that a subsequent construction drag + Reset restores prop defaults.
            MaterialConstructionPanel.EnsureOriginalsCache(_target);

            if (!MaterialCatalog.MaterialByName.TryGetValue(matName, out var mat) || mat == null) return;

            // Apply to every slot of every renderer.
            foreach (var r in renderers)
            {
                if (r == null) continue;
                int slots = r.sharedMaterials.Length;
                if (slots == 0) slots = 1;
                var mats = new Material[slots];
                for (int i = 0; i < slots; i++) mats[i] = mat;
                r.sharedMaterials = mats;
            }
            _target.materialConstructionId = -1;
            _rawMatNames[key] = matName;
            RefreshMatLabel();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Surface type dropdown
        // ─────────────────────────────────────────────────────────────────────

        void BuildSurfDd()
        {
            _surfDDGO = UIFactory.CreateUIObject("SurfDD", Owner.RootObject);
            _surfDDGO.AddComponent<Image>().color = new Color(0.13f, 0.13f, 0.17f, 0.97f);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(_surfDDGO,
                forceWidth: true, forceHeight: false,
                childControlWidth: true, childControlHeight: true,
                spacing: 0, padTop: 0, padBottom: 0, padLeft: 0, padRight: 0);

            var scroll = UIFactory.CreateScrollView(_surfDDGO, "List",
                out var surfContent, out _, new Color(0.09f, 0.09f, 0.11f));
            UIFactory.SetLayoutElement(scroll, minHeight: 200, flexibleWidth: 9999);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(surfContent,
                forceWidth: true, forceHeight: false,
                childControlWidth: true, childControlHeight: true,
                spacing: 1, padTop: 2, padBottom: 2, padLeft: 4, padRight: 4);

            foreach (var tag in PropMetadataEditor.KnownSurfaceTags)
            {
                var cap   = tag;
                string lbl = string.IsNullOrEmpty(tag) ? "(none)" : tag;
                var btn = UIFactory.CreateButton(surfContent, $"SI_{lbl}", lbl);
                UIFactory.SetLayoutElement(btn.Component.gameObject, minHeight: 24, flexibleWidth: 9999);
                PropBrowserUI.ApplyButtonColors(btn);
                btn.OnClick += () =>
                {
                    if (_target != null) PropInstanceServices.ApplySurfaceType(_target, cap);
                    CloseAllDds();
                    RefreshSurfLabel();
                };
            }

            _surfDDGO.SetActive(false);
        }

        void ToggleSurfDd()
        {
            bool want = !_surfOpen;
            CloseAllDds();
            if (!want) return;
            _surfOpen = true;
            _surfDDGO.SetActive(true);
            PositionDd(_surfDDGO, _surfBtn.Component.GetComponent<RectTransform>(), 200f);
            _surfDDGO.transform.SetAsLastSibling();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Dropdown helpers
        // ─────────────────────────────────────────────────────────────────────

        void CloseAllDds()
        {
            _physOpen = false; _physDDGO?.SetActive(false);
            _matOpen  = false; _matDDGO?.SetActive(false);
            _surfOpen = false; _surfDDGO?.SetActive(false);
        }

        void SyncOpenDdPosition()
        {
            if (_physOpen)
                PositionDd(_physDDGO, _physBtn.Component.GetComponent<RectTransform>(), 108f);
            else if (_matOpen)
                PositionDd(_matDDGO, _matBtn.Component.GetComponent<RectTransform>(), 218f);
            else if (_surfOpen)
                PositionDd(_surfDDGO, _surfBtn.Component.GetComponent<RectTransform>(), 200f);
        }

        // Position a dropdown GO so its top-left aligns with the bottom-left of the anchor button.
        // Uses TransformPoint→InverseTransformPoint instead of GetWorldCorners to avoid IL2CPP
        // array-marshaling issues that silently leave the corners array at zero.
        void PositionDd(GameObject dd, RectTransform anchor, float height)
        {
            if (dd == null || anchor == null) return;
            var rt = dd.GetComponent<RectTransform>();
            if (rt == null) return;
            var canvasRT = Owner.RootObject.GetComponent<RectTransform>();
            var ar = anchor.rect;
            // Convert button BL/BR from button-local → world → canvas-local.
            Vector2 blLocal = canvasRT.InverseTransformPoint(
                anchor.TransformPoint(new Vector3(ar.xMin, ar.yMin, 0f)));
            Vector2 brLocal = canvasRT.InverseTransformPoint(
                anchor.TransformPoint(new Vector3(ar.xMax, ar.yMin, 0f)));
            float w = Mathf.Max(250f, brLocal.x - blLocal.x);
            // anchor (0.5,0.5) = canvas center → anchoredPosition is canvas-local directly.
            rt.anchorMin        = new Vector2(0.5f, 0.5f);
            rt.anchorMax        = new Vector2(0.5f, 0.5f);
            rt.pivot            = new Vector2(0f, 1f);   // top-left: opens downward
            rt.sizeDelta        = new Vector2(w, height);
            rt.anchoredPosition = blLocal;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Public API
        // ─────────────────────────────────────────────────────────────────────

        public static void ShowForProp(LevelEditorObject leo)
        {
            if (Instance == null || leo == null) return;
            Instance.DeactivateEditField();
            Instance._lastClickField = -1;
            Instance._lastClickTime  = -1f;
            if (Instance._target != leo)
            {
                Instance._preview?.Teardown();
                Instance._preview       = null;
                Instance._hatModeIsHead = true;
                // Sync hair slider to new target (without firing the write-back listener).
                if (Instance._hairSlider != null)
                    Instance._hairSlider.SetValueWithoutNotify(1f - leo.hatHairAmt);
            }
            Instance._target = leo;
            Instance.UIRoot.SetActive(true);
            Instance.CloseAllDds();
            Instance.RefreshAll();
        }

        public static void ClosePanel()
        {
            if (Instance == null) return;
            Instance.DeactivateEditField();
            Instance._lastClickField = -1;
            Instance._lastClickTime  = -1f;
            Instance._target = null;
            Instance._preview?.Teardown();
            Instance._preview = null;
            Instance.CloseAllDds();
            Instance.UIRoot.SetActive(false);
            if (Instance._lineRT != null)
                Instance._lineRT.gameObject.SetActive(false);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Tick (called each frame from PropBrowserUI.OnUpdate)
        // ─────────────────────────────────────────────────────────────────────

        public void Tick()
        {
            // Right-click in scene: open panel only when released on the same prop as pressed.
            // Never close the panel on a right-click miss — RMB is also used for camera pan.
            if (!PropBrowserUI.IsTypingInUI && !PropBrowserUI.IsPointerOverPanel())
            {
                if (Input.GetMouseButtonDown(1))
                    _rmbDownLeo = RaycastForLeo();

                if (Input.GetMouseButtonUp(1))
                {
                    if (_rmbDownLeo != null)
                    {
                        var upLeo = RaycastForLeo();
                        if (upLeo == _rmbDownLeo)
                        {
                            LevelEditor.Select(upLeo);
                            ShowForProp(upLeo);
                        }
                    }
                    _rmbDownLeo = null;
                }
            }

            // Keep line hidden when panel is not showing (handles X-button close too).
            bool panelActive = UIRoot != null && UIRoot.activeSelf;
            if (_lineRT != null && (!panelActive || _target == null))
            {
                _lineRT.gameObject.SetActive(false);
                if (!panelActive) CloseAllDds();
            }

            // Always sync to current selection — makes disconnection impossible.
            // Guard removed so even a panel with a null/stale target re-syncs on next select.
            if (panelActive)
            {
                var sel = LevelEditor.selectedObject;
                if (sel != null && sel != _target)
                    ShowForProp(sel);
            }

            if (!panelActive) return;
            if (_target == null)
            {
                ClosePanel();
                return;
            }

            // Live filter material search text.
            if (_matOpen && _matSearch != null)
            {
                string q = _matSearch.Component.text ?? "";
                if (q != _matQuery) { _matQuery = q; RebuildMatList(); }
            }

            TickDrag();
            SyncOpenDdPosition();
            // Keep all fields in sync with live prop state every frame.
            RefreshTransform();
            RefreshPhysLabel();
            RefreshMatLabel();
            RefreshSurfLabel();
            RefreshFlagLabels();
            TickOffsets();
            UpdateLine();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Label refresh
        // ─────────────────────────────────────────────────────────────────────

        void RefreshAll()
        {
            _dragId = -1;
            RefreshTransform();
            RefreshPhysLabel();
            RefreshMatLabel();
            RefreshSurfLabel();
            RefreshFlagLabels();
            RefreshHatModeLabel();
            TickOffsets();
        }

        void RefreshHatModeLabel()
        {
            if (_hatModeLabel != null)
                _hatModeLabel.text = _hatModeIsHead ? "Head → Hand" : "Hand → Head";
        }

        void TickOffsets()
        {
            if (_target == null || _offsetRoot == null) return;

            var mode = _target.physicsMode;
            bool showOffsets = mode == PhysicsMode.Hat || mode == PhysicsMode.Grabable;

            // Apply all visibility changes first so the layout query below sees the final state.
            if (_offsetRoot.activeSelf != showOffsets)
                _offsetRoot.SetActive(showOffsets);
            if (_offsetHdrText != null)
                _offsetHdrText.text = mode == PhysicsMode.Hat ? "─ Hat Offsets" : "─ Grab Offsets";
            if (_hatModeRow != null)
                _hatModeRow.SetActive(showOffsets && mode == PhysicsMode.Hat);
            if (_hairSection != null)
                _hairSection.SetActive(showOffsets && mode == PhysicsMode.Hat);
            if (_hatSunglassesRow != null)
                _hatSunglassesRow.SetActive(showOffsets && mode == PhysicsMode.Hat);
            if (showOffsets && mode == PhysicsMode.Hat)
                RefreshHatSunglassesLabel();

            // Size the panel to exactly fit its visible content.
            // LayoutUtility.GetPreferredHeight traverses the hierarchy without triggering
            // a full rebuild, so it reflects the SetActive calls above immediately.
            var contentRT = ContentRoot.GetComponent<RectTransform>();
            float contentH    = UnityEngine.UI.LayoutUtility.GetPreferredHeight(contentRT);
            float previewExtra = showOffsets ? HatPreviewRenderer.PreviewSize + 8f : 0f;
            float targetH     = Mathf.Max(contentH + 4f + previewExtra, (float)MinHeight);
            if (!Mathf.Approximately(Rect.rect.height, targetH))
            {
                Rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, targetH);
                Dragger.OnEndResize();
            }

            if (!showOffsets)
            {
                if (_preview != null) { _preview.Teardown(); _preview = null; }
                return;
            }

            // Keep label current and sync slider to external changes (undo, level load).
            if (mode == PhysicsMode.Hat && _hairSlider != null && _hairLabel != null)
            {
                float propVal = _target.hatHairAmt;  // game value: 1=full hair, 0=none
                float sliderPos = 1f - propVal;
                if (!Mathf.Approximately(_hairSlider.value, sliderPos))
                {
                    _hairSlider.SetValueWithoutNotify(sliderPos);
                    var hat = _target.GetComponent<Hat>() ?? _target.GetComponentInChildren<Hat>(true);
                    _preview?.ApplyHairShader(propVal, hat);
                }
                _hairLabel.text = $"Hair: {Mathf.RoundToInt(sliderPos * 100f)}%";
            }

            // Grabable always shows hand-mode preview; Hat respects _hatModeIsHead.
            bool wantPreview   = mode == PhysicsMode.Hat || mode == PhysicsMode.Grabable;
            bool previewHeadMode = mode == PhysicsMode.Hat && _hatModeIsHead;
            if (wantPreview)
            {
                if (_preview == null || !_preview.IsReady)
                {
                    _preview = new HatPreviewRenderer();
                    _preview.Setup(_target, previewHeadMode);
                }
                else
                {
                    _preview.SyncPropFromTarget(_target, previewHeadMode);
                    _preview.UpdateCameraPosition();
                }
            }
            else if (_preview != null)
            {
                _preview.Teardown();
                _preview = null;
            }
        }

        void RefreshTransform()
        {
            if (_target == null || _dragId >= 0) return;
            var t = _target.transform;
            SetF(0, t.position.x);    SetF(1, t.position.y);    SetF(2, t.position.z);
            SetF(3, t.localScale.x);  SetF(4, t.localScale.y);  SetF(5, t.localScale.z);
            var e = t.eulerAngles;
            SetF(6, e.x);             SetF(7, e.y);             SetF(8, e.z);

            if (_offsetRoot != null && _offsetRoot.activeSelf)
            {
                bool useHat = _target.physicsMode == PhysicsMode.Hat && _hatModeIsHead;
                var op  = useHat ? _target.hatOffsetPos : _target.grabOffsetPos;
                var or_ = useHat ? _target.hatOffsetRot : _target.grabOffsetRot;
                SetF(9, op.x);  SetF(10, op.y); SetF(11, op.z);
                SetF(12, or_.x); SetF(13, or_.y); SetF(14, or_.z);
            }
        }

        void SetF(int i, float v)
        {
            var f = _fields[i];
            if (f?.Component == null || i == _editId) return;
            f.Component.text = v.ToString("F3", CultureInfo.InvariantCulture);
        }

        void RefreshPhysLabel()
        {
            if (_physLabel == null || _target == null) return;
            _physLabel.text = _target.physicsMode switch
            {
                PhysicsMode.Rigidbody => "Rigidbody",
                PhysicsMode.Grabable  => "Grabable",
                PhysicsMode.Hat       => "Hat",
                _                     => "Static",
            };
        }

        void RefreshMatLabel()
        {
            if (_matLabel == null || _target == null) return;
            int id  = _target.materialConstructionId;
            int key = _target.gameObject.GetInstanceID();
            if (id >= 0)
            {
                // A construction entry was applied — show the underlying raw material name.
                _rawMatNames.Remove(key);
                var entry = MaterialConstructionLibrary.FindById(id);
                _matLabel.text = entry != null && !string.IsNullOrEmpty(entry.materialName)
                    ? entry.materialName
                    : "?";
                return;
            }
            // id < 0: check whether the raw override we tracked is still actually on the prop.
            // If something external (undo, another tool) changed the material, _rawMatNames
            // would be stale — validate against the first renderer's shared material.
            if (_rawMatNames.TryGetValue(key, out var raw) && raw != null)
            {
                bool stillApplied = false;
                if (MaterialCatalog.MaterialByName.TryGetValue(raw, out var trackedMat) && trackedMat != null)
                {
                    var r = _target.GetComponentInChildren<Renderer>();
                    var sm = r?.sharedMaterial;
                    stillApplied = sm != null && sm.GetInstanceID() == trackedMat.GetInstanceID();
                }
                if (!stillApplied)
                    _rawMatNames.Remove(key);
            }
            _matLabel.text = _rawMatNames.TryGetValue(key, out var name) && name != null ? name : "None";
        }

        void RefreshSurfLabel()
        {
            if (_surfLabel == null || _target == null) return;
            string tag = _target.gameObject.tag;
            _surfLabel.text = string.IsNullOrEmpty(tag) || tag == "Untagged" ? "(none)" : tag;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Flag toggles
        // ─────────────────────────────────────────────────────────────────────

        void ToggleHatSunglasses()
        {
            if (_target == null || _target.physicsMode != PhysicsMode.Hat) return;
            var hat = _target.GetComponent<Hat>() ?? _target.GetComponentInChildren<Hat>(true);
            if (hat == null) return;
            hat.isSunglasses = !hat.isSunglasses;
            RefreshHatSunglassesLabel(hat);
        }

        void RefreshHatSunglassesLabel(Hat hat = null)
        {
            if (_hatSunglassesLabel == null) return;
            if (hat == null && _target != null)
                hat = _target.GetComponent<Hat>() ?? _target.GetComponentInChildren<Hat>(true);
            bool on = hat != null && hat.isSunglasses;
            _hatSunglassesLabel.text = on ? "Act As Sunglasses: On → Off" : "Act As Sunglasses: Off → On";
        }

        void ToggleSunglasses()
        {
            if (_target == null) return;
            bool newVal = !_target.sunglassesNeeded;
            _target.sunglassesNeeded = newVal;
            var existing = _target.GetComponent<BbSunglassesChecker>();
            if (newVal && existing == null)
                _target.gameObject.AddComponent<BbSunglassesChecker>();
            else if (!newVal && existing != null)
                UnityEngine.Object.DestroyImmediate(existing);
            RefreshFlagLabels();
        }

        void TogglePassthrough()
        {
            if (_target == null) return;
            bool newVal = !_target.playerPassthrough;
            _target.playerPassthrough = newVal;
            PropInstanceServices.SetBushPassthrough(_target.gameObject, newVal);
            RefreshFlagLabels();
        }

        void RefreshFlagLabels()
        {
            if (_target == null) return;
            // Drive sunglassesNeeded from actual component presence: construction entries also
            // add/remove BbSunglassesChecker, so the component is the source of truth.
            bool hasSunglasses = _target.GetComponent<BbSunglassesChecker>() != null;
            _target.sunglassesNeeded = hasSunglasses;
            if (_sunglassesLabel != null)
                _sunglassesLabel.text = (hasSunglasses ? "[ON]  " : "[OFF] ") + "Sunglasses Invisible";
            if (_passthroughLabel != null)
                _passthroughLabel.text = (_target.playerPassthrough ? "[ON]  " : "[OFF] ") + "No Player Collision";
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Physics apply
        // ─────────────────────────────────────────────────────────────────────

        void ApplyPhysicsMode(PhysicsMode mode)
        {
            if (_target == null) return;
            // Select target so SetPhysicsMode operates on it.
            LevelEditor.Select(_target);
            LevelEditor.SetPhysicsMode(mode);
            RefreshPhysLabel();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Drag-to-edit transform fields
        // ─────────────────────────────────────────────────────────────────────

        void CommitEditField()
        {
            if (_editId < 0) return;
            var ef = _fields[_editId];
            if (ef?.Component != null)
            {
                var p0 = _target.transform.position;
                var s0 = _target.transform.localScale;
                var r0 = _target.transform.rotation;
                float parsed = TryParse(ef.Component.text, GetVal(_editId));
                SetVal(_editId, parsed);
                if (_editId < 3)
                    LevelEditorManager.Instance?.SyncLoopBase(_target);
                if (_editId < 9)
                {
                    LevelEditorHistory.PushTransform(_target, p0, s0, r0);
                    if (_target.netId != 0)
                        Networking.ModNetworking.SendPropTransform(_target.netId,
                            _target.transform.position, _target.transform.rotation,
                            _target.transform.localScale, reliable: true);
                }
            }
            DeactivateEditField();
        }

        void DeactivateEditField()
        {
            if (_editId < 0) return;
            var ef = _fields[_editId];
            if (ef?.Component != null)
            {
                ef.Component.DeactivateInputField();
                ef.Component.text = GetVal(_editId).ToString("F3", CultureInfo.InvariantCulture);
            }
            _editId = -1;
            _editWasFocused = false;
        }

        void TickDrag()
        {
            if (_target == null) return;
            bool dn   = Input.GetMouseButtonDown(0);
            bool held = Input.GetMouseButton(0);
            bool up   = !held && _wasHeld; // synthesized — GetMouseButtonUp(0) is unreliable in IL2CPP
            _wasHeld  = held;
            Vector2 mp = Input.mousePosition;

            // While in text-edit mode: let native InputField handle typing.
            // Commit on click-outside or focus loss (Enter); restore on click-outside without commit.
            if (_editId >= 0)
            {
                var ef = _fields[_editId];
                bool isFocused = ef?.Component?.isFocused ?? false;

                if (dn)
                {
                    var ert = ef?.Component?.GetComponent<RectTransform>();
                    bool clickedEditField = ert != null
                        && RectTransformUtility.RectangleContainsScreenPoint(ert, mp, null);
                    if (!clickedEditField)
                        CommitEditField();
                }
                else if (_editWasFocused && !isFocused)
                {
                    CommitEditField();
                }

                _editWasFocused = isFocused;
            }

            if (dn)
            {
                bool inPanel = Rect != null && RectTransformUtility.RectangleContainsScreenPoint(Rect, mp, null);
                if (!inPanel)
                {
                    _lastClickField = -1;
                    _lastClickTime  = -1f;
                }
            }

            // Recover from a stuck drag (mouse released outside the window — up event was missed).
            if (_dragId >= 0 && !held && !up)
            {
                float stuckMoved = mp.x - _dragStartMx;
                if (Mathf.Abs(stuckMoved) >= 3f)
                {
                    LevelEditorHistory.PushTransform(_target, _dragPos0, _dragScale0, _dragRot0);
                    if (_target.netId != 0)
                        Networking.ModNetworking.SendPropTransform(_target.netId,
                            _target.transform.position, _target.transform.rotation,
                            _target.transform.localScale, reliable: true);
                }
                else
                    RestoreVec(_dragPos0, _dragScale0, _dragRot0);
                _dragId         = -1;
                _lastClickField = -1;
            }

            // Start drag: only when not editing, and only if a field was physically clicked.
            if (_dragId < 0 && dn && _editId < 0)
            {
                int clickedField = -1;
                int fieldCount = _offsetRoot != null && _offsetRoot.activeSelf ? 15 : 9;
                for (int i = 0; i < fieldCount; i++)
                {
                    var f = _fields[i];
                    if (f?.Component == null) continue;
                    var rt = f.Component.GetComponent<RectTransform>();
                    if (rt == null) continue;
                    bool hit = RectTransformUtility.RectangleContainsScreenPoint(rt, mp, null);
                    if (!hit) continue;
                    clickedField = i;
                    break;
                }
                if (clickedField >= 0)
                {
                    _dragId        = clickedField;
                    _dragStartMx   = mp.x;
                    _dragStartVal  = GetVal(clickedField);
                    _dragPos0      = _target.transform.position;
                    _dragScale0    = _target.transform.localScale;
                    _dragRot0      = _target.transform.rotation;
                    _dragRot0Euler = _target.transform.eulerAngles;
                }
            }

            // Update during drag.
            if (_dragId >= 0 && held)
            {
                float v = _dragStartVal + (mp.x - _dragStartMx) * FieldSens(_dragId);
                if (_dragId >= 6 && _dragId < 9)
                {
                    // Transform rotation: apply delta on top of captured start euler to avoid
                    // gimbal-lock flip when reading t.eulerAngles mid-drag.
                    var e = _dragRot0Euler;
                    if (_dragId == 6) e.x = v;
                    else if (_dragId == 7) e.y = v;
                    else e.z = v;
                    _target.transform.eulerAngles = e;
                }
                else
                {
                    SetVal(_dragId, v);
                    if (_dragId < 3)
                        LevelEditorManager.Instance?.SyncLoopBase(_target);
                }
                var f = _fields[_dragId];
                if (f?.Component != null)
                    f.Component.text = v.ToString("F3", CultureInfo.InvariantCulture);
            }

            // End drag.
            if (_dragId >= 0 && up)
            {
                float moved = mp.x - _dragStartMx;
                if (Mathf.Abs(moved) < 3f)
                {
                    if (_dragId < 9) RestoreVec(_dragPos0, _dragScale0, _dragRot0);
                    bool isDouble = _lastClickField == _dragId
                                 && Time.unscaledTime - _lastClickTime < 0.5f;
                    if (isDouble)
                    {
                        _editId = _dragId;
                        _editOriginalText = GetVal(_editId).ToString("F3", CultureInfo.InvariantCulture);
                        _editWasFocused = false;
                        var f = _fields[_editId];
                        if (f?.Component != null)
                        {
                            f.Component.text = _editOriginalText;
                            f.Component.ActivateInputField();
                            f.Component.Select();
                        }
                        _lastClickField = -1;
                        _lastClickTime  = -1f;
                    }
                    else
                    {
                        _lastClickField = _dragId;
                        _lastClickTime  = Time.unscaledTime;
                    }
                }
                else
                {
                    if (_dragId < 9)
                    {
                        LevelEditorHistory.PushTransform(_target, _dragPos0, _dragScale0, _dragRot0);
                        if (_target.netId != 0)
                            Networking.ModNetworking.SendPropTransform(_target.netId,
                                _target.transform.position, _target.transform.rotation,
                                _target.transform.localScale, reliable: true);
                    }
                    _lastClickField = -1;
                }
                _dragId = -1;
            }

            // Kill focus on all fields when not in edit mode so single-click/drag doesn't show a cursor.
            if (_editId < 0)
            {
                foreach (var f in _fields)
                {
                    if (f?.Component != null && f.Component.isFocused)
                        f.Component.DeactivateInputField();
                }
            }
        }

        float GetVal(int i)
        {
            if (_target == null) return 0f;
            var t = _target.transform;
            // Use hat offsets when in Hat mode AND head mode; grab offsets otherwise.
            bool useHat = _target.physicsMode == PhysicsMode.Hat && _hatModeIsHead;
            return i switch
            {
                0 => t.position.x,    1 => t.position.y,    2 => t.position.z,
                3 => t.localScale.x,  4 => t.localScale.y,  5 => t.localScale.z,
                6 => t.eulerAngles.x, 7 => t.eulerAngles.y, 8 => t.eulerAngles.z,
                9  => useHat ? _target.hatOffsetPos.x : _target.grabOffsetPos.x,
                10 => useHat ? _target.hatOffsetPos.y : _target.grabOffsetPos.y,
                11 => useHat ? _target.hatOffsetPos.z : _target.grabOffsetPos.z,
                12 => useHat ? _target.hatOffsetRot.x : _target.grabOffsetRot.x,
                13 => useHat ? _target.hatOffsetRot.y : _target.grabOffsetRot.y,
                14 => useHat ? _target.hatOffsetRot.z : _target.grabOffsetRot.z,
                _ => 0f,
            };
        }

        void SetVal(int i, float v)
        {
            if (_target == null) return;
            var t = _target.transform;
            switch (i)
            {
                case 0: t.position = new Vector3(v, t.position.y, t.position.z);   break;
                case 1: t.position = new Vector3(t.position.x, v, t.position.z);   break;
                case 2: t.position = new Vector3(t.position.x, t.position.y, v);   break;
                case 3: t.localScale = new Vector3(v, t.localScale.y, t.localScale.z); break;
                case 4: t.localScale = new Vector3(t.localScale.x, v, t.localScale.z); break;
                case 5: t.localScale = new Vector3(t.localScale.x, t.localScale.y, v); break;
                case 6: { var e = t.eulerAngles; t.eulerAngles = new Vector3(v, e.y, e.z);  } break;
                case 7: { var e = t.eulerAngles; t.eulerAngles = new Vector3(e.x, v, e.z);  } break;
                case 8: { var e = t.eulerAngles; t.eulerAngles = new Vector3(e.x, e.y, v);  } break;

                // Offset position (9-11)
                case 9:  SetOffsetPos(v, 0); break;
                case 10: SetOffsetPos(v, 1); break;
                case 11: SetOffsetPos(v, 2); break;
                // Offset rotation (12-14)
                case 12: SetOffsetRot(v, 0); break;
                case 13: SetOffsetRot(v, 1); break;
                case 14: SetOffsetRot(v, 2); break;
            }
        }

        void SetOffsetPos(float v, int axis)
        {
            bool useHat = _target.physicsMode == PhysicsMode.Hat && _hatModeIsHead;
            var p = useHat ? _target.hatOffsetPos : _target.grabOffsetPos;
            if (axis == 0) p.x = v; else if (axis == 1) p.y = v; else p.z = v;
            if (useHat)
            {
                _target.hatOffsetPos = p;
                LevelEditor.SetHatOffset(_target.hatOffsetPos, _target.hatOffsetRot);
                _preview?.ApplyHatOffset(_target, headMode: true);
            }
            else
            {
                _target.grabOffsetPos = p;
                // LevelEditor.SetGrabOffset skips Hat-mode props; call SyncGrabOffset directly.
                PhysicsObjectManager.SyncGrabOffset(_target);
                if (_target.physicsMode == PhysicsMode.Grabable)
                    LevelEditor.SetGrabOffset(_target.grabOffsetPos, _target.grabOffsetRot);
                _preview?.ApplyHatOffset(_target, headMode: false);
            }
        }

        void SetOffsetRot(float v, int axis)
        {
            bool useHat = _target.physicsMode == PhysicsMode.Hat && _hatModeIsHead;
            var r = useHat ? _target.hatOffsetRot : _target.grabOffsetRot;
            if (axis == 0) r.x = v; else if (axis == 1) r.y = v; else r.z = v;
            if (useHat)
            {
                _target.hatOffsetRot = r;
                LevelEditor.SetHatOffset(_target.hatOffsetPos, _target.hatOffsetRot);
                _preview?.ApplyHatOffset(_target, headMode: true);
            }
            else
            {
                _target.grabOffsetRot = r;
                PhysicsObjectManager.SyncGrabOffset(_target);
                if (_target.physicsMode == PhysicsMode.Grabable)
                    LevelEditor.SetGrabOffset(_target.grabOffsetPos, _target.grabOffsetRot);
                _preview?.ApplyHatOffset(_target, headMode: false);
            }
        }

        void RestoreVec(Vector3 pos, Vector3 scale, Quaternion rot)
        {
            if (_target == null) return;
            _target.transform.position   = pos;
            _target.transform.localScale = scale;
            _target.transform.rotation   = rot;
        }

        static float FieldSens(int i) =>
            i < 3  ? SensPos :
            i < 6  ? SensScale :
            i < 9  ? SensRot :
            i < 12 ? SensOffsetPos :
                     SensRot;

        static float TryParse(string s, float fallback) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        // ─────────────────────────────────────────────────────────────────────
        //  Connector line
        // ─────────────────────────────────────────────────────────────────────

        void UpdateLine()
        {
            if (_lineRT == null || _target == null) { if (_lineRT != null) _lineRT.gameObject.SetActive(false); return; }

            var cam = Camera.main;
            if (cam == null) { _lineRT.gameObject.SetActive(false); return; }

            var propScreen3D = cam.WorldToScreenPoint(GetPropWorldCenter(_target));
            if (propScreen3D.z < 0f) { _lineRT.gameObject.SetActive(false); return; }

            var canvasRT = Owner.RootObject.GetComponent<RectTransform>();

            // Prop position: 3D world → screen pixels → canvas-local.
            Vector2 propLocal;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                canvasRT, new Vector2(propScreen3D.x, propScreen3D.y), null, out propLocal);

            // Panel corners: panel-local → world → canvas-local (avoids GetWorldCorners array issue).
            var pr = Rect.rect;
            Vector2 blL = canvasRT.InverseTransformPoint(Rect.TransformPoint(new Vector3(pr.xMin, pr.yMin, 0f)));
            Vector2 brL = canvasRT.InverseTransformPoint(Rect.TransformPoint(new Vector3(pr.xMax, pr.yMin, 0f)));
            Vector2 tlL = canvasRT.InverseTransformPoint(Rect.TransformPoint(new Vector3(pr.xMin, pr.yMax, 0f)));
            Vector2 trL = canvasRT.InverseTransformPoint(Rect.TransformPoint(new Vector3(pr.xMax, pr.yMax, 0f)));

            float xMin = Mathf.Min(blL.x, brL.x, tlL.x, trL.x);
            float xMax = Mathf.Max(blL.x, brL.x, tlL.x, trL.x);
            float yMin = Mathf.Min(blL.y, brL.y, tlL.y, trL.y);
            float yMax = Mathf.Max(blL.y, brL.y, tlL.y, trL.y);

            // Closest point on the full panel perimeter to the prop.
            Vector2 panelEdge = ClosestPointOnRect(xMin, xMax, yMin, yMax, propLocal);
            Vector2 dir       = propLocal - panelEdge;
            float   dist      = dir.magnitude;

            if (dist < 10f) { _lineRT.gameObject.SetActive(false); return; }

            // _lineRT anchor (0.5, 0.5) → anchoredPosition = canvas-local offset from canvas center.
            _lineRT.gameObject.SetActive(true);
            _lineRT.anchoredPosition = (panelEdge + propLocal) * 0.5f;
            _lineRT.sizeDelta        = new Vector2(dist, 2f);
            _lineRT.localEulerAngles = new Vector3(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
        }

        static Vector2 ClosestPointOnRect(float xMin, float xMax, float yMin, float yMax, Vector2 p)
        {
            float cx = Mathf.Clamp(p.x, xMin, xMax);
            float cy = Mathf.Clamp(p.y, yMin, yMax);
            if (p.x < xMin || p.x > xMax || p.y < yMin || p.y > yMax)
                return new Vector2(cx, cy);
            // Point is inside the rect — project to nearest edge.
            float dL = p.x - xMin, dR = xMax - p.x, dB = p.y - yMin, dT = yMax - p.y;
            float m = Mathf.Min(dL, dR, dB, dT);
            if (m == dL) return new Vector2(xMin, p.y);
            if (m == dR) return new Vector2(xMax, p.y);
            if (m == dB) return new Vector2(p.x, yMin);
            return                 new Vector2(p.x, yMax);
        }

        // Returns the visual center of a prop (average of enabled renderer bounds centers).
        static Vector3 GetPropWorldCenter(LevelEditorObject leo)
        {
            var renderers = leo.GetComponentsInChildren<Renderer>();
            if (renderers == null || renderers.Length == 0) return leo.transform.position;
            var  sum   = Vector3.zero;
            int  count = 0;
            foreach (var r in renderers)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                sum += r.bounds.center;
                count++;
            }
            return count > 0 ? sum / count : leo.transform.position;
        }

        // Returns the nearest LevelEditorObject under the cursor, or null.
        static LevelEditorObject RaycastForLeo()
        {
            var cam = Camera.main;
            if (cam == null) return null;
            var ray  = cam.ScreenPointToRay(Input.mousePosition);
            LevelEditorObject found = null;
            float best = float.MaxValue;
            foreach (var h in Physics.RaycastAll(ray, 2000f, ~GizmoRenderer.Mask,
                                                  QueryTriggerInteraction.Collide))
            {
                if (h.distance >= best) continue;
                var leo = h.collider.GetComponent<LevelEditorObject>()
                       ?? h.collider.GetComponentInParent<LevelEditorObject>();
                if (leo == null) continue;
                best  = h.distance;
                found = leo;
            }
            return found;
        }
    }
}
