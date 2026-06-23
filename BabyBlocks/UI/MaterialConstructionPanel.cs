using System;
using System.Collections.Generic;
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
        static int _dragStartFrame = -1;
        static int _scrollOffset;
        static float _scrollTimer;
        static bool _scrollActive;
        static bool _scrollInDelay;

        const float ScrollInitialDelay   = 0.35f;
        const float ScrollRepeatInterval = 0.08f;

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

        // Display order, sorted alphabetically by name and refreshed each DrawPalette
        // call. _draggingIndex indexes into this list rather than the raw entry list
        // so dragging stays consistent with what's on screen.
        static readonly List<MaterialConstructionEntry> _displayEntries = new List<MaterialConstructionEntry>();

        public static bool IsDragging => _draggingIndex >= 0 && _draggingIndex < _displayEntries.Count;
        public static MaterialConstructionEntry DraggingEntry => IsDragging ? _displayEntries[_draggingIndex] : null;
        public static void CancelDrag() => _draggingIndex = -1;

        // See PropPalette.JustStartedDrag — same one-frame grace period to avoid
        // dropping/applying immediately on the frame the drag started.
        public static bool JustStartedDrag => IsDragging && Time.frameCount == _dragStartFrame;

        // Call from LevelEditor.Update so - = page through the materials palette
        // while it's visible, mirroring PropPalette.HandleScrollInput.
        public static void HandleScrollInput()
        {
            bool paletteVisible = (Core.DebugMode && Active) || (!Core.DebugMode && PropPalette.ShowingMaterials);
            if (!paletteVisible) return;

            int total = MaterialConstructionLibrary.Entries.Count;
            if (total == 0) return;

            bool minusHeld  = Input.GetKey(KeyCode.Minus);
            bool equalsHeld = Input.GetKey(KeyCode.Equals);

            if (!minusHeld && !equalsHeld)
            {
                _scrollActive = false;
                _scrollTimer  = 0f;
                return;
            }

            int dir  = minusHeld ? -1 : 1;
            int page = VisibleSlots;

            if (!_scrollActive)
            {
                _scrollActive  = true;
                _scrollInDelay = true;
                _scrollTimer   = 0f;
                _scrollOffset  = PropPalette.StepPageOffset(_scrollOffset, dir, page, total);
                return;
            }

            _scrollTimer += Time.unscaledDeltaTime;
            float threshold = _scrollInDelay ? ScrollInitialDelay : ScrollRepeatInterval;
            if (_scrollTimer >= threshold)
            {
                _scrollTimer  -= threshold;
                _scrollInDelay = false;
                _scrollOffset  = PropPalette.StepPageOffset(_scrollOffset, dir, page, total);
            }
        }

        // ── Left-side palette (replaces PropPalette while active) ───────────────

        public static void DrawPalette(Event e, bool showAddButton = true)
        {
            EnsureStyles();

            _displayEntries.Clear();
            _displayEntries.AddRange(MaterialConstructionLibrary.Entries);
            _displayEntries.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            var entries = _displayEntries;
            int total   = entries.Count;
            int visible = VisibleSlots;

            _scrollOffset = Mathf.Clamp(_scrollOffset, 0, Mathf.Max(0, total - 1));

            float panelH    = Pad + visible * (ItemH + Pad) + (showAddButton ? 22f : 0f) + 18f;
            var   panelRect = new Rect(10f, 10f, PanelW, panelH);
            PropPalette.PanelRect = panelRect;

            GUI.color = new Color(0f, 0f, 0f, 0.65f);
            GUI.Box(panelRect, "");
            GUI.color = Color.white;

            if (e.type == EventType.ScrollWheel && panelRect.Contains(e.mousePosition) && total > visible)
            {
                int dir = (int)Mathf.Sign(e.delta.y);
                _scrollOffset = PropPalette.StepPageOffset(_scrollOffset, dir, visible, total);
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
                        _draggingIndex  = entryIdx;
                        _dragStartFrame = Time.frameCount;
                        // Dragging straight out of a focused search/name field (without
                        // clicking elsewhere first to defocus it) can leave Unity's input
                        // state out of sync for the first frames of the drag, causing the
                        // dragged item to drop immediately. Clear focus up front.
                        GUI.FocusControl(null);
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

            // Page label at bottom of panel.
            if (total > 0)
            {
                int pageCount   = Mathf.CeilToInt(total / (float)visible);
                int currentPage = (_scrollOffset / visible) + 1;
                GUI.color = new Color(0.75f, 0.75f, 0.75f, 1f);
                float pageLabelY = 10f + Pad + visible * (ItemH + Pad) + (showAddButton ? 22f : 2f);
                GUI.Label(new Rect(10f + Pad, pageLabelY, ItemW, 18f), $"{currentPage}/{pageCount}");
                GUI.color = Color.white;
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
            string matLabel = string.IsNullOrEmpty(entry.materialName) ? PropMetadataStore.NoOverrideLabel : entry.materialName;
            if (GUILayout.Button(matLabel, GUILayout.Height(22f)))
                _showMaterialDropdown = !_showMaterialDropdown;

            if (_showMaterialDropdown)
            {
                MaterialCatalog.EnsureMaterialListLoaded();
                GUILayout.Label("Search");
                GUI.SetNextControlName(SearchField);
                _materialSearch = GUILayout.TextField(_materialSearch ?? "");

                _materialScroll = GUILayout.BeginScrollView(_materialScroll, GUILayout.Height(140f));
                var names  = MaterialCatalog.MaterialNames;
                var labels = MaterialCatalog.MaterialLabels;
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
                foreach (var tag in PropMetadataEditor.KnownSurfaceTags)
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

            GUILayout.Space(8f);
            bool newSunglasses = GUILayout.Toggle(entry.sunglassesNeeded, "Sunglasses Needed");
            if (newSunglasses != entry.sunglassesNeeded)
            {
                entry.sunglassesNeeded = newSunglasses;
                MarkDirty();
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
                    MaterialCatalog.EnsureMaterialListLoaded();
                    mat = MaterialCatalog.ResolveMaterialByName(materialName);
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

            // Only synced for props placed/received over the network this session (netId
            // != 0) - applying to a non-networked prop is a purely local edit.
            if (foundLeo.netId != 0)
                BabyBlocks.Networking.ModNetworking.SendMaterialApplied(foundLeo.netId, entry.id);
        }

        public static void ApplyToInstance(LevelEditorObject leo, MaterialConstructionEntry entry, bool pushHistory = true)
        {
            if (leo == null || entry == null) return;

            if (entry.id == int.MinValue)
            {
                ResetToDefaultMaterials(leo, pushHistory);
                return;
            }

            var renderers = leo.GetComponentsInChildren<Renderer>(true);
            var matsBefore = new Material[renderers.Length][];
            for (int i = 0; i < renderers.Length; i++)
                matsBefore[i] = renderers[i] != null ? renderers[i].sharedMaterials : null;

            var colliders = leo.GetComponentsInChildren<Collider>(true);
            var tagObjs = new GameObject[1 + colliders.Length];
            tagObjs[0] = leo.gameObject;
            for (int i = 0; i < colliders.Length; i++)
                tagObjs[1 + i] = colliders[i] != null ? colliders[i].gameObject : null;
            var tagsBefore = new string[tagObjs.Length];
            for (int i = 0; i < tagObjs.Length; i++)
                tagsBefore[i] = tagObjs[i] != null ? tagObjs[i].tag : null;

            int idBefore = leo.materialConstructionId;

            if (!string.IsNullOrEmpty(entry.materialName))
            {
                MaterialCatalog.EnsureMaterialListLoaded();
                var mat = MaterialCatalog.ResolveMaterialByName(entry.materialName);
                if (mat != null)
                {
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

            PropInstanceServices.ApplySurfaceType(leo, entry.surfaceType);
            leo.materialConstructionId = entry.id;

            var existingChecker = leo.GetComponent<BbSunglassesChecker>();
            if (entry.sunglassesNeeded)
            {
                if (existingChecker == null) leo.gameObject.AddComponent<BbSunglassesChecker>();
            }
            else
            {
                if (existingChecker != null) UnityEngine.Object.DestroyImmediate(existingChecker);
            }

            if (pushHistory)
            {
                LevelEditorHistory.PushMaterial(leo, renderers, matsBefore, tagObjs, tagsBefore, idBefore);
                PropHistory.RecordMaterialUse(entry.id);
            }
        }

        // ── Reset to default ─────────────────────────────────────────────────

        static void ResetToDefaultMaterials(LevelEditorObject leo, bool pushHistory)
        {
            var renderers  = leo.GetComponentsInChildren<Renderer>(true);
            var matsBefore = new Material[renderers.Length][];
            for (int i = 0; i < renderers.Length; i++)
                matsBefore[i] = renderers[i] != null ? renderers[i].sharedMaterials : null;

            var colliders = leo.GetComponentsInChildren<Collider>(true);
            var tagObjs   = new GameObject[1 + colliders.Length];
            tagObjs[0] = leo.gameObject;
            for (int i = 0; i < colliders.Length; i++)
                tagObjs[1 + i] = colliders[i] != null ? colliders[i].gameObject : null;
            var tagsBefore = new string[tagObjs.Length];
            for (int i = 0; i < tagObjs.Length; i++)
                tagsBefore[i] = tagObjs[i] != null ? tagObjs[i].tag : null;

            int idBefore = leo.materialConstructionId;

            // Restore original prefab materials from the loaded addressable
            var info = PropLibrary.FindById(leo.addressableKey);
            if (info?.sourcePrefab != null)
            {
                var srcRenderers = info.sourcePrefab.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length && i < srcRenderers.Length; i++)
                {
                    if (renderers[i] == null || srcRenderers[i] == null) continue;
                    renderers[i].sharedMaterials = srcRenderers[i].sharedMaterials;
                }
            }

            PropInstanceServices.ApplySurfaceType(leo, "");
            var existingChecker = leo.GetComponent<BbSunglassesChecker>();
            if (existingChecker != null) UnityEngine.Object.DestroyImmediate(existingChecker);

            leo.materialConstructionId = -1;

            if (pushHistory)
                LevelEditorHistory.PushMaterial(leo, renderers, matsBefore, tagObjs, tagsBefore, idBefore);
        }

        // ── Styles ───────────────────────────────────────────────────────────

        static void EnsureStyles()
        {
            GuiStyleHelpers.EnsureItemAndGhostStyles(ref _itemStyle, ref _ghostStyle);

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
