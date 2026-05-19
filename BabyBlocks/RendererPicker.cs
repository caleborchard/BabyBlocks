using System.Text;
using UnityEngine;

namespace BabyBlocks
{
    // Picks any rendered object under the cursor regardless of whether it has a collider,
    // and regardless of whether its Renderer is enabled (TVE and other GPU instancting
    // systems disable MeshRenderer but the MeshFilter + mesh data remain on the GameObject).
    static class RendererPicker
    {
        public static bool Active { get; private set; }

        static string  _info      = "";
        static Rect    _panelRect = new Rect(10f, 40f, 440f, 10f);
        static bool    _panelDrag;
        static Vector2 _panelDragOffset;

        const float CrosshairSize    = 10f;
        const float ScreenPickRadius = 50f; // px, fallback for objects that are tiny on screen

        public static void Toggle()
        {
            Active = !Active;
            if (!Active) _info = "";
        }

        public static void Update()
        {
            if (!Active) return;
            if (Input.GetMouseButtonDown(0) && !IsPointerOverPanel())
                Pick();
        }

        public static void OnGUI()
        {
            if (!Active) return;

            float cx = Screen.width  * 0.5f;
            float cy = Screen.height * 0.5f;
            GUI.color = Color.yellow;
            GUI.DrawTexture(new Rect(cx - CrosshairSize, cy - 1f, CrosshairSize * 2f, 2f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx - 1f, cy - CrosshairSize, 2f, CrosshairSize * 2f), Texture2D.whiteTexture);
            GUI.color = Color.white;

            if (_info.Length == 0) return;

            var ev = Event.current;
            var headerRect = new Rect(_panelRect.x, _panelRect.y, _panelRect.width, 20f);
            if (ev.type == EventType.MouseDown && headerRect.Contains(ev.mousePosition))
            {
                _panelDrag       = true;
                _panelDragOffset = ev.mousePosition - new Vector2(_panelRect.x, _panelRect.y);
            }
            if (_panelDrag)
            {
                if (ev.type == EventType.MouseUp) _panelDrag = false;
                else _panelRect.position = ev.mousePosition - _panelDragOffset;
            }

            int lines = 1;
            foreach (char c in _info) if (c == '\n') lines++;
            _panelRect.height = 24f + lines * 17f;

            GUI.color = new Color(0f, 0f, 0f, 0.78f);
            GUI.Box(_panelRect, "");
            GUI.color = Color.white;

            GUILayout.BeginArea(_panelRect);
            GUILayout.Label("RENDERER PICK  [I = close]");
            GUILayout.Label(_info);
            GUILayout.EndArea();
        }

        // ────────────────────────────────────────────────────────────────────

        static void Pick()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var ray = cam.ScreenPointToRay(Input.mousePosition);

            float bestDist = float.MaxValue;
            MeshFilter bestMf = null;

            // includeInactive=true: finds MeshFilters whose Renderer is disabled at runtime
            // (TVE disables MeshRenderer on foliage and renders via GPU indirect instancing,
            // but the MeshFilter + mesh data stay on the GameObject unchanged).
            var allMf = UnityEngine.Object.FindObjectsOfType<MeshFilter>(true);

            // ── pass 1: ray vs world-space mesh bounds ───────────────────────
            foreach (var mf in allMf)
            {
                if (!mf.gameObject.activeInHierarchy) continue;
                var mesh = mf.sharedMesh;
                if (mesh == null) continue;

                var wb = MeshWorldBounds(mesh, mf.transform);
                if (wb.IntersectRay(ray, out float dist) && dist >= 0f && dist < bestDist)
                {
                    bestDist = dist;
                    bestMf   = mf;
                }
            }

            // ── pass 2: screen-space fallback (handles collapsed / billboard bounds) ──
            if (bestMf == null)
            {
                float bestSd = ScreenPickRadius * ScreenPickRadius;
                var   mouse  = Input.mousePosition;
                foreach (var mf in allMf)
                {
                    if (!mf.gameObject.activeInHierarchy) continue;
                    var mesh = mf.sharedMesh;
                    if (mesh == null) continue;

                    var center = mf.transform.TransformPoint(mesh.bounds.center);
                    var sp     = cam.WorldToScreenPoint(center);
                    if (sp.z < 0f) continue;

                    float dx = sp.x - mouse.x;
                    float dy = sp.y - mouse.y;
                    float sd = dx * dx + dy * dy;
                    if (sd < bestSd)
                    {
                        bestSd   = sd;
                        bestDist = sp.z;
                        bestMf   = mf;
                    }
                }
            }

            _info = bestMf != null ? BuildInfo(bestMf) : "No mesh found at cursor.";
        }

        // Transforms all 8 AABB corners through the transform matrix for an accurate
        // world-space AABB regardless of object rotation.
        static Bounds MeshWorldBounds(Mesh mesh, Transform t)
        {
            var local  = mesh.bounds;
            var m      = t.localToWorldMatrix;
            var c      = local.center;
            var e      = local.extents;

            var min = new Vector3(float.MaxValue,  float.MaxValue,  float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (int i = 0; i < 8; i++)
            {
                var corner = c + new Vector3(
                    (i & 1) == 0 ? -e.x : e.x,
                    (i & 2) == 0 ? -e.y : e.y,
                    (i & 4) == 0 ? -e.z : e.z);
                var w = m.MultiplyPoint3x4(corner);
                min = Vector3.Min(min, w);
                max = Vector3.Max(max, w);
            }

            var b = new Bounds();
            b.SetMinMax(min, max);
            return b;
        }

        static string BuildInfo(MeshFilter mf)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Path:   " + GetPath(mf.transform));

            var mesh = mf.sharedMesh;
            sb.AppendLine("Mesh:   " + (mesh != null ? mesh.name : "<null>"));

            // Renderer might be disabled (TVE etc.) but sharedMaterials is still readable.
            var mr = mf.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                var mats = mr.sharedMaterials;
                sb.Append("Mats (" + mats.Length + "):" + (mr.enabled ? "" : "  [renderer disabled]"));
                for (int i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    sb.AppendLine();
                    sb.Append("  [" + i + "]  " + (mat != null ? mat.name : "<null>"));
                    if (mat?.shader != null) sb.Append("   shader: " + mat.shader.name);
                }
            }
            else
            {
                sb.Append("Renderer: none on this object");
            }

            return sb.ToString().TrimEnd();
        }

        static string GetPath(Transform t)
        {
            if (t.parent == null) return t.name;
            return GetPath(t.parent) + "/" + t.name;
        }

        static bool IsPointerOverPanel()
        {
            if (_info.Length == 0) return false;
            var mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            return _panelRect.Contains(mouse);
        }
    }
}
