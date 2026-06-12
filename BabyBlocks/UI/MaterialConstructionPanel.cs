using System;
using UnityEngine;

namespace BabyBlocks
{
    // Debug-mode tool: lets the user build named material/surface-type combos and drag
    // them onto individual placed props to re-skin just that one instance (as opposed to
    // PropMetadataPanel's per-prop-TYPE override, which applies to every placement).
    // While active, this panel replaces PropPalette on the left side of the screen and
    // PropMetadataPanel's per-prop body in the "Prop Details" window.
    static class MaterialConstructionPanel
    {
        const float Pad    = 8f;
        const float ItemH  = 60f;
        const float PanelW = 110f;
        const float ItemW  = PanelW - Pad * 2f; // 94
        const float AutoSaveDelay = 0.75f;
        const int PreviewSize = 128;

        const string NameField   = "matConstructionName";
        const string SearchField = "matConstructionSearch";

        public static bool IsTypingInUI
        {
            get
            {
                if (!Active) return false;
                string focused = GUI.GetNameOfFocusedControl();
                return focused == NameField || focused == SearchField;
            }
        }

        static int VisibleSlots =>
            Mathf.Clamp(Mathf.FloorToInt((Screen.height - 50f) / (ItemH + Pad)), 4, 15);

        static bool _active;
        public static bool Active
        {
            get => _active;
            set
            {
                _active = value;
                if (!value)
                {
                    CancelDrag();
                    _showMaterialDropdown = false;
                    _showSurfaceDropdown = false;
                }
            }
        }

        static MaterialConstructionEntry _editing;
        static int _draggingIndex = -1;
        static int _scrollOffset;

        static bool _showMaterialDropdown;
        static bool _showSurfaceDropdown;
        static string _materialSearch = "";
        static Vector2 _materialScroll;
        static Vector2 _surfaceScroll;

        static bool _dirty;
        static float _lastChangeTime;

        static GUIStyle _itemStyle;
        static GUIStyle _ghostStyle;
        static GUIStyle _buttonStyle;
        static GUIStyle _headerStyle;

        public static bool IsDragging => _draggingIndex >= 0 && _draggingIndex < MaterialConstructionLibrary.Entries.Count;
        public static MaterialConstructionEntry DraggingEntry => IsDragging ? MaterialConstructionLibrary.Entries[_draggingIndex] : null;
        public static void CancelDrag() => _draggingIndex = -1;

        // ── Left-side palette (replaces PropPalette while active) ───────────────

        public static void DrawPalette(Event e, bool showAddButton = true)
        {
            EnsureStyles();

            var entries = MaterialConstructionLibrary.Entries;
            int total   = entries.Count;
            int visible = VisibleSlots;

            _scrollOffset = Mathf.Clamp(_scrollOffset, 0, Mathf.Max(0, total - visible));

            float panelH    = Pad + visible * (ItemH + Pad) + (showAddButton ? 22f : 0f);
            var   panelRect = new Rect(10f, 10f, PanelW, panelH);
            PropPalette.PanelRect = panelRect;

            GUI.color = new Color(0f, 0f, 0f, 0.65f);
            GUI.Box(panelRect, "");
            GUI.color = Color.white;

            if (e.type == EventType.ScrollWheel && panelRect.Contains(e.mousePosition) && total > visible)
            {
                _scrollOffset = Mathf.Clamp(_scrollOffset + (int)Mathf.Sign(e.delta.y), 0, Mathf.Max(0, total - visible));
                e.Use();
            }

            for (int i = 0; i < visible; i++)
            {
                int   entryIdx = _scrollOffset + i;
                float y        = 10f + Pad + i * (ItemH + Pad);
                var   itemRect = new Rect(10f + Pad, y, ItemW, ItemH);

                if (entryIdx < total)
                {
                    var  entry    = entries[entryIdx];
                    bool isEditing = showAddButton && _editing == entry;
                    bool hovered   = itemRect.Contains(e.mousePosition) && !IsDragging;

                    GUI.color = isEditing
                        ? new Color(1f, 0.85f, 0.3f, 0.95f)
                        : hovered ? new Color(1f, 1f, 1f, 0.95f) : new Color(0.6f, 0.6f, 0.6f, 0.85f);
                    GUI.Box(itemRect, entry.name, _itemStyle);

                    if (e.type == EventType.MouseDown && e.button == 0 && itemRect.Contains(e.mousePosition))
                    {
                        if (showAddButton)
                        {
                            _editing = entry;
                            _showMaterialDropdown = false;
                            _showSurfaceDropdown = false;
                        }
                        _draggingIndex = entryIdx;
                        e.Use();
                    }
                }
                else
                {
                    GUI.color = new Color(0.25f, 0.25f, 0.25f, 0.5f);
                    GUI.Box(itemRect, "", _itemStyle);
                }
            }

            GUI.color = Color.white;

            // "New" button in the footer row.
            if (showAddButton)
            {
                var newBtnRect = new Rect(10f + Pad, 10f + Pad + visible * (ItemH + Pad) + 2f, ItemW, 18f);
                if (GUI.Button(newBtnRect, "+ New"))
                {
                    _editing = MaterialConstructionLibrary.CreateNew();
                    _showMaterialDropdown = false;
                    _showSurfaceDropdown = false;
                }
            }

            if (total == 0)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.85f);
                string emptyMsg = showAddButton ? "No materials yet.\nClick + New below." : "No materials created yet.";
                GUI.Label(new Rect(10f + Pad, 10f + Pad, ItemW, ItemH), emptyMsg, _itemStyle);
                GUI.color = Color.white;
            }

            // Ghost label following cursor while dragging.
            if (IsDragging && panelRect.Contains(e.mousePosition))
            {
                GUI.color = new Color(1f, 1f, 0.4f, 0.80f);
                GUI.Box(
                    new Rect(e.mousePosition.x + 12f, e.mousePosition.y + 12f,
                             ItemW * 0.75f, ItemH * 0.75f),
                    DraggingEntry?.name ?? "", _ghostStyle);
                GUI.color = Color.white;
            }
        }

        // ── Right-side constructor (replaces the per-prop body in Prop Details) ─

        public static void DrawConstructor()
        {
            EnsureStyles();

            GUILayout.Label("Material Construction", _headerStyle);
            GUILayout.Space(4f);
            GUILayout.Label("Items on the left are your saved material+surface combos. Click one to "
                + "edit it here, or drag it onto a prop in the world to apply it to just that prop.");
            GUILayout.Space(8f);

            if (GUILayout.Button("New Material"))
            {
                _editing = MaterialConstructionLibrary.CreateNew();
                _showMaterialDropdown = false;
                _showSurfaceDropdown = false;
            }

            var entry = _editing;
            if (entry == null || !MaterialConstructionLibrary.Entries.Contains(entry))
            {
                _editing = null;
                GUILayout.Space(8f);
                GUILayout.Label("Select an item on the left to edit it, or click New Material to create one.");
                AutoSaveIfIdle();
                return;
            }

            GUILayout.Space(8f);
            GUILayout.Label("Name");
            GUI.SetNextControlName(NameField);
            string newName = GUILayout.TextField(entry.name ?? "");
            if (!string.Equals(newName, entry.name, StringComparison.Ordinal))
            {
                entry.name = newName;
                MarkDirty();
            }

            GUILayout.Space(8f);
            DrawSpherePreview(entry);

            GUILayout.Space(8f);
            GUILayout.Label("Material");
            string matLabel = string.IsNullOrEmpty(entry.materialName) ? PropMetadataPanel.NoOverrideLabel : entry.materialName;
            if (GUILayout.Button(matLabel, GUILayout.Height(22f)))
                _showMaterialDropdown = !_showMaterialDropdown;

            if (_showMaterialDropdown)
            {
                PropMetadataPanel.EnsureMaterialListLoaded();
                GUILayout.Label("Search");
                GUI.SetNextControlName(SearchField);
                _materialSearch = GUILayout.TextField(_materialSearch ?? "");

                _materialScroll = GUILayout.BeginScrollView(_materialScroll, GUILayout.Height(140f));
                var names  = PropMetadataPanel.MaterialNames;
                var labels = PropMetadataPanel.MaterialLabels;
                string search = (_materialSearch ?? "").Trim();
                bool hasSearch = !string.IsNullOrEmpty(search);

                for (int i = 0; i < labels.Count; i++)
                {
                    string label = labels[i];
                    if (hasSearch && label.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    string name = i == 0 ? string.Empty : names[i];
                    if (string.Equals(name, entry.materialName, StringComparison.OrdinalIgnoreCase))
                        label = "> " + label;

                    if (GUILayout.Button(label, _buttonStyle))
                    {
                        entry.materialName = name;
                        _showMaterialDropdown = false;
                        MarkDirty();
                    }
                }
                GUILayout.EndScrollView();
            }

            GUILayout.Space(8f);
            GUILayout.Label("Surface type (traction / footstep audio)");
            string surfLabel = string.IsNullOrEmpty(entry.surfaceType) ? "(none — game default)" : entry.surfaceType;
            if (GUILayout.Button(surfLabel, GUILayout.Height(22f)))
                _showSurfaceDropdown = !_showSurfaceDropdown;

            if (_showSurfaceDropdown)
            {
                _surfaceScroll = GUILayout.BeginScrollView(_surfaceScroll, GUILayout.Height(120f));
                foreach (var tag in PropMetadataPanel.KnownSurfaceTags)
                {
                    string lbl = string.IsNullOrEmpty(tag) ? "(none — game default)" : tag;
                    if (string.Equals(tag, entry.surfaceType, StringComparison.Ordinal))
                        lbl = "> " + lbl;

                    if (GUILayout.Button(lbl, _buttonStyle))
                    {
                        entry.surfaceType = tag;
                        _showSurfaceDropdown = false;
                        MarkDirty();
                    }
                }
                GUILayout.EndScrollView();
            }

            GUILayout.Space(12f);
            if (GUILayout.Button("Delete this material"))
            {
                MaterialConstructionLibrary.Delete(entry);
                if (IsDragging && DraggingEntry == entry) CancelDrag();
                _editing = null;
            }

            AutoSaveIfIdle();
        }

        static void MarkDirty()
        {
            _dirty = true;
            _lastChangeTime = Time.unscaledTime;
            MaterialConstructionLibrary.MarkDirty();
        }

        static void AutoSaveIfIdle()
        {
            if (!_dirty) return;
            if (Time.unscaledTime - _lastChangeTime < AutoSaveDelay) return;
            MaterialConstructionLibrary.Save();
            _dirty = false;
        }

        // ── Sphere preview ───────────────────────────────────────────────────

        static GameObject _previewRoot;
        static GameObject _previewSphere;
        static Renderer _previewRenderer;
        static Camera _previewCamera;
        static RenderTexture _previewRT;
        static Material _defaultPreviewMaterial;
        static string _previewMatName;

        static void DrawSpherePreview(MaterialConstructionEntry entry)
        {
            EnsurePreviewRig();
            UpdatePreview(entry.materialName);
            _previewCamera.Render();

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            var rect = GUILayoutUtility.GetRect(PreviewSize, PreviewSize, GUILayout.Width(PreviewSize), GUILayout.Height(PreviewSize));
            GUI.DrawTexture(rect, _previewRT);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        static void EnsurePreviewRig()
        {
            if (_previewRoot != null) return;

            int layer = MaterialBaker.FindUnusedLayer();

            _previewRoot = new GameObject("BabyBlocks_MatConstructionPreview");
            _previewRoot.transform.position = new Vector3(0f, 50000f, 0f);
            UnityEngine.Object.DontDestroyOnLoad(_previewRoot);

            _previewSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _previewSphere.name = "PreviewSphere";
            _previewSphere.transform.SetParent(_previewRoot.transform, false);
            _previewSphere.transform.localPosition = Vector3.zero;
            _previewSphere.layer = layer;
            var col = _previewSphere.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col);

            _previewRenderer = _previewSphere.GetComponent<Renderer>();
            _defaultPreviewMaterial = _previewRenderer.sharedMaterial;

            var lightGo = new GameObject("PreviewLight");
            lightGo.transform.SetParent(_previewRoot.transform, false);
            lightGo.transform.rotation = Quaternion.Euler(40f, -30f, 0f);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.cullingMask = 1 << layer;
            light.intensity = 1.2f;

            var camGo = new GameObject("PreviewCamera");
            camGo.transform.SetParent(_previewRoot.transform, false);
            camGo.transform.localPosition = new Vector3(0f, 0f, -2.6f);
            camGo.transform.localRotation = Quaternion.identity;
            _previewCamera = camGo.AddComponent<Camera>();
            _previewCamera.enabled = false;
            _previewCamera.clearFlags = CameraClearFlags.SolidColor;
            _previewCamera.backgroundColor = new Color(0.12f, 0.12f, 0.12f, 1f);
            _previewCamera.cullingMask = 1 << layer;
            _previewCamera.fieldOfView = 28f;
            _previewCamera.nearClipPlane = 0.05f;
            _previewCamera.farClipPlane = 10f;

            _previewRT = new RenderTexture(PreviewSize, PreviewSize, 16, RenderTextureFormat.ARGB32);
            _previewRT.Create();
            _previewCamera.targetTexture = _previewRT;
        }

        static void UpdatePreview(string materialName)
        {
            if (!string.Equals(materialName, _previewMatName, StringComparison.Ordinal))
            {
                _previewMatName = materialName;
                Material mat = null;
                if (!string.IsNullOrEmpty(materialName))
                {
                    PropMetadataPanel.EnsureMaterialListLoaded();
                    mat = PropMetadataPanel.ResolveMaterialByName(materialName);
                }
                _previewRenderer.sharedMaterial = mat != null ? mat : _defaultPreviewMaterial;
            }

            _previewSphere.transform.rotation = Quaternion.Euler(15f, Time.realtimeSinceStartup * 25f % 360f, 0f);
        }

        // ── Drag-apply to a placed prop ─────────────────────────────────────────

        public static void TryApplyToHoveredProp()
        {
            var entry = DraggingEntry;
            if (entry == null) return;

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
            ApplyToInstance(foundLeo, entry);
        }

        public static void ApplyToInstance(LevelEditorObject leo, MaterialConstructionEntry entry)
        {
            if (leo == null || entry == null) return;

            if (!string.IsNullOrEmpty(entry.materialName))
            {
                PropMetadataPanel.EnsureMaterialListLoaded();
                var mat = PropMetadataPanel.ResolveMaterialByName(entry.materialName);
                if (mat != null)
                {
                    var renderers = leo.GetComponentsInChildren<Renderer>(true);
                    for (int i = 0; i < renderers.Length; i++)
                    {
                        var r = renderers[i];
                        if (r == null) continue;
                        var existing = r.sharedMaterials;
                        int count = existing != null && existing.Length > 0 ? existing.Length : 1;
                        var mats = new Material[count];
                        for (int m = 0; m < count; m++) mats[m] = mat;
                        r.sharedMaterials = mats;
                    }
                }
            }

            PropMetadataPanel.ApplySurfaceType(leo, entry.surfaceType);
            leo.materialConstructionId = entry.id;
        }

        // ── Styles ───────────────────────────────────────────────────────────

        static void EnsureStyles()
        {
            if (_itemStyle == null)
            {
                var padding = new RectOffset { left = 4, right = 4, top = 4, bottom = 4 };
                _itemStyle = new GUIStyle(GUI.skin.box)
                {
                    wordWrap = true,
                    alignment = TextAnchor.MiddleCenter,
                    clipping = TextClipping.Clip,
                    padding = padding
                };
            }

            if (_ghostStyle == null)
                _ghostStyle = new GUIStyle(_itemStyle);

            if (_buttonStyle == null)
            {
                var padding = new RectOffset { left = 6, right = 6, top = 2, bottom = 2 };
                _buttonStyle = new GUIStyle(GUI.skin.button)
                {
                    alignment = TextAnchor.MiddleLeft,
                    padding = padding
                };
            }

            if (_headerStyle == null)
            {
                _headerStyle = new GUIStyle(GUI.skin.label)
                {
                    fontStyle = FontStyle.Bold
                };
            }
        }
    }
}
