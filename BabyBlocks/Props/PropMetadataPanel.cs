using System;
using System.Collections.Generic;
using UnityEngine;

namespace BabyBlocks
{
    // IMGUI drawing only — all editing-session state and apply/sync logic lives in
    // Logic/Props/PropMetadataEditor.cs. A replacement UI can read/write PropMetadataEditor's
    // fields and call its methods directly without touching this file.
    static class PropMetadataPanel
    {
        public static bool Enabled = true;
        public static bool IsTypingInUI { get; private set; }
        public static bool IsPointerOverUI { get; private set; }

        const float HeaderH = 22f;
        const float Pad = 8f;
        const float MaterialListH = 140f;
        const float RendererListH = 120f;

        const string SearchField      = "propMetaSearch";
        const string DisplayNameField = "propMetaDisplayName";
        const string CategoryField    = "propMetaCategory";
        const string OverrideField    = "propMetaOverride";

        static Rect _windowRect;
        static bool _windowInitialized;
        static bool _windowDragging;
        static Vector2 _windowDragOffset;

        static bool _showMaterialDropdown;
        static Vector2 _materialScroll;
        static Vector2 _mainScroll;
        static string _materialSearch = "";
        static GUIStyle _materialButtonStyle;
        static GUIStyle _redLabelStyle;

        static bool _showRendererDropdown;
        static Vector2 _rendererScroll;

        static bool _showSurfaceTypeDropdown;
        static Vector2 _surfaceTypeScroll;

        static bool _showGrassTypeDropdown;
        static Vector2 _grassTypeScroll;

        static int _openSlotDropdown = -1;
        static Vector2 _slotDropdownScroll;
        static string _slotDropdownSearch = "";
        static string _forcedMaterialSlotsStr = "2";

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

            bool selectionChanged = PropMetadataEditor.SyncFromSelection(selectedObject);
            if (selectionChanged)
            {
                _showMaterialDropdown = false;
                _showRendererDropdown = false;
                _showSurfaceTypeDropdown = false;
                _showGrassTypeDropdown = false;
                _openSlotDropdown = -1;
                _slotDropdownSearch = string.Empty;
            }
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
            PropMetadataEditor.AutoSaveIfIdle();

            if (_showExportWindow)
                DrawExportWindow();

            string focused = GUI.GetNameOfFocusedControl();
            IsTypingInUI = focused == SearchField
                        || focused == DisplayNameField
                        || focused == CategoryField
                        || focused == OverrideField
                        || MaterialConstructionPanel.IsTypingInUI;
            if (string.IsNullOrEmpty(PropMetadataEditor.PropId) && !MaterialConstructionPanel.Active)
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

            bool newMetaMode = GUILayout.Toggle(PropLibrary.DebugSearchMetaNames, "Search metadata names");
            if (newMetaMode != PropLibrary.DebugSearchMetaNames)
            {
                PropLibrary.DebugSearchMetaNames = newMetaMode;
                PropLibrary.RebuildFiltered();
            }

            GUILayout.Space(4f);

            GUI.enabled = !string.IsNullOrEmpty(PropMetadataEditor.PropId);
            if (GUILayout.Button("Save"))
            {
                PropMetadataStore._loadedFromJson = true;
                PropMetadataEditor.ApplyCurrent();
                PropMetadataEditor.Dirty = false;
            }
            GUI.enabled = true;
            if (GUILayout.Button("Save All"))
            {
                PropMetadataStore._loadedFromJson = true;
                if (!string.IsNullOrEmpty(PropMetadataEditor.PropId))
                {
                    PropMetadataEditor.ApplyCurrent();
                    PropMetadataEditor.Dirty = false;
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

            if (string.IsNullOrEmpty(PropMetadataEditor.PropId))
            {
                GUILayout.Label("Select a prop in the world to edit its details.");
                GUILayout.EndScrollView();
                GUILayout.EndVertical();
                return;
            }

            GUILayout.BeginVertical(GUILayout.Width(375f));

            GUILayout.Label("Selected ID (click to copy)");
            if (GUILayout.Button(PropMetadataEditor.PropId ?? string.Empty, GUI.skin.textField))
                GUIUtility.systemCopyBuffer = PropMetadataEditor.PropId ?? string.Empty;

            var newExclude = GUILayout.Toggle(PropMetadataEditor.Excluded, "Exclude item");
            if (newExclude != PropMetadataEditor.Excluded)
            {
                PropMetadataEditor.Excluded = newExclude;
                PropMetadataEditor.MarkDirty();
            }

            GUILayout.Space(4f);

            if (_redLabelStyle == null)
            {
                _redLabelStyle = new GUIStyle(GUI.skin.label);
                _redLabelStyle.normal.textColor = Color.red;
            }
            bool isDuplicateName = !string.IsNullOrEmpty(PropMetadataEditor.DisplayName)
                && PropMetadataStore.HasDuplicateDisplayName(PropMetadataEditor.DisplayName, PropMetadataEditor.PropId);
            GUILayout.Label("Display name", isDuplicateName ? _redLabelStyle : GUI.skin.label);
            GUI.SetNextControlName(DisplayNameField);
            var newDisplayName = GUILayout.TextField(PropMetadataEditor.DisplayName ?? string.Empty);
            if (!string.Equals(newDisplayName, PropMetadataEditor.DisplayName, StringComparison.Ordinal))
            {
                PropMetadataEditor.DisplayName = newDisplayName;
                PropMetadataEditor.MarkDirty();
            }

            if (GUI.GetNameOfFocusedControl() == DisplayNameField && !string.IsNullOrEmpty(PropMetadataEditor.DisplayName) && PropMetadataEditor.DisplayName.Length >= 2)
            {
                int suggCount = 0;
                foreach (var kvp in PropMetadataStore._byId)
                {
                    if (kvp.Key == PropMetadataEditor.PropId) continue;
                    var dn = kvp.Value.displayName;
                    if (string.IsNullOrEmpty(dn)) continue;
                    if (dn.IndexOf(PropMetadataEditor.DisplayName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        PropMetadataEditor.DisplayName.IndexOf(dn, StringComparison.OrdinalIgnoreCase) >= 0)
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
            var newCategory = GUILayout.TextField(PropMetadataEditor.Category ?? string.Empty);
            if (!string.Equals(newCategory, PropMetadataEditor.Category, StringComparison.Ordinal))
            {
                PropMetadataEditor.Category = newCategory;
                PropMetadataEditor.MarkDirty();
            }

            if (GUI.GetNameOfFocusedControl() == CategoryField && !string.IsNullOrEmpty(PropMetadataEditor.Category) && PropMetadataEditor.Category.Length >= 2)
            {
                int suggCount = 0;
                var seenCats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in PropMetadataStore._byId)
                {
                    if (kvp.Key == PropMetadataEditor.PropId) continue;
                    var cat = kvp.Value.category;
                    if (string.IsNullOrEmpty(cat)) continue;
                    if (cat.IndexOf(PropMetadataEditor.Category, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        PropMetadataEditor.Category.IndexOf(cat, StringComparison.OrdinalIgnoreCase) >= 0)
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
            string surfaceLabel = string.IsNullOrEmpty(PropMetadataEditor.SurfaceType) ? "(none — game default)" : PropMetadataEditor.SurfaceType;
            if (GUILayout.Button(surfaceLabel, GUILayout.Height(22f)))
                _showSurfaceTypeDropdown = !_showSurfaceTypeDropdown;

            if (_showSurfaceTypeDropdown)
            {
                _surfaceTypeScroll = GUILayout.BeginScrollView(_surfaceTypeScroll, GUILayout.Height(120f));
                foreach (var tag in PropMetadataEditor.KnownSurfaceTags)
                {
                    string lbl = string.IsNullOrEmpty(tag) ? "(none — game default)" : tag;
                    if (string.Equals(tag, PropMetadataEditor.SurfaceType, StringComparison.Ordinal))
                        lbl = "> " + lbl;
                    EnsureMaterialButtonStyle();
                    if (GUILayout.Button(lbl, _materialButtonStyle))
                    {
                        PropMetadataEditor.SurfaceType = tag;
                        _showSurfaceTypeDropdown = false;
                        PropInstanceServices.ApplySurfaceType(PropMetadataEditor.SelectedLEO, tag);
                        PropMetadataEditor.MarkDirty();
                    }
                }
                GUILayout.EndScrollView();
            }

            GUILayout.Space(8f);

            var newUseMeshCol = GUILayout.Toggle(PropMetadataEditor.UseRenderMeshCollider, "Use render mesh as collider");
            if (newUseMeshCol != PropMetadataEditor.UseRenderMeshCollider)
            {
                PropMetadataEditor.UseRenderMeshCollider = newUseMeshCol;
                PropMetadataEditor.ApplyColliderToSelected(newUseMeshCol);
                PropMetadataEditor.BuildRendererEntries(PropMetadataEditor.SelectedLEO);
                PropMetadataEditor.ApplyRendererVisibility();
                PropMetadataEditor.ApplyCurrent();
                PropMetadataEditor.Dirty = false;
            }

            GUILayout.Label("Components");
            if (GUILayout.Button(_showRendererDropdown ? "Hide components" : "Show components"))
                _showRendererDropdown = !_showRendererDropdown;

            if (_showRendererDropdown)
            {
                _rendererScroll = GUILayout.BeginScrollView(_rendererScroll, GUILayout.Height(RendererListH));
                for (int i = 0; i < PropMetadataEditor.RendererEntries.Count; i++)
                {
                    var entry = PropMetadataEditor.RendererEntries[i];
                    bool newEnabled = GUILayout.Toggle(entry.enabled, entry.path);
                    if (newEnabled != entry.enabled)
                    {
                        entry.enabled = newEnabled;
                        if (entry.renderer != null) entry.renderer.enabled = newEnabled;
                        else if (entry.collider != null) entry.collider.enabled = newEnabled;
                        if (!newEnabled) PropMetadataEditor.DisabledRendererPaths.Add(entry.path);
                        else PropMetadataEditor.DisabledRendererPaths.Remove(entry.path);
                        PropMetadataEditor.MarkDirty();
                    }
                }
                GUILayout.EndScrollView();
            }

            GUILayout.Space(8f);

            MaterialCatalog.EnsureMaterialList();

            int effectiveSlotCount = PropMetadataEditor.MultiMaterialEnabled
                ? Math.Max(PropMetadataEditor.ForcedMaterialSlots, PropMetadataEditor.MaxMaterialSlots)
                : PropMetadataEditor.MaxMaterialSlots;

            var newMultiMat = GUILayout.Toggle(PropMetadataEditor.MultiMaterialEnabled, "Multiple materials");
            if (newMultiMat != PropMetadataEditor.MultiMaterialEnabled)
            {
                PropMetadataEditor.MultiMaterialEnabled = newMultiMat;
                int eff = PropMetadataEditor.MultiMaterialEnabled
                    ? Math.Max(PropMetadataEditor.ForcedMaterialSlots, PropMetadataEditor.MaxMaterialSlots)
                    : PropMetadataEditor.MaxMaterialSlots;
                while (PropMetadataEditor.PerSlotDefault.Count < eff) PropMetadataEditor.PerSlotDefault.Add(string.Empty);
                while (PropMetadataEditor.PerSlotSelected.Count < eff) PropMetadataEditor.PerSlotSelected.Add(string.Empty);
                _openSlotDropdown = -1;
                if (PropMetadataEditor.MultiMaterialEnabled)
                {
                    // Port an existing single-material override into slot 0 of the new setup.
                    if (!string.IsNullOrEmpty(PropMetadataEditor.OverrideMaterialName) && PropMetadataEditor.PerSlotSelected.Count > 0
                        && string.IsNullOrEmpty(PropMetadataEditor.PerSlotSelected[0]))
                        PropMetadataEditor.PerSlotSelected[0] = PropMetadataEditor.OverrideMaterialName;
                    PropMetadataEditor.ApplyAllSlotMaterials();
                }
                else
                    PropMetadataEditor.RestoreDefaultMaterials();
                PropMetadataEditor.MarkDirty();
            }

            if (PropMetadataEditor.MultiMaterialEnabled)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Slots:", GUILayout.Width(40f));
                string newSlotsStr = GUILayout.TextField(_forcedMaterialSlotsStr, GUILayout.Width(40f));
                if (!string.Equals(newSlotsStr, _forcedMaterialSlotsStr, StringComparison.Ordinal))
                {
                    _forcedMaterialSlotsStr = newSlotsStr;
                    if (int.TryParse(newSlotsStr, out int parsed) && parsed >= 2)
                    {
                        int oldEff = Math.Max(PropMetadataEditor.ForcedMaterialSlots, PropMetadataEditor.MaxMaterialSlots);
                        PropMetadataEditor.ForcedMaterialSlots = parsed;
                        int newEff = Math.Max(PropMetadataEditor.ForcedMaterialSlots, PropMetadataEditor.MaxMaterialSlots);

                        if (newEff < oldEff)
                        {
                            // Drop overrides and explicit-override tracking for removed slots.
                            for (int s = newEff; s < oldEff; s++)
                                PropMetadataEditor.SlotHasExplicitOverride.Remove(s);
                            while (PropMetadataEditor.PerSlotSelected.Count > newEff)
                                PropMetadataEditor.PerSlotSelected.RemoveAt(PropMetadataEditor.PerSlotSelected.Count - 1);

                            // Restore all renderers to their default material arrays first
                            // (collapses any over-extended arrays back to natural length),
                            // then re-apply only the remaining slots below.
                            PropMetadataEditor.RestoreDefaultMaterials();
                        }

                        while (PropMetadataEditor.PerSlotDefault.Count < newEff) PropMetadataEditor.PerSlotDefault.Add(string.Empty);
                        while (PropMetadataEditor.PerSlotSelected.Count < newEff) PropMetadataEditor.PerSlotSelected.Add(string.Empty);
                        PropMetadataEditor.ApplyAllSlotMaterials();
                        PropMetadataEditor.MarkDirty();
                    }
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Ignore collider submeshes (0-based):", GUILayout.Width(230f));
            string newIgnored = GUILayout.TextField(PropMetadataEditor.ColliderIgnoredSubmeshes ?? string.Empty);
            if (!string.Equals(newIgnored, PropMetadataEditor.ColliderIgnoredSubmeshes, StringComparison.Ordinal))
            {
                PropMetadataEditor.ColliderIgnoredSubmeshes = newIgnored;
                PropMetadataEditor.MarkDirty();
            }
            GUILayout.EndHorizontal();

            if (effectiveSlotCount <= 1)
            {
                GUILayout.Label("Override material");
                GUILayout.BeginHorizontal();
                string overrideLabel = PropMetadataEditor.GetOverrideLabel();
                GUI.SetNextControlName(OverrideField);
                if (GUILayout.Button(overrideLabel, GUILayout.Height(22f)))
                    _showMaterialDropdown = !_showMaterialDropdown;
                if (GUILayout.Button("Reset", GUILayout.Height(22f), GUILayout.Width(50f)))
                {
                    PropMetadataEditor.SelectedMaterialName = PropMetadataEditor.DefaultMaterialName ?? string.Empty;
                    PropMetadataEditor.OverrideMaterialName = string.Empty;
                    _showMaterialDropdown = false;
                    PropMetadataEditor.MaterialExplicitlyChosen = false;
                    // Prefer applying by name so we use the live Material object from MaterialCatalog.MaterialByName
                    // rather than the cached default-materials array, which may be contaminated by a prior override.
                    if (!string.IsNullOrEmpty(PropMetadataEditor.DefaultMaterialName))
                        PropMetadataEditor.ApplyPreviewMaterial(PropMetadataEditor.DefaultMaterialName);
                    else
                        PropMetadataEditor.ApplyPreviewMaterial(string.Empty);
                    PropMetadataEditor.MarkDirty();
                }
                GUILayout.EndHorizontal();

                if (_showMaterialDropdown)
                {
                    GUILayout.Label("Search");
                    _materialSearch = GUILayout.TextField(_materialSearch ?? string.Empty);

                    _materialScroll = GUILayout.BeginScrollView(_materialScroll, GUILayout.Height(MaterialListH));
                    int selectedIndex = PropMetadataEditor.GetMaterialIndex(PropMetadataEditor.SelectedMaterialName);
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
                            PropMetadataEditor.SelectMaterialByIndex(i);
                            _showMaterialDropdown = false;
                            PropMetadataEditor.MaterialExplicitlyChosen = true;
                            PropMetadataEditor.ApplyPreviewMaterial(PropMetadataEditor.SelectedMaterialName);
                            PropMetadataEditor.MarkDirty();
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
                    string slotDef = s < PropMetadataEditor.PerSlotDefault.Count ? PropMetadataEditor.PerSlotDefault[s] : string.Empty;
                    string slotSel = s < PropMetadataEditor.PerSlotSelected.Count ? PropMetadataEditor.PerSlotSelected[s] : slotDef;
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
                                while (PropMetadataEditor.PerSlotSelected.Count <= s) PropMetadataEditor.PerSlotSelected.Add(string.Empty);
                                PropMetadataEditor.PerSlotSelected[s] = string.IsNullOrEmpty(picked) ? slotDef : picked;
                                if (string.IsNullOrEmpty(picked))
                                    PropMetadataEditor.SlotHasExplicitOverride.Remove(s);
                                else
                                    PropMetadataEditor.SlotHasExplicitOverride.Add(s);
                                _openSlotDropdown = -1;
                                PropMetadataEditor.ApplySlotMaterial(s, picked);
                                PropMetadataEditor.MarkDirty();
                            }
                        }
                        GUILayout.EndScrollView();
                    }
                }

                if (GUILayout.Button("Reset all to default materials"))
                {
                    for (int s = 0; s < PropMetadataEditor.PerSlotSelected.Count; s++)
                        PropMetadataEditor.PerSlotSelected[s] = s < PropMetadataEditor.PerSlotDefault.Count ? PropMetadataEditor.PerSlotDefault[s] : string.Empty;
                    PropMetadataEditor.SlotHasExplicitOverride.Clear();
                    _openSlotDropdown = -1;
                    PropMetadataEditor.RestoreDefaultMaterials();
                    PropMetadataEditor.MarkDirty();
                }
            }

            GUILayout.Space(8f);

            var newKeepOrig = GUILayout.Toggle(PropMetadataEditor.KeepOriginalHierarchy, "Keep original hierarchy");
            if (newKeepOrig != PropMetadataEditor.KeepOriginalHierarchy)
            {
                PropMetadataEditor.KeepOriginalHierarchy = newKeepOrig;
                PropMetadataEditor.MarkDirty();
            }

            var newIsBush = GUILayout.Toggle(PropMetadataEditor.IsBush, "Is Bush");
            if (newIsBush != PropMetadataEditor.IsBush)
            {
                PropMetadataEditor.IsBush = newIsBush;
                PropMetadataEditor.ApplyBushCollider(PropMetadataEditor.SelectedLEO, newIsBush);
                PropMetadataEditor.ApplyCurrent();
                PropMetadataEditor.Dirty = false;
            }
            if (PropMetadataEditor.IsBush)
            {
                GUILayout.Label($"  Bush sphere radius: {PropMetadataEditor.BushRadius:F3} (local)");
                GUILayout.Label("  Grass type (sound)");
                if (GUILayout.Button(PropMetadataEditor.GrassTypeName(PropMetadataEditor.SoundGrassType), GUILayout.Height(22f)))
                    _showGrassTypeDropdown = !_showGrassTypeDropdown;
                if (_showGrassTypeDropdown)
                {
                    _grassTypeScroll = GUILayout.BeginScrollView(_grassTypeScroll, GUILayout.Height(120f));
                    foreach (var (lbl, val) in PropMetadataEditor.KnownGrassTypes)
                    {
                        string btnLbl = val == PropMetadataEditor.SoundGrassType ? "> " + lbl : lbl;
                        EnsureMaterialButtonStyle();
                        if (GUILayout.Button(btnLbl, _materialButtonStyle))
                        {
                            PropMetadataEditor.SoundGrassType = val;
                            _showGrassTypeDropdown = false;
                            if (PropMetadataEditor.SelectedLEO != null)
                            {
                                PropInstanceServices.BushAudioTracker.Unregister(PropMetadataEditor.SelectedLEO.transform);
                                PropInstanceServices.BushAudioTracker.Register(PropMetadataEditor.SelectedLEO.transform, PropMetadataEditor.BushRadius, PropMetadataEditor.SoundGrassType);
                            }
                            PropMetadataEditor.ApplyCurrent();
                            PropMetadataEditor.Dirty = false;
                        }
                    }
                    GUILayout.EndScrollView();
                }
            }

            GUILayout.Space(4f);
            GUILayout.Label(PropMetadataEditor.Index > 0
                ? $"Index: {PropMetadataEditor.Index}"
                : "Index: (not set)");

            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
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
