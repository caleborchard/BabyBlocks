using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace BabyBlocks
{
    [Serializable]
    public class PropExtraInfo
    {
        public string id;
        public string displayName;
        public string category;
        public bool excluded;
        public bool useRenderMeshCollider;
        public string overrideMaterialId;
        public string surfaceType;
        public int index;
        public List<string> disabledRenderers = new();
    }

    [Serializable]
    class PropExtraInfoSave
    {
        public int nextIndex = 1;
        public List<PropExtraInfo> items = new();
    }

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

        static float _lastChangeTime;
        static Renderer[] _selectedRenderers;
        static Material[][] _selectedDefaultMaterials;
        static LevelEditorObject _selectedLEO;

        static readonly List<Material> _microSplatLayerMats = new();

        static readonly Dictionary<string, PropExtraInfo> _byId = new(StringComparer.Ordinal);
        static bool _loaded;
        static int _nextIndex = 1;
        static bool _savePathLogged;
        static string _paletteSelectedId;

        static string SavePath =>
            Path.Combine(MelonEnvironment.UserDataDirectory, "BabyBlocks", "prop_metadata.json");

        public static void DrawGUI(LevelEditorObject selectedObject)
        {
            if (!Enabled)
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
            GUILayout.Space(4f);

            _mainScroll = GUILayout.BeginScrollView(_mainScroll, GUILayout.ExpandHeight(true));

            if (string.IsNullOrEmpty(_propId))
            {
                GUILayout.Label("Select a prop in the world to edit its details.");
                GUILayout.EndScrollView();
                GUILayout.EndVertical();
                return;
            }

            GUILayout.Label("Selected ID (click to copy)");
            var idContent = new GUIContent(_propId ?? string.Empty);
            var idRect = GUILayoutUtility.GetRect(idContent, GUI.skin.textField);
            if (GUI.Button(idRect, idContent, GUI.skin.textField))
                GUIUtility.systemCopyBuffer = _propId ?? string.Empty;

            GUILayout.Label(_index > 0
                ? $"Index: {_index}"
                : "Index: (not set)");

            GUILayout.Space(4f);

            GUILayout.Label("Display name");
            GUI.SetNextControlName(DisplayNameField);
            var newDisplayName = GUILayout.TextField(_displayName ?? string.Empty);
            if (!string.Equals(newDisplayName, _displayName, StringComparison.Ordinal))
            {
                _displayName = newDisplayName;
                MarkDirty();
            }

            GUILayout.Label("Category");
            GUI.SetNextControlName(CategoryField);
            var newCategory = GUILayout.TextField(_category ?? string.Empty);
            if (!string.Equals(newCategory, _category, StringComparison.Ordinal))
            {
                _category = newCategory;
                MarkDirty();
            }

            var newExclude = GUILayout.Toggle(_excluded, "Exclude item");
            if (newExclude != _excluded)
            {
                _excluded = newExclude;
                MarkDirty();
            }

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

            GUILayout.Space(4f);

            GUILayout.Label("Current material: " + (string.IsNullOrEmpty(_defaultMaterialName) ? "(unknown)" : _defaultMaterialName));

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

            GUILayout.Label("Override material");
            EnsureMaterialList();

            string overrideLabel = GetOverrideLabel();
            GUI.SetNextControlName(OverrideField);
            if (GUILayout.Button(overrideLabel, GUILayout.Height(22f)))
                _showMaterialDropdown = !_showMaterialDropdown;

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
                        ApplyPreviewMaterial(_selectedMaterialName);
                        MarkDirty();
                    }
                }
                GUILayout.EndScrollView();
            }

            if (GUILayout.Button("Reset to default material"))
            {
                _selectedMaterialName = _defaultMaterialName ?? string.Empty;
                _overrideMaterialName = string.Empty;
                _showMaterialDropdown = false;
                ApplyPreviewMaterial(string.Empty);
                MarkDirty();
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
        }

        static void AddMicroSplatLayerMaterials()
        {
            if (_microSplatLayerMats.Count > 0) return;
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

                MelonLogger.Msg(
                    $"[PropMetadata] MicroSplat base: '{baseMat.name}' shader: '{baseMat.shader.name}' " +
                    $"controlSlots: {controlProps.Length} layers: {layerCount} hasPerTexUV: {hasPerTexUV}");

                if (hasPerTexUV)
                {
                    var v0 = baseMat.GetVector("_PerTexUVScaleRotation0");
                    MelonLogger.Msg($"[PropMetadata] _PerTexUVScaleRotation0 = ({v0.x:F4},{v0.y:F4},{v0.z:F4},{v0.w:F4})");
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
                    MelonLogger.Msg($"[PropMetadata] Built {_microSplatLayerMats.Count} MicroSplat layer materials.");
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
            string overrideToSave = GetOverrideToSave();
            var info = Apply(_propId, _displayName, _category, _excluded, _useRenderMeshCollider,
                overrideToSave, _surfaceType, _disabledRendererPaths);
            if (info != null)
                _index = info.index;
            _overrideMaterialName = overrideToSave ?? string.Empty;
        }

        static string GetOverrideToSave()
        {
            if (string.IsNullOrEmpty(_selectedMaterialName)) return string.Empty;

            if (!string.IsNullOrEmpty(_defaultMaterialName)
                && string.Equals(_selectedMaterialName, _defaultMaterialName, StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            if (string.Equals(_selectedMaterialName, NoOverrideLabel, StringComparison.OrdinalIgnoreCase))
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
            _surfaceType = string.Empty;
            _index = -1;
            _dirty = false;
            _showMaterialDropdown = false;
            _showRendererDropdown = false;
            _showSurfaceTypeDropdown = false;
            _disabledRendererPaths.Clear();
            _rendererEntries.Clear();

            if (string.IsNullOrEmpty(id)) return;

            var propLibInfo = PropLibrary.FindById(id);
            _useRenderMeshCollider = propLibInfo == null || !propLibInfo.HasColliderParts;

            if (TryGet(id, out var info) && info != null)
            {
                _displayName = info.displayName ?? string.Empty;
                _category = info.category ?? string.Empty;
                _overrideMaterialName = info.overrideMaterialId ?? string.Empty;
                _surfaceType = info.surfaceType ?? string.Empty;
                _excluded = info.excluded;
                _useRenderMeshCollider = info.useRenderMeshCollider;
                _index = info.index;
                if (info.disabledRenderers != null)
                {
                    for (int i = 0; i < info.disabledRenderers.Count; i++)
                    {
                        var path = info.disabledRenderers[i];
                        if (!string.IsNullOrEmpty(path)) _disabledRendererPaths.Add(path);
                    }
                }
            }

            _selectedMaterialName = string.IsNullOrEmpty(_overrideMaterialName)
                ? _defaultMaterialName
                : _overrideMaterialName;

            BuildRendererEntries(selectedObject);
            ApplyRendererVisibility();
            EnsureMaterialList();
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
                RestoreDefaultMaterials();
                return;
            }
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
            float width = 400f;
            float height = 560f;
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

        static PropExtraInfo Apply(string id, string displayName, string category,
            bool excluded, bool useRenderMeshCollider, string overrideMaterialName,
            string surfaceType, HashSet<string> disabledRenderers)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return null;

            bool any = !string.IsNullOrEmpty(displayName)
                    || !string.IsNullOrEmpty(category)
                    || excluded
                    || useRenderMeshCollider
                    || !string.IsNullOrEmpty(overrideMaterialName)
                    || !string.IsNullOrEmpty(surfaceType)
                    || (disabledRenderers != null && disabledRenderers.Count > 0);

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
                info.overrideMaterialId = string.Empty;
                info.surfaceType        = string.Empty;
                info.disabledRenderers  = new List<string>();
                info.index              = 0;
            }
            else
            {
                if (info.index <= 0) info.index = _nextIndex++;
                info.displayName        = displayName ?? string.Empty;
                info.category           = category ?? string.Empty;
                info.excluded           = false;
                info.useRenderMeshCollider = useRenderMeshCollider;
                info.overrideMaterialId = overrideMaterialName ?? string.Empty;
                info.surfaceType        = surfaceType ?? string.Empty;
                info.disabledRenderers  = new List<string>();
                if (disabledRenderers != null)
                    foreach (var path in disabledRenderers) info.disabledRenderers.Add(path);
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
                    MelonLogger.Msg($"[PropMetadata] Saved to {SavePath}");
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
                    AppendJsonField(sb, "overrideMaterialId", item.overrideMaterialId, 6).Append(",\n");
                    AppendJsonField(sb, "surfaceType", item.surfaceType, 6).Append(",\n");
                    AppendJsonArray(sb, "disabledRenderers", item.disabledRenderers, 6).Append(",\n");
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
                    overrideMaterialId = ExtractString(obj, "overrideMaterialId"),
                    surfaceType = ExtractString(obj, "surfaceType"),
                    index = ExtractInt(obj, "index", 0),
                    disabledRenderers = ExtractStringArray(obj, "disabledRenderers")
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
    }
}
