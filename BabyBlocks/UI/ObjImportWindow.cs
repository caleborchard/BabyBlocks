using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;

namespace BabyBlocks
{
    static class ObjImportWindow
    {
        // Flip to true to enable this feature. When false the window is never drawn
        // and ContainsPoint / IsTypingInUI are always false — zero runtime cost.
        public static bool Enabled = false;

        const float WinW    = 370f;
        const float WinH    = 150f;
        const float HeaderH = 24f;
        const float Pad     = 7f;

        const string PathField = "ObjImportPath";

        static Rect    _windowRect;
        static bool    _initialized;
        static bool    _dragging;
        static Vector2 _dragOffset;
        static string  _path   = "";
        static string  _status = "";
        static bool    _invertFaces = false;

        static readonly List<MeshCollider> _chunkColliders = new();

        public static bool IsTypingInUI  { get; private set; }

        public static bool ContainsPoint(Vector2 guiPoint) =>
            _initialized && Enabled && _windowRect.Contains(guiPoint);

        static void EnsureInit()
        {
            if (_initialized) return;
            _initialized = true;
            _windowRect  = new Rect(20f, 200f, WinW, WinH);
        }

        public static void DrawGUI(Event e)
        {
            if (!Enabled) return;
            EnsureInit();

            _windowRect.x = Mathf.Clamp(_windowRect.x, 0f, Screen.width  - WinW);
            _windowRect.y = Mathf.Clamp(_windowRect.y, 0f, Screen.height - WinH);

            GUI.color = new Color(0f, 0f, 0f, 0.72f);
            GUI.Box(_windowRect, "");
            GUI.color = Color.white;

            GUI.Label(new Rect(_windowRect.x + Pad, _windowRect.y + 4f, WinW - Pad * 2f, 18f),
                      "OBJ Importer");

            var headerRect = new Rect(_windowRect.x, _windowRect.y, WinW, HeaderH);
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
                if (e.type == EventType.MouseUp) _dragging = false;
            }

            float y  = _windowRect.y + HeaderH + Pad;
            float x  = _windowRect.x + Pad;
            float iw = WinW - Pad * 2f;

            GUI.Label(new Rect(x, y, iw, 18f), "OBJ path:");
            y += 20f;

            GUI.SetNextControlName(PathField);
            _path = GUI.TextField(new Rect(x, y, iw, 20f), _path);
            y += 26f;

            _invertFaces = GUI.Toggle(new Rect(x, y, iw, 18f), _invertFaces, "Invert faces");
            y += 22f;

            if (GUI.Button(new Rect(x, y, 80f, 22f), "Import"))
                TryImport();

            if (GUI.Button(new Rect(x + 88f, y, 80f, 22f), "Import All"))
                TryImportAll();

            if (!string.IsNullOrEmpty(_status))
                GUI.Label(new Rect(x + 176f, y + 2f, iw - 176f, 22f), _status);

            IsTypingInUI = GUI.GetNameOfFocusedControl() == PathField;
        }

        static void TryImport()
        {
            _status = "";
            string path = _path.Trim();
            if (!File.Exists(path)) { _status = "File not found."; return; }

            try
            {
                string name = Path.GetFileNameWithoutExtension(path);
                var info = BuildPropInfo(path, name);
                if (info == null) { _status = "No geometry found."; return; }

                var mgr = LevelEditorManager.Instance;
                if (mgr == null) { _status = "Editor not active."; return; }

                Vector3 spawnPos = new Vector3(0f, 200f, 256f);

                var leo = mgr.SpawnFromPropInfo(info, spawnPos);
                if (leo != null)
                {
                    leo.transform.localScale = Vector3.one * 0.5f;
                    leo.loopBaseScale = leo.transform.localScale;
                    RegisterChunkColliders(leo.gameObject);
                    PropInstanceServices.ApplySurfaceType(leo, "Rock");
                }
                _status = $"Spawned '{name}'.";
            }
            catch (Exception ex)
            {
                _status = "Error: " + ex.Message;
                MelonLogger.Error($"[ObjImport] {ex}");
            }
        }

        static void TryImportAll()
        {
            _status = "";
            string path = _path.Trim();
            string dir = null;
            if (Directory.Exists(path)) dir = path;
            else if (File.Exists(path)) dir = Path.GetDirectoryName(path);
            else { _status = "Directory not found."; return; }

            var files = Directory.GetFiles(dir, "*.obj");
            if (files.Length == 0) { _status = "No OBJ files found."; return; }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            try
            {
                var mgr = LevelEditorManager.Instance;
                if (mgr == null) { _status = "Editor not active."; return; }

                int imported = 0;
                for (int i = 0; i < files.Length; i++)
                {
                    string file = files[i];
                    string name = Path.GetFileNameWithoutExtension(file);
                    var info = BuildPropInfo(file, name);
                    if (info == null)
                    {
                        MelonLogger.Warning($"[ObjImport] Skipping '{file}' - no geometry.");
                        continue;
                    }

                    Vector3 spawnPos = new Vector3(i * 3f, 200f, 256f);
                    var leo = mgr.SpawnFromPropInfo(info, spawnPos);
                    if (leo != null)
                    {
                        leo.transform.localScale = Vector3.one * 0.5f;
                        leo.loopBaseScale = leo.transform.localScale;
                        RegisterChunkColliders(leo.gameObject);
                        PropInstanceServices.ApplySurfaceType(leo, "Rock");
                        imported++;
                    }
                }

                _status = $"Imported {imported}/{files.Length} objs.";
            }
            catch (Exception ex)
            {
                _status = "Error: " + ex.Message;
                MelonLogger.Error($"[ObjImport] {ex}");
            }
        }

        static void RegisterChunkColliders(GameObject root)
        {
            int colIdx = 0;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;

                var colGO = new GameObject($"ChunkCol_{colIdx++}");
                colGO.transform.SetParent(mf.transform.parent, false);
                colGO.layer = mf.gameObject.layer;

                var col = colGO.AddComponent<MeshCollider>();
                col.sharedMesh = mf.sharedMesh;
                _chunkColliders.Add(col);
            }
        }

        public static void UpdateChunkColliders()
        {
            // Clean up destroyed colliders.
            for (int i = _chunkColliders.Count - 1; i >= 0; i--)
                if (_chunkColliders[i] == null) _chunkColliders.RemoveAt(i);
        }


        // PropInfo builder

        static PropInfo BuildPropInfo(string objPath, string name)
        {
            string objDir = Path.GetDirectoryName(objPath) ?? "";
            var (groups, mtllibFile, posArr, uvArr, normArr) = ParseObjFile(objPath, objDir);

            groups.RemoveAll(g => g.Tris.Count == 0);
            if (groups.Count == 0) return null;

            var mtlDefs = new Dictionary<string, MtlDef>(StringComparer.OrdinalIgnoreCase);
            if (mtllibFile != null && File.Exists(mtllibFile))
                mtlDefs = ParseMtl(mtllibFile);

            var info = new PropInfo($"obj://{name}", name);
            foreach (var group in groups)
            {
                var (mesh, mat) = BuildPartMesh(group.Tris, posArr, uvArr, normArr, group.MatName, mtlDefs);
                if (mesh == null) continue;
                info.parts.Add(new PropMeshPart
                {
                    mesh          = mesh,
                    materials     = new[] { mat },
                    localPosition = Vector3.zero,
                    localRotation = Quaternion.identity,
                    localScale    = Vector3.one,
                });
            }

            if (!info.HasMesh) return null;
            info.isLoaded = true;
            return info;
        }


        // OBJ file parser

        class MatGroup
        {
            public string     MatName;
            public List<int[]> Tris = new(); // each int[3] is [posIdx, uvIdx, normIdx] (0-based; -1 = absent)
        }

        static (List<MatGroup>, string mtllib, List<Vector3> pos, List<Vector2> uv, List<Vector3> norm)
            ParseObjFile(string path, string objDir)
        {
            var positions = new List<Vector3>();
            var uvs       = new List<Vector2>();
            var normals   = new List<Vector3>();
            var groups    = new List<MatGroup>();
            string mtllib = null;
            MatGroup cur  = null;

            void EnsureGroup(string matName)
            {
                if (cur != null && cur.MatName == matName) return;
                cur = new MatGroup { MatName = matName };
                groups.Add(cur);
            }
            EnsureGroup("__default");

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int sp = line.IndexOf(' ');
                if (sp < 0) continue;
                string tok  = line.Substring(0, sp);
                string rest = line.Substring(sp + 1).Trim();

                switch (tok)
                {
                    case "v":
                    {
                        float[] f = SplitFloats(rest);
                        // Negate Z: OBJ right-hand → Unity left-hand.
                        positions.Add(new Vector3(f[0], f[1], -f[2]));
                        break;
                    }
                    case "vt":
                    {
                        float[] f = SplitFloats(rest);
                        uvs.Add(new Vector2(f[0], f[1]));
                        break;
                    }
                    case "vn":
                    {
                        float[] f = SplitFloats(rest);
                        normals.Add(new Vector3(f[0], f[1], -f[2]));
                        break;
                    }
                    case "usemtl":
                        EnsureGroup(rest);
                        break;
                    case "mtllib":
                        mtllib = Path.Combine(objDir, rest);
                        break;
                    case "f":
                        ParseFaceInto(rest, positions.Count, uvs.Count, normals.Count, cur.Tris);
                        break;
                }
            }

            return (groups, mtllib, positions, uvs, normals);
        }

        static void ParseFaceInto(string s, int posC, int uvC, int normC, List<int[]> tris)
        {
            var tokens = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var fv = new int[tokens.Length][];
            for (int i = 0; i < tokens.Length; i++)
                fv[i] = ParseFaceVertex(tokens[i], posC, uvC, normC);

            // Fan-triangulate. Standard (OBJ) winding preserved — gives correct outward normals
            // after Z-flip; culling is handled by CullMode.Front on the material.
            for (int i = 1; i < fv.Length - 1; i++)
            {
                tris.Add(fv[0]);
                tris.Add(fv[i]);
                tris.Add(fv[i + 1]);
            }
        }

        static int[] ParseFaceVertex(string tok, int posC, int uvC, int normC)
        {
            var p = tok.Split('/');
            return new[] { ParseIdx(p, 0, posC), ParseIdx(p, 1, uvC), ParseIdx(p, 2, normC) };
        }

        static int ParseIdx(string[] parts, int slot, int total)
        {
            if (slot >= parts.Length || string.IsNullOrEmpty(parts[slot])) return -1;
            int n = int.Parse(parts[slot], CultureInfo.InvariantCulture);
            return n < 0 ? total + n : n - 1; // OBJ is 1-based; negative indices are relative to end
        }

        static float[] SplitFloats(string s)
        {
            var parts = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                result[i] = float.Parse(parts[i], CultureInfo.InvariantCulture);
            return result;
        }


        // Per-group mesh builder

        static (Mesh mesh, Material mat) BuildPartMesh(
            List<int[]>      tris,
            List<Vector3>    posArr,
            List<Vector2>    uvArr,
            List<Vector3>    normArr,
            string           matName,
            Dictionary<string, MtlDef> mtlDefs)
        {
            bool hasUv   = uvArr   != null && uvArr.Count   > 0;
            bool hasNorm = normArr != null && normArr.Count > 0;

            var vertKey = new Dictionary<(int, int, int), int>();
            var vPos    = new List<Vector3>();
            var vUv     = new List<Vector2>();
            var vNorm   = new List<Vector3>();
            var idxs    = new List<int>(tris.Count);

            foreach (int[] fv in tris)
            {
                int pi = fv[0], ti = fv[1], ni = fv[2];
                var key = (pi, ti, ni);
                if (!vertKey.TryGetValue(key, out int idx))
                {
                    idx          = vPos.Count;
                    vertKey[key] = idx;
                    vPos.Add(pi >= 0 && pi < posArr.Count ? posArr[pi] : Vector3.zero);
                    vUv.Add(hasUv   && ti >= 0 && ti < uvArr.Count   ? uvArr[ti]   : Vector2.zero);
                    vNorm.Add(hasNorm && ni >= 0 && ni < normArr.Count ? normArr[ni] : Vector3.up);
                }
                idxs.Add(idx);
            }

            if (vPos.Count == 0 || idxs.Count == 0) return (null, null);

            var mesh = new Mesh { name = matName };
            if (vPos.Count > 65535)
                mesh.indexFormat = IndexFormat.UInt32;

            mesh.vertices  = vPos.ToArray();
            if (hasUv) mesh.uv = vUv.ToArray();

            // Triangles are reversed winding (see ParseFaceInto) for correct backface culling
            // after the Z-flip. We optionally invert the winding here when _invertFaces is set.
            mesh.triangles = idxs.ToArray();

            if (_invertFaces)
            {
                var triArr = mesh.triangles;
                for (int i = 0; i < triArr.Length; i += 3)
                {
                    int t1 = triArr[i + 1];
                    triArr[i + 1] = triArr[i + 2];
                    triArr[i + 2] = t1;
                }
                mesh.triangles = triArr;
            }

            if (hasNorm)
            {
                // Explicit per-vertex normals from the OBJ (already Z-flipped).
                var normalsToAssign = vNorm.ToArray();
                if (_invertFaces)
                {
                    for (int i = 0; i < normalsToAssign.Length; i++)
                        normalsToAssign[i] = -normalsToAssign[i];
                }
                mesh.normals = normalsToAssign;
            }
            else
            {
                // Recalculate normals. When not inverting faces we must negate the
                // result because the parser produces reversed winding by default.
                mesh.RecalculateNormals();
                var ns = mesh.normals;
                if (!_invertFaces)
                {
                    for (int i = 0; i < ns.Length; i++) ns[i] = -ns[i];
                }
                mesh.normals = ns;
            }

            mesh.RecalculateBounds();

            mtlDefs.TryGetValue(matName, out var def);
            return (mesh, BuildMaterial(def, matName));
        }


        // MTL parser

        struct MtlDef
        {
            public bool   Initialized;
            public Color  Diffuse;
            public Color  Specular;
            public float  Smoothness;
            public float  Alpha;
            public string DiffuseTexPath;
        }

        static MtlDef DefaultMtlDef() => new MtlDef
        {
            Initialized    = true,
            Diffuse        = Color.white,
            Specular       = new Color(0.2f, 0.2f, 0.2f),
            Smoothness     = 0.1f,
            Alpha          = 1f,
            DiffuseTexPath = null,
        };

        static Dictionary<string, MtlDef> ParseMtl(string mtlPath)
        {
            var result  = new Dictionary<string, MtlDef>(StringComparer.OrdinalIgnoreCase);
            string dir  = Path.GetDirectoryName(mtlPath) ?? "";
            string curName = null;
            MtlDef cur  = DefaultMtlDef();

            void Flush() { if (curName != null) result[curName] = cur; }

            foreach (string raw in File.ReadAllLines(mtlPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int sp = line.IndexOf(' ');
                if (sp < 0) continue;
                string tok  = line.Substring(0, sp).ToLower();
                string rest = line.Substring(sp + 1).Trim();

                switch (tok)
                {
                    case "newmtl":
                        Flush();
                        curName = rest;
                        cur = DefaultMtlDef();
                        break;
                    case "kd":
                    {
                        float[] f = SplitFloats(rest);
                        cur.Diffuse = new Color(f[0], f[1], f.Length > 2 ? f[2] : 0f, cur.Alpha);
                        break;
                    }
                    case "ks":
                    {
                        float[] f = SplitFloats(rest);
                        cur.Specular = new Color(f[0], f[1], f.Length > 2 ? f[2] : 0f);
                        break;
                    }
                    case "ns":
                        cur.Smoothness = Mathf.Clamp01(float.Parse(rest, CultureInfo.InvariantCulture) / 1000f);
                        break;
                    case "d":
                        cur.Alpha     = float.Parse(rest, CultureInfo.InvariantCulture);
                        cur.Diffuse.a = cur.Alpha;
                        break;
                    case "tr":
                        cur.Alpha     = 1f - float.Parse(rest, CultureInfo.InvariantCulture);
                        cur.Diffuse.a = cur.Alpha;
                        break;
                    case "map_kd":
                        cur.DiffuseTexPath = Path.IsPathRooted(rest)
                            ? rest
                            : Path.Combine(dir, rest);
                        break;
                }
            }
            Flush();
            return result;
        }

        static Material BuildMaterial(MtlDef def, string fallbackName)
        {
            if (!def.Initialized) def = DefaultMtlDef();

            var shader = Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit");
            var mat    = new Material(shader) { name = fallbackName };

            mat.color = def.Diffuse;
            mat.SetColor("_SpecColor",   def.Specular);
            mat.SetFloat("_Glossiness",  def.Smoothness);

            if (def.Alpha < 1f)
            {
                mat.SetFloat("_Mode",     3f);
                mat.SetInt("_SrcBlend",   (int)BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend",   (int)BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite",     0);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                mat.renderQueue = 3000;
            }

            if (!string.IsNullOrEmpty(def.DiffuseTexPath) && File.Exists(def.DiffuseTexPath))
            {
                try
                {
                    var tex = new Texture2D(2, 2);
                    tex.LoadImage(File.ReadAllBytes(def.DiffuseTexPath));
                    tex.filterMode = FilterMode.Point;
                    mat.mainTexture = tex;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[ObjImport] Texture '{def.DiffuseTexPath}': {ex.Message}");
                }
            }

            return mat;
        }
    }
}
