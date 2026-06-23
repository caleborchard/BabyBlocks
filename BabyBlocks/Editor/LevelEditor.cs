using System;
using System.Collections.Generic;
using System.Linq;
using BabyBlocks.UI;
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
        public static bool              LocalMode      = true;   // true = local axes; false = world axes (G key)
        public static IReadOnlyList<LevelEditorObject> SelectedObjects => _selection;

        // True while actively dragging the translation gizmo's center sphere with shift held
        // (surface-snap mode). Used to suppress the selection outline and block the R
        // edit-mode-toggle shortcut, which is repurposed for cycling spawn orientation here.
        public static bool IsSurfaceSnapDragging =>
            _isDragging && currentTool == ToolMode.Translate && _dragAxis == 3
            && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));

        static readonly List<LevelEditorObject> _selection = new();
        static bool       _isDragging;
        static int        _dragAxis;
        static Vector3    _dragStartPos, _dragStartScale, _dragStartHit, _dragPlaneNormal, _dragPivot;
        static Quaternion _dragStartRot;
        static Vector2    _dragStartMouse;
        static Vector2    _rawMouseAccum;      // accumulated raw mouse delta (screen-px units); not clamped at edges
        static float      _dragGizmoScale;     // gizmo world-space size at drag start; used by scale tool
        static Quaternion _accumulatedFreeRot; // accumulated rotation for free-rotate (velocity-based)
        static int        _hoveredAxis = -1;
        static bool       _pivotLocked;

        public static bool IsTypingInUI => PropMetadataPanel.IsTypingInUI || ObjImportWindow.IsTypingInUI || PhysicsWindow.IsTypingInUI || PropBrowserUI.IsTypingInUI;
        public static bool IsDragging   => _isDragging;

        static readonly List<LevelEditorObject> _dragObjects = new();
        static readonly List<Vector3> _dragStartPositions = new();
        static readonly List<Vector3> _dragStartScales = new();
        static readonly List<Quaternion> _dragStartRotations = new();

        // Throttling for SendPropTransform broadcasts during an active drag, mirroring
        // the freecam update cadence (Unreliable at high frequency, periodic
        // ReliableOrdered keyframe so a mid-drag late-joiner still converges).
        const float NetTransformSendIntervalSeconds = 0.15f;
        const float NetTransformReliableIntervalSeconds = 1.5f;
        static float _nextNetTransformSendTime;
        static float _nextNetTransformReliableTime;

        // netIds last broadcast via SendPropSelected, so selection changes are only sent
        // when the set of selected networked props actually changes.
        static List<ulong> _lastBroadcastSelectedNetIds = new();

        // Throttling for SendPropGhostUpdate broadcasts while dragging a prop out of the
        // palette (before it's dropped), mirroring the drag-transform cadence above.
        const float GhostSendIntervalSeconds = 0.15f;
        const float GhostReliableIntervalSeconds = 1.5f;
        static float _nextGhostSendTime;
        static float _nextGhostReliableTime;

        struct CopyEntry
        {
            public string addressableKey;
            public PrimitiveType primType;
            public Vector3 offset;
            public Vector3 scale;
            public Quaternion rotation;
            public bool isAddressable;
            public int materialConstructionId; // -1 if none
            public PhysicsMode physicsMode;
            public bool sunglassesNeeded;
            public bool playerPassthrough;
        }

        static readonly List<CopyEntry> _copyEntries = new();
        static Vector3 _copyPivot;
        static Vector3 _copyBoundsSize = Vector3.one;
        static bool _copyHasValue;

        static bool _snapEnabled = false;
        public static bool SnapEnabled { get => _snapEnabled; set => _snapEnabled = value; }
        const float SnapStep = 0.1f;
        const float RotateSnapMultiplier = 100f;

        static float _lastUnloadCheck;
        const float UnloadCheckInterval = 15f;

        // Ghost preview shown while dragging a prop from the palette.
        static GameObject _propGhost;
        static PropInfo   _ghostProp;

        // The 6 ways to reorient the prop so a different local face points "up" (i.e. gets
        // aligned to the surface normal by ComputeSpawnRotation): +Y, -Y, the four sides.
        static readonly Quaternion[] FaceUpRotations =
        {
            Quaternion.identity,            // local +Y up   (upright)
            Quaternion.Euler(180f, 0f, 0f), // local -Y up   (upside-down)
            Quaternion.Euler(90f,  0f, 0f), // local -Z up   (lying on its back)
            Quaternion.Euler(-90f, 0f, 0f), // local +Z up   (lying on its front)
            Quaternion.Euler(0f, 0f, -90f), // local +X up   (lying on its right side)
            Quaternion.Euler(0f, 0f,  90f), // local -X up   (lying on its left side)
        };

        // R cycles through all 24 cube orientations: 6 faces-up × 4 spins around that
        // up axis. The face changes every press (cycling fastest) so each of the first
        // six presses already shows the prop resting on a different side — varied, not
        // just spinning in place — and the full spin range is reachable by continuing.
        static readonly Quaternion[] DragOrientations = BuildDragOrientations();

        static Quaternion[] BuildDragOrientations()
        {
            var result = new Quaternion[FaceUpRotations.Length * 4];
            for (int spin = 0; spin < 4; spin++)
                for (int face = 0; face < FaceUpRotations.Length; face++)
                    result[spin * FaceUpRotations.Length + face] =
                        FaceUpRotations[face] * Quaternion.Euler(0f, spin * 90f, 0f);
            return result;
        }
        static int _dragStep;

        // Unity's Input.GetMouseButton(0) can spuriously read false for a single
        // frame (e.g. after a brief hitch resets input state), which would otherwise
        // be misread as the user releasing the mouse and drop the dragged prop
        // instantly. Require a few consecutive "not held" frames before treating
        // it as a real release.
        const int DragReleaseDebounceFrames = 3;
        static int _propDragReleaseFrames;
        static int _matDragReleaseFrames;

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
                AddSelectionWithGroup(obj);
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
            var groupMembers = GetLogicalGroupSelection(obj);
            bool anySelected = groupMembers.Any(m => m != null && _selection.Contains(m));

            if (!anySelected)
            {
                foreach (var m in groupMembers)
                    if (m != null && !_selection.Contains(m)) _selection.Add(m);
                selectedObject = obj;
            }
            else
            {
                foreach (var m in groupMembers)
                    _selection.Remove(m);
                if (selectedObject == obj)
                    selectedObject = _selection.Count > 0 ? _selection[_selection.Count - 1] : null;
            }
            if (_selection.Count > 0 && !GizmoRenderer.IsReady) GizmoRenderer.Init();
        }

        static void AddSelectionWithGroup(LevelEditorObject obj)
        {
            foreach (var m in GetLogicalGroupSelection(obj))
                if (m != null && !_selection.Contains(m)) _selection.Add(m);
        }

        static IEnumerable<LevelEditorObject> GetLogicalGroupSelection(LevelEditorObject obj)
        {
            if (obj == null) yield break;
            var mgr = LevelEditorManager.Instance;
            if (mgr != null && obj.groupId > 0)
            {
                foreach (var m in mgr.GetLogicalGroupMembers(obj.groupId))
                    yield return m;
                yield break;
            }
            yield return obj;
        }

        static void ClearSelection()
        {
            _selection.Clear();
            selectedObject = null;
        }

        // Called after the entire level is cleared (locally via the Clear button, or
        // remotely via a peer's clear broadcast) since every LevelEditorObject reference
        // becomes stale at once - resets selection/drag/gizmo state accordingly.
        public static void ClearAllSelectionState()
        {
            _selection.Clear();
            selectedObject = null;
            _dragObjects.Clear();
            _dragStartPositions.Clear();
            _dragStartScales.Clear();
            _dragStartRotations.Clear();
            _isDragging = false;
            if (_pivotLocked)
            {
                GizmoRenderer.ClearPivotOverride();
                _pivotLocked = false;
            }
            HideGizmo();
        }

        // Called when a peer deletes a networked prop, so our copy is dropped from
        // selection/drag state before LevelEditorManager destroys it - otherwise we'd be
        // left holding a reference to a destroyed object.
        public static void RemoveDeletedObject(LevelEditorObject obj)
        {
            if (obj == null) return;
            _selection.Remove(obj);
            if (selectedObject == obj)
                selectedObject = _selection.Count > 0 ? _selection[_selection.Count - 1] : null;
            if (_selection.Count == 0) HideGizmo();

            int dragIdx = _dragObjects.IndexOf(obj);
            if (dragIdx >= 0)
            {
                _dragObjects.RemoveAt(dragIdx);
                _dragStartPositions.RemoveAt(dragIdx);
                _dragStartScales.RemoveAt(dragIdx);
                _dragStartRotations.RemoveAt(dragIdx);
            }
        }

        // Called once the post-save-load scene load burst settles (see
        // FlyCamController.OnUpdate). A native "load a different save" destroys every
        // LevelEditorObject in the old scene, but doesn't clear _selection/selectedObject
        // - leaving stale destroyed references behind. With those still in _selection,
        // Sync() sees selection.Count > 0 but every entry is "null" (Unity's destroyed-object
        // check), so GetSelectionBoundsCenter returns Vector3.zero (world origin) and
        // DrawOutline finds no live meshes to outline - the gizmo jumps to the origin and
        // the selection highlight disappears, both seemingly "broken" until the user
        // reselects something.
        internal static void PruneSelection()
        {
            for (int i = _selection.Count - 1; i >= 0; i--)
                if (_selection[i] == null) _selection.RemoveAt(i);

            if (selectedObject == null)
                selectedObject = _selection.Count > 0 ? _selection[_selection.Count - 1] : null;

            if (_selection.Count == 0) HideGizmo();
        }

        public static void Update()
        {
            LevelEditorManager.Instance?.EnsurePropsContainer();

            float now = Time.realtimeSinceStartup;
            if (now - _lastUnloadCheck >= UnloadCheckInterval)
            {
                _lastUnloadCheck = now;
                PropLibrary.ProcessUnloadQueue();
            }

            bool blockShortcuts = IsTypingInUI || Core.IsKeyboardCaptured;
            bool overUI = IsPointerOverUI();

            if (!blockShortcuts && Input.GetKeyDown(KeyCode.Space) && selectedObject != null)
                currentTool = currentTool == ToolMode.Translate ? ToolMode.Scale
                            : currentTool == ToolMode.Scale     ? ToolMode.Rotate
                            : ToolMode.Translate;

            if (!blockShortcuts && Input.GetKeyDown(KeyCode.T))
                LocalMode = !LocalMode;

            bool ctrlDown = !blockShortcuts
                && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl));

            if (!blockShortcuts && !ctrlDown && Input.GetKeyDown(KeyCode.Y))
                _snapEnabled = !_snapEnabled;

            if (!blockShortcuts)
            {
                // PropPalette.HandleScrollInput(); // legacy ImGui palette — see UI/UniverseLibDemo.cs
                MaterialConstructionPanel.HandleScrollInput();
            }

            // WARNING: EnsureCamera must be called here (once per frame, before Sync),
            // NOT inside Sync() itself. Past attempts to rebuild/reconfigure the overlay
            // camera inside Sync caused broken world streaming and player flings on long
            // teleports. See GizmoRenderer.EnsureCamera for the full explanation.
            GizmoRenderer.EnsureCamera();
            var main = Camera.main;
            if (main != null) GizmoRenderer.Sync(_selection, selectedObject, currentTool, main);
            UpdateHover(overUI);
            // Hide the gizmo while surface-snapping so it doesn't visually fight the
            // object as it jumps to follow the cursor across surfaces.
            if (!IsSurfaceSnapDragging)
                GizmoRenderer.Draw(_hoveredAxis, currentTool);
            else
                HideGizmo();
            if (_isDragging && currentTool == ToolMode.Translate
                && _dragObjects.Count > 0 && _dragObjects[0] != null)
                GizmoRenderer.SetTranslateDragDelta(_dragObjects[0].transform.position - _dragStartPositions[0]);
            else
                GizmoRenderer.ClearDragDelta();
            // Passing null/empty makes DrawOutline detach its command buffer and skip redrawing,
            // rather than leaving a stale outline rendered at the pre-drag position.
            GizmoRenderer.DrawOutline(IsSurfaceSnapDragging ? null : _selection, main);

            // Broadcast selection changes for the remote highlight feature. Only networked
            // props (netId != 0) are reported; an empty list tells peers to clear our
            // highlight entirely.
            var selectedNetIds = new List<ulong>();
            for (int i = 0; i < _selection.Count; i++)
            {
                var sel = _selection[i];
                if (sel != null && sel.netId != 0) selectedNetIds.Add(sel.netId);
            }
            if (!selectedNetIds.SequenceEqual(_lastBroadcastSelectedNetIds))
            {
                BabyBlocks.Networking.ModNetworking.SendPropSelected(selectedNetIds);
                _lastBroadcastSelectedNetIds = selectedNetIds;
            }

            if (Input.GetMouseButton(1)) return;

            if (!blockShortcuts && Input.GetKeyDown(KeyCode.Delete) && selectedObject != null)
                DeleteSelected();

            bool ctrl = ctrlDown;
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (ctrl && Input.GetKeyDown(KeyCode.Z)) LevelEditorHistory.Undo();
            if (ctrl && Input.GetKeyDown(KeyCode.Y)) LevelEditorHistory.Redo();
            if (ctrl && Input.GetKeyDown(KeyCode.C)) CopySelected();
            if (ctrl && shift && Input.GetKeyDown(KeyCode.V)) PasteCopy(pasteInPlace: true);
            else if (ctrl && !shift && Input.GetKeyDown(KeyCode.V)) PasteCopy(pasteInPlace: false);

            if (PropPalette.IsDragging)
            {
                bool mouseHeld = Input.GetMouseButton(0);
                if (mouseHeld || PropPalette.JustStartedDrag)
                {
                    _propDragReleaseFrames = 0;
                    if (Input.GetKeyDown(KeyCode.R)) _dragStep = (_dragStep + 1) % DragOrientations.Length;
                    UpdatePropGhostForFrame();
                }
                else if (++_propDragReleaseFrames >= DragReleaseDebounceFrames)
                {
                    bool placed = !overUI && TryDropProp();
                    if (!placed)
                        BabyBlocks.Networking.ModNetworking.SendPropGhostEnd();
                    DestroyPropGhost();
                    PropPalette.CancelDrag();
                    _propDragReleaseFrames = 0;
                }
                else
                {
                    UpdatePropGhostForFrame();
                }
                return;
            }

            if (MaterialConstructionPanel.IsDragging)
            {
                if (Input.GetMouseButton(0) || MaterialConstructionPanel.JustStartedDrag)
                {
                    _matDragReleaseFrames = 0;
                }
                else if (++_matDragReleaseFrames >= DragReleaseDebounceFrames)
                {
                    if (!overUI) MaterialConstructionPanel.TryApplyToHoveredProp();
                    MaterialConstructionPanel.CancelDrag();
                    _matDragReleaseFrames = 0;
                }
                return;
            }

            if (!overUI && Input.GetMouseButtonDown(0)) TryBeginDrag();

            if (_isDragging)
            {
                if (Input.GetMouseButton(0))
                {
                    if (IsSurfaceSnapDragging && Input.GetKeyDown(KeyCode.R))
                        _dragStep = (_dragStep + 1) % DragOrientations.Length;
                    ContinueDrag();
                    BroadcastDragTransforms();
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
                            if (obj.netId != 0)
                                BabyBlocks.Networking.ModNetworking.SendPropTransform(
                                    obj.netId, obj.transform.position, obj.transform.rotation, obj.transform.localScale, reliable: true);
                        }
                        if (LevelEditorManager.Instance != null)
                            LevelEditorManager.Instance.SyncLoopBases(_dragObjects);
                    }
                }
            }
        }

        public static void OnGUI()
        {
            if (Core.DebugMode) PropPalette.DrawGUI(Event.current);
            PropMetadataPanel.DrawGUI(selectedObject);
            SaveLoadWindow.DrawGUI(Event.current);
            PhysicsWindow.DrawGUI(Event.current);
            ObjImportWindow.DrawGUI(Event.current);
            MaterialInspectorPanel.DrawGUI();

            string tool  = currentTool == ToolMode.Translate ? "MOVE"
                         : currentTool == ToolMode.Scale     ? "SCALE" : "ROTATE";
            string space = LocalMode ? "LOCAL" : "GLOBAL";
            string snapTag = _snapEnabled ? " [SNAP]" : "";
            string msg   = selectedObject != null
                ? $"LEVEL EDITOR  [{tool}] [{space}]{snapTag}  |  Space=cycle tool  T=local/global  Y=snap  Del=delete  |  R=teleport mode  `=exit to player  |  LMB=teleport  RMB=orbit"
                : $"LEVEL EDITOR  |  Drag a prop from the palette onto the terrain  |  R=edit mode  `=exit to player  |  LMB=teleport  RMB=orbit";

            if (Core.DebugMode)
            {
                var allProps = PropLibrary.FilteredProps;
                int totalProps   = allProps.Count;
                int checkedProps = 0;
                for (int i = 0; i < totalProps; i++)
                    if (PropMetadataStore.HasMetadata(allProps[i].id)) checkedProps++;
                float pct = totalProps > 0 ? checkedProps * 100f / totalProps : 0f;
                msg += $"  |  {checkedProps}/{totalProps}  ({pct:F1}%)";
            }

            float barW = Screen.width - 20f;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.Box(new Rect(10, Screen.height - 28, barW, 22), "");
            GUI.color = Color.white;
            GUI.Label(new Rect(14, Screen.height - 27, barW - 8f, 20), msg);
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

        // Called when the local player exits cursor/editor mode so peers don't keep
        // showing a highlight around whatever prop was selected when we left, since the
        // selection-broadcast loop above stops running while the editor is closed.
        public static void ClearRemoteSelectionBroadcast()
        {
            if (_lastBroadcastSelectedNetIds.Count == 0) return;
            BabyBlocks.Networking.ModNetworking.SendPropSelected(new List<ulong>());
            _lastBroadcastSelectedNetIds = new List<ulong>();
        }

        static bool IsPointerOverUI()
        {
            if (PropBrowserUI.IsPointerOverPanel()) return true;
            var mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            if (PropPalette.PanelRect.Contains(mouse)) return true;
            if (PropMetadataPanel.ContainsPoint(mouse)) return true;
            if (SaveLoadWindow.ContainsPoint(mouse)) return true;
            if (PhysicsWindow.ContainsPoint(mouse)) return true;
            if (ObjImportWindow.ContainsPoint(mouse)) return true;
            return false;
        }

        // Returns true if the prop was successfully spawned and placed, false on any
        // early-return path (no raycast hit, no dragged prop, spawn failure) - callers
        // use this to know whether to send a SendPropGhostEnd cancellation.
        static bool TryDropProp()
        {
            var ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            bool didHit = Physics.Raycast(ray, out var hit, 2000f, LayerCache.PropTerrainMask);
            if (!didHit) return false;

            var prop = PropPalette.DraggingProp;
            if (prop == null) return false;

            EnsureManager();
            PropLibrary.LoadPropData(prop);

            var rot = ComputeDragRotation(hit.normal) * DragOrientations[_dragStep];
            var spawnPos = ComputeSpawnPosition(prop, hit, rot);
            var obj = LevelEditorManager.Instance.SpawnFromPropInfo(prop, spawnPos);
            if (obj == null) return false;
            obj.transform.rotation = rot;
            Select(obj);
            LevelEditorHistory.PushSpawn(obj);

            ulong netId = BabyBlocks.Networking.ModNetworking.RegisterNetworkedObject(obj);
            BabyBlocks.Networking.ModNetworking.SendPropPlaced(
                netId, prop.id, obj.transform.position, obj.transform.rotation, obj.transform.localScale);
            return true;
        }

        // While dragging a prop from the palette, holding shift suppresses surface-rotate
        // snapping so the prop spawns upright; releasing shift restores normal surface snap.
        static Quaternion ComputeDragRotation(Vector3 normal)
        {
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            return shift ? Quaternion.identity : ComputeSpawnRotation(normal);
        }

        // Returns the rotation that aligns the prop's local +Y with the hit surface normal.
        static Quaternion ComputeSpawnRotation(Vector3 normal)
        {
            if (Vector3.Dot(normal, Vector3.up) < -0.999f)
                return Quaternion.Euler(180f, 0f, 0f);  // ceiling: unambiguous 180° flip
            return Quaternion.FromToRotation(Vector3.up, normal);
        }

        // Places the prop so its nearest face (in the surface-normal direction) sits on the hit point,
        // accounting for the prop's spawn rotation so the offset is correct for rotated placements.
        // scale lets callers account for an object whose localScale differs from the prop's default
        // (1,1,1) bounds — e.g. surface-snapping an already-thinned object — so the offset reflects
        // its current size rather than floating/sinking relative to the surface.
        static Vector3 ComputeSpawnPosition(PropInfo prop, RaycastHit hit, Quaternion rotation, Vector3? scale = null)
        {
            var bounds = PropLibrary.GetPropBounds(prop);
            if (!bounds.HasValue)
                return hit.point + hit.normal * 0.5f;

            Vector3 s = scale ?? Vector3.one;
            Vector3 n = hit.normal;
            // Rotate the local-space center into world space.
            Vector3 rotCenter = rotation * Vector3.Scale(bounds.Value.center, s);
            // Support function for a rotated AABB: project each rotated half-axis onto n.
            Vector3 e = bounds.Value.extents;
            float extent = Mathf.Abs(Vector3.Dot(rotation * new Vector3(e.x * s.x, 0f,  0f),  n))
                         + Mathf.Abs(Vector3.Dot(rotation * new Vector3(0f,  e.y * s.y, 0f),  n))
                         + Mathf.Abs(Vector3.Dot(rotation * new Vector3(0f,  0f,  e.z * s.z), n));
            return hit.point - rotCenter + n * extent;
        }

        static void UpdatePropGhostForFrame()
        {
            var prop = PropPalette.DraggingProp;
            if (prop == null) { DestroyPropGhost(); return; }

            if (_propGhost == null || _ghostProp != prop)
            {
                DestroyPropGhost();
                // Intentionally not resetting _dragStep here: the chosen R orientation
                // persists across props dragged from the library (gizmo drags still reset it).
                PropLibrary.LoadPropData(prop);
                if (prop.HasMesh) { _propGhost = CreateGhostObject(prop); _ghostProp = prop; }
                _nextGhostSendTime = 0f;
                _nextGhostReliableTime = 0f;
            }

            if (_propGhost == null) return;

            // Raycast even while the cursor is over the palette so the ghost is
            // visible (at the last hit point) the instant the drag starts, rather
            // than staying hidden until the cursor first leaves the UI.
            var ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            bool didHit = Physics.Raycast(ray, out var hit, 2000f, LayerCache.PropTerrainMask);
            if (didHit)
            {
                var rot = ComputeDragRotation(hit.normal) * DragOrientations[_dragStep];
                _propGhost.SetActive(true);
                _propGhost.transform.SetPositionAndRotation(ComputeSpawnPosition(_ghostProp, hit, rot), rot);

                float now = Time.unscaledTime;
                if (now >= _nextGhostSendTime)
                {
                    bool reliable = now >= _nextGhostReliableTime;
                    BabyBlocks.Networking.ModNetworking.SendPropGhostUpdate(
                        _ghostProp.id, _propGhost.transform.position, _propGhost.transform.rotation,
                        _propGhost.transform.localScale, reliable);
                    _nextGhostSendTime = now + GhostSendIntervalSeconds;
                    if (reliable) _nextGhostReliableTime = now + GhostReliableIntervalSeconds;
                }
            }
            else
            {
                _propGhost.SetActive(false);
            }
        }

        static void DestroyPropGhost()
        {
            if (_propGhost != null)
            {
                UnityEngine.Object.Destroy(_propGhost);
                _propGhost = null;
            }
            _ghostProp = null;
        }

        // Mirrors the PropLayer constant in LevelEditorManager so the ghost is
        // rendered by the same camera passes as real props.
        const int GhostLayer = 16;

        // internal (not static-private) so RemotePropGhostManager can build matching
        // ghost previews for other players' in-progress placements.
        internal static GameObject CreateGhostObject(PropInfo prop)
        {
            var root = new GameObject("__PropGhost__");
            root.layer = GhostLayer;

            for (int i = 0; i < prop.parts.Count; i++)
            {
                var part = prop.parts[i];
                if (part?.mesh == null) continue;
                // Name matches the LEO convention (Part_0, Part_1…) so that
                // ApplyDisabledRenderersToRoot can find children by path.
                var child = new GameObject($"Part_{i}");
                child.layer = GhostLayer;
                child.transform.SetParent(root.transform, false);
                child.transform.localPosition = part.localPosition;
                child.transform.localRotation = part.localRotation;
                child.transform.localScale    = part.localScale;
                child.AddComponent<MeshFilter>().mesh = part.mesh;
                var mr = child.AddComponent<MeshRenderer>();
                if (part.materials != null) mr.sharedMaterials = part.materials;
            }

            // Honour metadata overrides so the ghost matches what will be placed.
            MaterialCatalog.ApplyMaterialOverridesToRoot(prop.id, root);
            PropInstanceServices.ApplyDisabledRenderersToRoot(prop.id, root);

            // The hole prop's "mesh" is just a placeholder cylinder (see
            // LoadNegativeCollisionProp) — hide it and show the same wireframe
            // box that GhostCubeConfig.Configure builds once the prop is placed.
            if (PropLibrary.IsNegativeCollisionProp(prop.id))
            {
                foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    renderer.forceRenderingOff = true;
                    renderer.enabled = false;
                }
                GhostCubeConfig.BuildFrame(root);
            }

            // The spawn point's "mesh" is just a placeholder capsule (see
            // LoadSpawnPointProp) — hide it and show the same wireframe marker
            // that SpawnPointConfig.Configure builds once the prop is placed.
            if (PropLibrary.IsSpawnPointProp(prop.id))
            {
                foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    renderer.forceRenderingOff = true;
                    renderer.enabled = false;
                }
                SpawnPointConfig.BuildMarker(root);
            }

            return root;
        }

        static void TryBeginDrag()
        {
            var ray    = Camera.main.ScreenPointToRay(Input.mousePosition);
            GizmoHandle chosen = selectedObject != null ? GizmoRenderer.RaycastHandle(ray) : null;

            if (chosen == null)
            {
                // WARNING: do NOT collapse this back into a single Physics.Raycast - this has
                // regressed multiple times. A plain Raycast (even with QueryTriggerInteraction.Collide)
                // stops at the first hit, which can be a game-world trigger volume (BBConvoStarter,
                // conversation zones, etc.) with no LevelEditorObject - making props behind it
                // (often rocks) unselectable. Bush props need Collide because SetBushPassthrough makes
                // all their colliders triggers. So gather every hit and take the nearest one that
                // actually has a LevelEditorObject in its hierarchy, skipping game-world triggers.
                LevelEditorObject foundLeo  = null;
                float              bestDist = float.MaxValue;
                foreach (var h in Physics.RaycastAll(ray, 2000f, ~GizmoRenderer.Mask, QueryTriggerInteraction.Collide))
                {
                    if (h.distance >= bestDist) continue;
                    var leo = h.collider.GetComponent<LevelEditorObject>()
                           ?? h.collider.GetComponentInParent<LevelEditorObject>();
                    if (leo == null) continue;
                    bestDist = h.distance;
                    foundLeo = leo;
                }
                if (foundLeo != null)
                {
                    bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                    if (shift) ToggleSelection(foundLeo);
                    else Select(foundLeo);
                    return;
                }
                if (!Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift))
                    ClearSelection();
                return;
            }

            _isDragging     = true;
            _dragAxis       = chosen.axisIndex;
            _dragStep       = 0;
            _dragStartPos   = selectedObject.transform.position;
            _dragStartScale = selectedObject.transform.localScale;
            _dragStartRot   = selectedObject.transform.rotation;
            _dragStartMouse = new Vector2(Input.mousePosition.x, Input.mousePosition.y);
            _rawMouseAccum  = Vector2.zero;
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
                if (_dragAxis == GizmoRenderer.FreeRotateAxis)
                {
                    // Velocity-based free rotate: reset accumulator; per-frame delta applied in ContinueDrag.
                    _accumulatedFreeRot = Quaternion.identity;
                    return;
                }

                _dragPlaneNormal = LocalMode && selectedObject != null
                    ? selectedObject.transform.rotation * AxisVec(_dragAxis)
                    : AxisVec(_dragAxis);
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
                _dragPlaneNormal = LocalMode
                    ? selectedObject.transform.rotation * planeNormal
                    : planeNormal;
            }
            else
            {
                // Single-axis drag: build a camera-facing plane that contains the drag axis.
                // In local mode the axis is object-local so the drag plane aligns with the visual arrow.
                var axis = LocalMode
                    ? selectedObject.transform.rotation * AxisVec(_dragAxis)
                    : AxisVec(_dragAxis);
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

            // Scale: capture the gizmo's world-space size so that CalcLineTranslation sensitivity
            // is proportional to the visual handle extent and thus naturally scales with camera distance.
            if (currentTool == ToolMode.Scale && selectedObject != null)
            {
                float camDist   = Vector3.Distance(cam.transform.position, _dragPivot);
                _dragGizmoScale = Mathf.Max(camDist * 0.14f, 0.02f);
            }
        }

        // Throttled per-frame broadcast of dragged networked props' transforms, mirroring
        // the fly-cam update cadence (mostly Unreliable, periodic ReliableOrdered keyframe).
        static void BroadcastDragTransforms()
        {
            if (_dragObjects.Count == 0) return;

            float now = Time.unscaledTime;
            if (now < _nextNetTransformSendTime) return;

            bool reliable = now >= _nextNetTransformReliableTime;
            for (int i = 0; i < _dragObjects.Count; i++)
            {
                var obj = _dragObjects[i];
                if (obj == null || obj.netId == 0) continue;
                BabyBlocks.Networking.ModNetworking.SendPropTransform(
                    obj.netId, obj.transform.position, obj.transform.rotation, obj.transform.localScale, reliable);
            }

            _nextNetTransformSendTime = now + NetTransformSendIntervalSeconds;
            if (reliable)
                _nextNetTransformReliableTime = now + NetTransformReliableIntervalSeconds;
        }

        static void ContinueDrag()
        {
            if (selectedObject == null) { _isDragging = false; return; }

            var cam   = Camera.main;
            var mouse = new Vector2(Input.mousePosition.x, Input.mousePosition.y);

            // Accumulate raw axis delta every frame so scale drags continue past screen edges.
            if (currentTool == ToolMode.Scale)
                _rawMouseAccum += new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * 10f;

            // FREE ROTATE (velocity-based; mirrors Unity's FreeRotate.cs)
            // Each frame: build a rotation axis perpendicular to the mouse-drag direction
            // in screen space and accumulate it.  Applied from drag-start so the total
            // rotation is always relative to the original orientation.
            if (currentTool == ToolMode.Rotate && _dragAxis == GizmoRenderer.FreeRotateAxis)
            {
                var rawDelta = new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * 10f;
                if (rawDelta.sqrMagnitude > 0.0001f)
                {
                    // Rotation axis: perpendicular to drag direction in screen space → world space.
                    // (drag right → rotate around cam.up; drag up → rotate around cam.right)
                    var   rotAxis  = cam.transform.TransformDirection(-rawDelta.y, rawDelta.x, 0f).normalized;
                    float angle    = rawDelta.magnitude * 0.3f;
                    _accumulatedFreeRot = Quaternion.AngleAxis(angle, rotAxis) * _accumulatedFreeRot;
                }

                for (int i = 0; i < _dragObjects.Count; i++)
                {
                    var obj = _dragObjects[i];
                    if (obj == null) continue;

                    Quaternion finalRot;
                    Quaternion deltaRot;
                    if (_snapEnabled)
                    {
                        // Snap the final world-space Euler angles so the sphere respects snap mode
                        // and normalizes any prior free-rotate angle to the nearest snap boundary.
                        var euler = (_accumulatedFreeRot * _dragStartRotations[i]).eulerAngles;
                        euler.x = SnapAngleValue(euler.x);
                        euler.y = SnapAngleValue(euler.y);
                        euler.z = SnapAngleValue(euler.z);
                        finalRot = Quaternion.Euler(euler);
                        deltaRot = finalRot * Quaternion.Inverse(_dragStartRotations[i]);
                    }
                    else
                    {
                        finalRot = _accumulatedFreeRot * _dragStartRotations[i];
                        deltaRot = _accumulatedFreeRot;
                    }

                    obj.transform.rotation = finalRot;
                    var rel = _dragStartPositions[i] - _dragPivot;
                    obj.transform.position = _dragPivot + deltaRot * rel;
                }
                return;
            }

            // RING ROTATE
            if (currentTool == ToolMode.Rotate)
            {
                var rotRay   = cam.ScreenPointToRay(Input.mousePosition);
                var rotPlane = new Plane(_dragPlaneNormal, _dragPivot);
                if (!rotPlane.Raycast(rotRay, out float rotEnter)) return;
                var hit = rotRay.GetPoint(rotEnter);

                var from = _dragStartHit - _dragPivot;
                var to   = hit - _dragPivot;
                if (from.sqrMagnitude < 0.001f || to.sqrMagnitude < 0.001f) return;

                float angle = Vector3.SignedAngle(from, to, _dragPlaneNormal);
                if (_snapEnabled) angle = SnapAngleValue(angle);
                var deltaRot = Quaternion.AngleAxis(angle, _dragPlaneNormal);

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

                    SyncDraggedPhysicsTransforms();
                }
                else if (selectedObject != null)
                {
                    selectedObject.transform.rotation = deltaRot * _dragStartRot;
                    var rel = _dragStartPos - _dragPivot;
                    selectedObject.transform.position = _dragPivot + deltaRot * rel;

                    SyncDraggedPhysicsTransforms();
                }
                return;
            }

            // TRANSLATE + SCALE: ray-plane intersection
            var ray   = cam.ScreenPointToRay(Input.mousePosition);
            var plane = new Plane(_dragPlaneNormal, _dragPivot);
            if (!plane.Raycast(ray, out float enter)) return;
            var delta = ray.GetPoint(enter) - _dragStartHit;

            // AXIS 3 (uniform sphere)
            if (_dragAxis == 3)
            {
                if (currentTool == ToolMode.Scale)
                {
                    // Project accumulated mouse delta onto the screen-diagonal direction (cam.right+cam.up).
                    // Moving one gizmo-unit along that direction doubles the scale (factor = 1 + dist/handleSize).
                    var   effectiveMouse = _dragStartMouse + _rawMouseAccum;
                    var   scaleDir       = (cam.transform.right + cam.transform.up).normalized;
                    float dist_cl        = CalcLineTranslation(_dragStartMouse, effectiveMouse, _dragPivot, scaleDir, cam);
                    float factor         = Mathf.Max(0.001f, 1f + dist_cl / _dragGizmoScale);
                    ApplyScaleToDragObjects(factor, factor, factor, true, true, true);
                }
                else if (currentTool == ToolMode.Translate
                         && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
                {
                    ApplySurfaceSnapTranslation();
                }
                else { ApplyTranslation(delta); }
                return;
            }

            // AXES 4-6 (plane handles)
            if (_dragAxis >= 4)
            {
                if (currentTool == ToolMode.Scale)
                {
                    if (TryGetPlaneAxes(_dragAxis, out int aIdx, out int bIdx))
                    {
                        // Diagonal of the plane handle's two (possibly camera-flipped) axes.
                        // GetEffectivePivotRot already accounts for the per-axis flip state.
                        var   rot    = selectedObject.transform.rotation; // scale is always local-axis
                        var   dirA   = GizmoRenderer.GetEffectivePivotRot(aIdx) * Vector3.up;
                        var   dirB   = GizmoRenderer.GetEffectivePivotRot(bIdx) * Vector3.up;
                        var   localDiag = rot * (dirA + dirB).normalized;
                        var   effectiveMouse = _dragStartMouse + _rawMouseAccum;
                        float dist_cl        = CalcLineTranslation(_dragStartMouse, effectiveMouse, _dragPivot, localDiag, cam);
                        float factor         = Mathf.Max(0.001f, 1f + dist_cl / _dragGizmoScale);
                        ApplyScaleToDragObjects(
                            aIdx == 0 || bIdx == 0 ? factor : 1f,
                            aIdx == 1 || bIdx == 1 ? factor : 1f,
                            aIdx == 2 || bIdx == 2 ? factor : 1f,
                            aIdx == 0 || bIdx == 0,
                            aIdx == 1 || bIdx == 1,
                            aIdx == 2 || bIdx == 2);
                    }
                }
                else
                {
                    // Translate: screen-space 2-D solve for natural diagonal movement at any angle.
                    if (TryGetPlaneAxes(_dragAxis, out int aIdx, out int bIdx))
                    {
                        var objRot = selectedObject.transform.rotation;
                        var   axA  = LocalMode ? objRot * AxisVec(aIdx) : AxisVec(aIdx);
                        var   axB  = LocalMode ? objRot * AxisVec(bIdx) : AxisVec(bIdx);
                        var   oScreen = cam.WorldToScreenPoint(_dragPivot);
                        var   screenA = new Vector2(cam.WorldToScreenPoint(_dragPivot + axA).x - oScreen.x,
                                                    cam.WorldToScreenPoint(_dragPivot + axA).y - oScreen.y);
                        var   screenB = new Vector2(cam.WorldToScreenPoint(_dragPivot + axB).x - oScreen.x,
                                                    cam.WorldToScreenPoint(_dragPivot + axB).y - oScreen.y);
                        var   sd      = mouse - _dragStartMouse;
                        float det     = screenA.x * screenB.y - screenB.x * screenA.y;
                        if (Mathf.Abs(det) > 0.5f)
                        {
                            float a  = (sd.x * screenB.y - sd.y * screenB.x) / det;
                            float b  = (sd.y * screenA.x - sd.x * screenA.y) / det;
                            var   td = axA * a + axB * b;
                            for (int i = 0; i < _dragObjects.Count; i++)
                            {
                                var obj = _dragObjects[i];
                                if (obj == null) continue;
                                var pos = _dragStartPositions[i] + td;
                                if (_snapEnabled) pos = SnapVector(pos);
                                obj.transform.position = pos;
                            }

                            SyncDraggedPhysicsTransforms();
                        }
                        else { ApplyTranslation(delta); } // fallback: plane nearly edge-on
                    }
                    else { ApplyTranslation(delta); }
                }
                return;
            }

            // AXES 0-2 (single-axis arrows)
            if (currentTool == ToolMode.Scale)
            {
                // Project onto the effective arrow direction (accounts for camera-side flip).
                // In local mode the gizmo inherits obj.rotation, so we apply it here too.
                var   scaleObjRot    = selectedObject.transform.rotation; // scale is always local-axis
                var   localAxis      = scaleObjRot * (GizmoRenderer.GetEffectivePivotRot(_dragAxis) * Vector3.up);
                var   effectiveMouse = _dragStartMouse + _rawMouseAccum;
                float dist_cl        = CalcLineTranslation(_dragStartMouse, effectiveMouse, _dragPivot, localAxis, cam);
                float factor         = Mathf.Max(0.001f, 1f + dist_cl / _dragGizmoScale);
                ApplyScaleToDragObjects(
                    _dragAxis == 0 ? factor : 1f,
                    _dragAxis == 1 ? factor : 1f,
                    _dragAxis == 2 ? factor : 1f,
                    _dragAxis == 0,
                    _dragAxis == 1,
                    _dragAxis == 2);
                return;
            }

            var ax = LocalMode ? selectedObject.transform.rotation * AxisVec(_dragAxis)
                               : AxisVec(_dragAxis);
            ApplyTranslation(ax * Vector3.Dot(delta, ax));
        }

        public static void SetPhysicsMode(PhysicsMode mode)
        {
            var mgr = LevelEditorManager.Instance;
            if (mgr == null || _selection.Count == 0) return;

            var targets = _selection.Where(o => o != null).Distinct().ToList();
            if (targets.Count == 0) return;

            foreach (var obj in targets)
                if (obj.physicsMode != PhysicsMode.Static) PhysicsObjectManager.ClearPhysics(obj, restoreMaterial: mode == PhysicsMode.Static);
            if (mode == PhysicsMode.Static) return;

            int sharedLogicalGroup  = targets.All(o => o.groupId > 0 && o.groupId == targets[0].groupId)
                ? targets[0].groupId : 0;
            int sharedPhysicsGroup  = targets.All(o => o.physicsGroupId > 0 && o.physicsGroupId == targets[0].physicsGroupId)
                ? targets[0].physicsGroupId : 0;

            bool multi = targets.Count > 1;
            if (multi && sharedLogicalGroup <= 0) sharedLogicalGroup = GroupManager.AllocateGroupId();

            if (mode == PhysicsMode.Grabable || mode == PhysicsMode.Hat || mode == PhysicsMode.Rigidbody)
            {
                if (multi && sharedPhysicsGroup <= 0)
                    sharedPhysicsGroup = sharedLogicalGroup > 0 ? sharedLogicalGroup : GroupManager.AllocateGroupId();
                if (sharedLogicalGroup <= 0 && sharedPhysicsGroup > 0)
                    sharedLogicalGroup = sharedPhysicsGroup;
            }

            foreach (var obj in targets)
            {
                if (sharedLogicalGroup > 0 || multi) obj.groupId = sharedLogicalGroup;
                if (mode == PhysicsMode.Grabable || mode == PhysicsMode.Hat || mode == PhysicsMode.Rigidbody)
                    obj.physicsGroupId = multi ? sharedPhysicsGroup : obj.physicsGroupId;
                else
                    obj.physicsGroupId = 0;
                obj.physicsMode = mode;
            }

            if (mode == PhysicsMode.Grabable || mode == PhysicsMode.Hat)
            {
                if (multi) GroupManager.ActivateWearableGroup(targets, mode);
                else       PhysicsObjectManager.ActivatePhysics(targets[0]);
            }
            else if (mode == PhysicsMode.Rigidbody)
            {
                if (multi) GroupManager.ActivateRigidbodyGroup(targets);
                else       PhysicsObjectManager.ActivatePhysics(targets[0]);
            }
            else
            {
                foreach (var obj in targets) PhysicsObjectManager.ActivatePhysics(obj);
            }
        }

        public static void SetHatHairAmount(float hairAmount)
        {
            var mgr = LevelEditorManager.Instance;
            if (mgr == null || _selection.Count == 0) return;
            hairAmount = Mathf.Clamp01(hairAmount);
            foreach (var obj in _selection)
            {
                if (obj == null || obj.physicsMode != PhysicsMode.Hat) continue;
                obj.hatHairAmt = hairAmount;
                PhysicsObjectManager.SyncHatHairAmount(obj);
            }
        }

        public static void SetGrabOffset(Vector3 pos, Vector3 rotEuler)
        {
            var mgr = LevelEditorManager.Instance;
            if (mgr == null || _selection.Count == 0) return;
            foreach (var obj in _selection)
            {
                if (obj == null || obj.physicsMode != PhysicsMode.Grabable) continue;
                obj.grabOffsetPos = pos;
                obj.grabOffsetRot = rotEuler;
                PhysicsObjectManager.SyncGrabOffset(obj);
            }
        }

        public static void SetHatOffset(Vector3 pos, Vector3 rotEuler)
        {
            if (_selection.Count == 0) return;
            foreach (var obj in _selection)
            {
                if (obj == null || obj.physicsMode != PhysicsMode.Hat) continue;
                obj.hatOffsetPos = pos;
                obj.hatOffsetRot = rotEuler;
            }
        }

        public static void GroupSelection()
        {
            var mgr = LevelEditorManager.Instance;
            var targets = _selection.Where(o => o != null).Distinct().ToList();
            if (mgr == null || targets.Count == 0) return;

            bool mixedPhysicsModes = targets.Select(o => o.physicsMode).Distinct().Count() > 1;
            if (mixedPhysicsModes)
            {
                foreach (var obj in targets)
                {
                    if (obj.physicsMode != PhysicsMode.Static) PhysicsObjectManager.ClearPhysics(obj);
                    obj.physicsMode    = PhysicsMode.Static;
                    obj.physicsGroupId = 0;
                }
            }

            var existingGroups = new HashSet<int>(targets.Where(o => o.groupId > 0).Select(o => o.groupId));
            foreach (var gid in existingGroups) GroupManager.DissolveGroup(gid);

            int groupId = GroupManager.AllocateGroupId();
            foreach (var obj in targets) obj.groupId = groupId;
            GroupManager.EnsureStaticGroupRoot(groupId, targets);
            Select(targets[0]);

            var netIds = targets.Where(o => o.netId != 0).Select(o => o.netId).ToList();
            if (netIds.Count > 0)
                BabyBlocks.Networking.ModNetworking.SendGroupSync(netIds, group: true);
        }

        public static void UngroupSelection()
        {
            var targets = _selection.Where(o => o != null).Distinct().ToList();
            if (targets.Count == 0) return;

            var mgr = LevelEditorManager.Instance;
            if (mgr != null)
            {
                var groupsToClear = new HashSet<int>(targets.Where(o => o.groupId > 0).Select(o => o.groupId));
                foreach (var gid in groupsToClear) GroupManager.DissolveGroup(gid);
            }
            else
            {
                foreach (var obj in targets) obj.groupId = 0;
            }

            selectedObject = targets[0];

            var netIds = targets.Where(o => o.netId != 0).Select(o => o.netId).ToList();
            if (netIds.Count > 0)
                BabyBlocks.Networking.ModNetworking.SendGroupSync(netIds, group: false);
        }

        static void DeleteSelected()
        {
            if (LevelEditorManager.Instance == null || _selection.Count == 0) return;
            for (int i = 0; i < _selection.Count; i++)
            {
                var obj = _selection[i];
                if (obj == null) continue;
                LevelEditorHistory.PushDelete(obj);
                ulong netId = obj.netId;
                LevelEditorManager.Instance.Remove(obj);
                if (netId != 0)
                    BabyBlocks.Networking.ModNetworking.SendPropDeleted(netId);
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
            _copyBoundsSize = GizmoRenderer.GetSelectionBounds(_selection).size;

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
                    isAddressable = !string.IsNullOrEmpty(obj.addressableKey),
                    materialConstructionId = obj.materialConstructionId,
                    physicsMode       = obj.physicsMode,
                    sunglassesNeeded  = obj.sunglassesNeeded,
                    playerPassthrough = obj.playerPassthrough,
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

        static void PasteCopy(bool pasteInPlace = false)
        {
            if (!_copyHasValue) return;
            EnsureManager();
            var pivot = _selection.Count > 0
                ? GizmoRenderer.GetSelectionBoundsCenter(_selection)
                : _copyPivot;

            Vector3 pasteOffset = pasteInPlace
                ? Vector3.zero
                : new Vector3(0, Mathf.Clamp(_copyBoundsSize.y * 0.1f, 0.05f, 0.5f), 0);
            var snappedOffset = SnapVector(pasteOffset);
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
                    {
                        obj = LevelEditorManager.Instance.SpawnFromPropInfo(info, targetPos);
                        PropHistory.RecordUse(entry.addressableKey);
                        if (entry.materialConstructionId >= 0)
                        {
                            var matEntry = MaterialConstructionLibrary.FindById(entry.materialConstructionId);
                            if (matEntry != null)
                                MaterialConstructionPanel.ApplyToInstance(obj, matEntry, pushHistory: false);
                        }
                    }
                }
                else
                {
                    obj = LevelEditorManager.Instance.SpawnPrimitive(entry.primType, targetPos);
                }

                if (obj == null) continue;
                obj.transform.localScale = entry.scale;
                obj.transform.rotation = entry.rotation;

                // Apply non-static physics mode — temporarily select the single object so
                // SetPhysicsMode's group/single branching logic works correctly.
                if (entry.physicsMode != PhysicsMode.Static)
                {
                    _selection.Clear();
                    _selection.Add(obj);
                    selectedObject = obj;
                    SetPhysicsMode(entry.physicsMode);
                }

                obj.sunglassesNeeded  = entry.sunglassesNeeded;
                obj.playerPassthrough = entry.playerPassthrough;
                if (entry.sunglassesNeeded && obj.GetComponent<BbSunglassesChecker>() == null)
                    obj.gameObject.AddComponent<BbSunglassesChecker>();
                if (entry.playerPassthrough)
                    PropInstanceServices.SetBushPassthrough(obj.gameObject, true);

                newSelection.Add(obj);
                LevelEditorHistory.PushSpawn(obj);

                // Only addressable (prop-library) pastes can be synced - peers reconstruct
                // the prop via PropLibrary.FindById(propId), which has no equivalent for
                // primitives spawned via SpawnPrimitive.
                if (entry.isAddressable)
                {
                    ulong netId = BabyBlocks.Networking.ModNetworking.RegisterNetworkedObject(obj);
                    BabyBlocks.Networking.ModNetworking.SendPropPlaced(
                        netId, entry.addressableKey, obj.transform.position, obj.transform.rotation, obj.transform.localScale);
                }
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
                // Restore drag-start rotation each frame: undoes any rotation applied while
                // surface-snap mode (shift) was active earlier in the same drag.
                obj.transform.SetPositionAndRotation(pos, _dragStartRotations[i]);
            }

            SyncDraggedPhysicsTransforms();
        }

        // Surface-snap translation: while holding shift on the center-sphere drag, the dragged
        // selection is stuck to whatever surface is under the cursor (position + rotation aligned
        // to the hit normal), mirroring how props snap onto surfaces when placed from the palette.
        // Releasing shift returns to normal free translation along the drag plane.
        static void ApplySurfaceSnapTranslation()
        {
            var cam = Camera.main;
            var ray = cam.ScreenPointToRay(Input.mousePosition);

            RaycastHit hit       = default;
            float      bestDist  = float.MaxValue;
            bool       found     = false;
            // RaycastAll + filter: a plain Raycast can hit the object being dragged itself
            // (it now sits under the cursor), which causes the snap point to jitter toward
            // the camera and back as the object repeatedly occludes/unoccludes the surface.
            foreach (var h in Physics.RaycastAll(ray, 2000f, LayerCache.PropTerrainMask))
            {
                if (h.distance >= bestDist || IsDraggedTransform(h.collider.transform)) continue;
                bestDist = h.distance;
                hit      = h;
                found    = true;
            }
            if (!found) return;

            var rot = ComputeSpawnRotation(hit.normal) * DragOrientations[_dragStep];

            // Reuse the same placement math as palette dragging (ComputeSpawnPosition) so the
            // prop's nearest face sits flush on the surface instead of snapping by its center.
            var primary  = _dragObjects[0];
            var prop     = primary != null ? PropLibrary.FindById(primary.addressableKey) : null;
            Vector3 targetPos;
            if (prop != null)
            {
                PropLibrary.LoadPropData(prop);
                targetPos = ComputeSpawnPosition(prop, hit, rot, primary.transform.localScale);
            }
            else
            {
                targetPos = hit.point + hit.normal * 0.5f;
            }

            var deltaRot = rot * Quaternion.Inverse(_dragStartRotations[0]);
            var posDelta = targetPos - _dragStartPositions[0];

            for (int i = 0; i < _dragObjects.Count; i++)
            {
                var obj = _dragObjects[i];
                if (obj == null) continue;
                obj.transform.rotation = deltaRot * _dragStartRotations[i];
                obj.transform.position = _dragStartPositions[i] + posDelta;
            }

            SyncDraggedPhysicsTransforms();
        }

        static bool IsDraggedTransform(Transform t)
        {
            for (int i = 0; i < _dragObjects.Count; i++)
            {
                var obj = _dragObjects[i];
                if (obj != null && (t == obj.transform || t.IsChildOf(obj.transform))) return true;
            }
            return false;
        }

        static void ApplyScaleToDragObjects(float xFactor, float yFactor, float zFactor,
            bool scaleX, bool scaleY, bool scaleZ)
        {
            var basis = LocalMode && selectedObject != null
                ? selectedObject.transform.rotation
                : Quaternion.identity;

            for (int i = 0; i < _dragObjects.Count; i++)
            {
                var obj = _dragObjects[i];
                if (obj == null) continue;

                // Spawn point markers are fixed-size — never scale them, and don't
                // shift their position when other selected objects are scaled around them.
                if (PropLibrary.IsSpawnPointProp(obj.addressableKey)) continue;

                var start = _dragStartScales[i];
                var final = new Vector3(
                    scaleX ? Mathf.Max(0.001f, start.x * xFactor) : start.x,
                    scaleY ? Mathf.Max(0.001f, start.y * yFactor) : start.y,
                    scaleZ ? Mathf.Max(0.001f, start.z * zFactor) : start.z);

                if (_snapEnabled) final = SnapVector(final);
                obj.transform.localScale = final;

                var rel = _dragStartPositions[i] - _dragPivot;
                var localRel = Quaternion.Inverse(basis) * rel;
                if (scaleX) localRel.x *= SafeScaleRatio(start.x, final.x);
                if (scaleY) localRel.y *= SafeScaleRatio(start.y, final.y);
                if (scaleZ) localRel.z *= SafeScaleRatio(start.z, final.z);
                obj.transform.position = _dragPivot + (basis * localRel);
            }

            SyncDraggedPhysicsTransforms();
        }

        static void SyncDraggedPhysicsTransforms()
        {
            for (int i = 0; i < _dragObjects.Count; i++)
            {
                var obj = _dragObjects[i];
                if (obj != null && obj.physicsMode != PhysicsMode.Static)
                {
                    Physics.SyncTransforms();
                    return;
                }
            }
        }

        static float SafeScaleRatio(float start, float final)
            => Mathf.Abs(start) < 0.00001f ? 1f : final / start;

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

        // Mirrors Unity's HandleUtility.CalcLineTranslation.
        // Projects srcMouse and dstMouse (screen-pixel positions) onto the screen-space projection
        // of the world-space axis (origin → origin+unitDir), and returns the displacement in
        // world-space units along that axis.  Camera-distance-aware: the result is ~1 world unit
        // when the cursor moves by exactly one "handle length" in screen space.
        static float CalcLineTranslation(Vector2 srcMouse, Vector2 dstMouse,
                                         Vector3 origin,   Vector3 unitDir, Camera cam)
        {
            var p1        = cam.WorldToScreenPoint(origin);
            var p2        = cam.WorldToScreenPoint(origin + unitDir);
            var screenDir = new Vector2(p2.x - p1.x, p2.y - p1.y);
            float sqMag   = screenDir.sqrMagnitude;
            if (sqMag < 0.01f) return 0f;
            var   o2d = new Vector2(p1.x, p1.y);
            float t0  = Vector2.Dot(srcMouse - o2d, screenDir) / sqMag;
            float t1  = Vector2.Dot(dstMouse - o2d, screenDir) / sqMag;
            return t1 - t0;
        }
    }
}
