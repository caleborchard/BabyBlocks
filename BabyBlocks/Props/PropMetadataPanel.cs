using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
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

static bool _showRendererDropdown;
        static Vector2 _rendererScroll;
        static readonly List<RendererEntry> _rendererEntries = new();

        public static readonly string[] KnownSurfaceTags =
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

        static string _paletteSelectedId;

        static bool _showExportWindow;
        static Rect _exportWindowRect;
        static bool _exportWindowInitialized;
        static bool _exportWindowDragging;
        static Vector2 _exportWindowDragOffset;
        static string _exportStatusMsg = "";

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

            if (_showExportWindow)
                DrawExportWindow();

            string focused = GUI.GetNameOfFocusedControl();
            IsTypingInUI = focused == SearchField
                        || focused == DisplayNameField
                        || focused == CategoryField
                        || focused == OverrideField
                        || MaterialConstructionPanel.IsTypingInUI;
            if (string.IsNullOrEmpty(_propId) && !MaterialConstructionPanel.Active)
                IsTypingInUI = false;

            var mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            IsPointerOverUI = rect.Contains(mouse);
            if (_showExportWindow && _exportWindowInitialized)
                IsPointerOverUI |= _exportWindowRect.Contains(mouse);
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
                PropMetadataStore._loadedFromJson = true;
                ApplyCurrent();
                _dirty = false;
            }
            GUI.enabled = true;
            if (GUILayout.Button("Save All"))
            {
                PropMetadataStore._loadedFromJson = true;
                if (!string.IsNullOrEmpty(_propId))
                {
                    ApplyCurrent();
                    _dirty = false;
                }
                PropMetadataStore.Save();
            }
            if (GUILayout.Button(_showExportWindow ? "Hide Export" : "Binary Export"))
                _showExportWindow = !_showExportWindow;
            if (GUILayout.Button(MaterialConstructionPanel.Active ? "Exit Material Construction" : "Material Construction"))
                MaterialConstructionPanel.Active = !MaterialConstructionPanel.Active;
            GUILayout.Space(4f);

            _mainScroll = GUILayout.BeginScrollView(_mainScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.ExpandHeight(true));

            if (MaterialConstructionPanel.Active)
            {
                MaterialConstructionPanel.DrawConstructor();
                GUILayout.EndScrollView();
                GUILayout.EndVertical();
                return;
            }

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
                && PropMetadataStore.HasDuplicateDisplayName(_displayName, _propId);
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
                foreach (var kvp in PropMetadataStore._byId)
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
                foreach (var kvp in PropMetadataStore._byId)
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
                        PropInstanceServices.ApplySurfaceType(_selectedLEO, tag);
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

            MaterialCatalog.EnsureMaterialList();

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
                    // Prefer applying by name so we use the live Material object from MaterialCatalog.MaterialByName
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
                    for (int i = 0; i < MaterialCatalog.MaterialLabels.Count; i++)
                    {
                        string label = MaterialCatalog.MaterialLabels[i];
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
                        for (int i = 0; i < MaterialCatalog.MaterialLabels.Count; i++)
                        {
                            string label = MaterialCatalog.MaterialLabels[i];
                            if (hasSlotSearch && label.IndexOf(slotSearch, StringComparison.OrdinalIgnoreCase) < 0)
                                continue;
                            bool isCurrent = string.Equals(slotSel, MaterialCatalog.MaterialNames[i], StringComparison.OrdinalIgnoreCase)
                                || (i == 0 && !isOverridden);
                            if (isCurrent) label = "> " + label;
                            EnsureMaterialButtonStyle();
                            if (GUILayout.Button(label, _materialButtonStyle))
                            {
                                string picked = i == 0 ? string.Empty : MaterialCatalog.MaterialNames[i];
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
                                PropInstanceServices.BushAudioTracker.Unregister(_selectedLEO.transform);
                                PropInstanceServices.BushAudioTracker.Register(_selectedLEO.transform, _bushRadius, _soundGrassType);
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
                return PropMetadataStore.NoOverrideLabel;

            if (string.IsNullOrEmpty(_overrideMaterialName)
                && !string.IsNullOrEmpty(_defaultMaterialName)
                && string.Equals(_selectedMaterialName, _defaultMaterialName, StringComparison.OrdinalIgnoreCase))
                return _selectedMaterialName + " (default)";

            return _selectedMaterialName;
        }

        static void SelectMaterialByIndex(int index)
        {
            if (index <= 0 || index >= MaterialCatalog.MaterialNames.Count)
            {
                _selectedMaterialName = "";
                return;
            }

            _selectedMaterialName = MaterialCatalog.MaterialNames[index];
        }

        static int GetMaterialIndex(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            for (int i = 0; i < MaterialCatalog.MaterialNames.Count; i++)
            {
                if (string.Equals(MaterialCatalog.MaterialNames[i], name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return 0;
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
                    if (MaterialVariantTracker.ShouldHideMaterial(mat.name)) continue;
                    // Track which prop this material came from and propagate to all saved entries
                    // that use it as an override but had no source recorded yet.
                    // Never record a source from a contaminated renderer: if the renderer material
                    // matches the saved override name, the renderer may be showing the override rather
                    // than the native material, so this prop is NOT the true source of that material.
                    // Note: we cannot rely on _defaultMaterialName here because the nativeMaterialName
                    // correction in SyncFromSelection runs after this method, so _defaultMaterialName
                    // may still reflect the contaminated state at this point.
                    bool isUntrustedOverride =
                        !string.IsNullOrEmpty(_overrideMaterialName)
                        && string.Equals(mat.name, _overrideMaterialName, StringComparison.OrdinalIgnoreCase);

                    if (!string.IsNullOrEmpty(_propId) && !isUntrustedOverride)
                    {
                        if (MaterialCatalog.BackfillMaterialSource(mat.name, _propId))
                            PropMetadataStore.Save();
                    }
                    // Lookup only — display list is owned by seenCount + catalog + scene variants.
                    if (!MaterialCatalog.MaterialByName.ContainsKey(mat.name))
                        MaterialCatalog.MaterialByName[mat.name] = mat;
                }
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
                var multiInfo = PropMetadataStore.Apply(_propId, _displayName, _category, _excluded, _useRenderMeshCollider,
                    string.Empty, _defaultMaterialName, string.Empty, _surfaceType, _disabledRendererPaths,
                    _colliderIgnoredSubmeshes, perSlotToSave, slotCountToSave, _isBush, _bushRadius, _soundGrassType,
                    _keepOriginalHierarchy);
                if (multiInfo != null)
                    _index = multiInfo.index;
                _materialExplicitlyChosen = false;
                return;
            }

            string overrideToSave = GetOverrideToSave();
            MaterialCatalog.KnownMaterialSources.TryGetValue(overrideToSave ?? string.Empty, out string srcPropId);
            var info = PropMetadataStore.Apply(_propId, _displayName, _category, _excluded, _useRenderMeshCollider,
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

            if (string.Equals(_selectedMaterialName, PropMetadataStore.NoOverrideLabel, StringComparison.OrdinalIgnoreCase))
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
            if (PropMetadataStore.TryGet(id, out var info) && info != null)
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
            MaterialCatalog.EnsureMaterialList();
            AddRendererMaterialsToList();

            // GPUI props have no live renderers — scan their loaded parts for native materials.
            // Load on first selection so source recording works even for old-format entries.
            if (propLibInfo != null && propLibInfo.IsGpui)
            {
                if (!propLibInfo.isLoaded) PropLibrary.LoadPropData(propLibInfo);
                if (propLibInfo.isLoaded) MaterialCatalog.AddPartsToMaterialList(propLibInfo);
            }

            // If any saved override material (single-slot or per-slot) is still not in the list,
            // do a fresh Resources scan — GPUI loads material instances lazily as the player moves,
            // so a material may have entered memory after EnsureMaterialSources ran at startup.
            // Also try the catalog path for any per-slot override still absent after the scan.
            bool needsFreshScan = (!string.IsNullOrEmpty(_overrideMaterialName)
                                   && !MaterialCatalog.MaterialByName.ContainsKey(_overrideMaterialName));
            if (!needsFreshScan)
            {
                foreach (var s in _slotHasExplicitOverride)
                {
                    string slotMat = s < _perSlotSelected.Count ? _perSlotSelected[s] : string.Empty;
                    if (!string.IsNullOrEmpty(slotMat) && !MaterialCatalog.MaterialByName.ContainsKey(slotMat))
                    { needsFreshScan = true; break; }
                }
            }

            if (needsFreshScan)
            {
                // Update the lookup map only — do not push these into the display list, which is
                // managed by the seenCount scan and catalog to keep variant hashing consistent.
                try
                {
                    var allMats = Resources.FindObjectsOfTypeAll<Material>();
                    for (int i = 0; i < allMats.Length; i++)
                    {
                        var m = allMats[i];
                        if (m == null || string.IsNullOrEmpty(m.name)) continue;
                        if (MaterialVariantTracker.ShouldHideMaterial(m.name)) continue;
                        if (!MaterialCatalog.MaterialByName.ContainsKey(m.name))
                            MaterialCatalog.MaterialByName[m.name] = m;
                    }
                }
                catch { }

                // Single-slot: if the override is still absent after the Resources scan, try the
                // catalog. This handles saved names with "(Instance)" suffixes where the scan
                // added the material under the clean name but the saved name still has no entry.
                // Scene-variant clones are runtime-only and won't be in the catalog — skip them.
                bool isVariantKey = MaterialVariantTracker.SceneVariantByDisplayName.ContainsKey(_overrideMaterialName);
                if (!string.IsNullOrEmpty(_overrideMaterialName) && !MaterialCatalog.MaterialByName.ContainsKey(_overrideMaterialName)
                    && !_overrideMaterialName.StartsWith("[MicroSplat]", StringComparison.Ordinal)
                    && !isVariantKey)
                {
                    try
                    {
                        var mat = MaterialPathCatalog.TryLoadMaterialByName(_overrideMaterialName, info.materialSourcePropId);
                        if (mat != null)
                        {
                            if (!MaterialCatalog.MaterialByName.ContainsKey(mat.name))
                            {
                                string shaderName = mat.shader != null ? mat.shader.name : string.Empty;
                                string label = string.IsNullOrEmpty(shaderName) ? mat.name : $"{mat.name}  [{shaderName}]";
                                MaterialCatalog.MaterialNames.Add(mat.name);
                                MaterialCatalog.MaterialLabels.Add(label);
                                MaterialCatalog.MaterialByName[mat.name] = mat;
                            }
                            if (!string.Equals(mat.name, _overrideMaterialName, StringComparison.OrdinalIgnoreCase)
                                && !MaterialCatalog.MaterialByName.ContainsKey(_overrideMaterialName))
                                MaterialCatalog.MaterialByName[_overrideMaterialName] = mat;
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
                    if (string.IsNullOrEmpty(slotMat) || MaterialCatalog.MaterialByName.ContainsKey(slotMat)) continue;
                    if (slotMat.StartsWith("[MicroSplat]", StringComparison.Ordinal)) continue;
                    // Scene-variant clones are runtime-only; skip catalog lookup for them.
                    if (MaterialVariantTracker.SceneVariantByDisplayName.ContainsKey(slotMat)) continue;
                    try
                    {
                        var mat = MaterialPathCatalog.TryLoadMaterialByName(slotMat, info.materialSourcePropId);
                        if (mat == null) continue;
                        if (!MaterialCatalog.MaterialByName.ContainsKey(mat.name))
                        {
                            string shaderName = mat.shader != null ? mat.shader.name : string.Empty;
                            string label = string.IsNullOrEmpty(shaderName) ? mat.name : $"{mat.name}  [{shaderName}]";
                            MaterialCatalog.MaterialNames.Add(mat.name);
                            MaterialCatalog.MaterialLabels.Add(label);
                            MaterialCatalog.MaterialByName[mat.name] = mat;
                        }
                        // Alias so lookups using the saved "(Instance)"-suffixed name also resolve.
                        if (!string.Equals(mat.name, slotMat, StringComparison.OrdinalIgnoreCase)
                            && !MaterialCatalog.MaterialByName.ContainsKey(slotMat))
                            MaterialCatalog.MaterialByName[slotMat] = mat;
                    }
                    catch { }
                }
            }

            // If a native material name was persisted, use it to un-contaminate _defaultMaterialName.
            // CacheDefaultMaterials reads the live renderer, which may already have the override applied.
            if (PropMetadataStore.TryGet(_propId, out var metaInfo) && metaInfo != null)
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
                        string recovered = MaterialCatalog.FindNativeFromParts(propLibInfo, metaInfo.overrideMaterialId);
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
                        && MaterialCatalog.KnownMaterialSources.TryGetValue(metaInfo.overrideMaterialId, out string knownSrc)
                        && !string.IsNullOrEmpty(knownSrc))
                    {
                        if (MaterialCatalog.BackfillMaterialSource(metaInfo.overrideMaterialId, knownSrc))
                            migrationDirty = true;
                    }
                }

                if (migrationDirty) PropMetadataStore.Save();
            }

            if (_multiMaterialEnabled || _maxMaterialSlots > 1)
                ApplyAllSlotMaterials();
            else
                ApplyPreviewMaterial(_selectedMaterialName);
            PropInstanceServices.ApplySurfaceType(selectedObject, _surfaceType);
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

            PropInstanceServices.BushAudioTracker.Unregister(root.transform);
            // Restore / set trigger state on all physics colliders (BushCollider sphere excluded,
            // since it hasn't been added yet and its isTrigger is set explicitly below).
            PropInstanceServices.SetBushPassthrough(root, enable);

            if (!enable) { _bushRadius = 0f; return; }

            _bushRadius = PropInstanceServices.ComputeBushRadius(leo);
            PropInstanceServices.BushAudioTracker.Register(root.transform, _bushRadius, _soundGrassType);
            var sphere = root.AddComponent<SphereCollider>();
            sphere.radius = _bushRadius;
            sphere.isTrigger = true;
            var bush = root.AddComponent<Il2Cpp.BushCollider>();
            // Set rad immediately; BushCollider.Start() mirrors this but may not have run yet.
            bush.rad = sphere.radius * root.transform.localScale.x;
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
                || string.Equals(materialName, PropMetadataStore.NoOverrideLabel, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(_defaultMaterialName)
                    && string.Equals(materialName, _defaultMaterialName, StringComparison.OrdinalIgnoreCase)))
            {
                RestoreDefaultMaterials();
                return;
            }

            if (!MaterialCatalog.MaterialByName.TryGetValue(materialName, out var mat) || mat == null)
            {
                mat = MaterialPathCatalog.TryLoadMaterialByName(materialName, MaterialCatalog.GetKnownMaterialSource(materialName));
                if (mat != null)
                {
                    if (!MaterialCatalog.MaterialByName.ContainsKey(mat.name)) MaterialCatalog.MaterialByName[mat.name] = mat;
                    if (!string.Equals(mat.name, materialName, StringComparison.OrdinalIgnoreCase)
                        && !MaterialCatalog.MaterialByName.ContainsKey(materialName))
                        MaterialCatalog.MaterialByName[materialName] = mat;
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
                || string.Equals(materialName, PropMetadataStore.NoOverrideLabel, StringComparison.OrdinalIgnoreCase)
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
                    if (!MaterialCatalog.MaterialByName.TryGetValue(materialName, out var mat) || mat == null)
                    {
                        mat = MaterialPathCatalog.TryLoadMaterialByName(materialName, MaterialCatalog.GetKnownMaterialSource(materialName));
                        if (mat != null)
                        {
                            if (!MaterialCatalog.MaterialByName.ContainsKey(mat.name)) MaterialCatalog.MaterialByName[mat.name] = mat;
                            if (!string.Equals(mat.name, materialName, StringComparison.OrdinalIgnoreCase)
                                && !MaterialCatalog.MaterialByName.ContainsKey(materialName))
                                MaterialCatalog.MaterialByName[materialName] = mat;
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

        static void DrawExportWindow()
        {
            if (!_exportWindowInitialized)
            {
                _exportWindowRect = new Rect(10f, Screen.height - 120f, 300f, 110f);
                _exportWindowInitialized = true;
            }

            var r = _exportWindowRect;
            GUI.Box(r, "Metadata Export", GUI.skin.window);

            var headerRect  = new Rect(r.x, r.y, r.width, HeaderH);
            var contentRect = new Rect(r.x + Pad, r.y + HeaderH + Pad, r.width - Pad * 2f, r.height - HeaderH - Pad * 2f);

            GUILayout.BeginArea(contentRect);
            GUILayout.BeginVertical();
            if (GUILayout.Button("Export prop_metadata.bin"))
            {
                try
                {
                    PropMetadataStore.SaveBinary(PropMetadataStore.BinaryExportPath);
                    _exportStatusMsg = PropMetadataStore.BinaryExportPath;
                }
                catch (Exception ex)
                {
                    _exportStatusMsg = "Error: " + ex.Message;
                }
            }
            if (!string.IsNullOrEmpty(_exportStatusMsg))
                GUILayout.Label(_exportStatusMsg);
            GUILayout.EndVertical();
            GUILayout.EndArea();

            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && headerRect.Contains(e.mousePosition))
            {
                _exportWindowDragging = true;
                _exportWindowDragOffset = e.mousePosition - new Vector2(r.x, r.y);
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && _exportWindowDragging)
            {
                _exportWindowRect.x = e.mousePosition.x - _exportWindowDragOffset.x;
                _exportWindowRect.y = e.mousePosition.y - _exportWindowDragOffset.y;
                e.Use();
            }
            else if (e.type == EventType.MouseUp && e.button == 0)
                _exportWindowDragging = false;
        }
    }
}
