using System;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UniverseLib.UI.Models;
using UniverseLib.UI.Panels;

namespace BabyBlocks.UI
{
    class FileBrowserPanel : PanelBase
    {
        static readonly string LevelsDir =
            Path.Combine(MelonEnvironment.UserDataDirectory, "BabyBlocks", "levels");

        public static FileBrowserPanel Instance { get; private set; }

        public override string  Name             => "Level Files";
        public override int     MinWidth         => 360;
        public override int     MinHeight        => 220;
        public override bool    CanDragAndResize => true;
        public override Vector2 DefaultAnchorMin => new(0.5f, 0.5f);
        public override Vector2 DefaultAnchorMax => new(0.5f, 0.5f);

        InputFieldRef _nameInput;
        GameObject    _fileListContent;
        Text          _statusText;
        float         _statusTime;

        // Two-step confirmation state (shared between load and delete)
        string _confirmPath;
        bool   _confirmDelete; // true = delete confirm, false = load confirm
        float  _confirmTime;

        const float ConfirmTimeout = 3f;
        const float StatusDuration = 5f;

        public FileBrowserPanel(UIBase owner) : base(owner)
        {
            Instance = this;
            UIRoot.SetActive(false);
        }

        // ---- Public API ----

        public static void OpenPanel()
        {
            if (Instance == null) return;
            Instance._confirmPath = null;
            Instance.Refresh();
            Instance.UIRoot.SetActive(true);
        }

        public static void ClosePanel()
        {
            if (Instance == null) return;
            Instance.UIRoot.SetActive(false);
        }

        // Called from PropBrowserUI.OnUpdate every frame while panel is open.
        public void Tick()
        {
            if (UIRoot == null || !UIRoot.activeSelf) return;

            // Expire pending confirmation after timeout
            if (_confirmPath != null && Time.realtimeSinceStartup - _confirmTime > ConfirmTimeout)
            {
                _confirmPath = null;
                RebuildFileList();
            }

            // Clear stale status text
            if (_statusText != null && !string.IsNullOrEmpty(_statusText.text)
                && Time.realtimeSinceStartup - _statusTime >= StatusDuration)
                _statusText.text = "";
        }

        // ---- Panel construction ----

        public override void SetDefaultSizeAndPosition()
        {
            Rect.pivot            = new Vector2(0.5f, 0.5f);
            Rect.anchorMin        = new Vector2(0.5f, 0.5f);
            Rect.anchorMax        = new Vector2(0.5f, 0.5f);
            Rect.sizeDelta        = new Vector2(400f, 520f);
            Rect.anchoredPosition = Vector2.zero;
        }

        protected override void ConstructPanelContent()
        {
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(ContentRoot,
                forceWidth: true, forceHeight: false,
                childControlWidth: true, childControlHeight: true,
                spacing: 4, padTop: 6, padBottom: 6, padLeft: 8, padRight: 8);

            // ── Save row ────────────────────────────────────────────────────
            var saveRow = UIFactory.CreateHorizontalGroup(ContentRoot, "SaveRow",
                false, false, true, true, spacing: 6);
            UIFactory.SetLayoutElement(saveRow, minHeight: 26, flexibleWidth: 9999);

            var nameLbl = UIFactory.CreateLabel(saveRow, "NameLbl", "Save as:",
                TextAnchor.MiddleLeft, fontSize: 14);
            UIFactory.SetLayoutElement(nameLbl.gameObject, minWidth: 62, flexibleWidth: 0);

            _nameInput = UIFactory.CreateInputField(saveRow, "NameInput", "filename");
            UIFactory.SetLayoutElement(_nameInput.Component.gameObject, flexibleWidth: 9999, minHeight: 22);
            _nameInput.Component.characterLimit = 128;

            var extLbl = UIFactory.CreateLabel(saveRow, "ExtLbl", ".bbb",
                TextAnchor.MiddleLeft, new Color(0.45f, 0.45f, 0.45f), fontSize: 13);
            UIFactory.SetLayoutElement(extLbl.gameObject, minWidth: 32, flexibleWidth: 0);

            var saveBtn = UIFactory.CreateButton(saveRow, "SaveBtn", "Save");
            UIFactory.SetLayoutElement(saveBtn.Component.gameObject, minWidth: 56, minHeight: 22);
            PropBrowserUI.ApplyButtonColors(saveBtn);
            saveBtn.OnClick += OnSaveClick;

            // ── Status label ─────────────────────────────────────────────────
            _statusText = UIFactory.CreateLabel(ContentRoot, "Status", "",
                TextAnchor.MiddleLeft, new Color(1f, 1f, 0.4f), fontSize: 13);
            UIFactory.SetLayoutElement(_statusText.gameObject, minHeight: 18, flexibleWidth: 9999);

            // ── Divider ───────────────────────────────────────────────────────
            var divLbl = UIFactory.CreateLabel(ContentRoot, "DivLbl", "── existing files ──",
                TextAnchor.MiddleCenter, new Color(0.38f, 0.38f, 0.38f), fontSize: 12);
            UIFactory.SetLayoutElement(divLbl.gameObject, minHeight: 16, flexibleWidth: 9999);

            // ── Scrollable file list ──────────────────────────────────────────
            var scrollView = UIFactory.CreateScrollView(ContentRoot, "FileList",
                out _fileListContent, out _, new Color(0.09f, 0.09f, 0.11f));
            UIFactory.SetLayoutElement(scrollView, flexibleHeight: 9999, flexibleWidth: 9999, minHeight: 80);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(_fileListContent,
                forceWidth: true, forceHeight: false,
                childControlWidth: true, childControlHeight: true,
                spacing: 2, padTop: 2, padBottom: 2, padLeft: 4, padRight: 4);
        }

        // ── File list ───────────────────────────────────────────────────────

        void Refresh()
        {
            if (_nameInput != null)
            {
                string last   = Core.LastSavePath ?? "";
                string defName = Path.GetFileNameWithoutExtension(last);
                // If the last path was somewhere outside LevelsDir, just suggest "level"
                if (string.IsNullOrEmpty(defName)) defName = "level";
                _nameInput.Component.text = defName;
            }
            RebuildFileList();
        }

        void RebuildFileList()
        {
            if (_fileListContent == null) return;

            for (int i = _fileListContent.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_fileListContent.transform.GetChild(i).gameObject);

            try { Directory.CreateDirectory(LevelsDir); }
            catch { /* silently skip if the path is inaccessible */ }

            string[] files = Array.Empty<string>();
            try { files = Directory.GetFiles(LevelsDir, "*.bbb"); }
            catch { }
            Array.Sort(files, (a, b) =>
                string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));

            if (files.Length == 0)
            {
                var emptyLbl = UIFactory.CreateLabel(_fileListContent, "EmptyLbl",
                    "No saved levels yet.",
                    TextAnchor.MiddleCenter, new Color(0.45f, 0.45f, 0.45f), fontSize: 13);
                UIFactory.SetLayoutElement(emptyLbl.gameObject, minHeight: 26, flexibleWidth: 9999);
                return;
            }

            foreach (var file in files)
            {
                string path     = file;
                string baseName = Path.GetFileNameWithoutExtension(file);
                bool loadArmed  = _confirmPath == path && !_confirmDelete;
                bool delArmed   = _confirmPath == path &&  _confirmDelete;

                var row = UIFactory.CreateHorizontalGroup(_fileListContent, $"Row_{baseName}",
                    false, false, true, true, spacing: 4);
                UIFactory.SetLayoutElement(row, minHeight: 28, flexibleWidth: 9999);

                // Filename label — clicking it fills the name input for quick overwrite
                var nameLbl = UIFactory.CreateLabel(row, "FileName", baseName,
                    TextAnchor.MiddleLeft, Color.white, fontSize: 14);
                UIFactory.SetLayoutElement(nameLbl.gameObject, flexibleWidth: 9999);

                // Make label clickable to fill the name input
                var nameGO = nameLbl.gameObject;
                var nameBtn = nameGO.AddComponent<Button>();
                var nameNav = nameBtn.navigation; nameNav.mode = Navigation.Mode.None; nameBtn.navigation = nameNav;
                nameBtn.onClick.AddListener(new Action(() =>
                {
                    if (_nameInput != null) _nameInput.Component.text = baseName;
                }));

                // Load button (turns "Sure?" while armed)
                string loadTxt = loadArmed ? "Sure?" : "Load";
                var loadBtn = UIFactory.CreateButton(row, "LoadBtn", loadTxt);
                UIFactory.SetLayoutElement(loadBtn.Component.gameObject, minWidth: 56, minHeight: 24);
                PropBrowserUI.ApplyButtonColors(loadBtn);
                if (loadArmed) TintButton(loadBtn, new Color(1f, 0.55f, 0.1f));
                loadBtn.OnClick += () => OnLoadClick(path, baseName);

                // Delete button (turns "Sure?" while armed)
                string delTxt = delArmed ? "Sure?" : "✕";
                var delBtn = UIFactory.CreateButton(row, "DelBtn", delTxt);
                UIFactory.SetLayoutElement(delBtn.Component.gameObject, minWidth: 40, minHeight: 24);
                PropBrowserUI.ApplyButtonColors(delBtn);
                TintButton(delBtn, delArmed ? new Color(1f, 0.28f, 0.28f) : new Color(0.8f, 0.18f, 0.18f));
                delBtn.OnClick += () => OnDeleteClick(path);
            }
        }

        // ── Button handlers ──────────────────────────────────────────────────

        void OnSaveClick()
        {
            string name = (_nameInput?.Component?.text ?? "").Trim();
            // Strip extension if the user typed it
            if (name.EndsWith(".bbb", StringComparison.OrdinalIgnoreCase))
                name = name[..^4].TrimEnd();
            if (string.IsNullOrEmpty(name)) { SetStatus("Enter a file name first."); return; }

            string path = Path.Combine(LevelsDir, name + ".bbb");
            try { Directory.CreateDirectory(LevelsDir); }
            catch (Exception e) { SetStatus($"Cannot create saves folder: {e.Message}"); return; }

            bool ok = LevelSaveLoad.Save(path);
            if (ok) Core.LastSavePath = path;
            SetStatus(ok ? $"Saved \"{name}.bbb\"." : "Save failed — see MelonLoader log.");
            _confirmPath = null;
            RebuildFileList();
        }

        void OnLoadClick(string path, string baseName)
        {
            var mgr = LevelEditorManager.Instance;
            bool hasObjects = mgr != null && mgr.Objects.Count > 0;

            bool confirmed = !hasObjects || (_confirmPath == path && !_confirmDelete);
            if (confirmed)
            {
                _confirmPath = null;
                var (ok, count, error) = LevelSaveLoad.Load(path);
                if (ok)
                {
                    Core.LastSavePath = path;
                    BabyBlocks.Networking.ModNetworking.BroadcastLevelLoad();
                }
                SetStatus(ok
                    ? $"Loaded \"{baseName}.bbb\" ({count} object(s))."
                    : $"Load failed: {error}");
                RebuildFileList();
            }
            else
            {
                _confirmPath   = path;
                _confirmDelete = false;
                _confirmTime   = Time.realtimeSinceStartup;
                SetStatus("Current level will be replaced — click Load again to confirm.");
                RebuildFileList();
            }
        }

        void OnDeleteClick(string path)
        {
            if (_confirmPath == path && _confirmDelete)
            {
                _confirmPath = null;
                string name = Path.GetFileNameWithoutExtension(path);
                try
                {
                    File.Delete(path);
                    if (string.Equals(Core.LastSavePath, path, StringComparison.OrdinalIgnoreCase))
                        Core.LastSavePath = "";
                    SetStatus($"Deleted \"{name}.bbb\".");
                }
                catch (Exception e)
                {
                    SetStatus($"Delete failed: {e.Message}");
                }
                RebuildFileList();
            }
            else
            {
                _confirmPath   = path;
                _confirmDelete = true;
                _confirmTime   = Time.realtimeSinceStartup;
                SetStatus("Click ✕ again to permanently delete this file.");
                RebuildFileList();
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        void SetStatus(string msg)
        {
            if (_statusText != null) _statusText.text = msg;
            _statusTime = Time.realtimeSinceStartup;
        }

        static void TintButton(ButtonRef btn, Color color)
        {
            if (btn?.Component == null) return;
            var img = btn.Component.GetComponent<Image>();
            if (img != null) img.color = color;
            var cols = btn.Component.colors;
            cols.normalColor      = color;
            cols.highlightedColor = Color.Lerp(color, Color.white, 0.15f);
            cols.pressedColor     = Color.Lerp(color, Color.black, 0.2f);
            btn.Component.colors  = cols;
        }
    }
}
