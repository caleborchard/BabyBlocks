using System;
using System.Collections.Generic;
using Il2Cpp;
using UnityEngine;

namespace BabyBlocks
{
    public static class LevelEditor
    {
        public enum ToolMode { Translate, Scale, Rotate }

        public static ToolMode          currentTool    = ToolMode.Translate;
        public static LevelEditorObject selectedObject;
        public static bool              isDragging     => _isDragging;
        public static IReadOnlyList<LevelEditorObject> SelectedObjects => _selection;

        static readonly List<LevelEditorObject> _selection = new();
        static bool       _isDragging;
        static int        _dragAxis;
        static Vector3    _dragStartPos, _dragStartScale, _dragStartHit, _dragPlaneNormal, _dragPivot;
        static Quaternion _dragStartRot;
        static Vector2    _dragStartMouse;
        static int        _hoveredAxis = -1;
        static bool       _pivotLocked;

        public static bool IsTypingInUI => PropMetadataPanel.IsTypingInUI;

        static readonly List<LevelEditorObject> _dragObjects = new();
        static readonly List<Vector3> _dragStartPositions = new();
        static readonly List<Vector3> _dragStartScales = new();
        static readonly List<Quaternion> _dragStartRotations = new();

        struct CopyEntry
        {
            public string addressableKey;
            public PrimitiveType primType;
            public Vector3 offset;
            public Vector3 scale;
            public Quaternion rotation;
            public bool isAddressable;
        }

        static readonly List<CopyEntry> _copyEntries = new();
        static Vector3 _copyPivot;
        static bool _copyHasValue;
        static readonly Vector3 PasteOffset = new Vector3(0.5f, 0f, 0.5f);

        static bool _snapEnabled = false;
        const float SnapStep = 0.1f;
        const float RotateSnapMultiplier = 100f;

        static float _lastUnloadCheck;
        const float UnloadCheckInterval = 15f;

        public static void EnsureManager()
        {
            if (LevelEditorManager.Instance != null) return;
            new GameObject("LevelEditorManager").AddComponent<LevelEditorManager>();
        }

        public static void HideGizmo() => GizmoRenderer.SetActive(false);

        public static void Select(LevelEditorObject obj)
        {
            _selection.Clear();
            if (obj != null)
            {
                _selection.Add(obj);
                selectedObject = obj;
                if (!GizmoRenderer.IsReady) GizmoRenderer.Init();
            }
            else
            {
                selectedObject = null;
            }
        }

        static void ToggleSelection(LevelEditorObject obj)
        {
            if (obj == null) return;
            if (!_selection.Contains(obj))
            {
                _selection.Add(obj);
                selectedObject = obj;
            }
            else
            {
                _selection.Remove(obj);
                if (selectedObject == obj)
                    selectedObject = _selection.Count > 0 ? _selection[_selection.Count - 1] : null;
            }
            if (_selection.Count > 0 && !GizmoRenderer.IsReady) GizmoRenderer.Init();
        }

        static void ClearSelection()
        {
            _selection.Clear();
            selectedObject = null;
        }

        public static void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastUnloadCheck >= UnloadCheckInterval)
            {
                _lastUnloadCheck = now;
                PropLibrary.ProcessUnloadQueue();
            }

            bool blockShortcuts = IsTypingInUI;
            bool overUI = IsPointerOverUI();

            if (!blockShortcuts && Input.GetKeyDown(KeyCode.Space) && selectedObject != null)
                currentTool = currentTool == ToolMode.Translate ? ToolMode.Scale
                            : currentTool == ToolMode.Scale     ? ToolMode.Rotate
                            : ToolMode.Translate;

            bool ctrlDown = !blockShortcuts
                && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl));

            if (!blockShortcuts && Input.GetKeyDown(KeyCode.X))
                _snapEnabled = !_snapEnabled;

            if (!blockShortcuts)
                PropPalette.HandleScrollInput();

            GizmoRenderer.EnsureCamera();
            var main = Camera.main;
            if (main != null) GizmoRenderer.Sync(_selection, selectedObject, currentTool, main);
            UpdateHover(overUI);
            GizmoRenderer.Draw(_hoveredAxis, currentTool);
            GizmoRenderer.DrawOutline(_selection, main);

            if (Input.GetMouseButton(1)) return;

            if (!blockShortcuts && Input.GetKeyDown(KeyCode.Delete) && selectedObject != null)
                DeleteSelected();

            bool ctrl = ctrlDown;
            if (ctrl && Input.GetKeyDown(KeyCode.Z)) LevelEditorHistory.Undo();
            if (ctrl && Input.GetKeyDown(KeyCode.Y)) LevelEditorHistory.Redo();
            if (ctrl && Input.GetKeyDown(KeyCode.C)) CopySelected();
            if (ctrl && Input.GetKeyDown(KeyCode.V)) PasteCopy();

            if (PropPalette.IsDragging)
            {
                if (!Input.GetMouseButton(0))
                {
                    if (!overUI) TryDropProp();
                    PropPalette.CancelDrag();
                }
                return;
            }

            if (!overUI && Input.GetMouseButtonDown(0)) TryBeginDrag();

            if (_isDragging)
            {
                if (Input.GetMouseButton(0))
                {
                    ContinueDrag();
                }
                else
                {
                    _isDragging = false;
                    if (_pivotLocked)
                    {
                        GizmoRenderer.ClearPivotOverride();
                        _pivotLocked = false;
                    }
                    if (_dragObjects.Count > 0)
                    {
                        for (int i = 0; i < _dragObjects.Count; i++)
                        {
                            var obj = _dragObjects[i];
                            if (obj == null) continue;
                            LevelEditorHistory.PushTransform(obj, _dragStartPositions[i], _dragStartScales[i], _dragStartRotations[i]);
                        }
                    }
                }
            }
        }

        public static void OnGUI()
        {
            PropPalette.DrawGUI(Event.current);
            PropMetadataPanel.DrawGUI(selectedObject);

            string tool = currentTool == ToolMode.Translate ? "MOVE"
                        : currentTool == ToolMode.Scale     ? "SCALE" : "ROTATE";
            string msg = selectedObject != null
                ? $"LEVEL EDITOR  [{tool}]  |  Drag arrows / sphere  |  Space = switch tool  |  ` = toggle editor  |  RMB = orbit"
                : "LEVEL EDITOR  |  Drag a prop from the palette onto the terrain  |  RMB = orbit";

            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.Box(new Rect(10, Screen.height - 28, 600, 22), "");
            GUI.color = Color.white;
            GUI.Label(new Rect(14, Screen.height - 27, 596, 20), msg);
            GUI.color = Color.white;
        }

        static void UpdateHover(bool overUI)
        {
            if (overUI || !GizmoRenderer.IsReady || selectedObject == null || Input.GetMouseButton(1))
            {
                _hoveredAxis = -1;
                return;
            }
            if (_isDragging) { _hoveredAxis = _dragAxis; return; }

            var handle = GizmoRenderer.RaycastHandle(Camera.main.ScreenPointToRay(Input.mousePosition));
            _hoveredAxis = handle != null ? handle.axisIndex : -1;
        }

        static bool IsPointerOverUI()
        {
            var mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            if (PropPalette.PanelRect.Contains(mouse)) return true;
            if (PropMetadataPanel.ContainsPoint(mouse)) return true;
            return false;
        }

        static void TryDropProp()
        {
            var ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            if (!Physics.Raycast(ray, out var hit, 2000f, LayerCache.PropTerrainMask)) return;

            var prop = PropPalette.DraggingProp;
            if (prop == null) return;

            EnsureManager();

            PropLibrary.LoadPropData(prop);
            var obj = LevelEditorManager.Instance.SpawnFromPropInfo(prop, hit.point + Vector3.up * 0.5f);
            if (obj == null) return;
            Select(obj);
            LevelEditorHistory.PushSpawn(obj);
        }

        static void TryBeginDrag()
        {
            var ray    = Camera.main.ScreenPointToRay(Input.mousePosition);
            GizmoHandle chosen = selectedObject != null ? GizmoRenderer.RaycastHandle(ray) : null;

            if (chosen == null)
            {
                if (Physics.Raycast(ray, out var hit, 2000f, ~GizmoRenderer.Mask))
                {
                    var leo = hit.collider.GetComponent<LevelEditorObject>()
                           ?? hit.collider.GetComponentInParent<LevelEditorObject>();
                    if (leo != null)
                    {
                        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                        if (shift) ToggleSelection(leo);
                        else Select(leo);
                        return;
                    }
                }
                if (!Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift))
                    ClearSelection();
                return;
            }

            _isDragging     = true;
            _dragAxis       = chosen.axisIndex;
            _dragStartPos   = selectedObject.transform.position;
            _dragStartScale = selectedObject.transform.localScale;
            _dragStartRot   = selectedObject.transform.rotation;
            _dragStartMouse = new Vector2(Input.mousePosition.x, Input.mousePosition.y);
            _dragPivot      = GizmoRenderer.PivotPosition;
            if (currentTool == ToolMode.Rotate)
            {
                GizmoRenderer.SetPivotOverride(_dragPivot);
                _pivotLocked = true;
            }

            _dragObjects.Clear();
            _dragStartPositions.Clear();
            _dragStartScales.Clear();
            _dragStartRotations.Clear();
            for (int i = 0; i < _selection.Count; i++)
            {
                var obj = _selection[i];
                if (obj == null) continue;
                _dragObjects.Add(obj);
                _dragStartPositions.Add(obj.transform.position);
                _dragStartScales.Add(obj.transform.localScale);
                _dragStartRotations.Add(obj.transform.rotation);
            }

            var cam = Camera.main;

            if (currentTool == ToolMode.Rotate)
            {
                _dragPlaneNormal = AxisVec(_dragAxis);
                var plane = new Plane(_dragPlaneNormal, _dragPivot);
                if (plane.Raycast(ray, out float enter))
                    _dragStartHit = ray.GetPoint(enter);

                var pivotScreen = cam.WorldToScreenPoint(_dragPivot);
                _dragStartMouse = new Vector2(Input.mousePosition.x, Input.mousePosition.y) - new Vector2(pivotScreen.x, pivotScreen.y);
                return;
            }

            if (_dragAxis == 3)
            {
                _dragPlaneNormal = cam.transform.forward;
            }
            else if (TryGetPlaneNormal(_dragAxis, out var planeNormal))
            {
                _dragPlaneNormal = currentTool == ToolMode.Scale
                    ? selectedObject.transform.rotation * planeNormal
                    : planeNormal;
            }
            else
            {
                var axis   = AxisVec(_dragAxis);
                var camFwd = cam.transform.forward;
                var normal = camFwd - Vector3.Dot(camFwd, axis) * axis;
                if (normal.sqrMagnitude < 0.01f)
                {
                    var camUp = cam.transform.up;
                    normal = camUp - Vector3.Dot(camUp, axis) * axis;
                }
                _dragPlaneNormal = normal.normalized;
            }

            var pl = new Plane(_dragPlaneNormal, _dragPivot);
            if (pl.Raycast(ray, out float e))
                _dragStartHit = ray.GetPoint(e);
        }

        static void ContinueDrag()
        {
            if (selectedObject == null) { _isDragging = false; return; }

            var cam   = Camera.main;
            var mouse = new Vector2(Input.mousePosition.x, Input.mousePosition.y);

            if (currentTool == ToolMode.Rotate)
            {
                var rotRay = cam.ScreenPointToRay(Input.mousePosition);
                var rotPlane = new Plane(_dragPlaneNormal, _dragPivot);
                if (!rotPlane.Raycast(rotRay, out float rotEnter)) return;
                var hit = rotRay.GetPoint(rotEnter);

                var from = _dragStartHit - _dragPivot;
                var to = hit - _dragPivot;
                if (from.sqrMagnitude < 0.001f || to.sqrMagnitude < 0.001f) return;

                float angle = Vector3.SignedAngle(from, to, AxisVec(_dragAxis));
                if (_snapEnabled) angle = SnapAngleValue(angle);
                var deltaRot = Quaternion.AngleAxis(angle, AxisVec(_dragAxis));

                if (_dragObjects.Count > 1)
                {
                    for (int i = 0; i < _dragObjects.Count; i++)
                    {
                        var obj = _dragObjects[i];
                        if (obj == null) continue;
                        obj.transform.rotation = deltaRot * _dragStartRotations[i];
                        var rel = _dragStartPositions[i] - _dragPivot;
                        obj.transform.position = _dragPivot + deltaRot * rel;
                    }
                }
                else if (selectedObject != null)
                {
                    selectedObject.transform.rotation = deltaRot * _dragStartRot;
                }
                return;
            }

            var ray   = cam.ScreenPointToRay(Input.mousePosition);
            var plane = new Plane(_dragPlaneNormal, _dragPivot);
            if (!plane.Raycast(ray, out float enter)) return;
            var delta = ray.GetPoint(enter) - _dragStartHit;

            if (_dragAxis == 3)
            {
                if (currentTool == ToolMode.Scale)
                {
                    float amount = Vector3.Dot(delta, cam.transform.up);
                    float factor = Mathf.Max(0.05f, 1f + amount * 0.5f);
                    for (int i = 0; i < _dragObjects.Count; i++)
                    {
                        var obj = _dragObjects[i];
                        if (obj == null) continue;
                        var sc = _dragStartScales[i] * factor;
                        if (_snapEnabled) sc = SnapVector(sc);
                        obj.transform.localScale = sc;
                    }
                }
                else
                {
                    ApplyTranslation(delta);
                }
                return;
            }

            if (_dragAxis >= 4)
            {
                if (currentTool == ToolMode.Scale)
                {
                    if (TryGetPlaneAxes(_dragAxis, out int aIdx, out int bIdx))
                    {
                        var localDir = (AxisVec(aIdx) + AxisVec(bIdx)).normalized;
                        var worldDir = selectedObject.transform.rotation * localDir;
                        var originV = cam.WorldToScreenPoint(_dragPivot);
                        var tipV = cam.WorldToScreenPoint(_dragPivot + worldDir);
                        var planeScrn = new Vector2(tipV.x - originV.x, tipV.y - originV.y);
                        if (tipV.z < 0f) planeScrn = -planeScrn;
                        if (planeScrn.sqrMagnitude < 0.01f) planeScrn = Vector2.up;
                        else planeScrn.Normalize();

                        float screenDelta = Vector2.Dot(mouse - _dragStartMouse, planeScrn);
                        for (int i = 0; i < _dragObjects.Count; i++)
                        {
                            var obj = _dragObjects[i];
                            if (obj == null) continue;
                            var sc = _dragStartScales[i];
                            sc[aIdx] = Mathf.Max(0.05f, _dragStartScales[i][aIdx] + screenDelta * 0.008f);
                            sc[bIdx] = Mathf.Max(0.05f, _dragStartScales[i][bIdx] + screenDelta * 0.008f);
                            if (_snapEnabled) sc = SnapVector(sc);
                            obj.transform.localScale = sc;
                        }
                    }
                }
                else
                {
                    ApplyTranslation(delta);
                }
                return;
            }

            if (currentTool == ToolMode.Scale)
            {
                // Arrow points along the object's local axis; account for object rotation
                // so the screen-space projection matches the visually rendered arrow.
                var arrowDir  = selectedObject.transform.rotation * GizmoRenderer.PivotRots[_dragAxis] * Vector3.up;
                var originV   = cam.WorldToScreenPoint(_dragPivot);
                var tipV      = cam.WorldToScreenPoint(_dragPivot + arrowDir);
                var arrowScrn = new Vector2(tipV.x - originV.x, tipV.y - originV.y);
                if (tipV.z < 0f) arrowScrn = -arrowScrn;
                if (arrowScrn.sqrMagnitude < 0.01f) arrowScrn = Vector2.up;
                else arrowScrn.Normalize();

                float screenDelta = Vector2.Dot(mouse - _dragStartMouse, arrowScrn);
                for (int i = 0; i < _dragObjects.Count; i++)
                {
                    var obj = _dragObjects[i];
                    if (obj == null) continue;
                    var sc = _dragStartScales[i];
                    sc[_dragAxis] = Mathf.Max(0.05f, _dragStartScales[i][_dragAxis] + screenDelta * 0.008f);
                    if (_snapEnabled) sc = SnapVector(sc);
                    obj.transform.localScale = sc;
                }
                return;
            }

            var ax = AxisVec(_dragAxis);
            ApplyTranslation(ax * Vector3.Dot(delta, ax));
        }

        static void DeleteSelected()
        {
            if (LevelEditorManager.Instance == null || _selection.Count == 0) return;
            for (int i = 0; i < _selection.Count; i++)
            {
                var obj = _selection[i];
                if (obj == null) continue;
                LevelEditorHistory.PushDelete(obj);
                LevelEditorManager.Instance.Remove(obj);
            }
            ClearSelection();
        }

        public static Vector3 AxisVec(int idx) =>
            idx == 0 ? Vector3.right : idx == 1 ? Vector3.up : Vector3.forward;

        static bool TryGetPlaneAxes(int axisIndex, out int axisA, out int axisB)
        {
            axisA = 0; axisB = 1;
            switch (axisIndex)
            {
                case 4: axisA = 0; axisB = 1; return true; // XY
                case 5: axisA = 1; axisB = 2; return true; // YZ
                case 6: axisA = 0; axisB = 2; return true; // XZ
                default: return false;
            }
        }

        static bool TryGetPlaneNormal(int axisIndex, out Vector3 normal)
        {
            normal = Vector3.forward;
            switch (axisIndex)
            {
                case 4: normal = Vector3.forward; return true; // XY
                case 5: normal = Vector3.right;   return true; // YZ
                case 6: normal = Vector3.up;      return true; // XZ
                default: return false;
            }
        }

        static void CopySelected()
        {
            if (_selection.Count == 0) return;
            _copyEntries.Clear();
            _copyPivot = GizmoRenderer.GetSelectionBoundsCenter(_selection);

            for (int i = 0; i < _selection.Count; i++)
            {
                var obj = _selection[i];
                if (obj == null) continue;

                var entry = new CopyEntry
                {
                    addressableKey = obj.addressableKey,
                    offset = obj.transform.position - _copyPivot,
                    scale = obj.transform.localScale,
                    rotation = obj.transform.rotation,
                    isAddressable = !string.IsNullOrEmpty(obj.addressableKey)
                };

                if (!entry.isAddressable)
                {
                    if (!Enum.TryParse(obj.objectType, out entry.primType))
                        entry.primType = PrimitiveType.Cube;
                }

                _copyEntries.Add(entry);
            }

            _copyHasValue = _copyEntries.Count > 0;
        }

        static void PasteCopy()
        {
            if (!_copyHasValue) return;
            EnsureManager();
            var pivot = _selection.Count > 0
                ? GizmoRenderer.GetSelectionBoundsCenter(_selection)
                : _copyPivot;

            var snappedOffset = SnapVector(PasteOffset);
            var newSelection = new List<LevelEditorObject>();
            for (int i = 0; i < _copyEntries.Count; i++)
            {
                var entry = _copyEntries[i];
                var targetPos = pivot + snappedOffset + entry.offset;

                LevelEditorObject obj = null;
                if (entry.isAddressable)
                {
                    var info = PropLibrary.FindById(entry.addressableKey);
                    if (info != null)
                        obj = LevelEditorManager.Instance.SpawnFromPropInfo(info, targetPos);
                }
                else
                {
                    obj = LevelEditorManager.Instance.SpawnPrimitive(entry.primType, targetPos);
                }

                if (obj == null) continue;
                obj.transform.localScale = entry.scale;
                obj.transform.rotation = entry.rotation;
                newSelection.Add(obj);
                LevelEditorHistory.PushSpawn(obj);
            }

            if (newSelection.Count > 0)
            {
                _selection.Clear();
                _selection.AddRange(newSelection);
                selectedObject = _selection[_selection.Count - 1];
            }
        }

        static void ApplyTranslation(Vector3 delta)
        {
            for (int i = 0; i < _dragObjects.Count; i++)
            {
                var obj = _dragObjects[i];
                if (obj == null) continue;
                var pos = _dragStartPositions[i] + delta;
                if (_snapEnabled) pos = SnapVector(pos);
                obj.transform.position = pos;
            }
        }

        static Vector3 SnapVector(Vector3 v)
        {
            return new Vector3(
                Mathf.Round(v.x / SnapStep) * SnapStep,
                Mathf.Round(v.y / SnapStep) * SnapStep,
                Mathf.Round(v.z / SnapStep) * SnapStep);
        }

        static float SnapAngleValue(float angle)
        {
            return Mathf.Round(angle / (SnapStep * RotateSnapMultiplier)) * (SnapStep * RotateSnapMultiplier);
        }
    }
}
