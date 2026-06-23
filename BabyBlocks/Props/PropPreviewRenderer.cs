using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;

namespace BabyBlocks
{
    // Off-screen isometric thumbnail renderer for prop library cards.
    // Call Request(info) to enqueue a prop; Get(propId) returns its Texture2D or null.
    // Call Update() once per frame to process one entry from the queue.
    // Completed renders are written to PropPreviewCache so they survive across sessions.
    static class PropPreviewRenderer
    {
        const int ThumbSize  = 256;
        const int JpgQuality = 85;

        static readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();
        static Material _fallbackMat;

        // Snow property names to zero across all suppression channels.
        // TVE variants: _TVE_SnowAmount (v4+), _TVE_SnowIntensity (v3), _TVE_ColorStageSnow.
        static readonly string[] _snowPropNames = {
            "_SnowAmount", "_SnowCover", "_Snow", "_SnowStrength", "_SnowWeight",
            "_TVE_SnowAmount", "_TVE_SeasonSnow", "_TVE_SnowIntensity", "_TVE_ColorStageSnow",
        };

        // Render geometry at a non-origin world position. TVE vertex data objects (wind, interaction)
        // are world-space scene objects and produce degenerate results at (0,0,0). The offset must
        // stay within the player's loaded terrain chunk so MicroSplat terrain materials remain valid.
        // (1000,50,1000) broke rocks — too far outside loaded terrain. (50,50,50) stays in range.
        static readonly Vector3 RenderWorldOffset = new Vector3(50f, 50f, 50f);

        static readonly Queue<PropInfo>               _queue = new();
        static readonly HashSet<string>               _seen  = new();
        static readonly Dictionary<string, Texture2D> _ready = new();

        public static void Request(PropInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.id)) return;
            if (_ready.ContainsKey(info.id)) return;
            if (_seen.Contains(info.id)) return;
            _seen.Add(info.id);
            _queue.Enqueue(info);
        }

        public static Texture2D Get(string propId) =>
            _ready.TryGetValue(propId, out var t) ? t : null;

        public static void Update()
        {
            if (_queue.Count > 0)
            {
                var info = _queue.Dequeue();
                if (info == null || string.IsNullOrEmpty(info.id)) return;

                var cached = PropPreviewCache.TryLoadTexture(info.id);
                if (cached != null) { _ready[info.id] = cached; return; }

                if (!info.HasMesh || !info.isLoaded)
                {
                    _seen.Remove(info.id);
                    return;
                }

                var tex = RenderThumbnail(info);
                if (tex == null) return;

                _ready[info.id] = tex;
                byte[] jpg = tex.EncodeToJPG(JpgQuality);
                if (jpg != null && jpg.Length > 0)
                    PropPreviewCache.Save(info.id, jpg);
                return;
            }

            // Process one material sphere per frame when the prop queue is idle.
            if (_matQueue.Count > 0)
            {
                var (matId, mat) = _matQueue.Dequeue();
                if (mat != null)
                {
                    var tex = RenderMaterialSphere(matId, mat);
                    if (tex != null)
                    {
                        _matReady[matId] = tex;
                        _matFailures.Remove(matId);
                    }
                    else
                    {
                        _matFailures.TryGetValue(matId, out int prev);
                        int fails = prev + 1;
                        _matFailures[matId] = fails;
                        BBLog.Msg($"[BB:MatSphere] id={matId} '{mat.name}' render FAILED (attempt {fails})");
                        if (fails < 5)
                            _matSeen.Remove(matId); // allow retry until giving up
                        // else: leave in _matSeen so it stops re-queuing
                    }
                }
            }
        }

        static Texture2D RenderThumbnail(PropInfo info)
        {
            PropMetadataStore.EnsureLoaded();
            PropMetadataStore._byId.TryGetValue(info.id, out var extra);

            BuildDisabledSets(extra, out var disabledSubPaths, out var disabledIndices);

            MaterialCatalog.EnsureMaterialList();
            // Mirror what drag-out does: ensure MicroSplat layer materials are built before resolving
            // overrides. This is a no-op once built (fast path); on first call it creates the layer
            // material clones from the live terrain shader — same as ApplyMaterialOverridesToRoot.
            MaterialCatalog.AddMicroSplatLayerMaterials();
            var effectiveMaterials = BuildEffectiveMaterials(info, extra, disabledSubPaths, disabledIndices);

            int drawLayer = MaterialBaker.FindUnusedLayer();
            if (drawLayer < 0) return null;

            var   bounds  = ComputeBounds(info, disabledSubPaths, disabledIndices);
            float maxDim  = Mathf.Max(bounds.extents.x, bounds.extents.y, bounds.extents.z) * 2f;
            if (maxDim < 0.001f) maxDim = 1f;

            // Holds props face away from the default camera angle — flip 180° around Y to face them.
            bool holdsCategory = string.Equals(PropMetadataStore.GetCategory(info.id), "Holds", StringComparison.OrdinalIgnoreCase);
            var   camDir  = holdsCategory ? new Vector3(1f, -1f, 1f).normalized : new Vector3(-1f, -1f, -1f).normalized;
            float camDist = maxDim * 3f;

            var camGO = new GameObject("BB_PreviewCam");
            var cam   = camGO.AddComponent<Camera>();
            cam.enabled       = false;
            cam.orthographic  = true;
            cam.clearFlags    = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.09f, 0.09f, 0.11f, 1f);
            cam.cullingMask   = 1 << drawLayer;
            cam.nearClipPlane = Mathf.Min(0.01f, camDist * 0.05f);
            cam.farClipPlane  = camDist * 4f;
            cam.aspect        = 1f;
            SetupCameraTransform(camGO, cam, bounds.center, camDir, camDist, bounds);

            var rt = new RenderTexture(ThumbSize, ThumbSize, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;

            var savedAmbientMode  = RenderSettings.ambientMode;
            var savedAmbientLight = RenderSettings.ambientLight;
            var sceneLights = UnityEngine.Object.FindObjectsOfType<Light>();
            var wasEnabled  = new bool[sceneLights.Length];
            for (int i = 0; i < sceneLights.Length; i++)
            {
                wasEnabled[i] = sceneLights[i].enabled;
                sceneLights[i].enabled = false;
            }
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.15f, 0.15f, 0.18f);

            var lightGO  = new GameObject("BB_PreviewLight");
            var keyLight = lightGO.AddComponent<Light>();
            keyLight.type      = LightType.Directional;
            keyLight.intensity = 1.2f;
            keyLight.color     = new Color(1f, 0.97f, 0.92f);
            keyLight.shadows   = LightShadows.None;
            lightGO.transform.rotation = camGO.transform.rotation;

            var rimGO    = new GameObject("BB_PreviewRim");
            var rimLight = rimGO.AddComponent<Light>();
            rimLight.type      = LightType.Directional;
            rimLight.intensity = 0.25f;
            rimLight.color     = new Color(0.6f, 0.7f, 1f);
            rimLight.shadows   = LightShadows.None;
            rimGO.transform.rotation = Quaternion.LookRotation(-camGO.transform.forward, camGO.transform.up);

            // Per-render clone cache: keyed by original material → temp clone with cull/snow overrides.
            // Clones are set at material level (not MPB) so shaders that don't declare
            // [PerRendererData] on _Cull/_SnowAmount still receive the correct values.
            var matClones = new Dictionary<Material, Material>();

            // Channel 1 — Global: zero snow globals before cam.Render().
            // Also injected via CommandBuffer (Channel 4) which fires AFTER any Camera.onPreRender
            // callbacks the game's snow/TVE manager may have registered — those callbacks reset
            // globals to the game's current snow level, overriding our SetGlobalFloat here.
            // The CommandBuffer runs at BeforeForwardOpaque, after all onPreRender hooks, so it wins.
            var savedSnowGlobals = new float[_snowPropNames.Length];
            for (int i = 0; i < _snowPropNames.Length; i++)
            {
                savedSnowGlobals[i] = Shader.GetGlobalFloat(_snowPropNames[i]);
                Shader.SetGlobalFloat(_snowPropNames[i], 0f);
            }

            // Channel 4 — CommandBuffer: sets globals at BeforeForwardOpaque, which runs after all
            // Camera.onPreRender callbacks have fired. This is the last chance to override globals
            // before opaque shaders execute, and defeats game-side per-camera snow reset logic.
            var snowCB = new CommandBuffer { name = "BB_SnowSuppress" };
            foreach (var name in _snowPropNames)
                snowCB.SetGlobalFloat(name, 0f);
            snowCB.SetGlobalVector("_SnowHeightAngleRange", new Vector4(1000000f, 1f, 0f, 1f));
            cam.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, snowCB);
            cam.AddCommandBuffer(CameraEvent.BeforeGBuffer,       snowCB);

            Texture2D result = null;
            var prevActive = RenderTexture.active;
            try
            {
                IssueDraws(info, disabledSubPaths, disabledIndices, effectiveMaterials, drawLayer, cam, matClones);
                cam.Render();

                RenderTexture.active = rt;
                result = new Texture2D(ThumbSize, ThumbSize, TextureFormat.RGB24, false);
                result.filterMode = FilterMode.Bilinear;
                result.ReadPixels(new Rect(0, 0, ThumbSize, ThumbSize), 0, 0);
                result.Apply();
            }
            catch (System.Exception e)
            {
                MelonLogger.Warning($"[PropPreviews] Render failed for \"{info.id}\": {e.Message}");
                if (result != null) { UnityEngine.Object.Destroy(result); result = null; }
            }
            finally
            {
                RenderTexture.active = prevActive;

                RenderSettings.ambientMode  = savedAmbientMode;
                RenderSettings.ambientLight = savedAmbientLight;
                for (int i = 0; i < sceneLights.Length; i++)
                    if (sceneLights[i] != null) sceneLights[i].enabled = wasEnabled[i];

                UnityEngine.Object.Destroy(lightGO);
                UnityEngine.Object.Destroy(rimGO);

                cam.RemoveAllCommandBuffers();
                snowCB.Release();

                cam.targetTexture = null;
                UnityEngine.Object.Destroy(camGO);
                rt.Release();
                UnityEngine.Object.Destroy(rt);

                foreach (var clone in matClones.Values)
                    if (clone != null) UnityEngine.Object.Destroy(clone);

                // Restore global snow properties.
                for (int i = 0; i < _snowPropNames.Length; i++)
                    Shader.SetGlobalFloat(_snowPropNames[i], savedSnowGlobals[i]);
            }

            return result;
        }

        // ---- Material clone helper ----

        // Returns a per-render clone of `src` with CullMode.Back and snow suppression applied
        // directly on the material (not via MPB). MPB cannot override properties like _Cull or
        // _SnowAmount unless the shader declares them [PerRendererData], which game shaders don't.
        // The clone cache avoids re-cloning the same material for multiple submeshes/parts.
        static Material GetOrCloneForRender(Material src, Dictionary<Material, Material> cache)
        {
            if (src == null) return GetOrCreateFallback();
            if (cache.TryGetValue(src, out var existing)) return existing;

            var clone = new Material(src);

            // Force CullMode.Back at material level — MPB alone cannot override shader render state.
            // TVE shaders use _RenderCull (not _Cull) — Cull [_RenderCull] in their ShaderLab passes.
            clone.SetFloat("_Cull",       (float)CullMode.Back);
            clone.SetFloat("_CullMode",   (float)CullMode.Back);
            clone.SetFloat("_RenderCull", (float)CullMode.Back);

            // Disable keywords that would select shader variants requiring external GPU context:
            //   _SNOW             → Better Lit / standard snow pass
            //   EFFECT_SUBSURFACE → TVE; causes back-face glow on double-sided leaves
            //   MUDBUN_PROCEDURAL → MudBun compute-shader path; needs scene-side GPU buffers that
            //                       don't exist in our off-screen render, causing pink/error output
            clone.DisableKeyword("_SNOW");
            clone.DisableKeyword("EFFECT_SUBSURFACE");
            clone.DisableKeyword("MUDBUN_PROCEDURAL");

            // Belt-and-suspenders float zeroing (covers shaders that do gate on these floats).
            foreach (var name in _snowPropNames)
                clone.SetFloat(name, 0f);
            clone.SetVector("_SnowHeightAngleRange", new Vector4(1000000f, 1f, 0f, 1f));

            // Diagnostic: if SetFloat silently failed (IL2CPP edge case), warn once.
            float verify = clone.GetFloat("_SnowAmount");
            if (verify > 0.001f)
                MelonLogger.Warning($"[PropPreviews] clone._SnowAmount SetFloat failed for \"{src.name}\" — reads back {verify:F3}; clone={clone.GetInstanceID()}");

            cache[src] = clone;
            return clone;
        }

        // ---- Camera helpers ----

        static void SetupCameraTransform(GameObject camGO, Camera cam, Vector3 center, Vector3 camDir, float camDist, Bounds bounds)
        {
            // Apply world offset so geometry and camera sit at RenderWorldOffset, not at origin.
            var worldCenter = center + RenderWorldOffset;
            camGO.transform.position = worldCenter - camDir * camDist;
            camGO.transform.LookAt(worldCenter);

            var bMin     = bounds.min;
            var bMax     = bounds.max;
            var camRight = camGO.transform.right;
            var camUp    = camGO.transform.up;
            float minR = float.MaxValue, maxR = float.MinValue;
            float minU = float.MaxValue, maxU = float.MinValue;
            for (int xi = 0; xi < 2; xi++)
            for (int yi = 0; yi < 2; yi++)
            for (int zi = 0; zi < 2; zi++)
            {
                var c = new Vector3(
                    xi == 0 ? bMin.x : bMax.x,
                    yi == 0 ? bMin.y : bMax.y,
                    zi == 0 ? bMin.z : bMax.z);
                float r = Vector3.Dot(c, camRight);
                float u = Vector3.Dot(c, camUp);
                if (r < minR) minR = r;
                if (r > maxR) maxR = r;
                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
            }
            float projHalf = Mathf.Max((maxR - minR) * 0.5f, (maxU - minU) * 0.5f);
            if (projHalf < 0.001f) projHalf = 0.5f;
            cam.orthographicSize = projHalf * 1.12f;
        }

        // ---- Disabled set parsing ----

        // Parses PropExtraInfo.disabledRenderers into two lookup structures:
        //   disabledIndices — set of integer part indices for "Part_N" format paths
        //                     (LEO-wrapped props name sub-objects "Part_0", "Part_1", …)
        //   disabledSubPaths — set of path substrings for older/non-LEO path format
        static void BuildDisabledSets(PropExtraInfo extra,
            out HashSet<string> disabledSubPaths, out HashSet<int> disabledIndices)
        {
            disabledSubPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            disabledIndices  = new HashSet<int>();

            if (extra?.disabledRenderers == null) return;

            foreach (var path in extra.disabledRenderers)
            {
                if (string.IsNullOrEmpty(path)) continue;
                int slash = path.IndexOf('/');
                string sub = slash >= 0 ? path.Substring(slash + 1) : path;
                if (string.IsNullOrEmpty(sub)) continue;

                // "Part_N" → index-based disable (matches info.parts[N])
                if (sub.StartsWith("Part_", System.StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(sub.Substring(5), System.Globalization.NumberStyles.Integer,
                                    System.Globalization.CultureInfo.InvariantCulture, out int idx))
                    disabledIndices.Add(idx);
                else
                    disabledSubPaths.Add(sub);
            }
        }

        static bool IsPartDisabled(int partIndex, PropMeshPart part,
            HashSet<string> disabledSubPaths, HashSet<int> disabledIndices)
        {
            if (disabledIndices.Contains(partIndex)) return true;
            if (disabledSubPaths.Count == 0) return false;
            if (string.IsNullOrEmpty(part.rendererSubPath)) return false;
            if (disabledSubPaths.Contains(part.rendererSubPath)) return true;
            // Prefix match: renderer is a child of a disabled parent object.
            foreach (var disabled in disabledSubPaths)
                if (part.rendererSubPath.StartsWith(disabled + "/", System.StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // ---- Draw issuing ----

        static void IssueDraws(PropInfo info, HashSet<string> disabledSubPaths, HashSet<int> disabledIndices,
            Material[][] effectiveMaterials, int drawLayer, Camera cam, Dictionary<Material, Material> matClones)
        {
            // Channel 2 — MPB: zero snow per-draw for shaders that declare snow as [PerRendererData].
            // This took precedence over per-material in version 8 and must be restored.
            _mpb.Clear();
            foreach (var name in _snowPropNames)
                _mpb.SetFloat(name, 0f);
            _mpb.SetVector("_SnowHeightAngleRange", new Vector4(1000000f, 1f, 0f, 1f));

            for (int pi = 0; pi < info.parts.Count; pi++)
            {
                var part = info.parts[pi];
                if (part.mesh == null) continue;
                if (IsPartDisabled(pi, part, disabledSubPaths, disabledIndices)) continue;

                var scale = part.localScale == Vector3.zero ? Vector3.one : part.localScale;
                var pos   = part.localPosition + RenderWorldOffset;
                var mtx   = Matrix4x4.TRS(pos, part.localRotation, scale);
                var mats  = (effectiveMaterials != null && pi < effectiveMaterials.Length)
                    ? effectiveMaterials[pi] : part.materials;

                for (int sub = 0; sub < part.mesh.subMeshCount; sub++)
                {
                    Material src = (mats != null && sub < mats.Length) ? mats[sub] : null;
                    var renderMat = GetOrCloneForRender(src, matClones);
                    if (renderMat == null) continue;
                    Graphics.DrawMesh(part.mesh, mtx, renderMat, drawLayer, cam, sub, _mpb);
                }
            }
        }

        // ---- Click-time diagnostics ----

        public static void LogBoundsDiagnostics(PropInfo info)
        {
            if (info == null) return;
            for (int pi = 0; pi < info.parts.Count; pi++)
            {
                var p = info.parts[pi];
                if (p.mesh == null)
                {
                    MelonLogger.Msg($"[PropPreviews]   bounds part[{pi}] mesh=null");
                    continue;
                }
                var mb = p.mesh.bounds;
                MelonLogger.Msg($"[PropPreviews]   bounds part[{pi}] localPos={p.localPosition} scale={p.localScale} meshCenter={mb.center} meshSize={mb.size}");
            }
            var dummy = new HashSet<string>();
            var dummyIdx = new HashSet<int>();
            var b2 = ComputeBounds(info, dummy, dummyIdx);
            float maxDim = Mathf.Max(b2.extents.x, b2.extents.y, b2.extents.z) * 2f;
            MelonLogger.Msg($"[PropPreviews]   bounds result: center={b2.center} size={b2.size} maxDim={maxDim:F2} orthoSize={maxDim * 3f * 0.5f * 1.12f:F2}");
        }

        static Material GetOrCreateFallback()
        {
            if (_fallbackMat != null) return _fallbackMat;
            var shader = Shader.Find("Standard");
            if (shader == null) return null;
            _fallbackMat = new Material(shader);
            if (_fallbackMat.HasProperty("_Color"))
                _fallbackMat.SetColor("_Color", new Color(1f, 0f, 1f));
            if (_fallbackMat.HasProperty("_EmissionColor"))
            {
                _fallbackMat.EnableKeyword("_EMISSION");
                _fallbackMat.SetColor("_EmissionColor", new Color(0.4f, 0f, 0.4f));
            }
            return _fallbackMat;
        }

        // ---- Material overrides ----

        // Resolves a material and evicts Standard/no-texture placeholders, retrying once so that
        // the source-prop scan path can find the real material on the same call.
        static Material ResolveNonPlaceholder(string matName, string sourcePropId)
        {
            var mat = MaterialCatalog.ResolveMaterial(matName, sourcePropId);
            if (mat != null && mat.shader != null && mat.shader.name == "Standard" && mat.mainTexture == null)
            {
                MaterialCatalog.MaterialByName.Remove(matName);
                MaterialCatalog.VerifiedSourceMaterials.Remove(matName);
                mat = MaterialCatalog.ResolveMaterial(matName, sourcePropId);
            }
            return mat;
        }

        static Material[][] BuildEffectiveMaterials(PropInfo info, PropExtraInfo extra,
            HashSet<string> disabledSubPaths, HashSet<int> disabledIndices)
        {
            Material   singleOverride   = null;
            Material[] perSlotOverrides = null;

            if (extra != null)
            {
                if (extra.perSlotMaterialOverrides != null && extra.perSlotMaterialOverrides.Count > 0)
                {
                    perSlotOverrides = new Material[extra.perSlotMaterialOverrides.Count];
                    for (int s = 0; s < extra.perSlotMaterialOverrides.Count; s++)
                    {
                        string name = extra.perSlotMaterialOverrides[s];
                        if (!string.IsNullOrEmpty(name)
                            && !string.Equals(name, PropMetadataStore.NoOverrideLabel, StringComparison.OrdinalIgnoreCase))
                            perSlotOverrides[s] = ResolveNonPlaceholder(name, extra.materialSourcePropId);
                    }
                }
                else if (!string.IsNullOrEmpty(extra.overrideMaterialId)
                    && !string.Equals(extra.overrideMaterialId, PropMetadataStore.NoOverrideLabel, StringComparison.OrdinalIgnoreCase))
                {
                    singleOverride = ResolveNonPlaceholder(extra.overrideMaterialId, extra.materialSourcePropId);
                    // Suppress [MicroSplat] warnings — those materials require terrain to be loaded
                    // and are retried automatically once the terrain shader becomes available.
                    if (singleOverride == null
                        && !extra.overrideMaterialId.StartsWith("[MicroSplat]", StringComparison.Ordinal))
                        MelonLogger.Warning($"[PropPreviews] ResolveMaterial(\"{extra.overrideMaterialId}\", \"{extra.materialSourcePropId}\") → null for \"{info.id}\"");
                }
            }

            bool needsOverride = singleOverride != null || perSlotOverrides != null;
            if (!needsOverride) return null;

            var result = new Material[info.parts.Count][];
            for (int pi = 0; pi < info.parts.Count; pi++)
            {
                var part = info.parts[pi];
                if (IsPartDisabled(pi, part, disabledSubPaths, disabledIndices)) { result[pi] = null; continue; }

                var baseMats  = part.materials;
                int baseCount = baseMats != null ? baseMats.Length : 0;
                int outCount  = baseCount;
                if (perSlotOverrides != null) outCount = Mathf.Max(outCount, perSlotOverrides.Length);
                if (singleOverride   != null) outCount = Mathf.Max(outCount, 1);
                if (outCount == 0) { result[pi] = null; continue; }

                var outMats = new Material[outCount];
                for (int s = 0; s < outCount; s++)
                {
                    Material mat = (baseMats != null && s < baseCount) ? baseMats[s] : null;
                    if (perSlotOverrides != null && s < perSlotOverrides.Length && perSlotOverrides[s] != null)
                        mat = perSlotOverrides[s];
                    else if (singleOverride != null)
                        mat = singleOverride;
                    outMats[s] = mat;
                }
                result[pi] = outMats;
            }
            return result;
        }

        // ---- Material sphere previews ----

        static Mesh _sphereMesh;
        static readonly Queue<(int id, Material mat)>    _matQueue    = new();
        static readonly HashSet<int>                     _matSeen     = new();
        static readonly Dictionary<int, Texture2D>       _matReady    = new();
        static readonly Dictionary<int, int>             _matFailures = new(); // per-id render failure count
        static readonly Dictionary<int, string>          _matId2Name  = new(); // name lookup for invalidation

        public static void RequestMaterialSphere(int id, Material mat)
        {
            if (mat == null) return;
            if (_matReady.ContainsKey(id)) return;
            if (_matSeen.Contains(id)) return;
            _matSeen.Add(id);
            _matId2Name[id] = mat.name;
            _matQueue.Enqueue((id, mat));
        }

        public static void InvalidateMicroSplatSpheres()
        {
            var toRemove = new System.Collections.Generic.List<int>();
            foreach (var kv in _matId2Name)
                if (kv.Value.StartsWith("[MicroSplat]", StringComparison.Ordinal))
                    toRemove.Add(kv.Key);
            foreach (var id in toRemove)
                InvalidateMaterialSphere(id);
        }

        public static Texture2D GetMaterialSphere(int id) =>
            _matReady.TryGetValue(id, out var t) ? t : null;

        public static bool IsMaterialSeen(int id) => _matSeen.Contains(id);
        public static bool IsMaterialReady(int id) => _matReady.ContainsKey(id);

        public static void InvalidateMaterialSphere(int id)
        {
            _matReady.Remove(id);
            _matSeen.Remove(id);
            _matFailures.Remove(id);
        }

        static Mesh GetSphereMesh()
        {
            if (_sphereMesh != null) return _sphereMesh;
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _sphereMesh = go.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.Destroy(go);
            return _sphereMesh;
        }

        static Texture2D RenderMaterialSphere(int matId, Material mat)
        {
            var mesh = GetSphereMesh();
            if (mesh == null) { BBLog.Msg($"[BB:MatSphere] id={matId} GetSphereMesh returned null"); return null; }

            int drawLayer = MaterialBaker.FindUnusedLayer();
            if (drawLayer < 0) { BBLog.Msg($"[BB:MatSphere] id={matId} no unused render layer available"); return null; }

            var   camDir  = new Vector3(-1f, -1f, -1f).normalized;
            float camDist = 4f;

            var camGO = new GameObject("BB_MatPreviewCam");
            var cam   = camGO.AddComponent<Camera>();
            cam.enabled         = false;
            cam.orthographic    = true;
            cam.clearFlags      = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.09f, 0.09f, 0.11f, 1f);
            cam.cullingMask     = 1 << drawLayer;
            cam.nearClipPlane   = 0.1f;
            cam.farClipPlane    = camDist * 4f;
            cam.aspect          = 1f;
            cam.orthographicSize = 0.68f;
            camGO.transform.position = RenderWorldOffset - camDir * camDist;
            camGO.transform.LookAt(RenderWorldOffset);

            var rt = new RenderTexture(ThumbSize, ThumbSize, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;

            var savedAmbientMode  = RenderSettings.ambientMode;
            var savedAmbientLight = RenderSettings.ambientLight;
            var sceneLights = UnityEngine.Object.FindObjectsOfType<Light>();
            var wasEnabled  = new bool[sceneLights.Length];
            for (int i = 0; i < sceneLights.Length; i++) { wasEnabled[i] = sceneLights[i].enabled; sceneLights[i].enabled = false; }
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.15f, 0.15f, 0.18f);

            var lightGO  = new GameObject("BB_MatPreviewKey");
            var keyLight = lightGO.AddComponent<Light>();
            keyLight.type      = LightType.Directional;
            keyLight.intensity = 1.2f;
            keyLight.color     = new Color(1f, 0.97f, 0.92f);
            keyLight.shadows   = LightShadows.None;
            lightGO.transform.rotation = camGO.transform.rotation;

            var rimGO    = new GameObject("BB_MatPreviewRim");
            var rimLight = rimGO.AddComponent<Light>();
            rimLight.type      = LightType.Directional;
            rimLight.intensity = 0.30f;
            rimLight.color     = new Color(0.5f, 0.65f, 1f);
            rimLight.shadows   = LightShadows.None;
            rimGO.transform.rotation = Quaternion.LookRotation(-camGO.transform.forward, camGO.transform.up);

            var matClones = new Dictionary<Material, Material>();

            var savedSnowGlobals = new float[_snowPropNames.Length];
            for (int i = 0; i < _snowPropNames.Length; i++)
            {
                savedSnowGlobals[i] = Shader.GetGlobalFloat(_snowPropNames[i]);
                Shader.SetGlobalFloat(_snowPropNames[i], 0f);
            }

            Texture2D result = null;
            var prevActive = RenderTexture.active;
            try
            {
                _mpb.Clear();
                foreach (var name in _snowPropNames) _mpb.SetFloat(name, 0f);
                _mpb.SetVector("_SnowHeightAngleRange", new Vector4(1000000f, 1f, 0f, 1f));

                var mtx       = Matrix4x4.TRS(RenderWorldOffset, Quaternion.identity, Vector3.one);
                var renderMat = GetOrCloneForRender(mat, matClones);
                if (renderMat != null)
                {
                    for (int sub = 0; sub < mesh.subMeshCount; sub++)
                        Graphics.DrawMesh(mesh, mtx, renderMat, drawLayer, cam, sub, _mpb);
                }

                cam.Render();

                RenderTexture.active = rt;
                result = new Texture2D(ThumbSize, ThumbSize, TextureFormat.RGB24, false);
                result.filterMode = FilterMode.Bilinear;
                result.ReadPixels(new Rect(0, 0, ThumbSize, ThumbSize), 0, 0);
                result.Apply();
            }
            catch (System.Exception e)
            {
                MelonLogger.Warning($"[PropPreviews] Material sphere render failed for id={matId}: {e.Message}");
                if (result != null) { UnityEngine.Object.Destroy(result); result = null; }
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderSettings.ambientMode  = savedAmbientMode;
                RenderSettings.ambientLight = savedAmbientLight;
                for (int i = 0; i < sceneLights.Length; i++)
                    if (sceneLights[i] != null) sceneLights[i].enabled = wasEnabled[i];
                UnityEngine.Object.Destroy(lightGO);
                UnityEngine.Object.Destroy(rimGO);
                cam.targetTexture = null;
                UnityEngine.Object.Destroy(camGO);
                rt.Release();
                UnityEngine.Object.Destroy(rt);
                foreach (var clone in matClones.Values) if (clone != null) UnityEngine.Object.Destroy(clone);
                for (int i = 0; i < _snowPropNames.Length; i++)
                    Shader.SetGlobalFloat(_snowPropNames[i], savedSnowGlobals[i]);
            }
            return result;
        }

        // ---- Bounds ----

        static Bounds ComputeBounds(PropInfo info, HashSet<string> disabledSubPaths, HashSet<int> disabledIndices)
        {
            var partBounds = new List<Bounds>(info.parts.Count);
            for (int pi = 0; pi < info.parts.Count; pi++)
            {
                var part = info.parts[pi];
                if (part.mesh == null) continue;
                if (IsPartDisabled(pi, part, disabledSubPaths, disabledIndices)) continue;

                var scale = part.localScale == Vector3.zero ? Vector3.one : part.localScale;
                var mb    = part.mesh.bounds;
                var he    = new Vector3(
                    Mathf.Abs(mb.extents.x * scale.x),
                    Mathf.Abs(mb.extents.y * scale.y),
                    Mathf.Abs(mb.extents.z * scale.z));

                // Stray embedded geometry (cracked-rock fragment vertices baked into the same mesh)
                // shows up as MESH-SPACE extents > 50 m.  Check in mesh space so large-scale props
                // whose mesh itself is normal-sized are not incorrectly sent through tight filtering.
                const float StrayThreshold = 50f;
                bool hasMeshStray = Mathf.Max(Mathf.Abs(mb.extents.x),
                                              Mathf.Abs(mb.extents.y),
                                              Mathf.Abs(mb.extents.z)) > StrayThreshold;
                if (hasMeshStray)
                {
                    var tight = GetTightMeshBounds(part.mesh, scale);
                    if (tight.HasValue)
                    {
                        // Compact prop with outlier stray vertices — use filtered tight bounds.
                        var tb  = tight.Value;
                        var wc2 = part.localPosition + part.localRotation * Vector3.Scale(tb.center, scale);
                        var he2 = new Vector3(
                            Mathf.Abs(tb.extents.x * scale.x),
                            Mathf.Abs(tb.extents.y * scale.y),
                            Mathf.Abs(tb.extents.z * scale.z));
                        partBounds.Add(new Bounds(wc2, he2 * 2f));
                        continue;
                    }
                    // null = genuinely large mesh (most vertices far from origin); fall through to raw path.
                }

                var wc = part.localPosition + part.localRotation * Vector3.Scale(mb.center, scale);
                partBounds.Add(new Bounds(wc, he * 2f));
            }

            if (partBounds.Count == 0) return new Bounds(Vector3.zero, Vector3.one);

            var full = partBounds[0];
            for (int i = 1; i < partBounds.Count; i++) full.Encapsulate(partBounds[i]);
            return full;
        }

        // Returns tight bounds using only vertices within 15m/maxScale of the mesh origin,
        // filtering out stray outlier vertices (e.g. cracked-rock fragment geometry baked into the mesh).
        // Returns null when: the mesh is genuinely large (fewer than half of vertices are inside the
        // filter radius — indicating real geometry, not outliers), or no vertices are found inside.
        static Bounds? GetTightMeshBounds(Mesh mesh, Vector3 scale)
        {
            float maxScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            if (maxScale < 0.0001f) maxScale = 1f;
            float maxDistSq = (15f / maxScale) * (15f / maxScale);
            try
            {
                var verts = mesh.vertices;
                if (verts == null || verts.Length == 0) return null;
                int inside = 0;
                bool any = false;
                var mn = Vector3.zero; var mx = Vector3.zero;
                for (int i = 0; i < verts.Length; i++)
                {
                    var v   = verts[i];
                    float d = v.x * v.x + v.y * v.y + v.z * v.z;
                    if (d > maxDistSq) continue;
                    inside++;
                    if (!any) { mn = mx = v; any = true; continue; }
                    if (v.x < mn.x) mn.x = v.x; else if (v.x > mx.x) mx.x = v.x;
                    if (v.y < mn.y) mn.y = v.y; else if (v.y > mx.y) mx.y = v.y;
                    if (v.z < mn.z) mn.z = v.z; else if (v.z > mx.z) mx.z = v.z;
                }
                // If fewer than half the vertices are near the origin, this is a genuinely large mesh
                // (sand tunnels, large terrain pieces), not a compact prop with a few stray outliers.
                if (!any || inside * 2 < verts.Length) return null;
                return new Bounds((mn + mx) * 0.5f, mx - mn);
            }
            catch { return null; }
        }

    }
}
