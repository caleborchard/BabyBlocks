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

        // Transform fields indexed 0-8: pos X/Y/Z, scale X/Y/Z, rot X/Y/Z
        readonly InputFieldRef[] _fields = new InputFieldRef[9];

        // Drag-to-edit state
        int        _dragId = -1;
        float      _dragStartMx, _dragStartVal;
        Vector3    _dragPos0;
        Vector3    _dragScale0;
        Quaternion _dragRot0;

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

        // Connector line (sibling of panel in canvas root)
        RectTransform _lineRT;

        // Right-click tracking: only open panel when released on the same prop as pressed.
        LevelEditorObject _rmbDownLeo;

        // Raw catalog material tracking: per-instance-ID name of last raw material applied.
        static readonly Dictionary<int, string> _rawMatNames = new();

        // Horizontal drag sensitivity per field group
        const float SensPos   = 0.05f;
        const float SensScale = 0.005f;
        const float SensRot   = 0.5f;

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
            Rect.sizeDelta        = new Vector2(300f, 470f);
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

            // ── Offsets (reserved for future use) ─────────────────────────────
            SectionHdr("Offsets (coming soon)");
        }

        void SectionHdr(string text)
        {
            var lbl = UIFactory.CreateLabel(ContentRoot, $"H_{text.Replace(" ", "_")}",
                $"─ {text}", TextAnchor.MiddleLeft, new Color(0.55f, 0.55f, 0.65f), fontSize: 12);
            UIFactory.SetLayoutElement(lbl.gameObject, minHeight: 16, flexibleWidth: 9999);
        }

        void Vec3Row(string label, int baseIdx)
        {
            var row = UIFactory.CreateHorizontalGroup(ContentRoot, $"Row{label}",
                false, false, true, true, spacing: 2);
            UIFactory.SetLayoutElement(row, minHeight: 24, flexibleWidth: 9999);

            var lbl = UIFactory.CreateLabel(row, "Lbl", label,
                TextAnchor.MiddleLeft, Color.white, fontSize: 13);
            UIFactory.SetLayoutElement(lbl.gameObject, minWidth: 64, flexibleWidth: 0);

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
                UIFactory.SetLayoutElement(al.gameObject, minWidth: 12, flexibleWidth: 0);

                var f = UIFactory.CreateInputField(row, $"F{label}{axes[i]}", "0");
                UIFactory.SetLayoutElement(f.Component.gameObject,
                    flexibleWidth: 1, minHeight: 22, minWidth: 36);
                f.Component.characterLimit = 14;
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
            Instance._target = leo;
            Instance.UIRoot.SetActive(true);
            Instance.CloseAllDds();
            Instance.RefreshAll();
        }

        public static void ClosePanel()
        {
            if (Instance == null) return;
            Instance._target = null;
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

            if (!panelActive || _target == null) return;

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
        }

        void RefreshTransform()
        {
            if (_target == null || _dragId >= 0) return;
            var t = _target.transform;
            SetF(0, t.position.x);    SetF(1, t.position.y);    SetF(2, t.position.z);
            SetF(3, t.localScale.x);  SetF(4, t.localScale.y);  SetF(5, t.localScale.z);
            var e = t.eulerAngles;
            SetF(6, e.x);             SetF(7, e.y);             SetF(8, e.z);
        }

        void SetF(int i, float v)
        {
            var f = _fields[i];
            if (f?.Component == null || f.Component.isFocused) return;
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

        void TickDrag()
        {
            if (_target == null) return;
            bool dn   = Input.GetMouseButtonDown(0);
            bool held = Input.GetMouseButton(0);
            bool up   = Input.GetMouseButtonUp(0);
            Vector2 mp = Input.mousePosition;

            // Start drag when LMB pressed over an unfocused field.
            if (_dragId < 0 && dn)
            {
                for (int i = 0; i < 9; i++)
                {
                    var f = _fields[i];
                    if (f?.Component == null || f.Component.isFocused) continue;
                    var rt = f.Component.GetComponent<RectTransform>();
                    if (rt == null) continue;
                    if (!RectTransformUtility.RectangleContainsScreenPoint(rt, mp, null)) continue;
                    _dragId       = i;
                    _dragStartMx  = mp.x;
                    _dragStartVal = GetVal(i);
                    _dragPos0     = _target.transform.position;
                    _dragScale0   = _target.transform.localScale;
                    _dragRot0     = _target.transform.rotation;
                    break;
                }
            }

            // Update during drag.
            if (_dragId >= 0 && held)
            {
                float v = _dragStartVal + (mp.x - _dragStartMx) * FieldSens(_dragId);
                SetVal(_dragId, v);
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
                    // Short click: restore original value and enter text-edit mode.
                    RestoreVec(_dragPos0, _dragScale0, _dragRot0);
                    var f = _fields[_dragId];
                    if (f?.Component != null)
                    {
                        f.Component.text = GetVal(_dragId).ToString("F3", CultureInfo.InvariantCulture);
                        f.Component.ActivateInputField();
                        f.Component.Select();
                    }
                }
                else
                {
                    LevelEditorHistory.PushTransform(_target, _dragPos0, _dragScale0, _dragRot0);
                }
                _dragId = -1;
            }

            // Confirm text-entered value on Return.
            for (int i = 0; i < 9; i++)
            {
                var f = _fields[i];
                if (f?.Component == null || !f.Component.isFocused) continue;
                if (!Input.GetKeyDown(KeyCode.Return) && !Input.GetKeyDown(KeyCode.KeypadEnter)) continue;
                var p0 = _target.transform.position;
                var s0 = _target.transform.localScale;
                var r0 = _target.transform.rotation;
                float parsed = TryParse(f.Component.text, GetVal(i));
                SetVal(i, parsed);
                LevelEditorHistory.PushTransform(_target, p0, s0, r0);
                PropBrowserUI.Deselect();
                break;
            }
        }

        float GetVal(int i)
        {
            if (_target == null) return 0f;
            var t = _target.transform;
            return i switch
            {
                0 => t.position.x,   1 => t.position.y,   2 => t.position.z,
                3 => t.localScale.x, 4 => t.localScale.y, 5 => t.localScale.z,
                6 => t.eulerAngles.x, 7 => t.eulerAngles.y, 8 => t.eulerAngles.z,
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
            }
        }

        void RestoreVec(Vector3 pos, Vector3 scale, Quaternion rot)
        {
            if (_target == null) return;
            _target.transform.position   = pos;
            _target.transform.localScale = scale;
            _target.transform.rotation   = rot;
        }

        static float FieldSens(int i) => i < 3 ? SensPos : i < 6 ? SensScale : SensRot;

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
