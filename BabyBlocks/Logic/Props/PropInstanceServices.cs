using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace BabyBlocks
{
    // Pure-logic helpers for applying saved per-prop metadata (surface tags, disabled
    // renderers, bush colliders) onto a spawned instance's GameObject hierarchy.
    internal static class PropInstanceServices
    {
        // --- Tint overlay (BabyBlocks/TintOverlay shader, injected from the shared bundle) ---

        static readonly List<LevelEditorObject> _tintedObjects = new();
        static Shader _overlayShader;

        // Called by GizmoRenderer.LoadScreenSpaceShaders once the shared bundle is loaded.
        // Also retroactively applies tint to any props that were loaded before the shader
        // was available (e.g. level loaded before the first prop was selected/gizmo inited).
        internal static void SetTintShader(Shader s)
        {
            _overlayShader = s;
            var mgr = LevelEditorManager.Instance;
            if (mgr == null) return;
            foreach (var leo in mgr.Objects)
            {
                if (leo == null || leo._tintMaterial != null) continue;
                var t = leo.materialTint;
                if (!Mathf.Approximately(t.x, 255f) || !Mathf.Approximately(t.y, 255f) || !Mathf.Approximately(t.z, 255f))
                    ApplyTint(leo, t);
            }
        }

        // Called from Core.OnUpdate — submits DrawMesh calls for every tinted prop.
        // Graphics.DrawMesh is collected by Unity and rendered at the correct point
        // in the frame (queue 2999 = after all opaques, before transparents).
        public static void RenderTints()
        {
            if (_tintedObjects.Count == 0) return;
            for (int idx = _tintedObjects.Count - 1; idx >= 0; idx--)
            {
                var leo = _tintedObjects[idx];
                if (leo == null || leo._tintMaterial == null || leo._tintRenderers == null)
                {
                    _tintedObjects.RemoveAt(idx);
                    continue;
                }
                foreach (var r in leo._tintRenderers)
                {
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                    Mesh mesh = null;
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null)
                        mesh = mf.sharedMesh;
                    else
                    {
                        var smr = r.TryCast<SkinnedMeshRenderer>();
                        if (smr != null) mesh = smr.sharedMesh;
                    }
                    if (mesh == null) continue;
                    var mat = leo._tintMaterial;
                    int layer = r.gameObject.layer;
                    for (int s = 0; s < mesh.subMeshCount; s++)
                        Graphics.DrawMesh(mesh, r.transform.localToWorldMatrix, mat, layer, null, s);
                }
            }
        }

        // Applies a material tint (RGB 0-255) using an overlay shader via DrawMesh.
        // The overlay shader uses Blend DstColor Zero (multiply) at queue 2999, so it
        // composites on top of whatever the original shaders already rendered — works
        // regardless of the original material or shader.  (255,255,255) = no tint.
        public static void ApplyTint(LevelEditorObject leo, Vector3 tint)
        {
            if (leo == null) return;
            leo.materialTint = tint;
            bool isWhite = Mathf.Approximately(tint.x, 255f)
                        && Mathf.Approximately(tint.y, 255f)
                        && Mathf.Approximately(tint.z, 255f);

            if (isWhite)
            {
                if (leo._tintMaterial != null)
                {
                    UnityEngine.Object.Destroy(leo._tintMaterial);
                    leo._tintMaterial = null;
                }
                leo._tintRenderers = null;
                _tintedObjects.Remove(leo);
                return;
            }

            var shader = _overlayShader;
            if (shader == null) return;

            var color = new Color(tint.x / 255f, tint.y / 255f, tint.z / 255f, 1f);

            // Create or reuse the per-prop overlay material and just update the color.
            if (leo._tintMaterial == null)
                leo._tintMaterial = new Material(shader);
            leo._tintMaterial.SetColor("_TintColor", color);

            // Snapshot renderers on first call (or after a reset).
            if (leo._tintRenderers == null)
            {
                var rs = leo.GetComponentsInChildren<Renderer>(true);
                leo._tintRenderers = new Renderer[rs.Length];
                for (int i = 0; i < rs.Length; i++)
                    leo._tintRenderers[i] = rs[i];

                if (!_tintedObjects.Contains(leo))
                    _tintedObjects.Add(leo);
            }
        }

        public static void ApplySurfaceType(LevelEditorObject leo, string surfaceTag)
        {
            if (leo == null) return;
            string tag = string.IsNullOrEmpty(surfaceTag) ? "Untagged" : surfaceTag;
            leo.surfaceTypeTag = tag;
            try
            {
                SetTagSafe(leo.gameObject, tag);
                foreach (var col in leo.GetComponentsInChildren<Collider>(true))
                {
                    if (col != null) SetTagSafe(col.gameObject, tag);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropMetadata] ApplySurfaceType failed: {e.Message}");
            }
        }

        public static void ApplySurfaceTypeToRoot(GameObject root, string surfaceTag)
        {
            if (root == null) return;
            try
            {
                string tag = string.IsNullOrEmpty(surfaceTag) ? "Untagged" : surfaceTag;
                SetTagSafe(root, tag);
                var leo = root.GetComponent<LevelEditorObject>();
                if (leo != null) leo.surfaceTypeTag = tag;
                foreach (var col in root.GetComponentsInChildren<Collider>(true))
                {
                    if (col != null) SetTagSafe(col.gameObject, tag);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropMetadata] ApplySurfaceTypeToRoot failed: {e.Message}");
            }
        }

        static void SetTagSafe(GameObject go, string tag)
        {
            try { go.tag = tag; }
            catch (Exception e) { MelonLogger.Warning($"[BabyBlocks][SetTag] Failed to set '{tag}' on '{go?.name}': {e.Message}"); }
        }

        public static void ApplyDisabledRenderersToRoot(string propId, GameObject root)
        {
            PropMetadataStore.EnsureLoaded();
            if (string.IsNullOrEmpty(propId) || root == null) return;
            if (!PropMetadataStore._byId.TryGetValue(propId, out var info)) return;
            if (info.disabledRenderers == null || info.disabledRenderers.Count == 0) return;

            foreach (var path in info.disabledRenderers)
            {
                if (string.IsNullOrEmpty(path)) continue;
                // Path format: "RootName/Child" — strip the leading root-name segment.
                int slashIdx = path.IndexOf('/');
                string subPath = slashIdx >= 0 ? path.Substring(slashIdx + 1) : path;
                if (string.IsNullOrEmpty(subPath)) continue;
                var t = root.transform.Find(subPath);
                if (t == null) continue;
                var r = t.GetComponent<Renderer>();
                if (r != null) r.enabled = false;
            }

            LevelEditorManager.NotifyVisualStateChanged(root);
        }

        public static void SetBushPassthrough(GameObject root, bool passthrough)
        {
            foreach (var col in root.GetComponentsInChildren<Collider>(true))
            {
                if (col == null) continue;
                // Leave the BushCollider's own SphereCollider alone (already a trigger, handled separately).
                if (col.gameObject.GetComponent<Il2Cpp.BushCollider>() != null) continue;
                // Non-convex MeshColliders cannot be triggers in Unity — force convex so isTrigger sticks.
                // The convex hull is sufficient for editor click-selection.
                if (passthrough)
                {
                    var mc = col.TryCast<MeshCollider>();
                    if (mc != null && !mc.convex) mc.convex = true;
                }
                col.isTrigger = passthrough;
            }
        }

        public static float ComputeBushRadius(LevelEditorObject leo)
        {
            var bounds = new Bounds();
            bool first = true;
            foreach (var r in leo.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (first) { bounds = r.bounds; first = false; }
                else bounds.Encapsulate(r.bounds);
            }
            if (first) return 1f;
            // BushCollider.Start() multiplies sphere.radius * localScale.x, so store in local space
            float worldRadius = Mathf.Max(bounds.extents.x, bounds.extents.z);
            float scale = Mathf.Max(0.001f, leo.transform.lossyScale.x);
            return worldRadius / scale;
        }

        public static void ApplyBushColliderToRoot(string propId, GameObject root)
        {
            PropMetadataStore.EnsureLoaded();
            if (string.IsNullOrEmpty(propId) || root == null) return;
            if (!PropMetadataStore._byId.TryGetValue(propId, out var info) || !info.isBush) return;

            var existingBush = root.GetComponent<Il2Cpp.BushCollider>();
            if (existingBush != null) UnityEngine.Object.DestroyImmediate(existingBush);
            var existingSphere = root.GetComponent<SphereCollider>();
            if (existingSphere != null) UnityEngine.Object.DestroyImmediate(existingSphere);

            SetBushPassthrough(root, true);

            float radius = info.bushRadius > 0f ? info.bushRadius : 1f;
            int grassType = info.soundGrassType > 0 ? info.soundGrassType : 1;
            BushAudioTracker.Register(root.transform, radius, grassType);
            var sphere = root.AddComponent<SphereCollider>();
            sphere.radius = radius;
            sphere.isTrigger = true;
            var bush = root.AddComponent<Il2Cpp.BushCollider>();
            bush.rad = sphere.radius * root.transform.localScale.x;
        }

        // Tracks editor bush spheres so the GetGrassAt Harmony patch can return a grass type
        // for positions inside them, enabling BodyCollisions rustle and PlayerMovement plant sounds.
        internal static class BushAudioTracker
        {
            static readonly List<(Transform t, float localRad, int grassType)> _bushes = new();

            public static void Register(Transform t, float localRad, int grassType = 1)
            {
                if (t != null) _bushes.Add((t, localRad, grassType));
            }

            public static void Unregister(Transform t)
            {
                for (int i = _bushes.Count - 1; i >= 0; i--)
                    if (_bushes[i].t == t) { _bushes.RemoveAt(i); return; }
            }

            // Returns the GrassType int of the first bush sphere containing pos, or 0 (none) if outside all.
            public static int GetGrassTypeAtPos(Vector3 pos)
            {
                for (int i = _bushes.Count - 1; i >= 0; i--)
                {
                    var (t, localRad, grassType) = _bushes[i];
                    if (t == null) { _bushes.RemoveAt(i); continue; }
                    float worldRad = localRad * Mathf.Max(0.001f, t.lossyScale.x);
                    if ((pos - t.position).sqrMagnitude < worldRad * worldRad) return grassType;
                }
                return 0;
            }
        }
    }
}
