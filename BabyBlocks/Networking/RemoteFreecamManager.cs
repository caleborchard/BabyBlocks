using System.Collections.Generic;
using Il2CppTMPro;
using UnityEngine;

namespace BabyBlocks.Networking
{
    // Editor-only spheres representing other connected players' fly-cam positions, each
    // tagged with a nametag matching the style of BabyStepsMultiplayerClient's RemotePlayer
    // nametags. Markers are only visible while the local player has the Baby Blocks editor
    // open (FlyCamActive && CursorMode), mirroring SpawnPointMarker's visibility rule.
    internal static class RemoteFreecamManager
    {
        const float SphereDiameter = 0.4f;
        const float StaleTimeoutSeconds = 6f;
        const float SphereAlpha = 0.35f;

        class Marker
        {
            public GameObject root;
            public MeshRenderer renderer;
            public TextMeshPro nameText;
            public Transform nameTagTransform;
            public Color color;
            public float lastUpdateTime;
        }

        static readonly Dictionary<byte, Marker> _markers = new();

        public static void Update()
        {
            if (_markers.Count == 0) return;

            bool editorActive = FlyCamController.FlyCamActive && FlyCamController.CursorMode;
            var cam = Camera.main;
            float now = Time.unscaledTime;

            List<byte> stale = null;
            foreach (var kv in _markers)
            {
                var marker = kv.Value;
                if (marker.root == null) continue;

                if (now - marker.lastUpdateTime > StaleTimeoutSeconds)
                {
                    (stale ??= new List<byte>()).Add(kv.Key);
                    continue;
                }

                if (marker.root.activeSelf != editorActive)
                    marker.root.SetActive(editorActive);

                if (!editorActive) continue;

                if (cam != null && marker.nameTagTransform != null)
                    marker.nameTagTransform.rotation =
                        Quaternion.LookRotation(marker.nameTagTransform.position - cam.transform.position);
            }

            if (stale != null)
                foreach (var uuid in stale) Remove(uuid);
        }

        public static void UpdateFreecam(byte uuid, Vector3 position, Color suitColor, string name)
        {
            if (!_markers.TryGetValue(uuid, out var marker) || marker.root == null)
            {
                marker = Create(suitColor, name);
                _markers[uuid] = marker;
            }

            marker.root.transform.position = position;
            marker.lastUpdateTime = Time.unscaledTime;

            if (marker.color != suitColor)
            {
                marker.color = suitColor;
                if (marker.renderer != null) SetSphereColor(marker.renderer.material, suitColor);
            }

            if (marker.nameText != null && marker.nameText.text != name)
                marker.nameText.text = name;
        }

        public static void Remove(byte uuid)
        {
            if (_markers.TryGetValue(uuid, out var marker))
            {
                if (marker.root != null) UnityEngine.Object.Destroy(marker.root);
                _markers.Remove(uuid);
            }
        }

        public static void ClearAll()
        {
            foreach (var marker in _markers.Values)
                if (marker.root != null) UnityEngine.Object.Destroy(marker.root);
            _markers.Clear();
        }

        // Sprites/Default renders with "Blend One OneMinusSrcAlpha" (premultiplied alpha) and
        // is unlit/fullbright. Setting an un-premultiplied color with alpha < 1 both looked
        // fully opaque AND added the full-brightness source color on top of the background
        // (the "glow"). Premultiplying RGB by alpha here gives correct, genuinely
        // see-through blending: final = srcRGB*alpha + dstRGB*(1-alpha).
        static Material CreateSphereMaterial(Color suitColor)
        {
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Standard");
            var mat = new Material(shader) { name = "BabyBlocks_RemoteFreecamSphere", renderQueue = 3000 };
            SetSphereColor(mat, suitColor);
            return mat;
        }

        static void SetSphereColor(Material mat, Color suitColor)
        {
            float a = SphereAlpha;
            mat.color = new Color(suitColor.r * a, suitColor.g * a, suitColor.b * a, a);
        }

        static Marker Create(Color suitColor, string name)
        {
            var root = new GameObject("BabyBlocks_RemoteFreecam");
            root.SetActive(false); // shown/hidden by Update() based on editor state

            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = "Sphere";
            sphere.transform.SetParent(root.transform, false);
            sphere.transform.localScale = Vector3.one * SphereDiameter;

            var collider = sphere.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);

            var renderer = sphere.GetComponent<MeshRenderer>();
            var mat = CreateSphereMaterial(suitColor);
            renderer.material = mat;

            var nameTagGo = new GameObject("Nametag");
            nameTagGo.transform.SetParent(root.transform, false);
            nameTagGo.transform.localPosition = new Vector3(0f, SphereDiameter * 0.5f + 0.35f, 0f);

            var text = nameTagGo.AddComponent<TextMeshPro>();
            text.alignment = TextAlignmentOptions.Center;
            text.fontSize = 1.5f;
            text.color = Color.white;
            text.text = name;

            var overlayShader = Shader.Find("TextMeshPro/Distance Field Overlay");
            if (overlayShader != null && text.font?.material != null)
            {
                var overlayMat = new Material(overlayShader);
                overlayMat.mainTexture = text.font.material.mainTexture;
                text.fontMaterial = overlayMat;
            }

            return new Marker
            {
                root = root,
                renderer = renderer,
                nameText = text,
                nameTagTransform = nameTagGo.transform,
                color = suitColor,
                lastUpdateTime = Time.unscaledTime,
            };
        }
    }
}
