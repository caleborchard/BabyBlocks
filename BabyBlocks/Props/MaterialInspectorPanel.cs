using System;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;

namespace BabyBlocks
{
    static class MaterialInspectorPanel
    {
        static bool _visible;
        static Rect _windowRect = new Rect(20, 60, 500, 600);
        static bool _dragging;
        static Vector2 _dragOffset;

        static string _search = "";
        static Material _inspected;
        static string _inspectedName = "";
        static Vector2 _scroll;

        const float HeaderH   = 22f;
        const float Pad       = 6f;
        const float ThumbSize = 48f;

        // ── Toggle ────────────────────────────────────────────────────────────
        public static void Toggle() => _visible = !_visible;
        public static bool Visible  => _visible;

        // ── Main draw entry ───────────────────────────────────────────────────
        public static void DrawGUI()
        {
            if (!_visible) return;

            // Drag
            var headerRect = new Rect(_windowRect.x, _windowRect.y, _windowRect.width, HeaderH);
            if (Event.current.type == EventType.MouseDown && headerRect.Contains(Event.current.mousePosition))
            {
                _dragging = true;
                _dragOffset = Event.current.mousePosition - new Vector2(_windowRect.x, _windowRect.y);
            }
            if (_dragging)
            {
                if (Event.current.type == EventType.MouseDrag)
                    _windowRect.position = Event.current.mousePosition - _dragOffset;
                if (Event.current.type == EventType.MouseUp)
                    _dragging = false;
            }

            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.Box(_windowRect, "");
            GUI.color = Color.white;

            GUILayout.BeginArea(_windowRect);

            // Header
            GUILayout.BeginHorizontal(GUILayout.Height(HeaderH));
            GUILayout.Label("  Material Inspector", GUILayout.ExpandWidth(true));
            if (GUILayout.Button("✕", GUILayout.Width(24))) _visible = false;
            GUILayout.EndHorizontal();

            GUILayout.Space(Pad);

            // Search row
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("matInspSearch");
            _search = GUILayout.TextField(_search ?? "", GUILayout.ExpandWidth(true));
            if (GUILayout.Button("Find", GUILayout.Width(50)))
                FindMaterial(_search);
            GUILayout.EndHorizontal();

            GUILayout.Space(Pad);

            if (_inspected != null)
                DrawProperties();
            else if (!string.IsNullOrEmpty(_inspectedName))
                GUILayout.Label($"No material found for \"{_inspectedName}\"");
            else
                GUILayout.Label("Enter a material name above and press Find.");

            GUILayout.EndArea();
        }

        // ── Lookup ────────────────────────────────────────────────────────────
        static void FindMaterial(string name)
        {
            _inspectedName = name ?? "";
            _inspected     = null;
            _scroll        = Vector2.zero;

            if (string.IsNullOrEmpty(name)) return;

            // Try PropMetadataPanel's cache first (includes scene variants).
            _inspected = MaterialCatalog.TryGetMaterialByName(name);
            if (_inspected != null) return;

            // Fall back to a linear scan of all loaded materials.
            var all = Resources.FindObjectsOfTypeAll<Material>();
            if (all == null) return;
            for (int i = 0; i < all.Length; i++)
            {
                var m = all[i];
                if (m == null) continue;
                try
                {
                    if (string.Equals(m.name, name, StringComparison.OrdinalIgnoreCase))
                    { _inspected = m; return; }
                }
                catch { }
            }
        }

        // ── Property display ──────────────────────────────────────────────────
        static void DrawProperties()
        {
            var mat = _inspected;
            Shader shader;
            try { shader = mat.shader; } catch { GUILayout.Label("(shader unavailable)"); return; }

            GUILayout.Label($"<b>{TryGetName(mat)}</b>  shader: {TryGetName(shader)}",
                new GUIStyle(GUI.skin.label) { richText = true });
            GUILayout.Space(4f);

            float bodyH = _windowRect.height - HeaderH - Pad * 3f - 60f;
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(bodyH));

            int count = 0;
            try { count = shader.GetPropertyCount(); } catch { }

            for (int i = 0; i < count; i++)
            {
                string propName;
                ShaderPropertyType propType;
                try
                {
                    propName = shader.GetPropertyName(i);
                    propType = shader.GetPropertyType(i);
                }
                catch { continue; }

                GUILayout.BeginHorizontal();
                GUILayout.Label($"<b>{propName}</b>", new GUIStyle(GUI.skin.label) { richText = true }, GUILayout.Width(160));

                try
                {
                    switch (propType)
                    {
                        case ShaderPropertyType.Float:
                        case ShaderPropertyType.Range:
                        {
                            float v = mat.GetFloat(propName);
                            GUILayout.Label($"{v:G6}");
                            break;
                        }
                        case ShaderPropertyType.Int:
                        {
                            int v = mat.GetInt(propName);
                            GUILayout.Label(v.ToString());
                            break;
                        }
                        case ShaderPropertyType.Color:
                        {
                            Color c = mat.GetColor(propName);
                            var prev = GUI.color;
                            GUI.color = new Color(c.r, c.g, c.b, 1f);
                            GUILayout.Box("", GUILayout.Width(16), GUILayout.Height(16));
                            GUI.color = prev;
                            GUILayout.Label($"  R:{c.r:F3}  G:{c.g:F3}  B:{c.b:F3}  A:{c.a:F3}");
                            break;
                        }
                        case ShaderPropertyType.Vector:
                        {
                            Vector4 v = mat.GetVector(propName);
                            GUILayout.Label($"({v.x:G5}, {v.y:G5}, {v.z:G5}, {v.w:G5})");
                            break;
                        }
                        case ShaderPropertyType.Texture:
                        {
                            Texture tex;
                            try { tex = mat.GetTexture(propName); } catch { tex = null; }

                            if (tex == null)
                            {
                                GUILayout.Label("(none)");
                            }
                            else
                            {
                                GUILayout.Label(TryGetName(tex), GUILayout.Width(200));
                                // Preview — only Texture2D can be drawn directly in IMGUI.
                                Texture2D tex2d = tex.TryCast<Texture2D>();
                                if (tex2d != null)
                                {
                                    var rect = GUILayoutUtility.GetRect(ThumbSize, ThumbSize,
                                        GUILayout.Width(ThumbSize), GUILayout.Height(ThumbSize));
                                    GUI.DrawTexture(rect, tex2d, ScaleMode.ScaleToFit);
                                }
                                else
                                {
                                    GUILayout.Label($"[{tex.GetType().Name}  {tex.width}×{tex.height}]");
                                }
                            }
                            break;
                        }
                        default:
                            GUILayout.Label($"({propType})");
                            break;
                    }
                }
                catch (Exception e)
                {
                    GUILayout.Label($"(error: {e.Message})");
                }

                GUILayout.EndHorizontal();
            }

            // Keywords
            GUILayout.Space(6f);
            GUILayout.Label("<b>Enabled keywords:</b>", new GUIStyle(GUI.skin.label) { richText = true });
            try
            {
                var kws = mat.shaderKeywords;
                if (kws == null || kws.Length == 0)
                    GUILayout.Label("  (none)");
                else
                    foreach (var kw in kws)
                        GUILayout.Label("  " + kw);
            }
            catch { GUILayout.Label("  (unavailable)"); }

            GUILayout.EndScrollView();
        }

        static string TryGetName(UnityEngine.Object obj)
        {
            try { return obj != null ? obj.name : "(null)"; }
            catch { return "(unavailable)"; }
        }
    }
}
