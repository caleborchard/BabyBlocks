using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace BabyBlocks
{
    static class PropMetadataPanel
    {
        public static bool Enabled = true;
        public static bool IsTypingInUI { get; private set; }
        public static bool IsPointerOverUI { get; private set; }

        const float HeaderH = 22f;
        const float Pad = 8f;
        const float MaterialListH = 140f;
        const float RendererListH = 120f;
        const float AutoSaveDelay = 0.75f;

        const string SearchField      = "propMetaSearch";
        const string DisplayNameField = "propMetaDisplayName";
        const string CategoryField    = "propMetaCategory";
        const string OverrideField    = "propMetaOverride";
        const string NoOverrideLabel  = "(no override)";

        static Rect _windowRect;
        static bool _windowInitialized;
        static bool _windowDragging;
        static Vector2 _windowDragOffset;

        static string _propId;
        static string _displayName = "";
        static string _category = "";
        static bool _excluded;
        static bool _useRenderMeshCollider;
        static string _colliderIgnoredSubmeshes = "";
        static string _overrideMaterialName = "";
        static string _selectedMaterialName = "";
        static string _defaultMaterialName = "";
        static int _index = -1;
        static bool _dirty;

        static bool _showMaterialDropdown;
        static Vector2 _materialScroll;
        static Vector2 _mainScroll;
        static string _materialSearch = "";
        static GUIStyle _materialButtonStyle;
        static GUIStyle _redLabelStyle;
        static readonly List<string> _materialNames  = new();
        static readonly List<string> _materialLabels = new();
        static readonly Dictionary<string, Material> _materialByName = new(StringComparer.OrdinalIgnoreCase);
        static bool _materialsLoaded;

        static bool _showRendererDropdown;
        static Vector2 _rendererScroll;
        static readonly List<RendererEntry> _rendererEntries = new();

        static readonly string[] KnownSurfaceTags =
        {
            "",            // (none — don't override tag)
            "Rock",
            "Cliff",
            "Stone",
            "Dirt",
            "Mud",
            "Sand",
            "Gravel",
            "Riverbed",
            "Grass",
            "DeadGrass",
            "ForestFloor",
            "Moss",
            "MossyRock",
            "Snow",
            "Ice",
            "Wood",
            "MassiveWood",
            "Metal",
            "Fabric",
            "Cactus",
            "SandCastle",
            "Milk",
        };

        static string _surfaceType = "";
        static bool _showSurfaceTypeDropdown;
        static Vector2 _surfaceTypeScroll;
        static readonly HashSet<string> _disabledRendererPaths = new(StringComparer.Ordinal);

        static bool _materialExplicitlyChosen;
        static float _lastChangeTime;
        static bool _isBush;
        static float _bushRadius;
        static int _soundGrassType = 1;
        static bool _keepOriginalHierarchy;
        static bool _showGrassTypeDropdown;
        static Vector2 _grassTypeScroll;

        static readonly (string label, int value)[] KnownGrassTypes =
        {
            ("Normal",       1),
            ("None (silent)",0),
            ("Tall",         2),
            ("Dry Tall",     3),
            ("Fern",         4),
            ("Dry Leaf",     5),
            ("Wet Leaf",     6),
            ("Leafy Shrub",  7),
            ("Dry Shrub",    8),
            ("Wild Flower",  9),
            ("Twiggy",      10),
            ("Needle",      11),
            ("Cedar",       12),
            ("Reed",        13),
        };

        static string GrassTypeName(int value)
        {
            foreach (var (label, val) in KnownGrassTypes)
                if (val == value) return label;
            return value.ToString();
        }

        static readonly List<string> _perSlotSelected = new();
        static readonly List<string> _perSlotDefault = new();
        static readonly HashSet<int> _slotHasExplicitOverride = new();
        static int _maxMaterialSlots;
        static int _openSlotDropdown = -1;
        static Vector2 _slotDropdownScroll;
        static string _slotDropdownSearch = "";
        static bool _multiMaterialEnabled;
        static int _forcedMaterialSlots = 2;
        static string _forcedMaterialSlotsStr = "2";
        static Renderer[] _selectedRenderers;
        static Material[][] _selectedDefaultMaterials;
        static LevelEditorObject _selectedLEO;

        static readonly List<Material> _microSplatLayerMats = new();

        static readonly Dictionary<string, PropExtraInfo> _byId = new(StringComparer.Ordinal);
        // Maps material name → prop ID of the prop whose asset natively contains that material.
        static readonly Dictionary<string, string> _knownMaterialSources = new(StringComparer.OrdinalIgnoreCase);
        static bool _materialSourcesLoaded;
        static bool _loaded;
        static int _nextIndex = 1;
        static bool _savePathLogged;
        static string _paletteSelectedId;

        static string SavePath =>
            Path.Combine(MelonEnvironment.UserDataDirectory, "BabyBlocks", "prop_metadata.json");

        public static void DrawGUI(LevelEditorObject selectedObject)
        {
            if (!Enabled || !Core.DebugMode)
            {
                IsTypingInUI = false;
                IsPointerOverUI = false;
                return;
            }

            SyncFromSelection(selectedObject);
            EnsureWindowRect();

            var rect = _windowRect;
            GUI.Box(rect, "Prop Details", GUI.skin.window);

            var headerRect = new Rect(rect.x, rect.y, rect.width, HeaderH);
            var contentRect = new Rect(
                rect.x + Pad,
                rect.y + HeaderH + Pad,
                rect.width - Pad * 2f,
                rect.height - HeaderH - Pad * 2f);

            GUILayout.BeginArea(contentRect);
            DrawContents();
            GUILayout.EndArea();

            HandleDrag(headerRect);
            AutoSaveIfIdle();

            string focused = GUI.GetNameOfFocusedControl();
            IsTypingInUI = focused == SearchField
                        || focused == DisplayNameField
                        || focused == CategoryField
                        || focused == OverrideField;
            if (string.IsNullOrEmpty(_propId))
                IsTypingInUI = false;

            var mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            IsPointerOverUI = rect.Contains(mouse);
        }

        public static bool ContainsPoint(Vector2 guiPoint)
        {
            if (!Enabled || !_windowInitialized) return false;
            return _windowRect.Contains(guiPoint);
        }

        static void DrawContents()
        {
            GUILayout.BeginVertical();

            GUILayout.Label("Search props");
            GUI.SetNextControlName(SearchField);
            string newSearch = GUILayout.TextField(PropLibrary.SearchText ?? string.Empty);
            if (!string.Equals(newSearch, PropLibrary.SearchText, StringComparison.Ordinal))
                PropLibrary.SetSearch(newSearch);

            GUILayout.Space(4f);

            GUI.enabled = !string.IsNullOrEmpty(_propId);
            if (GUILayout.Button("Save"))
            {
                ApplyCurrent();
                _dirty = false;
            }
            GUI.enabled = true;
            if (GUILayout.Button("Save All"))
            {
                if (!string.IsNullOrEmpty(_propId))
                {
                    ApplyCurrent();
                    _dirty = false;
                }
                Save();
            }
            GUILayout.Space(4f);

            _mainScroll = GUILayout.BeginScrollView(_mainScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.ExpandHeight(true));

            if (string.IsNullOrEmpty(_propId))
            {
                GUILayout.Label("Select a prop in the world to edit its details.");
                GUILayout.EndScrollView();
                GUILayout.EndVertical();
                return;
            }

            GUILayout.BeginVertical(GUILayout.Width(375f));

            GUILayout.Label("Selected ID (click to copy)");
            if (GUILayout.Button(_propId ?? string.Empty, GUI.skin.textField))
                GUIUtility.systemCopyBuffer = _propId ?? string.Empty;

            var newExclude = GUILayout.Toggle(_excluded, "Exclude item");
            if (newExclude != _excluded)
            {
                _excluded = newExclude;
                MarkDirty();
            }

            GUILayout.Space(4f);

            if (_redLabelStyle == null)
            {
                _redLabelStyle = new GUIStyle(GUI.skin.label);
                _redLabelStyle.normal.textColor = Color.red;
            }
            bool isDuplicateName = !string.IsNullOrEmpty(_displayName)
                && HasDuplicateDisplayName(_displayName, _propId);
            GUILayout.Label("Display name", isDuplicateName ? _redLabelStyle : GUI.skin.label);
            GUI.SetNextControlName(DisplayNameField);
            var newDisplayName = GUILayout.TextField(_displayName ?? string.Empty);
            if (!string.Equals(newDisplayName, _displayName, StringComparison.Ordinal))
            {
                _displayName = newDisplayName;
                MarkDirty();
            }

            if (GUI.GetNameOfFocusedControl() == DisplayNameField && !string.IsNullOrEmpty(_displayName) && _displayName.Length >= 2)
            {
                int suggCount = 0;
                foreach (var kvp in _byId)
                {
                    if (kvp.Key == _propId) continue;
                    var dn = kvp.Value.displayName;
                    if (string.IsNullOrEmpty(dn)) continue;
                    if (dn.IndexOf(_displayName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        _displayName.IndexOf(dn, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (suggCount == 0)
                        {
                            GUI.contentColor = new Color(1f, 1f, 0.5f, 1f);
                            GUILayout.Label("Similar names:");
                            GUI.contentColor = Color.white;
                        }
                        GUILayout.Label("  • " + dn);
                        suggCount++;
                        if (suggCount >= 5) break;
                    }
                }
            }

            GUILayout.Label("Category");
            GUI.SetNextControlName(CategoryField);
            var newCategory = GUILayout.TextField(_category ?? string.Empty);
            if (!string.Equals(newCategory, _category, StringComparison.Ordinal))
            {
                _category = newCategory;
                MarkDirty();
            }

            if (GUI.GetNameOfFocusedControl() == CategoryField && !string.IsNullOrEmpty(_category) && _category.Length >= 2)
            {
                int suggCount = 0;
                var seenCats = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in _byId)
                {
                    if (kvp.Key == _propId) continue;
                    var cat = kvp.Value.category;
                    if (string.IsNullOrEmpty(cat)) continue;
                    if (cat.IndexOf(_category, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        _category.IndexOf(cat, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (!seenCats.Add(cat)) continue;
                        if (suggCount == 0)
                        {
                            GUI.contentColor = new Color(1f, 1f, 0.5f, 1f);
                            GUILayout.Label("Similar categories:");
                            GUI.contentColor = Color.white;
                        }
                        GUILayout.Label("  • " + cat);
                        suggCount++;
                        if (suggCount >= 5) break;
                    }
                }
            }

            GUILayout.Space(4f);
            GUILayout.Label("Surface type (traction / footstep audio)");
            string surfaceLabel = string.IsNullOrEmpty(_surfaceType) ? "(none — game default)" : _surfaceType;
            if (GUILayout.Button(surfaceLabel, GUILayout.Height(22f)))
                _showSurfaceTypeDropdown = !_showSurfaceTypeDropdown;

            if (_showSurfaceTypeDropdown)
            {
                _surfaceTypeScroll = GUILayout.BeginScrollView(_surfaceTypeScroll, GUILayout.Height(120f));
                foreach (var tag in KnownSurfaceTags)
                {
                    string lbl = string.IsNullOrEmpty(tag) ? "(none — game default)" : tag;
                    if (string.Equals(tag, _surfaceType, StringComparison.Ordinal))
                        lbl = "> " + lbl;
                    EnsureMaterialButtonStyle();
                    if (GUILayout.Button(lbl, _materialButtonStyle))
                    {
                        _surfaceType = tag;
                        _showSurfaceTypeDropdown = false;
                        ApplySurfaceType(_selectedLEO, tag);
                        MarkDirty();
                    }
                }
                GUILayout.EndScrollView();
            }

            GUILayout.Space(8f);

            var newUseMeshCol = GUILayout.Toggle(_useRenderMeshCollider, "Use render mesh as collider");
            if (newUseMeshCol != _useRenderMeshCollider)
            {
                _useRenderMeshCollider = newUseMeshCol;
                ApplyColliderToSelected(newUseMeshCol);
                BuildRendererEntries(_selectedLEO);
                ApplyRendererVisibility();
                ApplyCurrent();
                _dirty = false;
            }

            GUILayout.Label("Components");
            if (GUILayout.Button(_showRendererDropdown ? "Hide components" : "Show components"))
                _showRendererDropdown = !_showRendererDropdown;

            if (_showRendererDropdown)
            {
                _rendererScroll = GUILayout.BeginScrollView(_rendererScroll, GUILayout.Height(RendererListH));
                for (int i = 0; i < _rendererEntries.Count; i++)
                {
                    var entry = _rendererEntries[i];
                    bool newEnabled = GUILayout.Toggle(entry.enabled, entry.path);
                    if (newEnabled != entry.enabled)
                    {
                        entry.enabled = newEnabled;
                        if (entry.renderer != null) entry.renderer.enabled = newEnabled;
                        else if (entry.collider != null) entry.collider.enabled = newEnabled;
                        if (!newEnabled) _disabledRendererPaths.Add(entry.path);
                        else _disabledRendererPaths.Remove(entry.path);
                        MarkDirty();
                    }
                }
                GUILayout.EndScrollView();
            }

            GUILayout.Space(8f);

            EnsureMaterialList();

            int effectiveSlotCount = _multiMaterialEnabled
                ? Math.Max(_forcedMaterialSlots, _maxMaterialSlots)
                : _maxMaterialSlots;

            var newMultiMat = GUILayout.Toggle(_multiMaterialEnabled, "Multiple materials");
            if (newMultiMat != _multiMaterialEnabled)
            {
                _multiMaterialEnabled = newMultiMat;
                int eff = _multiMaterialEnabled
                    ? Math.Max(_forcedMaterialSlots, _maxMaterialSlots)
                    : _maxMaterialSlots;
                while (_perSlotDefault.Count < eff) _perSlotDefault.Add(string.Empty);
                while (_perSlotSelected.Count < eff) _perSlotSelected.Add(string.Empty);
                _openSlotDropdown = -1;
                if (_multiMaterialEnabled)
                {
                    // Port an existing single-material override into slot 0 of the new setup.
                    if (!string.IsNullOrEmpty(_overrideMaterialName) && _perSlotSelected.Count > 0
                        && string.IsNullOrEmpty(_perSlotSelected[0]))
                        _perSlotSelected[0] = _overrideMaterialName;
                    ApplyAllSlotMaterials();
                }
                else
                    RestoreDefaultMaterials();
                MarkDirty();
            }

            if (_multiMaterialEnabled)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Slots:", GUILayout.Width(40f));
                string newSlotsStr = GUILayout.TextField(_forcedMaterialSlotsStr, GUILayout.Width(40f));
                if (!string.Equals(newSlotsStr, _forcedMaterialSlotsStr, StringComparison.Ordinal))
                {
                    _forcedMaterialSlotsStr = newSlotsStr;
                    if (int.TryParse(newSlotsStr, out int parsed) && parsed >= 2)
                    {
                        int oldEff = Math.Max(_forcedMaterialSlots, _maxMaterialSlots);
                        _forcedMaterialSlots = parsed;
                        int newEff = Math.Max(_forcedMaterialSlots, _maxMaterialSlots);

                        if (newEff < oldEff)
                        {
                            // Drop overrides and explicit-override tracking for removed slots.
                            for (int s = newEff; s < oldEff; s++)
                                _slotHasExplicitOverride.Remove(s);
                            while (_perSlotSelected.Count > newEff)
                                _perSlotSelected.RemoveAt(_perSlotSelected.Count - 1);

                            // Restore all renderers to their default material arrays first
                            // (collapses any over-extended arrays back to natural length),
                            // then re-apply only the remaining slots below.
                            RestoreDefaultMaterials();
                        }

                        while (_perSlotDefault.Count < newEff) _perSlotDefault.Add(string.Empty);
                        while (_perSlotSelected.Count < newEff) _perSlotSelected.Add(string.Empty);
                        ApplyAllSlotMaterials();
                        MarkDirty();
                    }
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Ignore collider submeshes (0-based):", GUILayout.Width(230f));
            string newIgnored = GUILayout.TextField(_colliderIgnoredSubmeshes ?? string.Empty);
            if (!string.Equals(newIgnored, _colliderIgnoredSubmeshes, StringComparison.Ordinal))
            {
                _colliderIgnoredSubmeshes = newIgnored;
                MarkDirty();
            }
            GUILayout.EndHorizontal();

            if (effectiveSlotCount <= 1)
            {
                GUILayout.Label("Override material");
                GUILayout.BeginHorizontal();
                string overrideLabel = GetOverrideLabel();
                GUI.SetNextControlName(OverrideField);
                if (GUILayout.Button(overrideLabel, GUILayout.Height(22f)))
                    _showMaterialDropdown = !_showMaterialDropdown;
                if (GUILayout.Button("Reset", GUILayout.Height(22f), GUILayout.Width(50f)))
                {
                    _selectedMaterialName = _defaultMaterialName ?? string.Empty;
                    _overrideMaterialName = string.Empty;
                    _showMaterialDropdown = false;
                    _materialExplicitlyChosen = false;
                    // Prefer applying by name so we use the live Material object from _materialByName
                    // rather than _selectedDefaultMaterials, which may be contaminated by a prior override.
                    if (!string.IsNullOrEmpty(_defaultMaterialName))
                        ApplyPreviewMaterial(_defaultMaterialName);
                    else
                        ApplyPreviewMaterial(string.Empty);
                    MarkDirty();
                }
                GUILayout.EndHorizontal();

                if (_showMaterialDropdown)
                {
                    GUILayout.Label("Search");
                    _materialSearch = GUILayout.TextField(_materialSearch ?? string.Empty);

                    _materialScroll = GUILayout.BeginScrollView(_materialScroll, GUILayout.Height(MaterialListH));
                    int selectedIndex = GetMaterialIndex(_selectedMaterialName);
                    string search = _materialSearch != null ? _materialSearch.Trim() : string.Empty;
                    bool hasSearch = !string.IsNullOrEmpty(search);
                    for (int i = 0; i < _materialLabels.Count; i++)
                    {
                        string label = _materialLabels[i];
                        if (hasSearch && label.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        if (i == selectedIndex)
                            label = "> " + label;

                        EnsureMaterialButtonStyle();
                        if (GUILayout.Button(label, _materialButtonStyle))
                        {
                            SelectMaterialByIndex(i);
                            _showMaterialDropdown = false;
                            _materialExplicitlyChosen = true;
                            ApplyPreviewMaterial(_selectedMaterialName);
                            MarkDirty();
                        }
                    }
                    GUILayout.EndScrollView();
                }
            }
            else
            {
                GUILayout.Label("Override materials");
                for (int s = 0; s < effectiveSlotCount; s++)
                {
                    string slotDef = s < _perSlotDefault.Count ? _perSlotDefault[s] : string.Empty;
                    string slotSel = s < _perSlotSelected.Count ? _perSlotSelected[s] : slotDef;
                    bool isOverridden = !string.IsNullOrEmpty(slotSel)
                        && !string.Equals(slotSel, slotDef, StringComparison.OrdinalIgnoreCase);
                    string btnLabel = isOverridden
                        ? slotSel
                        : (string.IsNullOrEmpty(slotDef) ? "(unknown)" : slotDef + " (default)");

                    GUILayout.Label("Slot " + s + ": " + (string.IsNullOrEmpty(slotDef) ? "(unknown)" : slotDef));
                    if (GUILayout.Button(btnLabel, GUILayout.Height(22f)))
                    {
                        if (_openSlotDropdown == s)
                            _openSlotDropdown = -1;
                        else
                        {
                            _openSlotDropdown = s;
                            _slotDropdownScroll = Vector2.zero;
                        }
                    }

                    if (_openSlotDropdown == s)
                    {
                        GUILayout.Label("Search");
                        _slotDropdownSearch = GUILayout.TextField(_slotDropdownSearch ?? string.Empty);
                        _slotDropdownScroll = GUILayout.BeginScrollView(_slotDropdownScroll, GUILayout.Height(MaterialListH));
                        string slotSearch = _slotDropdownSearch != null ? _slotDropdownSearch.Trim() : string.Empty;
                        bool hasSlotSearch = !string.IsNullOrEmpty(slotSearch);
                        for (int i = 0; i < _materialLabels.Count; i++)
                        {
                            string label = _materialLabels[i];
                            if (hasSlotSearch && label.IndexOf(slotSearch, StringComparison.OrdinalIgnoreCase) < 0)
                                continue;
                            bool isCurrent = string.Equals(slotSel, _materialNames[i], StringComparison.OrdinalIgnoreCase)
                                || (i == 0 && !isOverridden);
                            if (isCurrent) label = "> " + label;
                            EnsureMaterialButtonStyle();
                            if (GUILayout.Button(label, _materialButtonStyle))
                            {
                                string picked = i == 0 ? string.Empty : _materialNames[i];
                                while (_perSlotSelected.Count <= s) _perSlotSelected.Add(string.Empty);
                                _perSlotSelected[s] = string.IsNullOrEmpty(picked) ? slotDef : picked;
                                if (string.IsNullOrEmpty(picked))
                                    _slotHasExplicitOverride.Remove(s);
                                else
                                    _slotHasExplicitOverride.Add(s);
                                _openSlotDropdown = -1;
                                ApplySlotMaterial(s, picked);
                                MarkDirty();
                            }
                        }
                        GUILayout.EndScrollView();
                    }
                }

                if (GUILayout.Button("Reset all to default materials"))
                {
                    for (int s = 0; s < _perSlotSelected.Count; s++)
                        _perSlotSelected[s] = s < _perSlotDefault.Count ? _perSlotDefault[s] : string.Empty;
                    _slotHasExplicitOverride.Clear();
                    _openSlotDropdown = -1;
                    RestoreDefaultMaterials();
                    MarkDirty();
                }
            }

            GUILayout.Space(8f);

            var newKeepOrig = GUILayout.Toggle(_keepOriginalHierarchy, "Keep original hierarchy");
            if (newKeepOrig != _keepOriginalHierarchy)
            {
                _keepOriginalHierarchy = newKeepOrig;
                MarkDirty();
            }

            var newIsBush = GUILayout.Toggle(_isBush, "Is Bush");
            if (newIsBush != _isBush)
            {
                _isBush = newIsBush;
                ApplyBushCollider(_selectedLEO, newIsBush);
                ApplyCurrent();
                _dirty = false;
            }
            if (_isBush)
            {
                GUILayout.Label($"  Bush sphere radius: {_bushRadius:F3} (local)");
                GUILayout.Label("  Grass type (sound)");
                if (GUILayout.Button(GrassTypeName(_soundGrassType), GUILayout.Height(22f)))
                    _showGrassTypeDropdown = !_showGrassTypeDropdown;
                if (_showGrassTypeDropdown)
                {
                    _grassTypeScroll = GUILayout.BeginScrollView(_grassTypeScroll, GUILayout.Height(120f));
                    foreach (var (lbl, val) in KnownGrassTypes)
                    {
                        string btnLbl = val == _soundGrassType ? "> " + lbl : lbl;
                        EnsureMaterialButtonStyle();
                        if (GUILayout.Button(btnLbl, _materialButtonStyle))
                        {
                            _soundGrassType = val;
                            _showGrassTypeDropdown = false;
                            if (_selectedLEO != null)
                            {
                                BushAudioTracker.Unregister(_selectedLEO.transform);
                                BushAudioTracker.Register(_selectedLEO.transform, _bushRadius, _soundGrassType);
                            }
                            ApplyCurrent();
                            _dirty = false;
                        }
                    }
                    GUILayout.EndScrollView();
                }
            }

            GUILayout.Space(4f);
            GUILayout.Label(_index > 0
                ? $"Index: {_index}"
                : "Index: (not set)");

            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        static string GetOverrideLabel()
        {
            if (string.IsNullOrEmpty(_selectedMaterialName))
                return NoOverrideLabel;

            if (string.IsNullOrEmpty(_overrideMaterialName)
                && !string.IsNullOrEmpty(_defaultMaterialName)
                && string.Equals(_selectedMaterialName, _defaultMaterialName, StringComparison.OrdinalIgnoreCase))
                return _selectedMaterialName + " (default)";

            return _selectedMaterialName;
        }

        static void SelectMaterialByIndex(int index)
        {
            if (index <= 0 || index >= _materialNames.Count)
            {
                _selectedMaterialName = "";
                return;
            }

            _selectedMaterialName = _materialNames[index];
        }

        static int GetMaterialIndex(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            for (int i = 0; i < _materialNames.Count; i++)
            {
                if (string.Equals(_materialNames[i], name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return 0;
        }

        static bool ShouldHideMaterial(string name) =>
            name.IndexOf("Imposter", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Impostor", StringComparison.OrdinalIgnoreCase) >= 0;

        static void SortMaterialList()
        {
            if (_materialNames.Count <= 2) return;
            var pairs = new List<(string name, string label)>(_materialNames.Count - 1);
            for (int i = 1; i < _materialNames.Count; i++)
                pairs.Add((_materialNames[i], _materialLabels[i]));
            pairs.Sort((a, b) => string.Compare(a.label, b.label, StringComparison.OrdinalIgnoreCase));
            for (int i = 0; i < pairs.Count; i++)
            {
                _materialNames[i + 1] = pairs[i].name;
                _materialLabels[i + 1] = pairs[i].label;
            }
        }

        static void EnsureMaterialList()
        {
            if (_materialsLoaded) return;
            _materialsLoaded = true;
            _materialNames.Clear();
            _materialLabels.Clear();
            _materialByName.Clear();
            _materialNames.Add(NoOverrideLabel);
            _materialLabels.Add(NoOverrideLabel);

            try
            {
                var mats = Resources.FindObjectsOfTypeAll<Material>();
                if (mats != null)
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var m = mats[i];
                        if (m == null || string.IsNullOrEmpty(m.name)) continue;
                        if (!seen.Add(m.name)) continue;
                        if (ShouldHideMaterial(m.name)) continue;
                        string shaderName = m.shader != null ? m.shader.name : string.Empty;
                        string label = string.IsNullOrEmpty(shaderName)
                            ? m.name
                            : $"{m.name}  [{shaderName}]";
                        _materialNames.Add(m.name);
                        _materialLabels.Add(label);
                        _materialByName[m.name] = m;
                    }
                }

                AddMicroSplatLayerMaterials();

                // Sort by label but keep names and labels in sync.
                if (_materialNames.Count > 2)
                {
                    var pairs = new List<(string name, string label)>(_materialNames.Count - 1);
                    for (int i = 1; i < _materialNames.Count; i++)
                        pairs.Add((_materialNames[i], _materialLabels[i]));
                    pairs.Sort((a, b) => string.Compare(a.label, b.label, StringComparison.OrdinalIgnoreCase));
                    for (int i = 0; i < pairs.Count; i++)
                    {
                        _materialNames[i + 1]  = pairs[i].name;
                        _materialLabels[i + 1] = pairs[i].label;
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropMetadata] Material scan failed: {e.Message}");
            }

            EnsureMaterialSources();
        }

        // Registers a material source and back-fills materialSourcePropId on every saved entry that
        // shares the same overrideMaterialId but had no source recorded yet. Returns true if any were updated.
        static bool BackfillMaterialSource(string materialName, string sourcePropId)
        {
            if (string.IsNullOrEmpty(materialName) || string.IsNullOrEmpty(sourcePropId)) return false;
            // MicroSplat layer materials are generated at runtime — they have no asset source prop.
            if (materialName.StartsWith("[MicroSplat]", StringComparison.Ordinal)) return false;
            _knownMaterialSources[materialName] = sourcePropId;

            bool anyChanged = false;
            foreach (var kvp in _byId)
            {
                var item = kvp.Value;
                if (item.excluded) continue;
                if (!string.Equals(item.overrideMaterialId, materialName, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(item.materialSourcePropId, sourcePropId, StringComparison.OrdinalIgnoreCase)) continue;
                item.materialSourcePropId = sourcePropId;
                anyChanged = true;
            }
            return anyChanged;
        }

        // For each saved override whose source prop ID is known, load that prop so its asset bundle
        // (and thus its materials) comes into memory. Runs once after the initial material list is built.
        static void EnsureMaterialSources()
        {
            if (_materialSourcesLoaded) return;
            _materialSourcesLoaded = true;
            if (!PropLibrary.IsInitialized) return;

            EnsureLoaded();
            bool anyLoaded = false;
            bool anyBackfilled = false;
            foreach (var kvp in _byId)
            {
                var item = kvp.Value;
                if (string.IsNullOrEmpty(item.overrideMaterialId)) continue;
                if (_materialByName.ContainsKey(item.overrideMaterialId)) continue; // already in memory
                if (string.IsNullOrEmpty(item.materialSourcePropId)) continue;      // no source tracked yet

                var sourceInfo = PropLibrary.FindById(item.materialSourcePropId);
                if (sourceInfo == null) continue;

                try
                {
                    PropLibrary.LoadPropData(sourceInfo);
                    anyLoaded = true;
                    // Scan parts directly — GPUI materials don't reliably appear in
                    // Resources.FindObjectsOfTypeAll after loading, but parts hold the real refs.
                    AddPartsToMaterialList(sourceInfo);
                    // Propagate this source to all other entries that share the same override material.
                    if (BackfillMaterialSource(item.overrideMaterialId, item.materialSourcePropId))
                        anyBackfilled = true;
                }
                catch { }
            }

            if (anyBackfilled) Save();

            // Self-discovery pass: entries with a saved override but no recorded source.
            // Try loading the prop itself — it may natively contain its own override material.
            foreach (var kvp in _byId)
            {
                var item = kvp.Value;
                if (string.IsNullOrEmpty(item.overrideMaterialId)) continue;
                if (_materialByName.ContainsKey(item.overrideMaterialId)) continue;
                if (!string.IsNullOrEmpty(item.materialSourcePropId)) continue;
                if (item.overrideMaterialId.StartsWith("[MicroSplat]", StringComparison.Ordinal)) continue;

                var selfInfo = PropLibrary.FindById(item.id);
                if (selfInfo == null) continue;

                try
                {
                    PropLibrary.LoadPropData(selfInfo);
                    AddPartsToMaterialList(selfInfo); // sets materialSourcePropId + calls Save() if found
                    anyLoaded = true;
                }
                catch { }
            }

            // NOTE: Bulk pre-loading of all saved override materials via TryLoadMaterialByName was
            // removed here. Each WaitForCompletion() call can trigger game asset-management
            // callbacks (streaming, bundle eviction) that intermittently destroyed physics
            // colliders on previously placed props. Materials are now lazy-loaded on first use
            // in ApplyPreviewMaterial, ApplySlotMaterial, and ApplyMaterialOverridesToRoot.
            if (anyLoaded)
            {
                // Re-scan Resources to pick up materials from the newly-loaded asset bundles.
                try
                {
                    var allMats = Resources.FindObjectsOfTypeAll<Material>();
                    if (allMats != null)
                    {
                        for (int i = 0; i < allMats.Length; i++)
                        {
                            var m = allMats[i];
                            if (m == null || string.IsNullOrEmpty(m.name)) continue;
                            if (ShouldHideMaterial(m.name)) continue;
                            if (_materialByName.ContainsKey(m.name)) continue;
                            string shaderName = m.shader != null ? m.shader.name : string.Empty;
                            string label = string.IsNullOrEmpty(shaderName)
                                ? m.name
                                : $"{m.name}  [{shaderName}]";
                            _materialNames.Add(m.name);
                            _materialLabels.Add(label);
                            _materialByName[m.name] = m;
                        }
                    }
                }
                catch { }
            }

            // Full catalog index: add every material name from the catalog so the search list
            // is complete regardless of which asset bundles are currently loaded. Actual Material
            // objects are lazy-loaded on first use (see ApplyPreviewMaterial / ApplySlotMaterial).
            // IndexAllCatalogMaterials is a no-op on subsequent sessions (sentinel in cache).
            PropLibrary.IndexAllCatalogMaterials();
            var alreadyListed = new HashSet<string>(_materialNames, StringComparer.OrdinalIgnoreCase);
            bool anyCatalogAdded = false;
            foreach (var kvp in PropLibrary.MaterialCatalogPaths)
            {
                string name = kvp.Key;
                if (name == "__IDX__") continue;
                if (ShouldHideMaterial(name)) continue;
                if (!alreadyListed.Add(name)) continue;
                _materialNames.Add(name);
                _materialLabels.Add(name); // shader label unknown until the material is actually loaded
                anyCatalogAdded = true;
            }
            if (anyCatalogAdded) SortMaterialList();
        }

        // Returns the first material name found in the prop's parts that is NOT the override.
        // Used to recover the true native material when the live renderer is contaminated.
        static string FindNativeFromParts(PropInfo info, string overrideMaterialName)
        {
            if (info == null) return string.Empty;
            if (!info.isLoaded) PropLibrary.LoadPropData(info);
            if (info.parts == null) return string.Empty;
            foreach (var part in info.parts)
            {
                if (part?.materials == null) continue;
                foreach (var m in part.materials)
                {
                    if (m == null || string.IsNullOrEmpty(m.name)) continue;
                    if (!string.Equals(m.name, overrideMaterialName, StringComparison.OrdinalIgnoreCase))
                        return m.name;
                }
            }
            return string.Empty;
        }

        // Scans PropInfo.parts for materials and adds them to the list.
        // Used for GPUI props (no live renderers) where AddRendererMaterialsToList yields nothing.
        // Parts come from the loaded asset — always native materials, no contamination risk.
        static void AddPartsToMaterialList(PropInfo info)
        {
            if (info?.parts == null) return;
            foreach (var part in info.parts)
            {
                if (part?.materials == null) continue;
                foreach (var mat in part.materials)
                {
                    if (mat == null || string.IsNullOrEmpty(mat.name)) continue;
                    if (ShouldHideMaterial(mat.name)) continue;
                    if (!string.IsNullOrEmpty(info.id))
                    {
                        if (BackfillMaterialSource(mat.name, info.id))
                            Save();
                    }
                    if (_materialByName.ContainsKey(mat.name)) continue;
                    string shaderName = mat.shader != null ? mat.shader.name : string.Empty;
                    string label = string.IsNullOrEmpty(shaderName)
                        ? mat.name
                        : $"{mat.name}  [{shaderName}]";
                    _materialNames.Add(mat.name);
                    _materialLabels.Add(label);
                    _materialByName[mat.name] = mat;
                }
            }
        }


        static void AddRendererMaterialsToList()
        {
            if (_selectedRenderers == null) return;
            for (int i = 0; i < _selectedRenderers.Length; i++)
            {
                var r = _selectedRenderers[i];
                if (r == null) continue;
                var mats = r.sharedMaterials;
                if (mats == null) continue;
                for (int m = 0; m < mats.Length; m++)
                {
                    var mat = mats[m];
                    if (mat == null || string.IsNullOrEmpty(mat.name)) continue;
                    if (ShouldHideMaterial(mat.name)) continue;
                    // Track which prop this material came from and propagate to all saved entries
                    // that use it as an override but had no source recorded yet.
                    // Only skip if this material is the current prop's override AND the override differs
                    // from the native material — that means the renderer may be contaminated (showing
                    // the override rather than the native), so recording this prop as the source would
                    // be wrong. When override == native, loading this prop does bring the material into
                    // memory, so recording is correct.
                    bool isUntrustedOverride =
                        !string.IsNullOrEmpty(_overrideMaterialName)
                        && !string.IsNullOrEmpty(_defaultMaterialName)
                        && string.Equals(mat.name, _overrideMaterialName, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(_overrideMaterialName, _defaultMaterialName, StringComparison.OrdinalIgnoreCase);

                    if (!string.IsNullOrEmpty(_propId) && !isUntrustedOverride)
                    {
                        if (BackfillMaterialSource(mat.name, _propId))
                            Save();
                    }
                    if (_materialByName.ContainsKey(mat.name)) continue;
                    string shaderName = mat.shader != null ? mat.shader.name : string.Empty;
                    string label = string.IsNullOrEmpty(shaderName)
                        ? mat.name
                        : $"{mat.name}  [{shaderName}]";
                    _materialNames.Add(mat.name);
                    _materialLabels.Add(label);
                    _materialByName[mat.name] = mat;
                }
            }
        }

        static void AddMicroSplatLayerMaterials()
        {
            if (_microSplatLayerMats.Count > 0)
            {
                // Already built — just re-register in case _materialByName was cleared by EnsureMaterialList.
                foreach (var mat in _microSplatLayerMats)
                {
                    if (mat == null || _materialByName.ContainsKey(mat.name)) continue;
                    _materialNames.Add(mat.name);
                    _materialLabels.Add(mat.name);
                    _materialByName[mat.name] = mat;
                }
                return;
            }
            try
            {
                var allMats = Resources.FindObjectsOfTypeAll<Material>();

                // Prefer a material with _CustomControl0 — it has reliably overridable UV-sampled blend textures.
                Material baseMat = null;
                for (int i = 0; i < allMats.Length && baseMat == null; i++)
                {
                    var m = allMats[i];
                    if (m == null || m.shader == null) continue;
                    if (!m.shader.name.StartsWith("MicroSplat", StringComparison.OrdinalIgnoreCase)) continue;
                    if (m.HasProperty("_CustomControl0")) baseMat = m;
                }
                for (int i = 0; i < allMats.Length && baseMat == null; i++)
                {
                    var m = allMats[i];
                    if (m == null || m.shader == null) continue;
                    if (m.shader.name.StartsWith("MicroSplat", StringComparison.OrdinalIgnoreCase)) baseMat = m;
                }
                if (baseMat == null) return;

                // All slots must be blanked per clone; leaving higher slots with original terrain data bleeds through.
                bool useCustom = baseMat.HasProperty("_CustomControl0");
                var controlPropList = new List<string>();
                for (int ci = 0; ci <= 7; ci++)
                {
                    string pn = useCustom ? $"_CustomControl{ci}" : $"_Control{ci}";
                    if (baseMat.HasProperty(pn)) controlPropList.Add(pn);
                }
                if (controlPropList.Count == 0)
                {
                    MelonLogger.Warning("[PropMetadata] MicroSplat material has no recognized control map properties.");
                    return;
                }
                string[] controlProps = controlPropList.ToArray();

                int layerCount = 0;
                var arrays = Resources.FindObjectsOfTypeAll<Texture2DArray>();
                for (int a = 0; a < arrays.Length; a++)
                {
                    var arr = arrays[a];
                    if (arr != null && string.Equals(arr.name, "MicroSplatConfig_diff_tarray",
                            StringComparison.Ordinal))
                    { layerCount = arr.depth; break; }
                }
                if (layerCount == 0) layerCount = controlProps.Length * 4;

                bool hasPerTexUV = baseMat.HasProperty("_PerTexUVScaleRotation0");

                BBLog.Msg(
                    $"[PropMetadata] MicroSplat base: '{baseMat.name}' shader: '{baseMat.shader.name}' " +
                    $"controlSlots: {controlProps.Length} layers: {layerCount} hasPerTexUV: {hasPerTexUV}");

                if (hasPerTexUV)
                {
                    var v0 = baseMat.GetVector("_PerTexUVScaleRotation0");
                    BBLog.Msg($"[PropMetadata] _PerTexUVScaleRotation0 = ({v0.x:F4},{v0.y:F4},{v0.z:F4},{v0.w:F4})");
                }

                const float UVScaleMultiplier = 8f; // terrain layers tile at world scale; multiply to fit props

                var blankControl = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                blankControl.SetPixel(0, 0, Color.clear);
                blankControl.Apply();
                blankControl.name = "MicroSplat_BlankControl";

                for (int layer = 0; layer < layerCount; layer++)
                {
                    try
                    {
                        int mapIdx = layer / 4;
                        int channel = layer % 4;
                        if (mapIdx >= controlProps.Length) break;

                        var activeControl = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                        activeControl.SetPixel(0, 0, new Color(
                            channel == 0 ? 1f : 0f,
                            channel == 1 ? 1f : 0f,
                            channel == 2 ? 1f : 0f,
                            channel == 3 ? 1f : 0f));
                        activeControl.Apply();
                        activeControl.name = $"MicroSplat_SingleLayer_{layer}";

                        var mat = new Material(baseMat) { name = $"[MicroSplat] Layer {layer}" };

                        for (int c = 0; c < controlProps.Length; c++)
                            mat.SetTexture(controlProps[c], blankControl);
                        mat.SetTexture(controlProps[mapIdx], activeControl);

                        if (hasPerTexUV)
                        {
                            string uvProp = $"_PerTexUVScaleRotation{layer}";
                            var v = baseMat.GetVector(uvProp);
                            mat.SetVector(uvProp, new Vector4(
                                v.x * UVScaleMultiplier, v.y * UVScaleMultiplier, v.z, v.w));
                        }

                        _microSplatLayerMats.Add(mat);
                        _materialNames.Add(mat.name);
                        _materialLabels.Add(mat.name);
                        _materialByName[mat.name] = mat;
                    }
                    catch { }
                }

                if (_microSplatLayerMats.Count > 0)
                    BBLog.Msg($"[PropMetadata] Built {_microSplatLayerMats.Count} MicroSplat layer materials.");
                else
                    MelonLogger.Warning("[PropMetadata] MicroSplat base material found but no layer materials could be created.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropMetadata] MicroSplat layer material creation failed: {e.Message}");
            }
        }

        static void ApplyCurrent()
        {
            if (string.IsNullOrEmpty(_propId)) return;

            if (_multiMaterialEnabled || _maxMaterialSlots > 1)
            {
                int effSlots = _multiMaterialEnabled
                    ? Math.Max(_forcedMaterialSlots, _maxMaterialSlots)
                    : _maxMaterialSlots;
                var perSlotToSave = new List<string>(effSlots);
                for (int s = 0; s < effSlots; s++)
                {
                    string sel = s < _perSlotSelected.Count ? _perSlotSelected[s] : string.Empty;
                    perSlotToSave.Add(_slotHasExplicitOverride.Contains(s) ? sel : string.Empty);
                }
                int slotCountToSave = _multiMaterialEnabled ? _forcedMaterialSlots : 0;
                var multiInfo = Apply(_propId, _displayName, _category, _excluded, _useRenderMeshCollider,
                    string.Empty, _defaultMaterialName, string.Empty, _surfaceType, _disabledRendererPaths,
                    _colliderIgnoredSubmeshes, perSlotToSave, slotCountToSave, _isBush, _bushRadius, _soundGrassType,
                    _keepOriginalHierarchy);
                if (multiInfo != null)
                    _index = multiInfo.index;
                _materialExplicitlyChosen = false;
                return;
            }

            string overrideToSave = GetOverrideToSave();
            _knownMaterialSources.TryGetValue(overrideToSave ?? string.Empty, out string srcPropId);
            var info = Apply(_propId, _displayName, _category, _excluded, _useRenderMeshCollider,
                overrideToSave, _defaultMaterialName, srcPropId, _surfaceType, _disabledRendererPaths,
                _colliderIgnoredSubmeshes, null, 0, _isBush, _bushRadius, _soundGrassType,
                _keepOriginalHierarchy);
            if (info != null)
                _index = info.index;
            _overrideMaterialName = overrideToSave ?? string.Empty;
            _materialExplicitlyChosen = false;
        }

        static string GetOverrideToSave()
        {
            if (string.IsNullOrEmpty(_selectedMaterialName)) return string.Empty;

            if (string.Equals(_selectedMaterialName, NoOverrideLabel, StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            // Only treat selected == default as "no override" when:
            //   • the user did NOT explicitly pick it this session, AND
            //   • it doesn't match a previously-saved override name (which would mean the renderer is
            //     contaminated — the game persisted the override and CacheDefaultMaterials read it as
            //     the native material).
            if (!string.IsNullOrEmpty(_defaultMaterialName)
                && string.Equals(_selectedMaterialName, _defaultMaterialName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(_selectedMaterialName, _overrideMaterialName, StringComparison.OrdinalIgnoreCase)
                && !_materialExplicitlyChosen)
                return string.Empty;

            return _selectedMaterialName;
        }

        static void SyncFromSelection(LevelEditorObject selectedObject)
        {
            _selectedLEO = selectedObject;
            string id = GetSelectedPropId(selectedObject);
            if (string.Equals(id, _propId, StringComparison.Ordinal)) return;

            if (_dirty && !string.IsNullOrEmpty(_propId))
            {
                ApplyCurrent();
                _dirty = false;
            }

            _propId = id;
            _displayName = string.Empty;
            _category = string.Empty;
            _overrideMaterialName = string.Empty;
            _selectedMaterialName = string.Empty;
            _defaultMaterialName = string.Empty;
            CacheDefaultMaterials(selectedObject);
            _excluded = false;
            _useRenderMeshCollider = false;
            _colliderIgnoredSubmeshes = string.Empty;
            _surfaceType = string.Empty;
            _isBush = false;
            _bushRadius = 0f;
            _soundGrassType = 1;
            _showGrassTypeDropdown = false;
            _keepOriginalHierarchy = false;
            _index = -1;
            _dirty = false;
            _materialExplicitlyChosen = false;
            _showMaterialDropdown = false;
            _showRendererDropdown = false;
            _showSurfaceTypeDropdown = false;
            _disabledRendererPaths.Clear();
            _rendererEntries.Clear();
            _perSlotSelected.Clear();
            _slotHasExplicitOverride.Clear();
            _openSlotDropdown = -1;
            _slotDropdownSearch = string.Empty;
            _multiMaterialEnabled = false;
            _forcedMaterialSlots = 2;
            _forcedMaterialSlotsStr = "2";

            if (string.IsNullOrEmpty(id)) return;

            var propLibInfo = PropLibrary.FindById(id);
            _useRenderMeshCollider = propLibInfo == null || !propLibInfo.HasColliderParts;

            List<string> savedSlots = null;
            if (TryGet(id, out var info) && info != null)
            {
                _displayName = info.displayName ?? string.Empty;
                _category = info.category ?? string.Empty;
                _overrideMaterialName = info.overrideMaterialId ?? string.Empty;
                _surfaceType = info.surfaceType ?? string.Empty;
                _excluded = info.excluded;
                _useRenderMeshCollider = info.useRenderMeshCollider;
                _colliderIgnoredSubmeshes = info.colliderIgnoredSubmeshes ?? string.Empty;
                _isBush = info.isBush;
                _bushRadius = info.bushRadius;
                _soundGrassType = info.soundGrassType;
                _keepOriginalHierarchy = info.keepOriginalHierarchy;
                _index = info.index;
                if (info.disabledRenderers != null)
                {
                    for (int i = 0; i < info.disabledRenderers.Count; i++)
                    {
                        var path = info.disabledRenderers[i];
                        if (!string.IsNullOrEmpty(path)) _disabledRendererPaths.Add(path);
                    }
                }
                if (info.forcedMaterialSlots > 1)
                {
                    _multiMaterialEnabled = true;
                    _forcedMaterialSlots = info.forcedMaterialSlots;
                    _forcedMaterialSlotsStr = _forcedMaterialSlots.ToString();
                }
                savedSlots = info.perSlotMaterialOverrides;
            }

            _selectedMaterialName = string.IsNullOrEmpty(_overrideMaterialName)
                ? _defaultMaterialName
                : _overrideMaterialName;

            int effectiveSlots = _multiMaterialEnabled
                ? Math.Max(_forcedMaterialSlots, _maxMaterialSlots)
                : _maxMaterialSlots;

            while (_perSlotDefault.Count < effectiveSlots) _perSlotDefault.Add(string.Empty);
            for (int s = 0; s < effectiveSlots; s++)
            {
                string saved = savedSlots != null && s < savedSlots.Count ? savedSlots[s] ?? string.Empty : string.Empty;
                string def = s < _perSlotDefault.Count ? _perSlotDefault[s] : string.Empty;
                _perSlotSelected.Add(string.IsNullOrEmpty(saved) ? def : saved);
                if (!string.IsNullOrEmpty(saved))
                    _slotHasExplicitOverride.Add(s);
            }

            BuildRendererEntries(selectedObject);
            ApplyRendererVisibility();
            EnsureMaterialList();
            AddRendererMaterialsToList();

            // GPUI props have no live renderers — scan their loaded parts for native materials.
            // Load on first selection so source recording works even for old-format entries.
            if (propLibInfo != null && propLibInfo.IsGpui)
            {
                if (!propLibInfo.isLoaded) PropLibrary.LoadPropData(propLibInfo);
                if (propLibInfo.isLoaded) AddPartsToMaterialList(propLibInfo);
            }

            // If any saved override material (single-slot or per-slot) is still not in the list,
            // do a fresh Resources scan — GPUI loads material instances lazily as the player moves,
            // so a material may have entered memory after EnsureMaterialSources ran at startup.
            // Also try the catalog path for any per-slot override still absent after the scan.
            bool needsFreshScan = (!string.IsNullOrEmpty(_overrideMaterialName)
                                   && !_materialByName.ContainsKey(_overrideMaterialName));
            if (!needsFreshScan)
            {
                foreach (var s in _slotHasExplicitOverride)
                {
                    string slotMat = s < _perSlotSelected.Count ? _perSlotSelected[s] : string.Empty;
                    if (!string.IsNullOrEmpty(slotMat) && !_materialByName.ContainsKey(slotMat))
                    { needsFreshScan = true; break; }
                }
            }

            if (needsFreshScan)
            {
                try
                {
                    var allMats = Resources.FindObjectsOfTypeAll<Material>();
                    for (int i = 0; i < allMats.Length; i++)
                    {
                        var m = allMats[i];
                        if (m == null || string.IsNullOrEmpty(m.name)) continue;
                        if (ShouldHideMaterial(m.name)) continue;
                        if (_materialByName.ContainsKey(m.name)) continue;
                        string shaderName = m.shader != null ? m.shader.name : string.Empty;
                        string label = string.IsNullOrEmpty(shaderName)
                            ? m.name
                            : $"{m.name}  [{shaderName}]";
                        _materialNames.Add(m.name);
                        _materialLabels.Add(label);
                        _materialByName[m.name] = m;
                    }
                }
                catch { }

                // Single-slot: if the override is still absent after the Resources scan, try the
                // catalog. This handles saved names with "(Instance)" suffixes where the scan
                // added the material under the clean name but the saved name still has no entry.
                if (!string.IsNullOrEmpty(_overrideMaterialName) && !_materialByName.ContainsKey(_overrideMaterialName)
                    && !_overrideMaterialName.StartsWith("[MicroSplat]", StringComparison.Ordinal))
                {
                    try
                    {
                        var mat = PropLibrary.TryLoadMaterialByName(_overrideMaterialName);
                        if (mat != null)
                        {
                            if (!_materialByName.ContainsKey(mat.name))
                            {
                                string shaderName = mat.shader != null ? mat.shader.name : string.Empty;
                                string label = string.IsNullOrEmpty(shaderName) ? mat.name : $"{mat.name}  [{shaderName}]";
                                _materialNames.Add(mat.name);
                                _materialLabels.Add(label);
                                _materialByName[mat.name] = mat;
                            }
                            if (!string.Equals(mat.name, _overrideMaterialName, StringComparison.OrdinalIgnoreCase)
                                && !_materialByName.ContainsKey(_overrideMaterialName))
                                _materialByName[_overrideMaterialName] = mat;
                        }
                    }
                    catch { }
                }

                // Anything still missing after the Resources scan: try the catalog (covers
                // per-slot materials like Araucaria_Pine_SharedMat that are only in memory
                // near their native world area but are always findable via the catalog path).
                // Also handles saved names with "(Instance)" suffixes — TryLoadMaterialByName
                // strips those before searching, so we register the result under both names.
                foreach (var s in _slotHasExplicitOverride)
                {
                    string slotMat = s < _perSlotSelected.Count ? _perSlotSelected[s] : string.Empty;
                    if (string.IsNullOrEmpty(slotMat) || _materialByName.ContainsKey(slotMat)) continue;
                    if (slotMat.StartsWith("[MicroSplat]", StringComparison.Ordinal)) continue;
                    try
                    {
                        var mat = PropLibrary.TryLoadMaterialByName(slotMat);
                        if (mat == null) continue;
                        if (!_materialByName.ContainsKey(mat.name))
                        {
                            string shaderName = mat.shader != null ? mat.shader.name : string.Empty;
                            string label = string.IsNullOrEmpty(shaderName) ? mat.name : $"{mat.name}  [{shaderName}]";
                            _materialNames.Add(mat.name);
                            _materialLabels.Add(label);
                            _materialByName[mat.name] = mat;
                        }
                        // Alias so lookups using the saved "(Instance)"-suffixed name also resolve.
                        if (!string.Equals(mat.name, slotMat, StringComparison.OrdinalIgnoreCase)
                            && !_materialByName.ContainsKey(slotMat))
                            _materialByName[slotMat] = mat;
                    }
                    catch { }
                }
            }

            // If a native material name was persisted, use it to un-contaminate _defaultMaterialName.
            // CacheDefaultMaterials reads the live renderer, which may already have the override applied.
            if (TryGet(_propId, out var metaInfo) && metaInfo != null)
            {
                // Only trust nativeMaterialName when it differs from the override. If they match, the
                // value was stored when the renderer was contaminated (override already applied) and
                // using it would make ApplyPreviewMaterial treat the override as "restoring to default",
                // which either does nothing or restores a null material for GPUI props.
                if (!string.IsNullOrEmpty(metaInfo.nativeMaterialName)
                    && !string.Equals(metaInfo.nativeMaterialName, metaInfo.overrideMaterialId,
                                      StringComparison.OrdinalIgnoreCase))
                    _defaultMaterialName = metaInfo.nativeMaterialName;

                // Lazy migration: old entries have overrideMaterialId but no nativeMaterialName or
                // materialSourcePropId. Fill them in now while the renderer is fresh (after restart the
                // renderer shows the true original material, not the override).
                bool migrationDirty = false;
                if (!string.IsNullOrEmpty(metaInfo.overrideMaterialId))
                {
                    // Clean up stale nativeMaterialName == overrideMaterialId (stored when renderer was
                    // contaminated — useless and causes ApplyPreviewMaterial to misbehave).
                    if (!string.IsNullOrEmpty(metaInfo.nativeMaterialName)
                        && string.Equals(metaInfo.nativeMaterialName, metaInfo.overrideMaterialId,
                                         StringComparison.OrdinalIgnoreCase))
                    {
                        metaInfo.nativeMaterialName = string.Empty;
                        migrationDirty = true;
                    }

                    // If the renderer shows the override material (game persisted it), the default
                    // looks contaminated. Try to recover the true native from the prop's loaded parts.
                    if (string.IsNullOrEmpty(metaInfo.nativeMaterialName)
                        && string.Equals(_defaultMaterialName, metaInfo.overrideMaterialId,
                                         StringComparison.OrdinalIgnoreCase))
                    {
                        string recovered = FindNativeFromParts(propLibInfo, metaInfo.overrideMaterialId);
                        if (!string.IsNullOrEmpty(recovered))
                            _defaultMaterialName = recovered;
                    }

                    if (string.IsNullOrEmpty(metaInfo.nativeMaterialName)
                        && !string.IsNullOrEmpty(_defaultMaterialName)
                        && !string.Equals(_defaultMaterialName, metaInfo.overrideMaterialId,
                                          StringComparison.OrdinalIgnoreCase))
                    {
                        metaInfo.nativeMaterialName = _defaultMaterialName;
                        migrationDirty = true;
                    }

                    if (string.IsNullOrEmpty(metaInfo.materialSourcePropId)
                        && _knownMaterialSources.TryGetValue(metaInfo.overrideMaterialId, out string knownSrc)
                        && !string.IsNullOrEmpty(knownSrc))
                    {
                        if (BackfillMaterialSource(metaInfo.overrideMaterialId, knownSrc))
                            migrationDirty = true;
                    }
                }

                if (migrationDirty) Save();
            }

            if (_multiMaterialEnabled || _maxMaterialSlots > 1)
                ApplyAllSlotMaterials();
            else
                ApplyPreviewMaterial(_selectedMaterialName);
            ApplySurfaceType(selectedObject, _surfaceType);
        }

        static void CacheDefaultMaterials(LevelEditorObject obj)
        {
            _selectedRenderers = null;
            _selectedDefaultMaterials = null;
            if (obj == null) return;

            try
            {
                var renderers = obj.GetComponentsInChildren<Renderer>(true);
                if (renderers == null || renderers.Length == 0) return;
                _selectedRenderers = renderers;
                _selectedDefaultMaterials = new Material[renderers.Length][];
                _defaultMaterialName = string.Empty;
                for (int i = 0; i < renderers.Length; i++)
                {
                    var r = renderers[i];
                    if (r == null) continue;
                    var mats = r.sharedMaterials;
                    if (mats == null || mats.Length == 0)
                    {
                        var single = r.sharedMaterial;
                        if (single != null)
                        {
                            _selectedDefaultMaterials[i] = new[] { single };
                            if (string.IsNullOrEmpty(_defaultMaterialName))
                                _defaultMaterialName = single.name ?? string.Empty;
                        }
                        else
                        {
                            _selectedDefaultMaterials[i] = null;
                        }
                    }
                    else
                    {
                        var copy = new Material[mats.Length];
                        for (int m = 0; m < mats.Length; m++)
                            copy[m] = mats[m];
                        _selectedDefaultMaterials[i] = copy;

                        if (string.IsNullOrEmpty(_defaultMaterialName))
                        {
                            for (int m = 0; m < copy.Length; m++)
                            {
                                var mat = copy[m];
                                if (mat != null && !string.IsNullOrEmpty(mat.name))
                                {
                                    _defaultMaterialName = mat.name;
                                    break;
                                }
                            }
                        }
                    }
                }

                // Compute max material slot count and per-slot default names.
                _maxMaterialSlots = 0;
                _perSlotDefault.Clear();
                for (int i = 0; i < _selectedDefaultMaterials.Length; i++)
                {
                    var m = _selectedDefaultMaterials[i];
                    if (m != null && m.Length > _maxMaterialSlots)
                        _maxMaterialSlots = m.Length;
                }
                for (int s = 0; s < _maxMaterialSlots; s++)
                    _perSlotDefault.Add(string.Empty);
                for (int i = 0; i < _selectedDefaultMaterials.Length; i++)
                {
                    var m = _selectedDefaultMaterials[i];
                    if (m == null || m.Length != _maxMaterialSlots) continue;
                    for (int s = 0; s < _maxMaterialSlots; s++)
                        _perSlotDefault[s] = m[s] != null ? m[s].name ?? string.Empty : string.Empty;
                    break;
                }
            }
            catch { }
        }

        class RendererEntry
        {
            public string path;
            public Renderer renderer;
            public Collider collider;
            public bool enabled;
        }

        static void BuildRendererEntries(LevelEditorObject obj)
        {
            _rendererEntries.Clear();
            if (obj == null) return;

            var root = obj.transform;

            if (_selectedRenderers != null)
            {
                for (int i = 0; i < _selectedRenderers.Length; i++)
                {
                    var r = _selectedRenderers[i];
                    if (r == null) continue;
                    string path = BuildPath(root, r.transform);
                    var entry = new RendererEntry
                    {
                        path = path,
                        renderer = r,
                        enabled = r.enabled
                    };
                    if (_disabledRendererPaths.Contains(path)) entry.enabled = false;
                    _rendererEntries.Add(entry);
                }
            }

            var colliders = obj.GetComponentsInChildren<Collider>(true);
            if (colliders != null)
            {
                for (int i = 0; i < colliders.Length; i++)
                {
                    var c = colliders[i];
                    if (c == null) continue;
                    string path = BuildPath(root, c.transform) + " [" + c.GetType().Name + "]";
                    var entry = new RendererEntry
                    {
                        path = path,
                        collider = c,
                        enabled = c.enabled
                    };
                    if (_disabledRendererPaths.Contains(path)) entry.enabled = false;
                    _rendererEntries.Add(entry);
                }
            }
        }

        static void ApplyBushCollider(LevelEditorObject leo, bool enable)
        {
            if (leo == null) return;
            var root = leo.gameObject;

            var existingBush = root.GetComponent<Il2Cpp.BushCollider>();
            if (existingBush != null) UnityEngine.Object.DestroyImmediate(existingBush);
            var existingSphere = root.GetComponent<SphereCollider>();
            if (existingSphere != null) UnityEngine.Object.DestroyImmediate(existingSphere);

            BushAudioTracker.Unregister(root.transform);
            // Restore / set trigger state on all physics colliders (BushCollider sphere excluded,
            // since it hasn't been added yet and its isTrigger is set explicitly below).
            SetBushPassthrough(root, enable);

            if (!enable) { _bushRadius = 0f; return; }

            _bushRadius = ComputeBushRadius(leo);
            BushAudioTracker.Register(root.transform, _bushRadius, _soundGrassType);
            var sphere = root.AddComponent<SphereCollider>();
            sphere.radius = _bushRadius;
            sphere.isTrigger = true;
            var bush = root.AddComponent<Il2Cpp.BushCollider>();
            // Set rad immediately; BushCollider.Start() mirrors this but may not have run yet.
            bush.rad = sphere.radius * root.transform.localScale.x;
        }

        static void SetBushPassthrough(GameObject root, bool passthrough)
        {
            foreach (var col in root.GetComponentsInChildren<Collider>(true))
            {
                if (col == null) continue;
                // Leave the BushCollider's own SphereCollider alone (already a trigger, handled separately).
                if (col.gameObject.GetComponent<Il2Cpp.BushCollider>() != null) continue;
                col.isTrigger = passthrough;
            }
        }

        static float ComputeBushRadius(LevelEditorObject leo)
        {
            var bounds = new Bounds();
            bool first = true;
            foreach (var r in leo.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (first) { bounds = r.bounds; first = false; }
                else bounds.Encapsulate(r.bounds);
            }
            if (first) return 1f;
            // BushCollider.Start() multiplies sphere.radius * localScale.x, so store in local space
            float worldRadius = Mathf.Max(bounds.extents.x, bounds.extents.z);
            float scale = Mathf.Max(0.001f, leo.transform.lossyScale.x);
            return worldRadius / scale;
        }

        public static void ApplyBushColliderToRoot(string propId, GameObject root)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(propId) || root == null) return;
            if (!_byId.TryGetValue(propId, out var info) || !info.isBush) return;

            var existingBush = root.GetComponent<Il2Cpp.BushCollider>();
            if (existingBush != null) UnityEngine.Object.DestroyImmediate(existingBush);
            var existingSphere = root.GetComponent<SphereCollider>();
            if (existingSphere != null) UnityEngine.Object.DestroyImmediate(existingSphere);

            SetBushPassthrough(root, true);

            float radius = info.bushRadius > 0f ? info.bushRadius : 1f;
            int grassType = info.soundGrassType > 0 ? info.soundGrassType : 1;
            BushAudioTracker.Register(root.transform, radius, grassType);
            var sphere = root.AddComponent<SphereCollider>();
            sphere.radius = radius;
            sphere.isTrigger = true;
            var bush = root.AddComponent<Il2Cpp.BushCollider>();
            bush.rad = sphere.radius * root.transform.localScale.x;
        }

        public static bool GetIsBush(string id)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return false;
            return _byId.TryGetValue(id, out var info) && info.isBush;
        }

        public static bool GetKeepOriginalHierarchy(string id)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return false;
            return _byId.TryGetValue(id, out var info) && info.keepOriginalHierarchy;
        }

        // Tracks editor bush spheres so the GetGrassAt Harmony patch can return a grass type
        // for positions inside them, enabling BodyCollisions rustle and PlayerMovement plant sounds.
        internal static class BushAudioTracker
        {
            static readonly List<(Transform t, float localRad, int grassType)> _bushes = new();

            public static void Register(Transform t, float localRad, int grassType = 1)
            {
                if (t != null) _bushes.Add((t, localRad, grassType));
            }

            public static void Unregister(Transform t)
            {
                for (int i = _bushes.Count - 1; i >= 0; i--)
                    if (_bushes[i].t == t) { _bushes.RemoveAt(i); return; }
            }

            // Returns the GrassType int of the first bush sphere containing pos, or 0 (none) if outside all.
            public static int GetGrassTypeAtPos(Vector3 pos)
            {
                for (int i = _bushes.Count - 1; i >= 0; i--)
                {
                    var (t, localRad, grassType) = _bushes[i];
                    if (t == null) { _bushes.RemoveAt(i); continue; }
                    float worldRad = localRad * Mathf.Max(0.001f, t.lossyScale.x);
                    if ((pos - t.position).sqrMagnitude < worldRad * worldRad) return grassType;
                }
                return 0;
            }
        }

        static void ApplyColliderToSelected(bool enable)
        {
            if (_selectedLEO == null) return;

            var info = PropLibrary.FindById(_selectedLEO.addressableKey);

            var toDestroy = new System.Collections.Generic.List<GameObject>();
            foreach (var t in _selectedLEO.GetComponentsInChildren<Transform>(true))
                if (t != null && t != _selectedLEO.transform && t.gameObject.name.StartsWith("PropCollider"))
                    toDestroy.Add(t.gameObject);
            foreach (var go in toDestroy)
                if (go != null) UnityEngine.Object.DestroyImmediate(go);

            var stale = new System.Collections.Generic.List<string>();
            foreach (var p in _disabledRendererPaths)
                if (p.Contains("PropCollider")) stale.Add(p);
            foreach (var p in stale) _disabledRendererPaths.Remove(p);

            if (info != null)
                LevelEditorManager.ApplyColliderParts(_selectedLEO.gameObject, info, enable);
        }

        static void ApplyRendererVisibility()
        {
            if (_rendererEntries.Count == 0) return;
            for (int i = 0; i < _rendererEntries.Count; i++)
            {
                var entry = _rendererEntries[i];
                bool enabled = !_disabledRendererPaths.Contains(entry.path);
                if (entry.renderer != null) entry.renderer.enabled = enabled;
                else if (entry.collider != null) entry.collider.enabled = enabled;
                entry.enabled = enabled;
            }
            // TODO: Skip instantiating disabled renderers when spawning a new object.
        }

        static string BuildPath(Transform root, Transform t)
        {
            if (t == null) return "(null)";
            if (root == null) return t.name;

            var parts = new List<string>();
            var cur = t;
            while (cur != null)
            {
                parts.Add(cur.name);
                if (cur == root) break;
                cur = cur.parent;
            }
            parts.Reverse();

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) sb.Append('/');
                sb.Append(parts[i]);
            }
            return sb.ToString();
        }

        static void EnsureMaterialButtonStyle()
        {
            if (_materialButtonStyle != null) return;
            var padding = new RectOffset();
            padding.left = 6;
            padding.right = 6;
            padding.top = 2;
            padding.bottom = 2;
            _materialButtonStyle = new GUIStyle(GUI.skin.button)
            {
                alignment = TextAnchor.MiddleLeft,
                padding = padding
            };
        }

        static void ApplyPreviewMaterial(string materialName)
        {
            if (_selectedRenderers == null || _selectedRenderers.Length == 0) return;

            if (string.IsNullOrEmpty(materialName)
                || string.Equals(materialName, NoOverrideLabel, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(_defaultMaterialName)
                    && string.Equals(materialName, _defaultMaterialName, StringComparison.OrdinalIgnoreCase)))
            {
                RestoreDefaultMaterials();
                return;
            }

            if (!_materialByName.TryGetValue(materialName, out var mat) || mat == null)
            {
                mat = PropLibrary.TryLoadMaterialByName(materialName);
                if (mat != null)
                {
                    if (!_materialByName.ContainsKey(mat.name)) _materialByName[mat.name] = mat;
                    if (!string.Equals(mat.name, materialName, StringComparison.OrdinalIgnoreCase)
                        && !_materialByName.ContainsKey(materialName))
                        _materialByName[materialName] = mat;
                }
            }
            if (mat == null) { RestoreDefaultMaterials(); return; }
            for (int i = 0; i < _selectedRenderers.Length; i++)
            {
                var r = _selectedRenderers[i];
                if (r == null) continue;
                int count = 1;
                var existing = r.sharedMaterials;
                if (existing != null && existing.Length > 0) count = existing.Length;
                var mats = new Material[count];
                for (int m = 0; m < count; m++) mats[m] = mat;
                r.sharedMaterials = mats;
            }
        }

        static void RestoreDefaultMaterials()
        {
            if (_selectedDefaultMaterials == null) return;
            for (int i = 0; i < _selectedRenderers.Length; i++)
            {
                var r = _selectedRenderers[i];
                if (r == null) continue;
                var mats = _selectedDefaultMaterials[i];
                if (mats == null) continue;
                var copy = new Material[mats.Length];
                for (int m = 0; m < mats.Length; m++)
                    copy[m] = mats[m];
                r.sharedMaterials = copy;
            }
        }

        static void ApplySlotMaterial(int slot, string materialName)
        {
            if (_selectedRenderers == null) return;
            bool restore = string.IsNullOrEmpty(materialName)
                || string.Equals(materialName, NoOverrideLabel, StringComparison.OrdinalIgnoreCase)
                || (slot < _perSlotDefault.Count && string.Equals(materialName, _perSlotDefault[slot], StringComparison.OrdinalIgnoreCase));

            for (int i = 0; i < _selectedRenderers.Length; i++)
            {
                var r = _selectedRenderers[i];
                if (r == null) continue;
                var mats = r.sharedMaterials;
                if (mats == null) mats = new Material[0];

                // Expand the array if the slot exceeds what the renderer currently has.
                if (slot >= mats.Length)
                {
                    if (restore) continue; // nothing to restore for a slot that doesn't exist yet
                    var expanded = new Material[slot + 1];
                    for (int m = 0; m < mats.Length; m++) expanded[m] = mats[m];
                    mats = expanded;
                }

                if (restore)
                {
                    var defaults = _selectedDefaultMaterials?[i];
                    mats[slot] = defaults != null && slot < defaults.Length ? defaults[slot] : null;
                }
                else
                {
                    if (!_materialByName.TryGetValue(materialName, out var mat) || mat == null)
                    {
                        mat = PropLibrary.TryLoadMaterialByName(materialName);
                        if (mat != null)
                        {
                            if (!_materialByName.ContainsKey(mat.name)) _materialByName[mat.name] = mat;
                            if (!string.Equals(mat.name, materialName, StringComparison.OrdinalIgnoreCase)
                                && !_materialByName.ContainsKey(materialName))
                                _materialByName[materialName] = mat;
                        }
                    }
                    if (mat == null) continue;
                    mats[slot] = mat;
                }
                r.sharedMaterials = mats;
            }
        }

        static void ApplyAllSlotMaterials()
        {
            for (int s = 0; s < _perSlotSelected.Count; s++)
                ApplySlotMaterial(s, _perSlotSelected[s]);
        }

        static void MarkDirty()
        {
            _dirty = true;
            _lastChangeTime = Time.unscaledTime;
        }

        static void AutoSaveIfIdle()
        {
            if (!_dirty || string.IsNullOrEmpty(_propId)) return;
            if (Time.unscaledTime - _lastChangeTime < AutoSaveDelay) return;

            ApplyCurrent();
            _dirty = false;
        }

        public static void SetPaletteSelection(string propId)
        {
            _paletteSelectedId = propId;
        }

        static string GetSelectedPropId(LevelEditorObject obj)
        {
            if (obj != null)
            {
                _paletteSelectedId = null;
                if (!string.IsNullOrEmpty(obj.addressableKey)) return obj.addressableKey;
                if (!string.IsNullOrEmpty(obj.objectType)) return "primitive://" + obj.objectType;
                return null;
            }
            return string.IsNullOrEmpty(_paletteSelectedId) ? null : _paletteSelectedId;
        }

        static void EnsureWindowRect()
        {
            if (_windowInitialized) return;
            float width = 410f;
            float height = 700f;
            float x = Screen.width - width - 10f;
            if (x < 10f) x = 10f;
            _windowRect = new Rect(x, 10f, width, height);
            _windowInitialized = true;
        }

        static void HandleDrag(Rect headerRect)
        {
            var e = Event.current;
            if (e == null) return;

            if (e.type == EventType.MouseDown && e.button == 0 && headerRect.Contains(e.mousePosition))
            {
                _windowDragging = true;
                _windowDragOffset = e.mousePosition - new Vector2(_windowRect.x, _windowRect.y);
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && _windowDragging)
            {
                _windowRect.x = e.mousePosition.x - _windowDragOffset.x;
                _windowRect.y = e.mousePosition.y - _windowDragOffset.y;

                float maxX = Mathf.Max(0f, Screen.width - _windowRect.width);
                float maxY = Mathf.Max(0f, Screen.height - _windowRect.height);
                _windowRect.x = Mathf.Clamp(_windowRect.x, 0f, maxX);
                _windowRect.y = Mathf.Clamp(_windowRect.y, 0f, maxY);

                e.Use();
            }
            else if (e.type == EventType.MouseUp && e.button == 0)
            {
                _windowDragging = false;
            }
        }

        static void EnsureLoaded()
        {
            if (_loaded) return;
            Load();
        }

        static bool TryGetInfoById(string id, out PropExtraInfo info)
        {
            info = null;
            if (string.IsNullOrEmpty(id)) return false;
            if (_byId.TryGetValue(id, out info)) return true;

            string canonical = PropLibrary.ResolveCanonicalId(id);
            return !string.IsNullOrEmpty(canonical) && _byId.TryGetValue(canonical, out info);
        }

        static bool MergeItem(PropExtraInfo target, PropExtraInfo source)
        {
            if (target == null || source == null || ReferenceEquals(target, source)) return false;

            bool changed = false;

            if (target.index <= 0 && source.index > 0) { target.index = source.index; changed = true; }
            if (string.IsNullOrEmpty(target.displayName) && !string.IsNullOrEmpty(source.displayName)) { target.displayName = source.displayName; changed = true; }
            if (string.IsNullOrEmpty(target.category) && !string.IsNullOrEmpty(source.category)) { target.category = source.category; changed = true; }
            if (string.IsNullOrEmpty(target.colliderIgnoredSubmeshes) && !string.IsNullOrEmpty(source.colliderIgnoredSubmeshes)) { target.colliderIgnoredSubmeshes = source.colliderIgnoredSubmeshes; changed = true; }
            if (string.IsNullOrEmpty(target.overrideMaterialId) && !string.IsNullOrEmpty(source.overrideMaterialId)) { target.overrideMaterialId = source.overrideMaterialId; changed = true; }
            if (string.IsNullOrEmpty(target.nativeMaterialName) && !string.IsNullOrEmpty(source.nativeMaterialName)) { target.nativeMaterialName = source.nativeMaterialName; changed = true; }
            if (string.IsNullOrEmpty(target.materialSourcePropId) && !string.IsNullOrEmpty(source.materialSourcePropId)) { target.materialSourcePropId = source.materialSourcePropId; changed = true; }
            if (string.IsNullOrEmpty(target.surfaceType) && !string.IsNullOrEmpty(source.surfaceType)) { target.surfaceType = source.surfaceType; changed = true; }
            if (!target.useRenderMeshCollider && source.useRenderMeshCollider) { target.useRenderMeshCollider = true; changed = true; }
            if (target.forcedMaterialSlots <= 1 && source.forcedMaterialSlots > 1) { target.forcedMaterialSlots = source.forcedMaterialSlots; changed = true; }

            if ((target.disabledRenderers == null || target.disabledRenderers.Count == 0)
                && source.disabledRenderers != null && source.disabledRenderers.Count > 0)
            {
                target.disabledRenderers = new List<string>(source.disabledRenderers);
                changed = true;
            }

            if (!HasNonEmptySlot(target.perSlotMaterialOverrides) && HasNonEmptySlot(source.perSlotMaterialOverrides))
            {
                target.perSlotMaterialOverrides = new List<string>(source.perSlotMaterialOverrides);
                changed = true;
            }

            if (!target.excluded && source.excluded)
            {
                target.excluded = true;
                changed = true;
            }

            if (!target.isBush && source.isBush) { target.isBush = true; changed = true; }
            if (target.bushRadius <= 0f && source.bushRadius > 0f) { target.bushRadius = source.bushRadius; changed = true; }

            return changed;
        }

        public static void MigratePropIdsToCanonical()
        {
            EnsureLoaded();
            if (_byId.Count == 0) return;

            bool changed = false;
            var remaps = new List<(string fromId, string toId, PropExtraInfo item)>();
            foreach (var kvp in _byId)
            {
                string canonical = PropLibrary.ResolveCanonicalId(kvp.Key);
                if (string.IsNullOrEmpty(canonical) || string.Equals(canonical, kvp.Key, StringComparison.Ordinal))
                    continue;
                remaps.Add((kvp.Key, canonical, kvp.Value));
            }

            for (int i = 0; i < remaps.Count; i++)
            {
                var remap = remaps[i];
                if (!_byId.TryGetValue(remap.fromId, out var source))
                    continue;

                _byId.Remove(remap.fromId);
                changed = true;

                source.id = remap.toId;
                if (_byId.TryGetValue(remap.toId, out var existing))
                {
                    if (MergeItem(existing, source)) changed = true;
                }
                else
                {
                    _byId[remap.toId] = source;
                }
            }

            foreach (var item in _byId.Values)
            {
                if (item == null || string.IsNullOrEmpty(item.materialSourcePropId)) continue;
                string canonicalSource = PropLibrary.ResolveCanonicalId(item.materialSourcePropId);
                if (string.IsNullOrEmpty(canonicalSource)
                    || string.Equals(canonicalSource, item.materialSourcePropId, StringComparison.Ordinal))
                    continue;

                item.materialSourcePropId = canonicalSource;
                changed = true;
            }

            if (changed) Save();
        }

        public static bool GetUseRenderMeshCollider(string id)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return false;
            if (!_byId.TryGetValue(id, out var info))
            {
                var propInfo = PropLibrary.FindById(id);
                return propInfo == null || !propInfo.HasColliderParts;
            }
            return info.useRenderMeshCollider;
        }

        public static HashSet<int> GetColliderIgnoredSubmeshes(string id)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return null;
            if (!_byId.TryGetValue(id, out var info)) return null;
            return ParseIntSet(info.colliderIgnoredSubmeshes);
        }

        public static string GetSurfaceType(string id)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return string.Empty;
            if (!_byId.TryGetValue(id, out var info)) return string.Empty;
            return info.surfaceType ?? string.Empty;
        }

        public static void ApplySurfaceType(LevelEditorObject leo, string surfaceTag)
        {
            if (leo == null) return;
            try
            {
                string tag = string.IsNullOrEmpty(surfaceTag) ? "Untagged" : surfaceTag;
                SetTagSafe(leo.gameObject, tag);
                foreach (var col in leo.GetComponentsInChildren<Collider>(true))
                {
                    if (col != null) SetTagSafe(col.gameObject, tag);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropMetadata] ApplySurfaceType failed: {e.Message}");
            }
        }

        public static void ApplySurfaceTypeToRoot(GameObject root, string surfaceTag)
        {
            if (root == null) return;
            try
            {
                string tag = string.IsNullOrEmpty(surfaceTag) ? "Untagged" : surfaceTag;
                SetTagSafe(root, tag);
                foreach (var col in root.GetComponentsInChildren<Collider>(true))
                {
                    if (col != null) SetTagSafe(col.gameObject, tag);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropMetadata] ApplySurfaceTypeToRoot failed: {e.Message}");
            }
        }

        static void SetTagSafe(GameObject go, string tag)
        {
            try { go.tag = tag; } catch { }
        }

        public static void ApplyDisabledRenderersToRoot(string propId, GameObject root)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(propId) || root == null) return;
            if (!_byId.TryGetValue(propId, out var info)) return;
            if (info.disabledRenderers == null || info.disabledRenderers.Count == 0) return;

            foreach (var path in info.disabledRenderers)
            {
                if (string.IsNullOrEmpty(path)) continue;
                // Path format: "RootName/Child" — strip the leading root-name segment.
                int slashIdx = path.IndexOf('/');
                string subPath = slashIdx >= 0 ? path.Substring(slashIdx + 1) : path;
                if (string.IsNullOrEmpty(subPath)) continue;
                var t = root.transform.Find(subPath);
                if (t == null) continue;
                var r = t.GetComponent<Renderer>();
                if (r != null) r.enabled = false;
            }

            LevelEditorManager.NotifyVisualStateChanged(root);
        }

        public static void ApplyMaterialOverridesToRoot(string propId, GameObject root)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(propId) || root == null) return;
            if (!_byId.TryGetValue(propId, out var info)) return;

            // MicroSplat materials are runtime-generated; ensure they're in _materialByName even
            // when the metadata panel UI has never been shown (e.g. non-debug mode or first launch).
            AddMicroSplatLayerMaterials();

            // Per-slot overrides
            var overrides = info.perSlotMaterialOverrides;
            if (overrides != null && overrides.Count > 0)
            {
                var renderers = root.GetComponentsInChildren<Renderer>(true);
                if (renderers == null || renderers.Length == 0) return;

                for (int s = 0; s < overrides.Count; s++)
                {
                    string matName = overrides[s];
                    if (string.IsNullOrEmpty(matName)
                        || string.Equals(matName, NoOverrideLabel, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var mat = ResolveMaterial(matName);
                    if (mat == null) continue;

                    for (int i = 0; i < renderers.Length; i++)
                    {
                        var r = renderers[i];
                        if (r == null) continue;
                        var mats = r.sharedMaterials;
                        if (mats == null) mats = new Material[0];
                        if (s >= mats.Length)
                        {
                            var expanded = new Material[s + 1];
                            for (int m = 0; m < mats.Length; m++) expanded[m] = mats[m];
                            mats = expanded;
                        }
                        mats[s] = mat;
                        r.sharedMaterials = mats;
                    }
                }
                return;
            }

            // Single-slot override (overrideMaterialId)
            // This is the common case for props with one material slot: the override is stored
            // in overrideMaterialId, not perSlotMaterialOverrides. ApplyPreviewMaterial applies
            // the same material to every slot of every renderer, so we mirror that here.
            string singleOverride = info.overrideMaterialId;
            if (string.IsNullOrEmpty(singleOverride)
                || string.Equals(singleOverride, NoOverrideLabel, StringComparison.OrdinalIgnoreCase))
                return;

            {
                var singleMat = ResolveMaterial(singleOverride);
                if (singleMat == null) return;

                var renderers = root.GetComponentsInChildren<Renderer>(true);
                if (renderers == null || renderers.Length == 0) return;

                for (int i = 0; i < renderers.Length; i++)
                {
                    var r = renderers[i];
                    if (r == null) continue;
                    int count = 1;
                    var existing = r.sharedMaterials;
                    if (existing != null && existing.Length > 0) count = existing.Length;
                    var mats = new Material[count];
                    for (int m = 0; m < count; m++) mats[m] = singleMat;
                    r.sharedMaterials = mats;
                }
            }
        }

        // Looks up a material by name in the in-memory cache, falling back to a catalog load.
        // Caches the result so subsequent spawns of the same prop don't re-hit the catalog.
        static Material ResolveMaterial(string matName)
        {
            if (string.IsNullOrEmpty(matName)) return null;
            if (_materialByName.TryGetValue(matName, out var mat) && mat != null) return mat;

            mat = PropLibrary.TryLoadMaterialByName(matName);
            if (mat == null) return null;

            if (!_materialByName.ContainsKey(mat.name)) _materialByName[mat.name] = mat;
            // Also cache under the original saved name in case it differs (e.g. "(Instance)" suffix).
            if (!string.Equals(mat.name, matName, StringComparison.OrdinalIgnoreCase)
                && !_materialByName.ContainsKey(matName))
                _materialByName[matName] = mat;
            return mat;
        }

        public static bool HasMetadata(string id)
        {
            EnsureLoaded();
            return !string.IsNullOrEmpty(id) && _byId.ContainsKey(id);
        }

        public static bool IsExcluded(string id)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return false;
            return _byId.TryGetValue(id, out var info) && info.excluded;
        }

        // Returns true if the prop has been indexed but is only partially filled:
        // it has at least one but not all of displayName, category, surfaceType.
        public static bool IsPartiallyFilled(string id)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return false;
            if (!_byId.TryGetValue(id, out var info)) return false;
            if (info.excluded || info.index <= 0) return false;
            int count = 0;
            if (!string.IsNullOrEmpty(info.displayName)) count++;
            if (!string.IsNullOrEmpty(info.category)) count++;
            if (!string.IsNullOrEmpty(info.surfaceType)) count++;
            return count > 0 && count < 3;
        }

        static bool HasDuplicateDisplayName(string name, string excludeId)
        {
            foreach (var kvp in _byId)
            {
                if (string.Equals(kvp.Key, excludeId, StringComparison.Ordinal)) continue;
                if (string.Equals(kvp.Value.displayName, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // Called after ScanGpuiProps so EnsureMaterialSources can find GPUI prop entries.
        // If the material list is already built, re-run source discovery immediately with the
        // now-populated PropLibrary. If not built yet, the reset flag is picked up when
        // EnsureMaterialList eventually calls EnsureMaterialSources.
        public static void InvalidateMaterialSources()
        {
            _materialSourcesLoaded = false;
            if (_materialsLoaded)
                EnsureMaterialSources();
        }

        static bool TryGet(string id, out PropExtraInfo info)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id))
            {
                info = null;
                return false;
            }
            return _byId.TryGetValue(id, out info);
        }

        static bool HasNonEmptySlot(List<string> list)
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
                if (!string.IsNullOrEmpty(list[i])) return true;
            return false;
        }

        static PropExtraInfo Apply(string id, string displayName, string category,
            bool excluded, bool useRenderMeshCollider, string overrideMaterialName,
            string nativeMaterialName, string materialSourcePropId, string surfaceType,
            HashSet<string> disabledRenderers, string colliderIgnoredSubmeshes,
            List<string> perSlotOverrides = null, int forcedMaterialSlots = 0,
            bool isBush = false, float bushRadius = 0f, int soundGrassType = 1,
            bool keepOriginalHierarchy = false)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return null;

            bool any = !string.IsNullOrEmpty(displayName)
                    || !string.IsNullOrEmpty(category)
                    || excluded
                    || useRenderMeshCollider
                    || !string.IsNullOrEmpty(overrideMaterialName)
                    || !string.IsNullOrEmpty(surfaceType)
                    || (disabledRenderers != null && disabledRenderers.Count > 0)
                    || !string.IsNullOrEmpty(colliderIgnoredSubmeshes)
                    || HasNonEmptySlot(perSlotOverrides)
                    || forcedMaterialSlots > 1
                    || isBush
                    || keepOriginalHierarchy;

            if (!_byId.TryGetValue(id, out var info))
            {
                if (!any) return null;
                info = new PropExtraInfo { id = id };
                _byId[id] = info;
            }

            if (excluded)
            {
                info.displayName        = string.Empty;
                info.category           = string.Empty;
                info.excluded           = true;
                info.useRenderMeshCollider = false;
                info.colliderIgnoredSubmeshes = string.Empty;
                info.overrideMaterialId       = string.Empty;
                info.surfaceType              = string.Empty;
                info.disabledRenderers        = new List<string>();
                info.perSlotMaterialOverrides = null;
                info.forcedMaterialSlots      = 0;
                info.isBush                   = false;
                info.bushRadius               = 0f;
                info.soundGrassType           = 1;
                info.keepOriginalHierarchy    = false;
                info.index                    = 0;
            }
            else
            {
                if (info.index <= 0) info.index = _nextIndex++;
                info.displayName        = displayName ?? string.Empty;
                info.category           = category ?? string.Empty;
                info.excluded           = false;
                info.useRenderMeshCollider = useRenderMeshCollider;
                info.colliderIgnoredSubmeshes = colliderIgnoredSubmeshes ?? string.Empty;
                info.overrideMaterialId = overrideMaterialName ?? string.Empty;
                // Store the true original and source only once — when a non-empty override is first applied.
                // Clear both when the override is removed so they don't linger.
                if (!string.IsNullOrEmpty(overrideMaterialName))
                {
                    // Only store nativeMaterialName when it differs from the override; if they match
                    // the renderer was contaminated and the value would be useless for un-contamination.
                    if (string.IsNullOrEmpty(info.nativeMaterialName) && !string.IsNullOrEmpty(nativeMaterialName)
                        && !string.Equals(nativeMaterialName, overrideMaterialName, StringComparison.OrdinalIgnoreCase))
                        info.nativeMaterialName = nativeMaterialName;
                    if (string.IsNullOrEmpty(info.materialSourcePropId) && !string.IsNullOrEmpty(materialSourcePropId))
                        info.materialSourcePropId = materialSourcePropId;
                }
                else
                {
                    info.nativeMaterialName    = string.Empty;
                    info.materialSourcePropId  = string.Empty;
                }
                info.surfaceType        = surfaceType ?? string.Empty;
                info.disabledRenderers  = new List<string>();
                if (disabledRenderers != null)
                    foreach (var path in disabledRenderers) info.disabledRenderers.Add(path);
                info.perSlotMaterialOverrides = HasNonEmptySlot(perSlotOverrides) ? perSlotOverrides : null;
                info.forcedMaterialSlots = forcedMaterialSlots;
                info.isBush = isBush;
                info.bushRadius = bushRadius;
                info.soundGrassType = soundGrassType;
                info.keepOriginalHierarchy = keepOriginalHierarchy;
            }

            Save();
            return info;
        }

        static void Load()
        {
            _loaded = true;
            _byId.Clear();
            _nextIndex = 1;

            try
            {
                if (!File.Exists(SavePath)) return;
                var json = File.ReadAllText(SavePath);
                if (string.IsNullOrEmpty(json)) return;

                var data = Deserialize(json);
                if (data == null || data.items == null) return;

                _nextIndex = Math.Max(1, data.nextIndex);
                int maxIndex = _nextIndex - 1;
                foreach (var item in data.items)
                {
                    if (item == null || string.IsNullOrEmpty(item.id)) continue;
                    if (!item.excluded && item.index <= 0) item.index = _nextIndex++;
                    if (item.index > maxIndex) maxIndex = item.index;
                    _byId[item.id] = item;
                }
                _nextIndex = Math.Max(_nextIndex, maxIndex + 1);

                // One-time cleanup: MicroSplat materials are runtime-generated so they can't have
                // a real source prop. Clear any that were incorrectly recorded.
                bool anyFixed = false;
                foreach (var item in _byId.Values)
                {
                    if (string.IsNullOrEmpty(item.materialSourcePropId)) continue;
                    if (string.IsNullOrEmpty(item.overrideMaterialId)) continue;
                    if (!item.overrideMaterialId.StartsWith("[MicroSplat]", StringComparison.Ordinal)) continue;
                    item.materialSourcePropId = string.Empty;
                    anyFixed = true;
                }
                if (anyFixed) Save();
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropMetadata] Load failed: {e.Message}");
            }
        }

        static void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(SavePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var data = new PropExtraInfoSave
                {
                    nextIndex = _nextIndex,
                    items = new List<PropExtraInfo>(_byId.Values)
                };

                string json = Serialize(data);
                File.WriteAllText(SavePath, json);

                if (!_savePathLogged)
                {
                    _savePathLogged = true;
                    BBLog.Msg($"[PropMetadata] Saved to {SavePath}");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropMetadata] Save failed: {e.Message}");
            }
        }

        static string Serialize(PropExtraInfoSave data) => SerializeManual(data);

        static PropExtraInfoSave Deserialize(string json)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    IncludeFields = true
                };
                return JsonSerializer.Deserialize<PropExtraInfoSave>(json, options);
            }
            catch
            {
                return DeserializeManual(json);
            }
        }

        static string SerializeManual(PropExtraInfoSave data)
        {
            var sb = new System.Text.StringBuilder(1024);
            sb.Append("{\n  \"nextIndex\": ").Append(data.nextIndex).Append(",\n  \"items\": [\n");
            for (int i = 0; i < data.items.Count; i++)
            {
                var item = data.items[i];
                if (item == null) continue;
                sb.Append("    {\n");
                if (item.excluded)
                {
                    AppendJsonField(sb, "id", item.id, 6).Append(",\n");
                    sb.Append("      \"excluded\": true\n");
                }
                else
                {
                    AppendJsonField(sb, "id", item.id, 6).Append(",\n");
                    AppendJsonField(sb, "displayName", item.displayName, 6).Append(",\n");
                    AppendJsonField(sb, "category", item.category, 6).Append(",\n");
                    sb.Append("      \"excluded\": false,\n");
                    sb.Append("      \"useRenderMeshCollider\": ").Append(item.useRenderMeshCollider ? "true" : "false").Append(",\n");
                    AppendJsonField(sb, "colliderIgnoredSubmeshes", item.colliderIgnoredSubmeshes, 6).Append(",\n");
                    AppendJsonField(sb, "overrideMaterialId", item.overrideMaterialId, 6).Append(",\n");
                    AppendJsonField(sb, "nativeMaterialName", item.nativeMaterialName, 6).Append(",\n");
                    AppendJsonField(sb, "materialSourcePropId", item.materialSourcePropId, 6).Append(",\n");
                    AppendJsonField(sb, "surfaceType", item.surfaceType, 6).Append(",\n");
                    AppendJsonArray(sb, "disabledRenderers", item.disabledRenderers, 6).Append(",\n");
                    if (HasNonEmptySlot(item.perSlotMaterialOverrides))
                        AppendJsonArray(sb, "perSlotMaterialOverrides", item.perSlotMaterialOverrides, 6).Append(",\n");
                    if (item.forcedMaterialSlots > 1)
                        sb.Append("      \"forcedMaterialSlots\": ").Append(item.forcedMaterialSlots).Append(",\n");
                    if (item.isBush)
                    {
                        sb.Append("      \"isBush\": true,\n");
                        sb.Append("      \"bushRadius\": ").Append(item.bushRadius.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(",\n");
                        sb.Append("      \"soundGrassType\": ").Append(item.soundGrassType).Append(",\n");
                    }
                    if (item.keepOriginalHierarchy)
                        sb.Append("      \"keepOriginalHierarchy\": true,\n");
                    sb.Append("      \"index\": ").Append(item.index).Append("\n");
                }
                sb.Append("    }");
                if (i < data.items.Count - 1) sb.Append(",");
                sb.Append("\n");
            }
            sb.Append("  ]\n}");
            return sb.ToString();
        }

        static System.Text.StringBuilder AppendJsonField(System.Text.StringBuilder sb, string key, string value, int indent)
        {
            sb.Append(' ', indent).Append('"').Append(key).Append("\": ");
            AppendJsonString(sb, value ?? string.Empty);
            return sb;
        }

        static System.Text.StringBuilder AppendJsonArray(System.Text.StringBuilder sb, string key, List<string> values, int indent)
        {
            sb.Append(' ', indent).Append('"').Append(key).Append("\": [");
            if (values != null)
            {
                for (int i = 0; i < values.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    AppendJsonString(sb, values[i] ?? string.Empty);
                }
            }
            sb.Append(']');
            return sb;
        }

        static void AppendJsonString(System.Text.StringBuilder sb, string value)
        {
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        static PropExtraInfoSave DeserializeManual(string json)
        {
            var data = new PropExtraInfoSave { items = new List<PropExtraInfo>() };
            if (string.IsNullOrEmpty(json)) return data;

            data.nextIndex = ExtractInt(json, "nextIndex", 1);

            int itemsIdx = json.IndexOf("\"items\"", StringComparison.OrdinalIgnoreCase);
            if (itemsIdx < 0) return data;
            int arrStart = json.IndexOf('[', itemsIdx);
            if (arrStart < 0) return data;
            int arrEnd = FindMatching(json, arrStart, '[', ']');
            if (arrEnd < 0) return data;

            int i = arrStart + 1;
            while (i < arrEnd)
            {
                SkipWhitespace(json, ref i);
                if (i >= arrEnd) break;
                if (json[i] == ',') { i++; continue; }
                if (json[i] != '{') { i++; continue; }
                int objEnd = FindMatching(json, i, '{', '}');
                if (objEnd < 0) break;
                string obj = json.Substring(i, objEnd - i + 1);
                var item = new PropExtraInfo
                {
                    id = ExtractString(obj, "id"),
                    displayName = ExtractString(obj, "displayName"),
                    category = ExtractString(obj, "category"),
                    excluded = ExtractBool(obj, "excluded"),
                    useRenderMeshCollider = ExtractBool(obj, "useRenderMeshCollider"),
                    colliderIgnoredSubmeshes = ExtractString(obj, "colliderIgnoredSubmeshes"),
                    overrideMaterialId = ExtractString(obj, "overrideMaterialId"),
                    nativeMaterialName = ExtractString(obj, "nativeMaterialName"),
                    materialSourcePropId = ExtractString(obj, "materialSourcePropId"),
                    surfaceType = ExtractString(obj, "surfaceType"),
                    index = ExtractInt(obj, "index", 0),
                    disabledRenderers = ExtractStringArray(obj, "disabledRenderers"),
                    perSlotMaterialOverrides = ExtractStringArray(obj, "perSlotMaterialOverrides"),
                    forcedMaterialSlots = ExtractInt(obj, "forcedMaterialSlots", 0),
                    isBush = ExtractBool(obj, "isBush"),
                    bushRadius = ExtractFloat(obj, "bushRadius", 0f),
                    soundGrassType = ExtractInt(obj, "soundGrassType", 1),
                    keepOriginalHierarchy = ExtractBool(obj, "keepOriginalHierarchy")
                };
                if (!string.IsNullOrEmpty(item.id))
                    data.items.Add(item);
                i = objEnd + 1;
            }

            return data;
        }

        static int FindMatching(string text, int start, char open, char close)
        {
            int depth = 0;
            bool inString = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"' && (i == 0 || text[i - 1] != '\\')) inString = !inString;
                if (inString) continue;
                if (c == open) depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        static HashSet<int> ParseIntSet(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var set = new HashSet<int>();
            var parts = text.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                if (int.TryParse(parts[i], out int value) && value >= 0)
                    set.Add(value);
            }
            return set.Count > 0 ? set : null;
        }

        static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        }

        static int ExtractInt(string json, string key, int fallback)
        {
            if (!TryFindKey(json, key, out int valueStart)) return fallback;
            int i = valueStart;
            SkipWhitespace(json, ref i);
            int sign = 1;
            if (i < json.Length && json[i] == '-') { sign = -1; i++; }
            int value = 0;
            bool found = false;
            while (i < json.Length && char.IsDigit(json[i]))
            {
                value = value * 10 + (json[i] - '0');
                i++;
                found = true;
            }
            return found ? value * sign : fallback;
        }

        static bool ExtractBool(string json, string key)
        {
            if (!TryFindKey(json, key, out int valueStart)) return false;
            int i = valueStart;
            SkipWhitespace(json, ref i);
            if (json.IndexOf("true", i, StringComparison.OrdinalIgnoreCase) == i) return true;
            return false;
        }

        static float ExtractFloat(string json, string key, float fallback)
        {
            if (!TryFindKey(json, key, out int valueStart)) return fallback;
            int i = valueStart;
            SkipWhitespace(json, ref i);
            int start = i;
            if (i < json.Length && (json[i] == '-' || json[i] == '+')) i++;
            while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '.')) i++;
            if (i == start) return fallback;
            string raw = json.Substring(start, i - start);
            return float.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;
        }

        static string ExtractString(string json, string key)
        {
            if (!TryFindKey(json, key, out int valueStart)) return string.Empty;
            int i = valueStart;
            SkipWhitespace(json, ref i);
            if (i >= json.Length || json[i] != '"') return string.Empty;
            i++;
            var sb = new System.Text.StringBuilder();
            while (i < json.Length)
            {
                char c = json[i++];
                if (c == '"') break;
                if (c == '\\' && i < json.Length)
                {
                    char esc = json[i++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 3 < json.Length)
                            {
                                string hex = json.Substring(i, 4);
                                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int code))
                                    sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(esc); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        static bool TryFindKey(string json, string key, out int valueStart)
        {
            valueStart = -1;
            int idx = json.IndexOf('"' + key + '"', StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            int colon = json.IndexOf(':', idx);
            if (colon < 0) return false;
            valueStart = colon + 1;
            return true;
        }

        static List<string> ExtractStringArray(string json, string key)
        {
            var result = new List<string>();
            if (!TryFindKey(json, key, out int valueStart)) return result;
            int i = valueStart;
            SkipWhitespace(json, ref i);
            if (i >= json.Length || json[i] != '[') return result;

            int end = FindMatching(json, i, '[', ']');
            if (end < 0) return result;
            int pos = i + 1;
            while (pos < end)
            {
                SkipWhitespace(json, ref pos);
                if (pos >= end) break;
                if (json[pos] == ',') { pos++; continue; }
                if (json[pos] != '"') { pos++; continue; }

                int start = pos;
                string val = ExtractString(json.Substring(start, end - start), string.Empty);
                if (!string.IsNullOrEmpty(val)) result.Add(val);

                int nextQuote = json.IndexOf('"', pos + 1);
                if (nextQuote < 0 || nextQuote >= end) break;
                pos = nextQuote + 1;
            }
            return result;
        }

        public static int GetMetaIndex(string id)
        {
            EnsureLoaded();
            return TryGetInfoById(id, out var info) ? info.index : 0;
        }

        public static string FindIdByIndex(int index)
        {
            EnsureLoaded();
            if (index <= 0) return null;
            foreach (var kvp in _byId)
                if (kvp.Value.index == index) return kvp.Key;
            return null;
        }

        public static string GetDisplayName(string id)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return null;
            return TryGetInfoById(id, out var info) && !string.IsNullOrEmpty(info.displayName)
                ? info.displayName : null;
        }

        public static string GetCategory(string id)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return "";
            return TryGetInfoById(id, out var info) ? (info.category ?? "") : "";
        }

        public static bool HasCategory(string id)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return false;
            return TryGetInfoById(id, out var info) && !string.IsNullOrEmpty(info.category);
        }

        public static List<string> GetAllCategories()
        {
            EnsureLoaded();
            var cats = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in _byId)
                if (!string.IsNullOrEmpty(kvp.Value.category))
                    cats.Add(kvp.Value.category);
            return new List<string>(cats);
        }
    }
}
