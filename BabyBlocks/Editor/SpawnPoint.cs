using System;
using UnityEngine;

namespace BabyBlocks
{
    // Static helpers for constructing and configuring the spawn point marker GameObject.
    static class SpawnPointConfig
    {
        static Material _frameMaterial;
        static readonly Color MarkerColor = new Color(0.3f, 1f, 0.45f, 0.9f);

        public static void Configure(GameObject go)
        {
            if (go == null) return;

            foreach (var renderer in go.GetComponentsInChildren<MeshRenderer>(true))
            {
                renderer.forceRenderingOff = true;
                renderer.enabled = false;
            }

            BuildMarker(go);

            var box = go.GetComponent<BoxCollider>();
            if (box == null) box = go.AddComponent<BoxCollider>();
            box.center    = new Vector3(0f, 0.9f, 0f);
            box.size      = new Vector3(0.8f, 1.8f, 0.8f);
            box.isTrigger = true;

            if (go.GetComponent<SpawnPointMarker>() == null)
                go.AddComponent<SpawnPointMarker>();
        }

        // Builds a green wireframe marker: a footprint ring on the ground, a vertical
        // pole showing player height, and a forward arrow showing spawn facing (+Z local).
        internal static void BuildMarker(GameObject root)
        {
            if (root == null) return;

            var mat = GetFrameMaterial();
            if (mat == null) return;

            var existing = root.transform.Find("SpawnFrame");
            if (existing != null) UnityEngine.Object.Destroy(existing.gameObject);

            var frameRoot = new GameObject("SpawnFrame");
            frameRoot.transform.SetParent(root.transform, false);
            frameRoot.layer = root.layer;

            // Footprint ring on the ground.
            const int ringSegments = 24;
            const float ringRadius = 0.4f;
            var ringPoints = new Vector3[ringSegments];
            for (int i = 0; i < ringSegments; i++)
            {
                float angle = (i / (float)ringSegments) * Mathf.PI * 2f;
                ringPoints[i] = new Vector3(Mathf.Cos(angle) * ringRadius, 0.02f, Mathf.Sin(angle) * ringRadius);
            }
            AddLineLoop(frameRoot, "Ring", ringPoints, mat, true);

            // Vertical pole showing player height.
            AddLine(frameRoot, "Pole", mat,
                new Vector3(0f, 0f, 0f),
                new Vector3(0f, 1.8f, 0f));

            // Forward arrow (local +Z) showing spawn facing direction.
            const float arrowLen = 0.7f;
            const float headLen  = 0.18f;
            var tip = new Vector3(0f, 0.05f, arrowLen);
            AddLine(frameRoot, "ArrowShaft", mat, new Vector3(0f, 0.05f, 0f), tip);
            AddLine(frameRoot, "ArrowHeadL", mat, tip, tip + Quaternion.Euler(0f, 150f, 0f) * Vector3.forward * headLen);
            AddLine(frameRoot, "ArrowHeadR", mat, tip, tip + Quaternion.Euler(0f, -150f, 0f) * Vector3.forward * headLen);
        }

        static void AddLine(GameObject parent, string name, Material mat, Vector3 a, Vector3 b)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.layer = parent.layer;

            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace     = false;
            line.positionCount     = 2;
            line.SetPosition(0, a);
            line.SetPosition(1, b);
            line.startWidth        = 0.015f;
            line.endWidth          = 0.015f;
            line.numCapVertices    = 0;
            line.numCornerVertices = 0;
            line.alignment         = LineAlignment.View;
            line.material          = mat;
            line.startColor        = MarkerColor;
            line.endColor          = MarkerColor;
        }

        static void AddLineLoop(GameObject parent, string name, Vector3[] points, Material mat, bool loop)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.layer = parent.layer;

            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace     = false;
            line.loop              = loop;
            line.positionCount     = points.Length;
            line.SetPositions(points);
            line.startWidth        = 0.015f;
            line.endWidth          = 0.015f;
            line.numCapVertices    = 0;
            line.numCornerVertices = 0;
            line.alignment         = LineAlignment.View;
            line.material          = mat;
            line.startColor        = MarkerColor;
            line.endColor          = MarkerColor;
        }

        static Material GetFrameMaterial()
        {
            if (_frameMaterial != null) return _frameMaterial;
            var shader = Shader.Find("Sprites/Default")
                      ?? Shader.Find("Unlit/Color")
                      ?? Shader.Find("Standard");
            _frameMaterial = new Material(shader) { name = "BabyBlocks_SpawnFrame", renderQueue = 5000 };
            return _frameMaterial;
        }
    }

    // The green spawn marker is only useful while editing - hide it during normal
    // gameplay so it doesn't show up at the spawn location in-game.
    public class SpawnPointMarker : MonoBehaviour
    {
        public SpawnPointMarker(IntPtr ptr) : base(ptr) { }

        Transform _frameRoot;

        void Awake()
        {
            _frameRoot = transform.Find("SpawnFrame");
        }

        void Update()
        {
            if (_frameRoot == null) _frameRoot = transform.Find("SpawnFrame");
            if (_frameRoot == null) return;

            bool editorActive = FlyCamController.FlyCamActive && FlyCamController.CursorMode;
            if (_frameRoot.gameObject.activeSelf != editorActive)
                _frameRoot.gameObject.SetActive(editorActive);
        }
    }
}
