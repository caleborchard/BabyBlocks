using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace BabyBlocks
{
    // Draggable Physics window — cycles selected props through Static/Rigidbody/Grabable/Hat,
    // shows the current physics mode and group, and exposes Group/Ungroup buttons.
    static class PhysicsWindow
    {
        const float WinW = 210f;
        const float HeaderH = 30f;
        const float Pad = 7f;
        const float LineH = 22f;

        static Rect    _windowRect;
        static bool    _initialized;
        static bool    _dragging;
        static Vector2 _dragOffset;

        // Text field state for grab/hat offsets
        static string[] _grabPosStr = { "0", "0", "0" };
        static string[] _grabRotStr = { "0", "0", "0" };
        static string[] _hatPosStr = { "0", "0", "0" };
        static string[] _hatRotStr = { "0", "0", "0" };
        static LevelEditorObject _lastPrimary;

        public static bool IsTypingInUI { get; private set; }

        static void EnsureInit()
        {
            if (_initialized) return;
            _initialized = true;
            float h = ComputeHeight(false, false, false, false, false);
            _windowRect = new Rect(Screen.width - 320f - WinW - 10f,
                                   Screen.height - h - 40f, WinW, h);
        }

        static float ComputeHeight(bool showGroup, bool showHatHair, bool showGrabOffset, bool showHatOffset, bool showBakeToggle)
            => HeaderH + Pad + LineH
             + (showHatHair    ? LineH + 24f : 0f)
             + (showGrabOffset ? 3 * LineH   : 0f)
             + (showHatOffset  ? 3 * LineH   : 0f)
             + (showGroup      ? LineH       : 0f)
             + (showBakeToggle ? LineH       : 0f)
             + LineH + LineH + Pad;

        public static bool ContainsPoint(Vector2 guiPoint) =>
            _initialized && _windowRect.Contains(guiPoint);

        public static void DrawGUI(Event e)
        {
            EnsureInit();

            var sel = LevelEditor.SelectedObjects;
            bool hasSelection = false;
            PhysicsMode? sharedMode = null;
            int sharedGroup = 0;
            bool anyGroup = false;
            var primary = LevelEditor.selectedObject;

            var bakingPropIds = new HashSet<string>();
            foreach (var obj in sel)
            {
                if (obj == null) continue;
                if (!hasSelection)
                {
                    hasSelection = true;
                    sharedMode = obj.physicsMode;
                    sharedGroup = obj.groupId;
                }
                else
                {
                    if (sharedMode != obj.physicsMode) sharedMode = null;
                    if (sharedGroup != obj.groupId)    sharedGroup = 0;
                }
                if (obj.groupId > 0) anyGroup = true;
                if (!string.IsNullOrEmpty(obj.addressableKey)) bakingPropIds.Add(obj.addressableKey);
            }

            bool? sharedDisableBaking = null;
            bool firstBakeProp = true;
            foreach (var id in bakingPropIds)
            {
                bool val = PropMetadataStore.GetDisableBaking(id);
                if (firstBakeProp) { sharedDisableBaking = val; firstBakeProp = false; }
                else if (sharedDisableBaking != val) sharedDisableBaking = null;
            }

            bool showGroup = hasSelection && sharedGroup > 0;
            bool showHatHair = hasSelection && primary != null && primary.physicsMode == PhysicsMode.Hat;
            bool showGrabOffset = hasSelection && primary != null && primary.physicsMode == PhysicsMode.Grabable;
            bool showHatOffset = hasSelection && primary != null && primary.physicsMode == PhysicsMode.Hat;
            bool showBakeToggle = bakingPropIds.Count > 0;

            // Sync text field strings when the selected object changes
            if (primary != _lastPrimary)
            {
                _lastPrimary = primary;
                SyncStrings(primary);
            }

            float winH = ComputeHeight(showGroup, showHatHair, showGrabOffset, showHatOffset, showBakeToggle);
            _windowRect.width = WinW;
            _windowRect.height = winH;
            _windowRect.x = Mathf.Clamp(_windowRect.x, 0f, Screen.width  - WinW);
            _windowRect.y = Mathf.Clamp(_windowRect.y, 0f, Screen.height - winH);

            GUI.color = new Color(0f, 0f, 0f, 0.72f);
            GUI.Box(_windowRect, "");
            GUI.color = Color.white;

            var headerRect = new Rect(_windowRect.x, _windowRect.y, WinW, HeaderH);
            GUI.Label(new Rect(_windowRect.x + Pad, _windowRect.y + 6f, WinW - Pad * 2f, 20f), "Physics");

            if (e.type == EventType.MouseDown && e.button == 0 && headerRect.Contains(e.mousePosition))
            {
                _dragging = true;
                _dragOffset = e.mousePosition - new Vector2(_windowRect.x, _windowRect.y);
                e.Use();
            }
            if (_dragging)
            {
                if (e.type == EventType.MouseDrag)
                {
                    _windowRect.x = e.mousePosition.x - _dragOffset.x;
                    _windowRect.y = e.mousePosition.y - _dragOffset.y;
                    e.Use();
                }
                if (e.type == EventType.MouseUp)
                    _dragging = false;
            }

            float y = _windowRect.y + HeaderH + Pad;
            float x = _windowRect.x + Pad;
            float w = WinW - Pad * 2f;

            string modeLabel = !hasSelection        ? "—"
                             : sharedMode == null   ? "Mixed"
                             : sharedMode == PhysicsMode.Static    ? "Static"
                             : sharedMode == PhysicsMode.Rigidbody ? "Rigidbody"
                             : sharedMode == PhysicsMode.Grabable  ? "Grabable"
                             : "Hat";
            GUI.Label(new Rect(x, y, w, LineH), $"Mode: {modeLabel}");
            y += LineH;

            if (showBakeToggle)
            {
                bool disableBaking = sharedDisableBaking ?? false;
                string bakeLabel = sharedDisableBaking == null
                    ? " Disable texture baking (Mixed)"
                    : " Disable texture baking";
                bool newDisableBaking = GUI.Toggle(new Rect(x, y, w, LineH), disableBaking, bakeLabel);
                if (newDisableBaking != disableBaking)
                {
                    foreach (var id in bakingPropIds)
                    {
                        PropMetadataStore.SetDisableBaking(id, newDisableBaking);
                        if (LevelEditorManager.Instance != null) PhysicsObjectManager.RefreshBakingForProp(id);
                    }
                }
                y += LineH;
            }

            if (showHatHair)
            {
                GUI.Label(new Rect(x, y, w, LineH), $"Hair cut: {Mathf.RoundToInt(Mathf.Clamp01(primary.hatHairAmt) * 100f)}%");
                y += LineH;
                float newHairAmt = GUI.HorizontalSlider(new Rect(x, y + 4f, w, 18f), primary.hatHairAmt, 0f, 1f);
                if (!Mathf.Approximately(newHairAmt, primary.hatHairAmt))
                    LevelEditor.SetHatHairAmount(newHairAmt);
                y += 24f;
            }

            if (showGrabOffset)
            {
                GUI.Label(new Rect(x, y, w, LineH), "Grab Offset");
                y += LineH;

                var newGrabPos = Vec3Row(x, y, w, "Pos", primary.grabOffsetPos, _grabPosStr,
                                        "grab_px", "grab_py", "grab_pz");
                y += LineH;
                var newGrabRot = Vec3Row(x, y, w, "Rot", primary.grabOffsetRot, _grabRotStr,
                                        "grab_rx", "grab_ry", "grab_rz");
                y += LineH;

                if (newGrabPos != primary.grabOffsetPos || newGrabRot != primary.grabOffsetRot)
                    LevelEditor.SetGrabOffset(newGrabPos, newGrabRot);
            }

            if (showHatOffset)
            {
                GUI.Label(new Rect(x, y, w, LineH), "Hat Offset");
                y += LineH;

                var newHatPos = Vec3Row(x, y, w, "Pos", primary.hatOffsetPos, _hatPosStr,
                                       "hat_px", "hat_py", "hat_pz");
                y += LineH;
                var newHatRot = Vec3Row(x, y, w, "Rot", primary.hatOffsetRot, _hatRotStr,
                                       "hat_rx", "hat_ry", "hat_rz");
                y += LineH;

                if (newHatPos != primary.hatOffsetPos || newHatRot != primary.hatOffsetRot)
                    LevelEditor.SetHatOffset(newHatPos, newHatRot);
            }

            // Track whether any offset field is focused (blocks editor shortcuts)
            var focused = GUI.GetNameOfFocusedControl();
            IsTypingInUI = focused == "grab_px" || focused == "grab_py" || focused == "grab_pz"
                        || focused == "grab_rx" || focused == "grab_ry" || focused == "grab_rz"
                        || focused == "hat_px"  || focused == "hat_py"  || focused == "hat_pz"
                        || focused == "hat_rx"  || focused == "hat_ry"  || focused == "hat_rz";

            if (showGroup)
            {
                GUI.Label(new Rect(x, y, w, LineH), $"Group: {sharedGroup}");
                y += LineH;
            }

            string nextLabel = !hasSelection                        ? "Cycle"
                             : sharedMode == null                   ? "→ Static"
                             : sharedMode == PhysicsMode.Static     ? "→ Rigidbody"
                             : sharedMode == PhysicsMode.Rigidbody  ? "→ Grabable"
                             : sharedMode == PhysicsMode.Grabable   ? "→ Hat"
                             : "→ Static";

            GUI.enabled = hasSelection;
            if (GUI.Button(new Rect(x, y, w, LineH), nextLabel))
                CycleMode(sharedMode);
            y += LineH + 2f;

            float halfW = (w - 6f) * 0.5f;
            GUI.enabled = hasSelection;
            if (GUI.Button(new Rect(x, y, halfW, LineH), "Group"))
                LevelEditor.GroupSelection();
            GUI.enabled = hasSelection && anyGroup;
            if (GUI.Button(new Rect(x + halfW + 6f, y, halfW, LineH), "Ungroup"))
                LevelEditor.UngroupSelection();
            GUI.enabled = true;
        }

        // Draws a labelled row of three float text fields. Returns the parsed Vector3.
        static Vector3 Vec3Row(float x, float y, float w, string label,
                               Vector3 current, string[] strs,
                               string ctrlX, string ctrlY, string ctrlZ)
        {
            const float LabelW = 28f;
            const float Gap = 3f;
            float fw = (w - LabelW - Gap * 2f) / 3f;

            GUI.Label(new Rect(x, y + 2f, LabelW, LineH), label);

            float fx = x + LabelW;
            GUI.SetNextControlName(ctrlX);
            strs[0] = GUI.TextField(new Rect(fx, y + 1f, fw, LineH - 2f), strs[0]);
            fx += fw + Gap;
            GUI.SetNextControlName(ctrlY);
            strs[1] = GUI.TextField(new Rect(fx, y + 1f, fw, LineH - 2f), strs[1]);
            fx += fw + Gap;
            GUI.SetNextControlName(ctrlZ);
            strs[2] = GUI.TextField(new Rect(fx, y + 1f, fw, LineH - 2f), strs[2]);

            float vx = TryParseFloat(strs[0], current.x);
            float vy = TryParseFloat(strs[1], current.y);
            float vz = TryParseFloat(strs[2], current.z);
            return new Vector3(vx, vy, vz);
        }

        static float TryParseFloat(string s, float fallback)
            => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        static void SyncStrings(LevelEditorObject primary)
        {
            if (primary == null)
            {
                for (int i = 0; i < 3; i++)
                    _grabPosStr[i] = _grabRotStr[i] = _hatPosStr[i] = _hatRotStr[i] = "0";
                return;
            }
            FormatVec(primary.grabOffsetPos, _grabPosStr);
            FormatVec(primary.grabOffsetRot, _grabRotStr);
            FormatVec(primary.hatOffsetPos,  _hatPosStr);
            FormatVec(primary.hatOffsetRot,  _hatRotStr);
        }

        static void FormatVec(Vector3 v, string[] strs)
        {
            strs[0] = v.x.ToString("F3", CultureInfo.InvariantCulture);
            strs[1] = v.y.ToString("F3", CultureInfo.InvariantCulture);
            strs[2] = v.z.ToString("F3", CultureInfo.InvariantCulture);
        }

        static void CycleMode(PhysicsMode? current)
        {
            PhysicsMode next = (current == null || current == PhysicsMode.Hat)
                                    ? PhysicsMode.Static
                             : current == PhysicsMode.Static
                                    ? PhysicsMode.Rigidbody
                             : current == PhysicsMode.Rigidbody
                                    ? PhysicsMode.Grabable
                                    : PhysicsMode.Hat;
            LevelEditor.SetPhysicsMode(next);
        }
    }
}
