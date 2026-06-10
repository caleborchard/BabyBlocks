using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;

namespace BabyBlocks
{
    // -- World-space material baking ------------------------------------------------
    // Props that use triplanar or world-position-mapped materials look wrong when turned
    // into Rigidbodies/Grabables/Hats because their shaders sample texture coordinates
    // from world space, so the texture stays fixed in the world while the object moves.
    // Rather than reverse-engineering each shader's properties, this photographs the mesh
    // exactly as the GPU currently shows it - using its real material/shader - from six
    // axis-aligned orthographic views, then builds a UV atlas with ONE dedicated cell per
    // triangle, sized to that triangle's real-world dimensions and filled by resampling
    // the photo of whichever view faces that triangle. Every atlas cell is fully covered
    // by real captured pixels (no shared/empty regions), so there's no stretching and no
    // "splotches" of filler color regardless of the mesh's shape. The result is applied
    // via a plain Standard material, so the baked look is shader-agnostic and frozen in
    // place.
    public static class MaterialBaker
    {
        // Stash of (original mesh, original materials) per MeshRenderer instance ID, so a
        // baked prop can be restored exactly on revert to Static. Also doubles as the
        // "already baked" guard - re-baking an already-baked atlas would feed it back
        // through the brightness normalization again, compounding exposure drift.
        static readonly Dictionary<int, (Mesh mesh, Material[] mats)> _bakeStash = new();

        // `propId` (the prop's addressable key) is optional - when given, an on-disk cache
        // (see MaterialBakeCache) is checked first for a previously-baked mesh+atlas for
        // this prop+material combination (see PropMetadataPanel.GetMaterialCacheKey), and
        // any renderers freshly baked by this call are written back to that cache for
        // future loads/bakes to reuse. Renderers covered by the cache are imported via
        // ImportBakedData before the GPU loop below, so its "already baked" guard
        // (_bakeStash) skips re-capturing them.
        public static void Bake(GameObject root, string propId = null)
        {
            string materialKey = null;
            List<BakedPartData> diskCache = null;
            if (!string.IsNullOrEmpty(propId))
            {
                materialKey = PropMetadataPanel.GetMaterialCacheKey(propId);
                diskCache = MaterialBakeCache.TryLoad(propId, materialKey);
                if (diskCache != null) ImportBakedData(root, diskCache);
            }

            var lightingOverride = OverrideLightingForBake();
            var newlyBaked = new List<MeshRenderer>();
            var prevScale = root.transform.localScale;
            try
            {
                // Bake as if the prop were at its base (unscaled) size. The mesh/atlas
                // (and the disk cache derived from them) are then independent of this
                // particular instance's scale - an instance left at base scale renders
                // correctly from the bake/cache, while an instance the user has scaled
                // up/down may look slightly off (texture/photo density mismatch) since
                // the capture was framed for the base size, not its current size.
                root.transform.localScale = Vector3.one;

                // Computed once per Bake() call rather than per child renderer - it scans
                // every Renderer in the scene, which is wasteful to repeat for each part of
                // a multi-part prop. The bake cameras for one part are destroyed before the
                // next part starts, so reusing the same layer across parts is safe.
                int drawLayer = FindUnusedLayer();

                foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    int key = mr.GetInstanceID();
                    if (_bakeStash.ContainsKey(key))
                        continue;

                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf == null) continue;
                    var srcMesh = mf.sharedMesh;
                    if (srcMesh == null) continue;

                    var mats = mr.sharedMaterials;
                    if (mats == null || mats.Length == 0) continue;

                    if (!TryReadMeshForBake(srcMesh, out var positions, out var normals, out var submeshTris))
                        continue;

                    _bakeStash[key] = (srcMesh, mr.sharedMaterials);

                    BakeOne(mr, mf, srcMesh, mats, positions, normals, submeshTris, lightingOverride, drawLayer);
                    newlyBaked.Add(mr);
                }
            }
            finally
            {
                root.transform.localScale = prevScale;
                RestoreLighting(lightingOverride);
            }

            if (!string.IsNullOrEmpty(propId))
            {
                if (diskCache == null)
                {
                    // No disk cache entry yet for this prop+material. Besides anything
                    // freshly GPU-baked above, `root` may already carry baked parts
                    // imported from a loaded .bbb save (LevelSaveLoad.ReadBakedData runs
                    // before this is called) - export everything in _bakeStash so a
                    // level load also seeds the disk cache, not just a fresh bake.
                    var allParts = ExportBakedData(root);
                    if (allParts.Count > 0)
                        MaterialBakeCache.Save(propId, materialKey, allParts);
                }
                else if (newlyBaked.Count > 0)
                {
                    var newParts = ExportBakedData(root, newlyBaked);
                    if (newParts.Count > 0)
                    {
                        diskCache.AddRange(newParts);
                        MaterialBakeCache.Save(propId, materialKey, diskCache);
                    }
                }
            }
        }

        public static void RestoreOriginal(GameObject root)
        {
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null) continue;
                int key = mr.GetInstanceID();
                if (!_bakeStash.TryGetValue(key, out var stash)) continue;
                mf.sharedMesh      = stash.mesh;
                mr.sharedMaterials = stash.mats;
                _bakeStash.Remove(key);
            }
        }

        // ---------------------------------------------------------------------------
        // Persisted bake data (save/load)
        // ---------------------------------------------------------------------------

        // Everything needed to rebuild one baked MeshRenderer's mesh+material without
        // re-running the GPU bake - see ExportBakedData/ImportBakedData.
        public sealed class BakedPartData
        {
            public string rendererPath;
            public Vector3[] positions;
            public Vector3[] normals;
            public Vector2[] uv;
            public int[] triangles;
            public byte[] atlasImage;
        }

        // Pulls the already-baked mesh/atlas back out of every baked MeshRenderer under
        // root, for the save file to persist (see LevelSaveLoad). Purely CPU-side reads of
        // data already produced by a prior Bake() - no GPU work. The atlas is JPG-encoded:
        // its alpha is always forced to 1 (see BuildPerTriangleAtlas/CreateBakedMaterial),
        // so the alpha channel carries no information and lossy RGB compression is a big
        // size win over PNG for these photographic captures.
        public static List<BakedPartData> ExportBakedData(GameObject root) => ExportBakedData(root, null);

        // `only`, when non-null, restricts the export to these specific renderers (used by
        // Bake() to export just the renderers it freshly baked, without re-encoding the
        // JPG atlases of renderers that were imported from a cache unchanged).
        public static List<BakedPartData> ExportBakedData(GameObject root, ICollection<MeshRenderer> only)
        {
            var result = new List<BakedPartData>();
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (only != null && !only.Contains(mr)) continue;

                if (!_bakeStash.ContainsKey(mr.GetInstanceID()))
                {
                    MelonLogger.Warning($"[BabyBlocks] ExportBakedData \"{mr.name}\": no _bakeStash entry (instanceId={mr.GetInstanceID()}, stashCount={_bakeStash.Count})");
                    continue;
                }

                var mf = mr.GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                var mat = (mr.sharedMaterials != null && mr.sharedMaterials.Length > 0) ? mr.sharedMaterials[0] : null;
                var atlas = GetBakedAtlasTexture(mat);
                if (mesh == null || atlas == null)
                {
                    MelonLogger.Warning($"[BabyBlocks] ExportBakedData \"{mr.name}\": skipped (mesh={(mesh != null)}, atlas={(atlas != null)}, mat={(mat != null ? mat.shader.name : "null")})");
                    continue;
                }

                result.Add(new BakedPartData
                {
                    rendererPath = GetRelativePath(root.transform, mr.transform),
                    positions    = mesh.vertices,
                    normals      = mesh.normals,
                    uv           = mesh.uv,
                    triangles    = mesh.triangles,
                    atlasImage   = atlas.EncodeToJPG(90),
                });
            }
            return result;
        }

        // Rebuilds each part's baked mesh/material directly from previously-exported data
        // (see ExportBakedData) and stashes the renderer's pre-bake mesh/materials exactly
        // like Bake() would - so Bake()'s "already baked" guard (_bakeStash) skips
        // recapturing these renderers, while any renderer NOT covered by `parts` (e.g. a
        // part added to the prop since the data was saved) is left for Bake() to capture
        // normally.
        public static void ImportBakedData(GameObject root, List<BakedPartData> parts)
        {
            if (root == null || parts == null) return;

            foreach (var part in parts)
            {
                var target = ResolvePath(root.transform, part.rendererPath);
                if (target == null) continue;

                var mr = target.GetComponent<MeshRenderer>();
                var mf = target.GetComponent<MeshFilter>();
                if (mr == null || mf == null) continue;

                int key = mr.GetInstanceID();
                if (_bakeStash.ContainsKey(key)) continue;

                var atlas = new Texture2D(2, 2, TextureFormat.RGBA32, false, true)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                if (!ImageConversion.LoadImage(atlas, part.atlasImage, true))
                {
                    UnityEngine.Object.Destroy(atlas);
                    continue;
                }

                var bakedMesh = new Mesh { name = (mf.sharedMesh != null ? mf.sharedMesh.name : "Mesh") + "_baked" };
                if (part.positions.Length > 65535)
                    bakedMesh.indexFormat = IndexFormat.UInt32;
                bakedMesh.vertices = part.positions;
                bakedMesh.normals  = part.normals;
                bakedMesh.uv       = part.uv;
                bakedMesh.SetTriangles(part.triangles, 0);
                bakedMesh.RecalculateBounds();

                _bakeStash[key] = (mf.sharedMesh, mr.sharedMaterials);

                mf.sharedMesh = bakedMesh;
                mr.sharedMaterials = new[] { CreateBakedMaterial(_bakeStash[key].mats, atlas) };
            }
        }

        // ---------------------------------------------------------------------------
        // Shared binary (de)serialization for BakedPartData lists - used by both the
        // .bbb save format (LevelSaveLoad) and the on-disk bake cache (MaterialBakeCache).
        // ---------------------------------------------------------------------------

        internal static void WritePartList(BinaryWriter bw, List<BakedPartData> parts)
        {
            bw.Write(parts.Count);
            foreach (var part in parts)
            {
                WriteStr(bw, part.rendererPath);

                int vCount = part.positions.Length;
                bw.Write(vCount);
                for (int v = 0; v < vCount; v++) { bw.Write(part.positions[v].x); bw.Write(part.positions[v].y); bw.Write(part.positions[v].z); }
                for (int v = 0; v < vCount; v++) { bw.Write(part.normals[v].x); bw.Write(part.normals[v].y); bw.Write(part.normals[v].z); }
                for (int v = 0; v < vCount; v++) { bw.Write(part.uv[v].x); bw.Write(part.uv[v].y); }

                bw.Write(part.triangles.Length);
                foreach (var tri in part.triangles) bw.Write(tri);

                bw.Write(part.atlasImage.Length);
                bw.Write(part.atlasImage);
            }
        }

        internal static List<BakedPartData> ReadPartList(BinaryReader br)
        {
            int partCount = br.ReadInt32();
            var parts = new List<BakedPartData>(partCount);
            for (int p = 0; p < partCount; p++)
            {
                var part = new BakedPartData { rendererPath = ReadStr(br) };

                int vCount = br.ReadInt32();
                part.positions = new Vector3[vCount];
                for (int v = 0; v < vCount; v++) part.positions[v] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

                part.normals = new Vector3[vCount];
                for (int v = 0; v < vCount; v++) part.normals[v] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

                part.uv = new Vector2[vCount];
                for (int v = 0; v < vCount; v++) part.uv[v] = new Vector2(br.ReadSingle(), br.ReadSingle());

                int triCount = br.ReadInt32();
                part.triangles = new int[triCount];
                for (int t = 0; t < triCount; t++) part.triangles[t] = br.ReadInt32();

                int atlasLen = br.ReadInt32();
                part.atlasImage = br.ReadBytes(atlasLen);

                parts.Add(part);
            }
            return parts;
        }

        internal static void WriteStr(BinaryWriter w, string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s ?? "");
            w.Write(bytes.Length);
            w.Write(bytes);
        }

        internal static string ReadStr(BinaryReader r)
        {
            int len = r.ReadInt32();
            return Encoding.UTF8.GetString(r.ReadBytes(len));
        }

        // Path from root down to target as sibling indices ("2/0/1"), stable across
        // sessions for a given prop's hierarchy (the addressable prefab is instantiated
        // identically every time), unlike GetInstanceID().
        static string GetRelativePath(Transform root, Transform target)
        {
            var indices = new List<int>();
            var t = target;
            while (t != root)
            {
                if (t == null) return null;
                indices.Add(t.GetSiblingIndex());
                t = t.parent;
            }
            indices.Reverse();
            return string.Join("/", indices);
        }

        static Transform ResolvePath(Transform root, string path)
        {
            var t = root;
            if (string.IsNullOrEmpty(path)) return t;
            foreach (var part in path.Split('/'))
            {
                if (t == null || !int.TryParse(part, out int idx) || idx < 0 || idx >= t.childCount) return null;
                t = t.GetChild(idx);
            }
            return t;
        }

        // ---------------------------------------------------------------------------
        // Per-object bake
        // ---------------------------------------------------------------------------

        static readonly Vector3[] BakeDirs = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        static readonly Vector3[] BakeUps  = { Vector3.up,    Vector3.up,   Vector3.forward, Vector3.forward, Vector3.up, Vector3.up };

        // Resolution of each directional source photo. These are intermediate captures -
        // every triangle resamples its own small region out of one of these - so this just
        // needs to be high enough that a triangle's region isn't blocky.
        const int CapturePx = 512;

        static void BakeOne(MeshRenderer mr, MeshFilter mf, Mesh srcMesh, Material[] mats,
            Vector3[] positions, Vector3[] normals, int[][] submeshTris, BakeLightingOverride lightingOverride, int drawLayer)
        {
            ClassifyTriangleDirections(positions, normals, submeshTris, mr.transform, out var triDir, out var dirUsed);

            var bounds   = mr.bounds;
            var camGOs   = new GameObject[6];
            var cams     = new Camera[6];
            var captures = new Color[6][];

            try
            {
                // Issue every direction's GPU renders first, then read all of them back in
                // a second pass below - see IssueCaptureRenders for why this ordering
                // matters for performance.
                for (int d = 0; d < 6; d++)
                {
                    if (!dirUsed[d]) continue;
                    camGOs[d] = CreateBakeCamera(bounds, BakeDirs[d], BakeUps[d], drawLayer, out cams[d]);
                    // Aim the bake "headlight" the same way as this direction's camera so the
                    // face it sees gets full, even, front-facing direct light.
                    lightingOverride.bakeLight.transform.rotation = Quaternion.LookRotation(-BakeDirs[d], BakeUps[d]);
                    IssueCaptureRenders(srcMesh, mats, mr.transform.localToWorldMatrix, cams[d], drawLayer, CapturePx, d);
                }

                for (int d = 0; d < 6; d++)
                {
                    if (!dirUsed[d]) continue;
                    captures[d] = ReadbackCapture(d, CapturePx);
                }

                var atlas = BuildPerTriangleAtlas(positions, submeshTris, mr.transform, triDir, cams, captures, CapturePx,
                    out var bakeUV);

                BuildBakedMeshData(positions, normals, submeshTris, bakeUV, out var bakePositions, out var bakeNormals, out var bakeUVOut, out var bakeTris);

                var bakedMesh = new Mesh { name = srcMesh.name + "_baked" };
                if (bakePositions.Length > 65535)
                    bakedMesh.indexFormat = IndexFormat.UInt32;
                bakedMesh.vertices = bakePositions;
                bakedMesh.normals  = bakeNormals;
                bakedMesh.uv       = bakeUVOut;
                bakedMesh.SetTriangles(bakeTris, 0);
                bakedMesh.RecalculateBounds();
                mf.sharedMesh = bakedMesh;

                mr.sharedMaterials = new[] { CreateBakedMaterial(mats, atlas) };

                MelonLogger.Msg($"[BabyBlocks] Baked \"{mr.name}\": {bakedMesh.triangles.Length / 3} tris, atlas {atlas.width}x{atlas.height}");
            }
            finally
            {
                for (int d = 0; d < 6; d++)
                {
                    if (camGOs[d] != null) UnityEngine.Object.Destroy(camGOs[d]);
                }
            }
        }

        // ---------------------------------------------------------------------------
        // Lighting override (fixed lighting for repeatable captures)
        // ---------------------------------------------------------------------------

        // The capture needs FIXED lighting to be repeatable/comparable across bakes. The
        // scene's actual lighting (sun/sky via Enviro, local reflection probes) varies
        // continuously with time of day and isn't fully controllable via a one-time
        // RenderSettings.ambientLight write (Enviro keeps overwriting ambientProbe), so
        // instead we temporarily disable every Light and ReflectionProbe in the scene and
        // force flat ambient + a neutral gray "reflection" for the duration of the bake,
        // then restore everything afterwards. This makes captures fully reproducible
        // regardless of time of day/weather.
        sealed class BakeLightingOverride
        {
            public Light[] lights;
            public bool[] lightWasEnabled;
            public ReflectionProbe[] probes;
            public bool[] probeWasEnabled;
            public AmbientMode prevAmbientMode;
            public Color prevAmbientLight;
            public DefaultReflectionMode prevReflectionMode;
            public Texture prevReflectionTexture;
            public float prevReflectionIntensity;
            public GameObject bakeLightGO;
            public Light bakeLight;
        }

        const float CaptureAmbient = 0.3f;
        const float BakeLightIntensity = 0.001f;

        static BakeLightingOverride OverrideLightingForBake()
        {
            var o = new BakeLightingOverride();

            o.lights = UnityEngine.Object.FindObjectsOfType<Light>();
            o.lightWasEnabled = new bool[o.lights.Length];
            for (int i = 0; i < o.lights.Length; i++)
            {
                o.lightWasEnabled[i] = o.lights[i].enabled;
                o.lights[i].enabled = false;
            }

            o.probes = UnityEngine.Object.FindObjectsOfType<ReflectionProbe>();
            o.probeWasEnabled = new bool[o.probes.Length];
            for (int i = 0; i < o.probes.Length; i++)
            {
                o.probeWasEnabled[i] = o.probes[i].enabled;
                o.probes[i].enabled = false;
            }

            o.prevAmbientMode  = RenderSettings.ambientMode;
            o.prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.white * CaptureAmbient;

            o.prevReflectionMode      = RenderSettings.defaultReflectionMode;
            o.prevReflectionTexture   = RenderSettings.customReflectionTexture;
            o.prevReflectionIntensity = RenderSettings.reflectionIntensity;
            RenderSettings.defaultReflectionMode   = DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = NeutralCubemap();
            RenderSettings.reflectionIntensity     = 0f;

            // Some shaders (e.g. MicroSplat's terrain/rock blend) barely respond to ambient
            // alone - with no direct light, even a pure-white surface came back ~17% gray.
            // Add one temporary white directional "headlight" that gets re-aimed to match
            // each bake camera's view direction (see BakeOne), so every captured face gets
            // the same full-on, front-facing direct light its shader expects, with no
            // per-direction seams.
            o.bakeLightGO = new GameObject("BabyBlocks_BakeLight");
            o.bakeLight = o.bakeLightGO.AddComponent<Light>();
            o.bakeLight.type      = LightType.Directional;
            o.bakeLight.color     = Color.white;
            o.bakeLight.intensity = BakeLightIntensity;
            o.bakeLight.shadows   = LightShadows.None;

            return o;
        }

        static void RestoreLighting(BakeLightingOverride o)
        {
            for (int i = 0; i < o.lights.Length; i++)
                if (o.lights[i] != null) o.lights[i].enabled = o.lightWasEnabled[i];
            for (int i = 0; i < o.probes.Length; i++)
                if (o.probes[i] != null) o.probes[i].enabled = o.probeWasEnabled[i];

            RenderSettings.ambientMode  = o.prevAmbientMode;
            RenderSettings.ambientLight = o.prevAmbientLight;

            RenderSettings.defaultReflectionMode   = o.prevReflectionMode;
            RenderSettings.customReflectionTexture = o.prevReflectionTexture;
            RenderSettings.reflectionIntensity     = o.prevReflectionIntensity;

            if (o.bakeLightGO != null) UnityEngine.Object.Destroy(o.bakeLightGO);
        }

        static Cubemap _neutralCubemap;
        static Cubemap NeutralCubemap()
        {
            if (_neutralCubemap == null)
            {
                _neutralCubemap = new Cubemap(4, TextureFormat.RGBA32, false) { name = "BabyBlocks_NeutralCubemap" };
                var px = new Color[4 * 4];
                var gray = new Color(0.5f, 0.5f, 0.5f, 1f);
                for (int i = 0; i < px.Length; i++) px[i] = gray;
                for (int f = 0; f < 6; f++)
                    _neutralCubemap.SetPixels(px, (CubemapFace)f);
                _neutralCubemap.Apply(false);
            }
            return _neutralCubemap;
        }

        // ---------------------------------------------------------------------------
        // Triangle direction classification + camera setup
        // ---------------------------------------------------------------------------

        // Picks one of six axis directions (+X,-X,+Y,-Y,+Z,-Z) for each triangle based on the
        // world-space direction its face points, so it can later sample the photo taken from
        // the camera that actually sees that face.
        //
        // Uses the triangle's own GEOMETRIC normal (cross product of its edges in world
        // space) rather than the mesh's stored vertex-normal attribute. Some meshes store
        // normals in a format/stream TryReadMeshForBake doesn't recognize and fall back to a
        // placeholder Vector3.up for every vertex - with vertex normals, that silently
        // classified every triangle as facing +Y. The geometric normal is always correct
        // regardless of the mesh's vertex format.
        static void ClassifyTriangleDirections(
            Vector3[] positions, Vector3[] normals, int[][] submeshTris, Transform transform,
            out int[] triDir, out bool[] dirUsed)
        {
            int totalTris = 0;
            foreach (var st in submeshTris) totalTris += st.Length / 3;
            triDir  = new int[totalTris];
            dirUsed = new bool[6];

            int ti = 0;
            foreach (var st in submeshTris)
            {
                for (int t = 0; t + 2 < st.Length; t += 3)
                {
                    int i0 = st[t], i1 = st[t + 1], i2 = st[t + 2];
                    Vector3 wp0 = transform.TransformPoint(positions[i0]);
                    Vector3 wp1 = transform.TransformPoint(positions[i1]);
                    Vector3 wp2 = transform.TransformPoint(positions[i2]);
                    Vector3 fn  = Vector3.Cross(wp1 - wp0, wp2 - wp0);

                    float ax = fn.x, ay = fn.y, az = fn.z;
                    float aax = Mathf.Abs(ax), aay = Mathf.Abs(ay), aaz = Mathf.Abs(az);
                    int d;
                    if (aax >= aay && aax >= aaz) d = ax >= 0 ? 0 : 1;
                    else if (aay >= aaz)          d = ay >= 0 ? 2 : 3;
                    else                          d = az >= 0 ? 4 : 5;

                    triDir[ti] = d;
                    dirUsed[d] = true;
                    ti++;
                }
            }
        }

        // A layer with no name (LayerMask.LayerToName == "") can still have real scene
        // renderers assigned to its numeric index. If our temporary bake camera's
        // cullingMask collides with such a layer, cam.Render() draws that scene geometry
        // too, baking the background behind the prop into its texture (looks like the baked
        // object is "see-through" to whatever was behind it). Scan actual renderers to find
        // a layer index nothing currently uses.
        static int FindUnusedLayer()
        {
            var used = new bool[32];
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                used[r.gameObject.layer] = true;
            for (int i = 31; i >= 0; i--)
                if (!used[i]) return i;
            return 31;
        }

        // Sets up a temporary orthographic camera from one of the six axis directions,
        // framed around the mesh's whole bounds so it sees the object's full silhouette
        // from that direction. The capture render target is square, so the camera always
        // uses a square viewport (aspect 1) too - using a non-square aspect here would get
        // silently reset to 1 the moment a square RenderTexture is assigned as
        // targetTexture, making the render and any WorldToViewportPoint-based UVs disagree.
        static GameObject CreateBakeCamera(Bounds worldBounds, Vector3 dir, Vector3 up, int drawLayer, out Camera cam)
        {
            var go = new GameObject("BabyBlocks_BakeCam");
            cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.cullingMask = 1 << drawLayer;
            // Capture in HDR so bright lighting/highlights aren't clamped to 1.0 before the
            // exposure rescale is computed (see BuildNormalizedPixels).
            cam.allowHDR = true;

            Vector3 right = Vector3.Cross(up, dir).normalized;
            Vector3 ext = worldBounds.extents;
            float extRight = Mathf.Max(Mathf.Abs(Vector3.Dot(ext, right)), 0.02f);
            float extUp    = Mathf.Max(Mathf.Abs(Vector3.Dot(ext, up)), 0.02f);

            float depth;
            if (Mathf.Abs(dir.x) > 0.5f)      depth = ext.x;
            else if (Mathf.Abs(dir.y) > 0.5f) depth = ext.y;
            else                              depth = ext.z;
            if (depth < 0.05f) depth = 0.05f;

            cam.orthographicSize = Mathf.Max(extRight, extUp) * 1.02f;
            cam.aspect = 1f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane  = depth * 2f + 0.5f;

            go.transform.position = worldBounds.center + dir.normalized * (depth + 0.25f);
            go.transform.rotation = Quaternion.LookRotation(-dir, up);

            return go;
        }

        // ---------------------------------------------------------------------------
        // Capture
        // ---------------------------------------------------------------------------

        // Issues the GPU draw+render commands for one direction's real and mask passes
        // (into that direction's own cached RenderTextures) WITHOUT reading the result
        // back. BakeOne issues all 6 directions like this in a first pass, then reads each
        // one back via ReadbackCapture in a second pass - giving the GPU a head start on
        // the whole queue before the CPU starts stalling on ReadPixels.
        static void IssueCaptureRenders(Mesh mesh, Material[] mats, Matrix4x4 localToWorld, Camera cam, int drawLayer, int size, int dir)
        {
            var rt = GetCaptureRT(dir, size);
            cam.targetTexture = rt;
            PrepareCullOffMPB();
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                var mat = mats[Mathf.Min(i, mats.Length - 1)];
                if (mat == null) continue;
                Graphics.DrawMesh(mesh, localToWorld, mat, drawLayer, cam, i, _bakeMPB);
            }
            cam.Render();

            // Render a second pass with a known-good plain white material to get a
            // silhouette mask independent of the real material's own alpha output. Some
            // shaders (e.g. terrain/rock blends) write low alpha for dark areas like mortar
            // lines or shadowed crevices - using the REAL capture's own alpha to decide
            // "background vs. surface" then misclassifies those dark-but-real pixels as
            // background and erases them. The mask only ever depends on our own material.
            var maskRt = GetMaskRT(dir, size);
            cam.targetTexture = maskRt;
            PrepareCullOffMPB();
            for (int i = 0; i < mesh.subMeshCount; i++)
                Graphics.DrawMesh(mesh, localToWorld, MaskMaterial(), drawLayer, cam, i, _bakeMPB);
            cam.Render();

            cam.targetTexture = null;
        }

        // Reads back one direction's real+mask RenderTextures (rendered earlier by
        // IssueCaptureRenders), normalizes/exposure-corrects the real capture against the
        // mask (see BuildNormalizedPixels), and flat-fills any remaining background gaps.
        static Color[] ReadbackCapture(int dir, int size)
        {
            var real = ReadRenderTexture(_captureRTs[dir], ref _captureTex, TextureFormat.RGBAHalf, size);
            var mask = ReadRenderTexture(_maskRTs[dir], ref _maskTex, TextureFormat.RGBA32, size);

            var pixels = BuildNormalizedPixels(real, mask);

            // The background (and any self-occluded gaps) is left transparent (alpha=0).
            // Each triangle resamples whatever region of this photo it projects onto, which
            // can land on a gap (silhouette edge, occluded area, or a triangle that's still
            // back-facing despite the Cull-off override above, or simply outside the real
            // silhouette when coverage is low). Now that the dedicated mask (above) keeps
            // dark-but-real surface pixels from being misclassified as background, these
            // gaps are small/edge-only, so a single flat fill with the capture's own
            // average color is enough - and is a single O(pixels) pass with no
            // neighbor-search/queue overhead.
            FillRemainingTransparent(pixels);

            return pixels;
        }

        // Draws the mesh's submeshes with their own materials and reads the camera's render
        // back into a Color[]. Uses an HDR float format so bright lighting/highlights
        // aren't clamped to 1.0 at capture time - BuildNormalizedPixels rescales the result
        // to CaptureTargetBrightness afterwards.
        static readonly MaterialPropertyBlock _bakeMPB = new MaterialPropertyBlock();

        // A plain white, fully unlit material used purely to render a silhouette mask (see
        // IssueCaptureRenders) - completely independent of whatever shader the prop's real
        // materials use AND of the bake lighting (BakeLightIntensity/CaptureAmbient), so its
        // pixels are always exactly white and the mask[i].r < 0.5f threshold (see
        // BuildNormalizedPixels) is reliable regardless of how the bake lighting is tuned.
        // Must be unlit: an earlier "white Standard" mask material was itself dimmed by
        // BakeLightIntensity, so when that intensity was lowered enough, the mask read as
        // "background" everywhere and the entire bake came out solid black.
        static Material _maskMat;
        static Material MaskMaterial()
        {
            if (_maskMat == null)
            {
                var shader = Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
                _maskMat = new Material(shader) { name = "BabyBlocks_BakeMask" };
                _maskMat.SetColor("_Color", Color.white);
                if (_maskMat.HasProperty("_Glossiness")) _maskMat.SetFloat("_Glossiness", 0f);
                if (_maskMat.HasProperty("_Metallic")) _maskMat.SetFloat("_Metallic", 0f);
            }
            return _maskMat;
        }

        // One render target per direction (instead of a single shared/reused RT) so all 6
        // directions' renders can be issued before any of them are read back - see
        // IssueCaptureRenders. The readback Texture2Ds, by contrast, are still single
        // shared instances reused across directions: GetPixels() copies their contents into
        // a Color[] before the next ReadPixels overwrites them, so sharing is safe and
        // avoids holding 12 CPU-side textures.
        //
        // The real and mask passes use differently-costed RT formats: the real capture
        // needs HDR (ARGBHalf, see CaptureTargetBrightness) but no longer uses MSAA - every
        // pixel is later bilinearly resampled into a (usually much smaller) atlas cell, so
        // 4x supersampling here was mostly wasted GPU work. The mask is a flat black/white
        // silhouette with no HDR range or fine detail to preserve, so it gets a plain
        // 8-bit, non-MSAA target - roughly halving the cost of 6 of the 12 passes.
        static readonly RenderTexture[] _captureRTs = new RenderTexture[6];
        static readonly RenderTexture[] _maskRTs = new RenderTexture[6];
        static Texture2D _captureTex;
        static Texture2D _maskTex;

        // Disables backface culling for the capture. ClassifyTriangleDirections picks a
        // direction from each triangle's geometric normal, but if that disagrees with
        // the mesh's actual winding order, the triangle is back-facing (and culled) from
        // its "own" camera - leaving its screen-space region transparent in that photo,
        // which later samples as black when its atlas cell is filled. Setting these
        // common cull-property names is a no-op for shaders whose Cull state isn't
        // driven by a property.
        static void PrepareCullOffMPB()
        {
            _bakeMPB.Clear();
            _bakeMPB.SetFloat("_Cull", (float)CullMode.Off);
            _bakeMPB.SetFloat("_CullMode", (float)CullMode.Off);
        }

        static RenderTexture GetCaptureRT(int dir, int size)
        {
            var rt = _captureRTs[dir];
            if (rt == null || rt.width != size)
            {
                if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
                rt = new RenderTexture(size, size, 16, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                rt.Create();
                _captureRTs[dir] = rt;
            }
            return rt;
        }

        static RenderTexture GetMaskRT(int dir, int size)
        {
            var rt = _maskRTs[dir];
            if (rt == null || rt.width != size)
            {
                if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
                rt = new RenderTexture(size, size, 16, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                rt.Create();
                _maskRTs[dir] = rt;
            }
            return rt;
        }

        // Reads a RenderTexture's pixels back to the CPU via a single shared, lazily
        // (re)created Texture2D - GetPixels() copies the data out before the next call
        // overwrites cacheTex, so reuse across directions/frames is safe.
        static Color[] ReadRenderTexture(RenderTexture rt, ref Texture2D cacheTex, TextureFormat format, int size)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            if (cacheTex == null || cacheTex.width != size || cacheTex.format != format)
            {
                if (cacheTex != null) UnityEngine.Object.Destroy(cacheTex);
                cacheTex = new Texture2D(size, size, format, false);
            }
            cacheTex.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
            cacheTex.Apply(false);
            RenderTexture.active = prev;
            return cacheTex.GetPixels();
        }

        // A tunable "how bright should this capture's average pixel look in the baked
        // atlas" - the real capture (under OverrideLightingForBake's fixed neutral
        // lighting) is rescaled by a single global factor so its average brightness lands
        // here, while every pixel keeps its relative brightness/contrast to its neighbors -
        // i.e. the photographed detail is preserved, just exposure-corrected. User-tuned;
        // do not change without new instruction.
        const float CaptureTargetBrightness = 0.15f;

        // Rescales the real capture by a single global exposure factor so its average
        // brightness (over pixels the mask marks as real surface) becomes
        // CaptureTargetBrightness, preserving all per-pixel relative detail/contrast. The
        // final value is clamped to [0,1] only here, once. `mask` (see RenderMaskToTexture)
        // - not `real`'s own alpha - decides which pixels are background, since some
        // shaders write low alpha for legitimately dark/shadowed surface pixels.
        static Color[] BuildNormalizedPixels(Color[] real, Color[] mask)
        {
            double sum = 0; int n = 0;
            for (int i = 0; i < real.Length; i++)
            {
                if (mask[i].r < 0.5f) continue;
                var c = real[i];
                sum += (c.r + c.g + c.b) / 3.0;
                n++;
            }
            float scale = (n > 0 && sum > 0) ? CaptureTargetBrightness / (float)(sum / n) : 1f;

            var result = new Color[real.Length];
            for (int i = 0; i < real.Length; i++)
            {
                if (mask[i].r < 0.5f) { result[i] = new Color(0, 0, 0, 0); continue; } // outside silhouette - leave transparent

                var c = real[i];
                result[i] = new Color(Mathf.Clamp01(c.r * scale), Mathf.Clamp01(c.g * scale), Mathf.Clamp01(c.b * scale), 1f);
            }
            return result;
        }

        // Repeatedly fills fully-transparent pixels with a neighboring opaque pixel's
        // color, growing the opaque silhouette outward by `iterations` pixels.
        static void DilateEdges(Color[] pixels, int size, int iterations)
        {
            for (int it = 0; it < iterations; it++)
            {
                var src = (Color[])pixels.Clone();
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int i = y * size + x;
                    if (src[i].a != 0) continue;

                    Color? fill = null;
                    if (x > 0 && src[i - 1].a != 0) fill = src[i - 1];
                    else if (x < size - 1 && src[i + 1].a != 0) fill = src[i + 1];
                    else if (y > 0 && src[i - size].a != 0) fill = src[i - size];
                    else if (y < size - 1 && src[i + size].a != 0) fill = src[i + size];

                    if (fill.HasValue)
                    {
                        var c = fill.Value;
                        c.a = 1f;
                        pixels[i] = c;
                    }
                }
            }
        }

        // Flat-fills any pixels DilateEdges couldn't reach (more than its iteration count
        // away from real coverage) with the average color of the real, covered pixels - so
        // every pixel in the photo is opaque before it's used as a resampling source.
        static void FillRemainingTransparent(Color[] pixels)
        {
            double r = 0, g = 0, b = 0; int n = 0;
            foreach (var c in pixels)
            {
                if (c.a < 0.5f) continue;
                r += c.r; g += c.g; b += c.b; n++;
            }
            if (n == 0) return;

            var avg = new Color((float)(r / n), (float)(g / n), (float)(b / n), 1f);
            for (int i = 0; i < pixels.Length; i++)
                if (pixels[i].a < 0.5f) pixels[i] = avg;
        }

        // ---------------------------------------------------------------------------
        // Per-triangle atlas packing
        // ---------------------------------------------------------------------------

        // How many atlas texels represent one world-space meter. Chosen so a typical small
        // prop (tens of cm) gets a few hundred texels per face - matches the resolution the
        // old fixed 512px-per-direction cells gave a full-size face.
        const float TexelsPerUnit = 1024f;

        // Gap, in texels, left between adjacent triangle cells so bilinear sampling near a
        // cell's edge doesn't bleed into its neighbor.
        const int AtlasPadding = 2;

        const int MinAtlasSize = 256;
        const int MaxAtlasSize = 2048;

        // One triangle's footprint in the atlas: its shape in its own 2D plane (in
        // object/local-space units, so the atlas is independent of the prop instance's
        // current scale - see BuildPerTriangleAtlas) plus the source direction/camera to
        // resample its color from.
        struct TriCell
        {
            public int triIndex;
            public Vector2 p0, p1, p2; // 2D coords in the triangle's own plane, local units, p0 = origin
            public float w, h;         // bounding size of (p0,p1,p2), local units
            public int dir;
            public int cellX, cellY, cellW, cellH; // assigned pixel rect (set by packer)
        }

        // Builds an atlas with one dedicated cell per triangle, each filled by resampling
        // that triangle's own region out of the directional photo it faces. Outputs the
        // per-(triangle,corner) UV into the atlas for BuildBakedMeshData.
        static Texture2D BuildPerTriangleAtlas(
            Vector3[] positions, int[][] submeshTris, Transform transform, int[] triDir,
            Camera[] cams, Color[][] captures, int captureSize,
            out Vector2[][] outUV)
        {
            int totalTris = triDir.Length;
            var cells = new TriCell[totalTris];
            outUV = new Vector2[totalTris][];
            for (int i = 0; i < totalTris; i++) outUV[i] = new Vector2[3];

            int ti = 0;
            foreach (var st in submeshTris)
            {
                for (int t = 0; t + 2 < st.Length; t += 3)
                {
                    // Cell shape/UVs are derived from the mesh's own LOCAL-space triangle
                    // (not world-transformed) so the atlas/UVs are the same regardless of
                    // the instance's current scale - otherwise a prop baked while scaled
                    // up would have its texture appear squished/stretched when that same
                    // bake (e.g. from MaterialBakeCache, which is keyed by prop+material
                    // only) is applied to a differently-scaled instance. The directional
                    // photo itself (sampled below via vp0/vp1/vp2) still uses world space,
                    // since it's a real capture of the scaled appearance.
                    Vector3 wp0 = positions[st[t]];
                    Vector3 wp1 = positions[st[t + 1]];
                    Vector3 wp2 = positions[st[t + 2]];

                    Vector3 e1raw = wp1 - wp0;
                    Vector3 e2raw = wp2 - wp0;
                    Vector3 normal = Vector3.Cross(e1raw, e2raw);
                    float len1 = e1raw.magnitude;

                    Vector2 p0 = Vector2.zero, p1, p2;
                    if (len1 > 1e-6f && normal.sqrMagnitude > 1e-12f)
                    {
                        Vector3 axisU = e1raw / len1;
                        Vector3 axisV = Vector3.Cross(normal.normalized, axisU);
                        p1 = new Vector2(len1, 0f);
                        p2 = new Vector2(Vector3.Dot(e2raw, axisU), Vector3.Dot(e2raw, axisV));
                    }
                    else
                    {
                        // Degenerate (zero-area) triangle - give it a tiny placeholder cell.
                        p1 = new Vector2(0.001f, 0f);
                        p2 = new Vector2(0f, 0.001f);
                    }

                    float minX = Mathf.Min(p0.x, p1.x, p2.x), maxX = Mathf.Max(p0.x, p1.x, p2.x);
                    float minY = Mathf.Min(p0.y, p1.y, p2.y), maxY = Mathf.Max(p0.y, p1.y, p2.y);
                    p0 -= new Vector2(minX, minY);
                    p1 -= new Vector2(minX, minY);
                    p2 -= new Vector2(minX, minY);

                    cells[ti] = new TriCell
                    {
                        triIndex = ti,
                        p0 = p0, p1 = p1, p2 = p2,
                        w = Mathf.Max(maxX - minX, 0.001f),
                        h = Mathf.Max(maxY - minY, 0.001f),
                        dir = triDir[ti],
                    };
                    ti++;
                }
            }

            int atlasSize = PackCells(cells, out float texelsPerUnit);

            var atlasPixels = new Color[atlasSize * atlasSize];

            // Compute each triangle's source viewport UVs once and rasterize its cell.
            ti = 0;
            foreach (var st in submeshTris)
            {
                for (int t = 0; t + 2 < st.Length; t += 3)
                {
                    ref var cell = ref cells[ti];
                    var cam = cams[cell.dir];
                    var src = captures[cell.dir];

                    Vector3 wp0 = transform.TransformPoint(positions[st[t]]);
                    Vector3 wp1 = transform.TransformPoint(positions[st[t + 1]]);
                    Vector3 wp2 = transform.TransformPoint(positions[st[t + 2]]);
                    Vector2 vp0 = cam.WorldToViewportPoint(wp0);
                    Vector2 vp1 = cam.WorldToViewportPoint(wp1);
                    Vector2 vp2 = cam.WorldToViewportPoint(wp2);

                    // Pixel-space triangle within this cell, with AtlasPadding/2 inset so the
                    // triangle never touches the cell's outer edge.
                    float pad = AtlasPadding * 0.5f;
                    Vector2 a = cell.p0 * texelsPerUnit + new Vector2(pad, pad);
                    Vector2 b = cell.p1 * texelsPerUnit + new Vector2(pad, pad);
                    Vector2 c = cell.p2 * texelsPerUnit + new Vector2(pad, pad);

                    RasterizeTriangle(atlasPixels, atlasSize, cell.cellX, cell.cellY, cell.cellW, cell.cellH,
                        a, b, c, vp0, vp1, vp2, src, captureSize);

                    // UVs for the 3 corners = their 2D cell-local position (same a/b/c used
                    // above), converted to atlas-space [0,1].
                    outUV[ti][0] = new Vector2((cell.cellX + a.x) / atlasSize, (cell.cellY + a.y) / atlasSize);
                    outUV[ti][1] = new Vector2((cell.cellX + b.x) / atlasSize, (cell.cellY + b.y) / atlasSize);
                    outUV[ti][2] = new Vector2((cell.cellX + c.x) / atlasSize, (cell.cellY + c.y) / atlasSize);

                    ti++;
                }
            }

            // Fill each cell's small leftover corner (the gap between the triangle and its
            // bounding rect) and the inter-cell padding by spreading each cell's own
            // triangle pixels outward a few texels - cheap since AtlasPadding is tiny.
            DilateEdges(atlasPixels, atlasSize, AtlasPadding + 2);

            var atlas = new Texture2D(atlasSize, atlasSize, TextureFormat.RGBA32, false, true);
            atlas.wrapMode = TextureWrapMode.Clamp;
            atlas.filterMode = FilterMode.Bilinear;
            var px32 = new Color32[atlasPixels.Length];
            for (int i = 0; i < px32.Length; i++)
            {
                var c = atlasPixels[i];
                c.a = 1f;
                px32[i] = c;
            }
            atlas.SetPixels32(px32);
            atlas.Apply(false);
            return atlas;
        }

        // Packs every triangle's bounding rect into a square atlas using a simple
        // shelf packer, picking the smallest texel density / atlas size (within bounds)
        // that fits everything.
        static int PackCells(TriCell[] cells, out float texelsPerUnit)
        {
            texelsPerUnit = TexelsPerUnit;

            for (int attempt = 0; attempt < 4; attempt++)
            {
                for (int atlasSize = MinAtlasSize; atlasSize <= MaxAtlasSize; atlasSize *= 2)
                {
                    if (TryPack(cells, atlasSize, texelsPerUnit))
                        return atlasSize;
                }
                // Nothing fit even at MaxAtlasSize - the prop is unusually large/detailed
                // relative to TexelsPerUnit. Halve the density and try again.
                texelsPerUnit *= 0.5f;
            }

            // Last resort: pack at MaxAtlasSize with whatever density we ended up at,
            // even if some cells overflow (TryPack still places everything it can; any
            // cell that can't fit on a fresh shelf is clamped into the atlas, which may
            // overlap - exceedingly unlikely given the halving above).
            TryPack(cells, MaxAtlasSize, texelsPerUnit);
            return MaxAtlasSize;
        }

        static bool TryPack(TriCell[] cells, int atlasSize, float texelsPerUnit)
        {
            // Sort by cell height descending for a tighter shelf pack. Work on indices so
            // the original triangle order (and outUV/cells correspondence) is preserved.
            var order = new int[cells.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (ia, ib) => cells[ib].h.CompareTo(cells[ia].h));

            int x = 0, y = 0, shelfH = 0;
            foreach (int i in order)
            {
                int cw = Mathf.Max(Mathf.CeilToInt(cells[i].w * texelsPerUnit) + AtlasPadding, 1 + AtlasPadding);
                int ch = Mathf.Max(Mathf.CeilToInt(cells[i].h * texelsPerUnit) + AtlasPadding, 1 + AtlasPadding);
                if (cw > atlasSize || ch > atlasSize) return false;

                if (x + cw > atlasSize) { x = 0; y += shelfH; shelfH = 0; }
                if (y + ch > atlasSize) return false;

                cells[i].cellX = x;
                cells[i].cellY = y;
                cells[i].cellW = cw;
                cells[i].cellH = ch;

                x += cw;
                if (ch > shelfH) shelfH = ch;
            }
            return true;
        }

        // Fills the pixels of `dst` covered by triangle (a,b,c) - all in cell-local pixel
        // coordinates relative to (cellX,cellY) - by barycentric-interpolating the source
        // viewport UVs (vpA/vpB/vpC, 0..1) and bilinearly sampling `src` (a captureSize x
        // captureSize image).
        static void RasterizeTriangle(Color[] dst, int atlasSize, int cellX, int cellY, int cellW, int cellH,
            Vector2 a, Vector2 b, Vector2 c, Vector2 vpA, Vector2 vpB, Vector2 vpC, Color[] src, int captureSize)
        {
            float denom = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
            if (Mathf.Abs(denom) < 1e-9f) return; // degenerate triangle - nothing to rasterize

            int minX = Mathf.Max(Mathf.FloorToInt(Mathf.Min(a.x, b.x, c.x)), 0);
            int maxX = Mathf.Min(Mathf.CeilToInt(Mathf.Max(a.x, b.x, c.x)), cellW - 1);
            int minY = Mathf.Max(Mathf.FloorToInt(Mathf.Min(a.y, b.y, c.y)), 0);
            int maxY = Mathf.Min(Mathf.CeilToInt(Mathf.Max(a.y, b.y, c.y)), cellH - 1);

            const float eps = 0.01f;
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float l1 = ((b.y - c.y) * (px - c.x) + (c.x - b.x) * (py - c.y)) / denom;
                    float l2 = ((c.y - a.y) * (px - c.x) + (a.x - c.x) * (py - c.y)) / denom;
                    float l3 = 1f - l1 - l2;
                    if (l1 < -eps || l2 < -eps || l3 < -eps) continue;

                    Vector2 vp = l1 * vpA + l2 * vpB + l3 * vpC;
                    Color sampled = SampleBilinear(src, captureSize, vp.x, vp.y);
                    sampled.a = 1f;

                    int ax = cellX + x, ay = cellY + y;
                    dst[ay * atlasSize + ax] = sampled;
                }
            }
        }

        // Bilinear-samples a captureSize x captureSize Color[] (row-major, y=0 at bottom -
        // matching Texture2D.GetPixels and Camera.WorldToViewportPoint) at normalized
        // viewport coordinates (u,v), clamping to the edges. The source has already been
        // fully dilated/filled (see CaptureMeshDirection), so every sample is opaque.
        static Color SampleBilinear(Color[] src, int size, float u, float v)
        {
            float fx = Mathf.Clamp01(u) * (size - 1);
            float fy = Mathf.Clamp01(v) * (size - 1);
            int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
            int x1 = Mathf.Min(x0 + 1, size - 1), y1 = Mathf.Min(y0 + 1, size - 1);
            float tx = fx - x0, ty = fy - y0;

            Color c00 = src[y0 * size + x0];
            Color c10 = src[y0 * size + x1];
            Color c01 = src[y1 * size + x0];
            Color c11 = src[y1 * size + x1];

            Color top = Color.Lerp(c00, c10, tx);
            Color bot = Color.Lerp(c01, c11, tx);
            return Color.Lerp(top, bot, ty);
        }

        // ---------------------------------------------------------------------------
        // Output mesh
        // ---------------------------------------------------------------------------

        // Duplicates every triangle into 3 unique vertices (each triangle owns its own
        // atlas cell, so corners can't share UVs across triangles) and assigns each the UV
        // computed by BuildPerTriangleAtlas.
        static void BuildBakedMeshData(
            Vector3[] positions, Vector3[] normals, int[][] submeshTris, Vector2[][] triUV,
            out Vector3[] outPositions, out Vector3[] outNormals, out Vector2[] outUV, out int[] outTris)
        {
            int totalTris    = triUV.Length;
            int totalCorners = totalTris * 3;
            outPositions = new Vector3[totalCorners];
            outNormals   = new Vector3[totalCorners];
            outUV        = new Vector2[totalCorners];
            outTris      = new int[totalCorners];

            int corner = 0, ti = 0;
            foreach (var st in submeshTris)
            {
                for (int t = 0; t + 2 < st.Length; t += 3)
                {
                    int[] idx = { st[t], st[t + 1], st[t + 2] };
                    for (int c = 0; c < 3; c++)
                    {
                        int vi = idx[c];
                        outPositions[corner + c] = positions[vi];
                        outNormals[corner + c]   = normals[vi];
                        outUV[corner + c]        = triUV[ti][c];
                        outTris[corner + c]      = corner + c;
                    }
                    corner += 3;
                    ti++;
                }
            }
        }

        // ---------------------------------------------------------------------------
        // Baked material
        // ---------------------------------------------------------------------------

        // Property names checked (in order) for the baked atlas - kept in sync with
        // CreateBakedMaterial's texture-assignment loop below. Most candidate shaders use
        // _MainTex, but if none of them are found and CreateBakedMaterial falls back to the
        // prop's own (e.g. URP) shader, the atlas may end up on _BaseMap/_BaseColorMap/etc
        // instead - Material.mainTexture only ever looks at _MainTex, so ExportBakedData
        // uses this instead to find the atlas regardless of which property it landed on.
        static readonly string[] BakedAtlasTexProps = { "_MainTex", "_BaseMap", "_BaseColorMap", "_UnlitColorMap" };

        static Texture2D GetBakedAtlasTexture(Material m)
        {
            if (m == null) return null;
            foreach (var p in BakedAtlasTexProps)
                if (m.HasProperty(p))
                    return m.GetTexture(p)?.TryCast<Texture2D>();
            return null;
        }

        // The captures are exposure-rescaled (see BuildNormalizedPixels) to a controlled
        // brightness, so the atlas is closer to a neutral pattern than a frozen lighting
        // snapshot. "Unlit/Texture" would otherwise be the ideal display shader (no re-lighting,
        // normal Cull Back/ZWrite On occlusion) but in this game it doesn't actually render
        // anything - confirmed see-through even with Cull Back + ZWrite On. "Standard" does
        // render/occlude correctly, so use it as plain albedo and let it re-light the atlas
        // normally under the current scene lighting, matching nearby (un-baked) objects.
        static Material CreateBakedMaterial(Material[] srcMats, Texture2D atlas)
        {
            var srcMat = (srcMats != null && srcMats.Length > 0) ? srcMats[0] : null;

            Shader sh = null;
            foreach (var name in new[]
            {
                "Standard",
                "Legacy Shaders/Diffuse",
                "Sprites/Default",
                "UI/Default",
                "Unlit/Color",
                "Unlit/Texture",
            })
            {
                sh = Shader.Find(name);
                if (sh != null) break;
            }
            if (sh == null && srcMat != null) sh = srcMat.shader;
            if (sh == null) return srcMat;

            var m = new Material(sh) { name = "BakedAtlas" };

            foreach (string p in BakedAtlasTexProps)
            {
                if (m.HasProperty(p))
                {
                    m.SetTexture(p, atlas);
                    m.SetTextureScale(p, Vector2.one);
                    m.SetTextureOffset(p, Vector2.zero);
                    break;
                }
            }
            foreach (string p in new[] { "_Color", "_BaseColor", "_UnlitColor" })
                if (m.HasProperty(p)) { m.SetColor(p, Color.white); break; }

            // Force a plain opaque, single-sided, depth-written surface regardless of what
            // mode the chosen shader defaults to.
            if (m.HasProperty("_Mode")) m.SetFloat("_Mode", 0f); // Standard: Opaque
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0f);
            if (m.HasProperty("_Metallic"))   m.SetFloat("_Metallic", 0f);
            if (m.HasProperty("_SrcBlend"))   m.SetInt("_SrcBlend", (int)BlendMode.One);
            if (m.HasProperty("_DstBlend"))   m.SetInt("_DstBlend", (int)BlendMode.Zero);
            if (m.HasProperty("_ZWrite"))     m.SetInt("_ZWrite", 1);
            if (m.HasProperty("_Cull"))       m.SetInt("_Cull", (int)CullMode.Back);
            m.DisableKeyword("_ALPHATEST_ON");
            m.DisableKeyword("_ALPHABLEND_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");

            m.renderQueue = 2000;

            return m;
        }

        // ---------------------------------------------------------------------------
        // Mesh reading (IL2Cpp-safe)
        // ---------------------------------------------------------------------------

        static bool TryReadMeshForBake(Mesh src, out Vector3[] positions, out Vector3[] normals, out int[][] submeshTris)
        {
            positions   = null;
            normals     = null;
            submeshTris = null;
            try
            {
                int normalFIdx  = -1;
                int floatCursor = 0;
                bool posOk = false;

                foreach (var a in src.GetVertexAttributes())
                {
                    if (a.stream != 0) { floatCursor += BakeAttrFloatStride(a); continue; }

                    if (a.attribute == VertexAttribute.Position
                     && a.format    == VertexAttributeFormat.Float32
                     && a.dimension == 3)
                        posOk = true;

                    if (a.format    == VertexAttributeFormat.Float32
                     && a.attribute == VertexAttribute.Normal
                     && a.dimension == 3)
                        normalFIdx = floatCursor;

                    floatCursor += BakeAttrFloatStride(a);
                }
                if (!posOk) return false;

                var vb    = src.GetVertexBuffer(0);
                int fPerV = vb.stride / 4;
                int vCnt  = src.vertexCount;
                var raw   = new Il2CppStructArray<float>(vCnt * fPerV);
                vb.GetData(raw.Cast<Il2CppSystem.Array>());
                vb.Release();

                positions = new Vector3[vCnt];
                for (int i = 0; i < vCnt; i++)
                {
                    int b = i * fPerV;
                    positions[i] = new Vector3(raw[b], raw[b + 1], raw[b + 2]);
                }

                normals = new Vector3[vCnt];
                if (normalFIdx >= 0)
                {
                    for (int i = 0; i < vCnt; i++)
                    {
                        int b = i * fPerV + normalFIdx;
                        normals[i] = new Vector3(raw[b], raw[b + 1], raw[b + 2]);
                    }
                }
                else
                {
                    for (int i = 0; i < vCnt; i++) normals[i] = Vector3.up;
                }

                var ib   = src.GetIndexBuffer();
                int iCnt = ib.count;
                var tris = new int[iCnt];
                if (ib.stride == 2)
                {
                    var buf = new Il2CppStructArray<ushort>(iCnt);
                    ib.GetData(buf.Cast<Il2CppSystem.Array>());
                    for (int i = 0; i < iCnt; i++) tris[i] = buf[i];
                }
                else
                {
                    var buf = new Il2CppStructArray<int>(iCnt);
                    ib.GetData(buf.Cast<Il2CppSystem.Array>());
                    for (int i = 0; i < iCnt; i++) tris[i] = buf[i];
                }
                ib.Release();

                int subs = src.subMeshCount;
                submeshTris = new int[subs][];
                for (int s = 0; s < subs; s++)
                {
                    var sm  = src.GetSubMesh(s);
                    int end = sm.indexStart + sm.indexCount;
                    var st  = new int[sm.indexCount];
                    for (int j = sm.indexStart; j < end; j++)
                        st[j - sm.indexStart] = tris[j];
                    submeshTris[s] = st;
                }
                return true;
            }
            catch { return false; }
        }

        // Returns how many float32 words |attr| occupies in the vertex buffer.
        static int BakeAttrFloatStride(VertexAttributeDescriptor attr)
        {
            int byteSize;
            switch (attr.format)
            {
                case VertexAttributeFormat.Float16:
                case VertexAttributeFormat.UNorm16:
                case VertexAttributeFormat.SNorm16:
                case VertexAttributeFormat.UInt16:
                case VertexAttributeFormat.SInt16:
                    byteSize = attr.dimension * 2; break;
                case VertexAttributeFormat.UNorm8:
                case VertexAttributeFormat.SNorm8:
                case VertexAttributeFormat.UInt8:
                case VertexAttributeFormat.SInt8:
                    byteSize = attr.dimension; break;
                default:
                    byteSize = attr.dimension * 4; break;
            }
            return (byteSize + 3) / 4;
        }
    }
}
