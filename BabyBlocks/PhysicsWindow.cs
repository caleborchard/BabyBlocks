using UnityEngine;

namespace BabyBlocks
{
    // Draggable Physics window — cycles selected props through Static/Rigidbody/Grabable/Hat,
    // shows the current physics mode and group, and exposes Group/Ungroup buttons.
    static class PhysicsWindow
    {
        const float WinW    = 170f;
        const float HeaderH = 30f;
        const float Pad     = 7f;
        const float LineH   = 22f;

        static Rect    _windowRect;
        static bool    _initialized;
        static bool    _dragging;
        static Vector2 _dragOffset;

        static void EnsureInit()
        {
            if (_initialized) return;
            _initialized = true;
            float h = ComputeHeight(false, false);
            _windowRect = new Rect(Screen.width - 320f - WinW - 10f,
                                   Screen.height - h - 40f, WinW, h);
        }

        static float ComputeHeight(bool showGroup, bool showHatHair)
            => HeaderH + Pad + LineH + (showHatHair ? LineH + 24f : 0f) + (showGroup ? LineH : 0f) + LineH + LineH + Pad;

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

            foreach (var obj in sel)
            {
                if (obj == null) continue;
                if (!hasSelection)
                {
                    hasSelection = true;
                    sharedMode   = obj.physicsMode;
                    sharedGroup  = obj.groupId;
                }
                else
                {
                    if (sharedMode != obj.physicsMode) sharedMode = null;
                    if (sharedGroup != obj.groupId)    sharedGroup = 0;
                }
                if (obj.groupId > 0) anyGroup = true;
            }

            bool showGroup   = hasSelection && sharedGroup > 0;
            bool showHatHair = hasSelection && primary != null && primary.physicsMode == PhysicsMode.Hat;
            float winH = ComputeHeight(showGroup, showHatHair);
            _windowRect.width  = WinW;
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
                _dragging   = true;
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

            if (showHatHair)
            {
                GUI.Label(new Rect(x, y, w, LineH), $"Hair cut: {Mathf.RoundToInt(Mathf.Clamp01(primary.hatHairAmt) * 100f)}%");
                y += LineH;
                float newHairAmt = GUI.HorizontalSlider(new Rect(x, y + 4f, w, 18f), primary.hatHairAmt, 0f, 1f);
                if (!Mathf.Approximately(newHairAmt, primary.hatHairAmt))
                    LevelEditor.SetHatHairAmount(newHairAmt);
                y += 24f;
            }

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
