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

        static bool       _isDragging;
        static int        _dragAxis;
        static Vector3    _dragStartPos, _dragStartScale, _dragStartHit, _dragPlaneNormal;
        static Quaternion _dragStartRot;
        static Vector2    _dragStartMouse, _dragRotTangent;
        static int        _hoveredAxis = -1;

        public static void EnsureManager()
        {
            if (LevelEditorManager.Instance != null) return;
            new GameObject("LevelEditorManager").AddComponent<LevelEditorManager>();
        }

        public static void HideGizmo() => GizmoRenderer.SetActive(false);

        public static void Select(LevelEditorObject obj)
        {
            selectedObject = obj;
            if (!GizmoRenderer.IsReady) GizmoRenderer.Init();
        }

        public static void Update()
        {
            if (Input.GetKeyDown(KeyCode.Space) && selectedObject != null)
                currentTool = currentTool == ToolMode.Translate ? ToolMode.Scale
                            : currentTool == ToolMode.Scale     ? ToolMode.Rotate
                            : ToolMode.Translate;

            PropPalette.HandleScrollInput();

            var main = Camera.main;
            if (main != null) GizmoRenderer.Sync(selectedObject, currentTool, main);
            UpdateHover();
            GizmoRenderer.Draw(_hoveredAxis, currentTool);
            GizmoRenderer.DrawOutline(selectedObject);

            if (Input.GetMouseButton(1)) return;

            if (Input.GetKeyDown(KeyCode.Delete) && selectedObject != null)
                DeleteSelected();

            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (ctrl && Input.GetKeyDown(KeyCode.Z)) LevelEditorHistory.Undo();
            if (ctrl && Input.GetKeyDown(KeyCode.Y)) LevelEditorHistory.Redo();

            if (PropPalette.IsDragging)
            {
                if (!Input.GetMouseButton(0))
                {
                    TryDropProp();
                    PropPalette.CancelDrag();
                }
                return;
            }

            if (Input.GetMouseButtonDown(0)) TryBeginDrag();

            if (_isDragging)
            {
                if (Input.GetMouseButton(0))
                {
                    ContinueDrag();
                }
                else
                {
                    _isDragging = false;
                    if (selectedObject != null)
                        LevelEditorHistory.PushTransform(selectedObject, _dragStartPos, _dragStartScale, _dragStartRot);
                }
            }
        }

        public static void OnGUI()
        {
            PropPalette.DrawGUI(Event.current);

            string tool = currentTool == ToolMode.Translate ? "MOVE"
                        : currentTool == ToolMode.Scale     ? "SCALE" : "ROTATE";
            string msg = selectedObject != null
                ? $"LEVEL EDITOR  [{tool}]  |  Drag arrows / sphere  |  Space = switch tool  |  RMB = orbit"
                : "LEVEL EDITOR  |  Drag a prop from the palette onto the terrain  |  RMB = orbit";

            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.Box(new Rect(10, Screen.height - 28, 600, 22), "");
            GUI.color = Color.white;
            GUI.Label(new Rect(14, Screen.height - 27, 596, 20), msg);
            GUI.color = Color.white;
        }

        static void UpdateHover()
        {
            if (!GizmoRenderer.IsReady || selectedObject == null || Input.GetMouseButton(1))
            {
                _hoveredAxis = -1;
                return;
            }
            if (_isDragging) { _hoveredAxis = _dragAxis; return; }

            var handle = GizmoRenderer.RaycastHandle(Camera.main.ScreenPointToRay(Input.mousePosition));
            _hoveredAxis = handle != null ? handle.axisIndex : -1;
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
                    if (leo != null) { Select(leo); return; }
                }
                selectedObject = null;
                return;
            }

            _isDragging     = true;
            _dragAxis       = chosen.axisIndex;
            _dragStartPos   = selectedObject.transform.position;
            _dragStartScale = selectedObject.transform.localScale;
            _dragStartRot   = selectedObject.transform.rotation;
            _dragStartMouse = new Vector2(Input.mousePosition.x, Input.mousePosition.y);

            var cam = Camera.main;

            if (currentTool == ToolMode.Rotate)
            {
                _dragPlaneNormal = AxisVec(_dragAxis);
                var plane = new Plane(_dragPlaneNormal, _dragStartPos);
                if (plane.Raycast(ray, out float enter))
                    _dragStartHit = ray.GetPoint(enter);

                var toHit = (_dragStartHit - _dragStartPos).normalized;
                if (toHit.sqrMagnitude < 0.001f) toHit = Vector3.up;
                var tangent = Vector3.Cross(AxisVec(_dragAxis), toHit).normalized;

                var tBase = cam.WorldToScreenPoint(_dragStartPos);
                var tTip  = cam.WorldToScreenPoint(_dragStartPos + tangent);
                _dragRotTangent = new Vector2(tTip.x - tBase.x, tTip.y - tBase.y);
                if (tTip.z < 0f) _dragRotTangent = -_dragRotTangent;
                if (_dragRotTangent.sqrMagnitude < 0.01f) _dragRotTangent = Vector2.right;
                else _dragRotTangent.Normalize();
                return;
            }

            if (_dragAxis == 3)
            {
                _dragPlaneNormal = cam.transform.forward;
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

            var pl = new Plane(_dragPlaneNormal, _dragStartPos);
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
                float screenDelta = Vector2.Dot(mouse - _dragStartMouse, _dragRotTangent);
                selectedObject.transform.rotation =
                    Quaternion.AngleAxis(screenDelta * 0.5f, AxisVec(_dragAxis)) * _dragStartRot;
                return;
            }

            var ray   = cam.ScreenPointToRay(Input.mousePosition);
            var plane = new Plane(_dragPlaneNormal, _dragStartPos);
            if (!plane.Raycast(ray, out float enter)) return;
            var delta = ray.GetPoint(enter) - _dragStartHit;

            if (_dragAxis == 3)
            {
                if (currentTool == ToolMode.Scale)
                {
                    float amount = Vector3.Dot(delta, cam.transform.up);
                    float factor = Mathf.Max(0.05f, 1f + amount * 0.5f);
                    selectedObject.transform.localScale = _dragStartScale * factor;
                }
                else
                {
                    selectedObject.transform.position = _dragStartPos + delta;
                }
                return;
            }

            if (currentTool == ToolMode.Scale)
            {
                // Arrow points along the object's local axis; account for object rotation
                // so the screen-space projection matches the visually rendered arrow.
                var arrowDir  = selectedObject.transform.rotation * GizmoRenderer.PivotRots[_dragAxis] * Vector3.up;
                var originV   = cam.WorldToScreenPoint(_dragStartPos);
                var tipV      = cam.WorldToScreenPoint(_dragStartPos + arrowDir);
                var arrowScrn = new Vector2(tipV.x - originV.x, tipV.y - originV.y);
                if (tipV.z < 0f) arrowScrn = -arrowScrn;
                if (arrowScrn.sqrMagnitude < 0.01f) arrowScrn = Vector2.up;
                else arrowScrn.Normalize();

                float screenDelta = Vector2.Dot(mouse - _dragStartMouse, arrowScrn);
                var sc = _dragStartScale;
                sc[_dragAxis] = Mathf.Max(0.05f, _dragStartScale[_dragAxis] + screenDelta * 0.008f);
                selectedObject.transform.localScale = sc;
                return;
            }

            var ax = AxisVec(_dragAxis);
            selectedObject.transform.position = _dragStartPos + ax * Vector3.Dot(delta, ax);
        }

        static void DeleteSelected()
        {
            if (selectedObject == null || LevelEditorManager.Instance == null) return;
            LevelEditorHistory.PushDelete(selectedObject);
            LevelEditorManager.Instance.Remove(selectedObject);
            selectedObject = null;
        }

        public static Vector3 AxisVec(int idx) =>
            idx == 0 ? Vector3.right : idx == 1 ? Vector3.up : Vector3.forward;
    }
}
