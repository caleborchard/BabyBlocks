using Il2Cpp;
using UnityEngine;

namespace BabyBlocks.UI
{
    internal class HatPreviewRenderer
    {
        static readonly Vector3 HatDefaultLocalPos = new(0f, 0.207f, -0.02f);
        static readonly Vector3 PreviewOrigin      = new(0f, -9000f, 0f);

        const int PreviewSize = 200;

        static bool  _orbitDrag;
        static float _lastMouseX;
        static float _lastMouseY;

        static HatPreviewRenderer _activeInstance;

        GameObject    _playerClone;
        Transform     _previewHeadBone;
        GameObject    _propClone;
        GameObject    _lightGO;
        Camera        _cam;
        RenderTexture _rt;
        float         _orbitAngleY;
        float         _orbitDist = 0.5f;

        public bool IsReady => _rt != null && _cam != null && _propClone != null;

        // ── IMGUI entry point — called from Core.OnGUI() ───────────────────────
        public static void DrawWindowGUI()
        {
            var inst = _activeInstance;
            if (inst == null || !inst.IsReady) return;

            // Position preview inside the Properties panel, centered horizontally
            // just below its title bar.
            var panelRT = PropertiesPanel.Instance?.Rect;
            if (panelRT == null) return;

            var pr = panelRT.rect;
            // Screen Space Overlay: TransformPoint gives screen-pixel coords (Y-up).
            Vector3 worldTL = panelRT.TransformPoint(new Vector3(pr.xMin, pr.yMax, 0f));
            Vector3 worldTR = panelRT.TransformPoint(new Vector3(pr.xMax, pr.yMax, 0f));

            float panelLeft  = worldTL.x;
            float panelWidth = worldTR.x - worldTL.x;
            // Convert screen Y (bottom-up) to IMGUI Y (top-down).
            float imguiTop   = Screen.height - worldTL.y;

            float previewX = panelLeft + (panelWidth - PreviewSize) * 0.5f;
            float previewY = imguiTop + 30f; // below UniverseLib title bar

            var contentRect = new Rect(previewX, previewY, PreviewSize, PreviewSize);

            var e = Event.current;
            if (e != null)
            {
                if (e.type == EventType.MouseDown && e.button == 0 && contentRect.Contains(e.mousePosition))
                {
                    _orbitDrag  = true;
                    _lastMouseX = e.mousePosition.x;
                    _lastMouseY = e.mousePosition.y;
                    e.Use();
                }
                else if (e.type == EventType.MouseDrag && _orbitDrag)
                {
                    float dx = e.mousePosition.x - _lastMouseX;
                    float dy = e.mousePosition.y - _lastMouseY;
                    inst._orbitAngleY -= dx * 0.5f;
                    // IMGUI Y increases downward: drag up (dy<0) → zoom in (smaller dist).
                    inst._orbitDist = Mathf.Clamp(inst._orbitDist + dy * 0.003f, 0.1f, 2.5f);
                    _lastMouseX = e.mousePosition.x;
                    _lastMouseY = e.mousePosition.y;
                    inst.UpdateCameraPosition();
                    e.Use();
                }
                else if (e.type == EventType.MouseUp)
                {
                    _orbitDrag = false;
                }
            }

            inst._cam.Render();
            GUI.DrawTexture(contentRect, inst._rt);
        }

        // ── Instance lifecycle ──────────────────────────────────────────────────
        public void Setup(LevelEditorObject target)
        {
            Teardown();

            var pm = PlayerMovement.me;
            if (pm == null) return;

            var visualRoot = FindVisualRoot(pm);
            if (visualRoot == null) return;

            _playerClone = UnityEngine.Object.Instantiate(visualRoot.gameObject);
            _playerClone.name = "[PlayerPreview]";
            _playerClone.transform.SetPositionAndRotation(PreviewOrigin, Quaternion.identity);

            foreach (var c in _playerClone.GetComponentsInChildren<Rigidbody>())     UnityEngine.Object.Destroy(c);
            foreach (var c in _playerClone.GetComponentsInChildren<Collider>())      UnityEngine.Object.Destroy(c);
            foreach (var c in _playerClone.GetComponentsInChildren<MonoBehaviour>()) UnityEngine.Object.Destroy(c);

            var anim = _playerClone.GetComponentInChildren<Animator>();
            if (anim != null) anim.enabled = false;

            var smr = _playerClone.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr != null) smr.updateWhenOffscreen = true;

            _previewHeadBone = FindDescendant(_playerClone.transform, "head.x");
            if (_previewHeadBone == null) { Teardown(); return; }

            _propClone = UnityEngine.Object.Instantiate(target.gameObject);
            _propClone.name = "[HatPreview]";
            foreach (var c in _propClone.GetComponentsInChildren<Rigidbody>())     UnityEngine.Object.Destroy(c);
            foreach (var c in _propClone.GetComponentsInChildren<Collider>())      UnityEngine.Object.Destroy(c);
            foreach (var c in _propClone.GetComponentsInChildren<MonoBehaviour>()) UnityEngine.Object.Destroy(c);
            _propClone.transform.SetParent(_previewHeadBone, worldPositionStays: false);
            ApplyHatOffset(target);

            _lightGO = new GameObject("[HatPreviewLight]");
            var light = _lightGO.AddComponent<Light>();
            light.type      = LightType.Point;
            light.range     = 30f;
            light.intensity = 3f;
            light.color     = Color.white;

            _rt = new RenderTexture(PreviewSize, PreviewSize, 16, RenderTextureFormat.ARGB32);
            _rt.Create();

            var camGO = new GameObject("[HatPreviewCam]");
            _cam = camGO.AddComponent<Camera>();
            _cam.enabled         = false;
            _cam.cullingMask     = -1;
            _cam.fieldOfView     = 50f;
            _cam.nearClipPlane   = 0.02f;
            _cam.farClipPlane    = 5000f;
            _cam.clearFlags      = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.12f, 0.12f, 0.12f, 1f);
            _cam.targetTexture   = _rt;

            _orbitAngleY = 0f;
            _orbitDist   = 0.5f;
            UpdateCameraPosition();
            _activeInstance = this;
        }

        // Syncs live prop scale and hat offset to the clone — call each frame.
        public void SyncPropFromTarget(LevelEditorObject target)
        {
            if (_propClone == null || target == null) return;
            _propClone.transform.localScale = target.transform.localScale;
            ApplyHatOffset(target);
        }

        public void ApplyHatOffset(LevelEditorObject target)
        {
            if (_propClone == null) return;
            _propClone.transform.localPosition = HatDefaultLocalPos + target.hatOffsetPos;
            _propClone.transform.localRotation = Quaternion.Euler(
                -25f + target.hatOffsetRot.x, target.hatOffsetRot.y, target.hatOffsetRot.z);
        }

        public void UpdateCameraPosition()
        {
            if (_cam == null || _previewHeadBone == null) return;
            var headPos = _previewHeadBone.position;
            float rad   = _orbitAngleY * Mathf.Deg2Rad;
            var offset  = new Vector3(Mathf.Sin(rad) * _orbitDist, 0.2f, Mathf.Cos(rad) * _orbitDist);
            _cam.transform.position = headPos + offset;
            _cam.transform.LookAt(headPos + new Vector3(0f, 0.15f, 0f));
            if (_lightGO != null)
                _lightGO.transform.position = headPos + new Vector3(0f, 0.5f, 1.0f);
        }

        public void Teardown()
        {
            if (_activeInstance == this) _activeInstance = null;
            if (_propClone   != null) { UnityEngine.Object.Destroy(_propClone);      _propClone   = null; }
            if (_playerClone != null) { UnityEngine.Object.Destroy(_playerClone);    _playerClone = null; }
            if (_lightGO     != null) { UnityEngine.Object.Destroy(_lightGO);        _lightGO     = null; }
            if (_cam         != null) { UnityEngine.Object.Destroy(_cam.gameObject); _cam         = null; }
            if (_rt          != null) { _rt.Release(); UnityEngine.Object.Destroy(_rt); _rt       = null; }
            _previewHeadBone = null;
        }

        static Transform FindVisualRoot(PlayerMovement pm)
        {
            var root  = pm.transform;
            var named = root.Find("IKTargets");
            if (named != null && named.GetComponentInChildren<SkinnedMeshRenderer>() != null)
                return named;
            named = root.Find("NateMesh");
            if (named != null) return named;
            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (child.GetComponentInChildren<SkinnedMeshRenderer>() != null)
                    return child;
            }
            return null;
        }

        static Transform FindDescendant(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindDescendant(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
