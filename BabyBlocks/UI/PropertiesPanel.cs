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

        // Connector line (sibling of panel in canvas root)
        RectTransform _lineRT;

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
            _lineRT.anchorMin = Vector2.zero;
            _lineRT.anchorMax = Vector2.zero;
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
            RebuildMatList();
            _matDDGO.SetActive(true);
            // search bar 24px + 2px gap + scroll 180px + 8px padding ≈ 214px
            PositionDd(_matDDGO, _matBtn.Component.GetComponent<RectTransform>(), 218f);
            _matDDGO.transform.SetAsLastSibling();
        }

        void RebuildMatList()
        {
            if (_matListContent == null) return;
            for (int i = _matListContent.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_matListContent.transform.GetChild(i).gameObject);

            AddMatItem(_resetEntry);
            foreach (var m in MaterialConstructionLibrary.Entries)
            {
                if (!string.IsNullOrEmpty(_matQuery) &&
                    (m.name ?? "").IndexOf(_matQuery, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                AddMatItem(m);
            }
        }

        void AddMatItem(MaterialConstructionEntry entry)
        {
            var cap = entry;
            var btn = UIFactory.CreateButton(_matListContent, $"MI{entry.id}", entry.name ?? "");
            UIFactory.SetLayoutElement(btn.Component.gameObject, minHeight: 24, flexibleWidth: 9999);
            PropBrowserUI.ApplyButtonColors(btn);
            btn.OnClick += () =>
            {
                if (_target != null) MaterialConstructionPanel.ApplyToInstance(_target, cap);
                CloseAllDds();
                RefreshMatLabel();
            };
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

        // Position a dropdown GO so its top-left aligns with the bottom-left of the anchor button.
        void PositionDd(GameObject dd, RectTransform anchor, float height)
        {
            if (dd == null || anchor == null) return;
            var rt = dd.GetComponent<RectTransform>();
            if (rt == null) return;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot     = new Vector2(0f, 1f); // top-left pivot

            var corners = new Vector3[4];
            anchor.GetWorldCorners(corners);
            // corners[0]=BL, [3]=BR in screen/canvas pixel space (y=0 at screen bottom)
            float w = Mathf.Max(250f, corners[3].x - corners[0].x);
            rt.sizeDelta        = new Vector2(w, height);
            rt.anchoredPosition = new Vector2(corners[0].x, corners[0].y);
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
            // Right-click in scene: target a prop, or close if hitting empty space.
            // Runs regardless of whether panel is open so the user can open it from scratch.
            if (!PropBrowserUI.IsTypingInUI && Input.GetMouseButtonDown(1)
                && !PropBrowserUI.IsPointerOverPanel())
            {
                var ray = Camera.main.ScreenPointToRay(Input.mousePosition);
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
                if (found != null) ShowForProp(found);
                else               ClosePanel();
            }

            // Keep line hidden when panel is not showing (handles X-button close too).
            bool panelActive = UIRoot != null && UIRoot.activeSelf;
            if (_lineRT != null && (!panelActive || _target == null))
            {
                _lineRT.gameObject.SetActive(false);
                if (!panelActive) CloseAllDds();
            }

            if (!panelActive || _target == null) return;

            // Live filter material search text.
            if (_matOpen && _matSearch != null)
            {
                string q = _matSearch.Component.text ?? "";
                if (q != _matQuery) { _matQuery = q; RebuildMatList(); }
            }

            TickDrag();
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
            int id = _target.materialConstructionId;
            string name = id >= 0 ? (MaterialConstructionLibrary.FindById(id)?.name ?? "?") : "None";
            _matLabel.text = name;
        }

        void RefreshSurfLabel()
        {
            if (_surfLabel == null || _target == null) return;
            string tag = _target.gameObject.tag;
            _surfLabel.text = string.IsNullOrEmpty(tag) || tag == "Untagged" ? "(none)" : tag;
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

            var propScreen = cam.WorldToScreenPoint(_target.transform.position);
            if (propScreen.z < 0f) { _lineRT.gameObject.SetActive(false); return; }

            // Panel left-center in canvas/screen pixel space (y=0 at bottom).
            var corners = new Vector3[4];
            Rect.GetWorldCorners(corners);
            // corners[0]=BL, corners[1]=TL
            Vector2 panelLeft = ((Vector2)corners[0] + (Vector2)corners[1]) * 0.5f;
            Vector2 propPos   = new Vector2(propScreen.x, propScreen.y);
            Vector2 dir       = propPos - panelLeft;
            float   dist      = dir.magnitude;

            if (dist < 10f) { _lineRT.gameObject.SetActive(false); return; }

            _lineRT.gameObject.SetActive(true);
            _lineRT.anchoredPosition = panelLeft + dir * 0.5f;
            _lineRT.sizeDelta        = new Vector2(dist, 2f);
            _lineRT.localEulerAngles = new Vector3(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
        }
    }
}
