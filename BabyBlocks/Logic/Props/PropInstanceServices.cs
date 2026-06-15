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
        public static void ApplySurfaceType(LevelEditorObject leo, string surfaceTag)
        {
            if (leo == null) return;
            try
            {
                string tag = string.IsNullOrEmpty(surfaceTag) ? "Untagged" : surfaceTag;
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
            try { go.tag = tag; } catch { }
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
