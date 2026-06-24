using Il2Cpp;
using UnityEngine;

namespace BabyBlocks.UI
{
    internal class HatPreviewRenderer
    {
        static readonly Vector3 HatDefaultLocalPos = new(0f, 0.207f, -0.02f);
        static readonly Vector3 PreviewOrigin      = new(0f, -9000f, 0f);

        internal const int PreviewSize = 200;

        static RectTransform _anchorRT;

        static bool  _orbitDrag;
        static float _lastMouseX;
        static float _lastMouseY;

        static HatPreviewRenderer _activeInstance;

        internal LevelEditorObject _target;
        GameObject    _playerClone;
        Transform     _previewHeadBone;
        Transform     _previewHandBone;
        GameObject    _propClone;
        GameObject    _lightGO;
        Camera        _cam;
        RenderTexture _rt;
        float         _orbitAngleY;
        float         _orbitDist  = 0.5f;
        bool          _headMode   = true;  // tracks where prop is currently parented
        bool          _propOnHead = true;
        int           _groupRootInstanceId;

        public bool IsReady => _rt != null && _cam != null && _propClone != null;

        public static void SetAnchor(RectTransform rt) => _anchorRT = rt;

        // ── IMGUI entry point ───────────────────────────────────────────────────
        public static void DrawWindowGUI()
        {
            if (!FlyCamController.FlyCamActive || !FlyCamController.CursorMode) return;
            if (PropertiesPanel.Instance == null || !PropertiesPanel.Instance.UIRoot.activeSelf) return;

            var inst = _activeInstance;
            if (inst == null || !inst.IsReady || _anchorRT == null) return;

            var ar = _anchorRT.rect;
            Vector3 worldTL = _anchorRT.TransformPoint(new Vector3(ar.xMin, ar.yMax, 0f));
            Vector3 worldTR = _anchorRT.TransformPoint(new Vector3(ar.xMax, ar.yMax, 0f));

            float anchorLeft  = worldTL.x;
            float anchorWidth = worldTR.x - worldTL.x;
            float imguiTop    = Screen.height - worldTL.y;

            float previewX  = anchorLeft + (anchorWidth - PreviewSize) * 0.5f;
            var contentRect = new Rect(previewX, imguiTop, PreviewSize, PreviewSize);

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
        public void Setup(LevelEditorObject target, bool headMode = true)
        {
            Teardown();
            _target   = target;
            _headMode = headMode;

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
            if (anim != null)
            {
                // Rebind resets the animator to its default state (T/idle pose),
                // then Update(0) bakes that pose into the bone transforms before we freeze it.
                anim.Rebind();
                anim.Update(0f);
                anim.enabled = false;
            }

            var smr = _playerClone.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr != null) smr.updateWhenOffscreen = true;

            _previewHeadBone = FindDescendant(_playerClone.transform, "head.x");
            if (_previewHeadBone == null) { Teardown(); return; }

            // Try common right-hand bone names from the Blender/Unity rig.
            _previewHandBone = FindDescendant(_playerClone.transform, "hand.r.x")
                            ?? FindDescendant(_playerClone.transform, "hand.r")
                            ?? FindDescendant(_playerClone.transform, "wrist.r.x")
                            ?? FindDescendant(_playerClone.transform, "wrist.r");

            // Clone the group root (contains all members) if the target is in a group;
            // otherwise clone just the single target prop.
            var groupRoot = ResolveGroupRoot(target);
            var cloneSrc  = groupRoot != null ? groupRoot : target.gameObject;
            _groupRootInstanceId = cloneSrc.GetInstanceID();

            _propClone = UnityEngine.Object.Instantiate(cloneSrc);
            _propClone.name = "[HatPreview]";

            // DestroyImmediate with includeInactive:true so scripts on ALL child GOs
            // (active or inactive) are gone before any Awake/Update can run.
            // Deferred Destroy would let Hat.Update reset localScale every frame.
            foreach (var c in _propClone.GetComponentsInChildren<Rigidbody>(true))     UnityEngine.Object.DestroyImmediate(c);
            foreach (var c in _propClone.GetComponentsInChildren<Collider>(true))      UnityEngine.Object.DestroyImmediate(c);
            foreach (var c in _propClone.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(c);

            // Ensure the clone is visible (source may have been inactive).
            if (!_propClone.activeSelf) _propClone.SetActive(true);

            var attachBone = headMode ? _previewHeadBone : (_previewHandBone ?? _previewHeadBone);
            _propClone.transform.SetParent(attachBone, worldPositionStays: false);
            _propOnHead = headMode;
            ApplyHatOffset(target, headMode);

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

            // hatHairAmt is stored as the game/shader value (1=full hair, 0=none).
            var hat = target.GetComponent<Hat>()
                   ?? target.transform.parent?.GetComponent<Hat>()
                   ?? target.GetComponentInChildren<Hat>(true);
            ApplyHairShader(target.hatHairAmt, hat);
        }

        public void SyncPropFromTarget(LevelEditorObject target, bool headMode)
        {
            if (_propClone == null || target == null) return;
            _target   = target;
            _headMode = headMode;

            // Scale is stored on individual members (group root stays at (1,1,1)), so sync
            // each child of the clone from the corresponding live child.
            var groupRoot = ResolveGroupRoot(target);
            if (groupRoot != null)
            {
                int n = Mathf.Min(groupRoot.transform.childCount, _propClone.transform.childCount);
                for (int ci = 0; ci < n; ci++)
                {
                    var liveChild  = groupRoot.transform.GetChild(ci);
                    var cloneChild = _propClone.transform.GetChild(ci);
                    cloneChild.localScale    = liveChild.localScale;
                    cloneChild.localPosition = liveChild.localPosition;
                }
            }
            else
            {
                _propClone.transform.localScale = target.transform.localScale;
            }

            // Re-parent if mode changed.
            if (headMode != _propOnHead)
            {
                var bone = headMode ? _previewHeadBone : (_previewHandBone ?? _previewHeadBone);
                _propClone.transform.SetParent(bone, worldPositionStays: false);
                _propOnHead = headMode;
            }

            ApplyHatOffset(target, headMode);
            SyncMaterials(target);
        }

        // Returns true when the group root used at Setup is no longer the current live root
        // (e.g. after a game-mode toggle that replaces the PhysicsGroup root with a new static root).
        // The caller should teardown and rebuild the preview in that case.
        public bool NeedsRebuildForTarget(LevelEditorObject target)
        {
            var root = ResolveGroupRoot(target);
            int id   = root != null ? root.GetInstanceID() : (target?.gameObject.GetInstanceID() ?? 0);
            return id != _groupRootInstanceId;
        }

        // Copies sharedMaterials from the live prop hierarchy to the preview clone.
        // Called each frame so material-construction drags show up in the preview immediately.
        void SyncMaterials(LevelEditorObject target)
        {
            if (_propClone == null || target == null) return;
            var groupRoot = ResolveGroupRoot(target);
            var liveRoot  = groupRoot != null ? groupRoot : target.gameObject;
            var src = liveRoot.GetComponentsInChildren<Renderer>(true);
            var dst = _propClone.GetComponentsInChildren<Renderer>(true);
            int n = src.Length < dst.Length ? src.Length : dst.Length;
            for (int i = 0; i < n; i++)
            {
                if (src[i] == null || dst[i] == null) continue;
                dst[i].sharedMaterials = src[i].sharedMaterials;
            }
        }

        public void ApplyHatOffset(LevelEditorObject target, bool headMode = true)
        {
            if (_propClone == null) return;
            var pos = headMode ? target.hatOffsetPos : target.grabOffsetPos;
            var rot = headMode ? target.hatOffsetRot : target.grabOffsetRot;
            if (headMode)
            {
                _propClone.transform.localPosition = HatDefaultLocalPos + pos;
                _propClone.transform.localRotation = Quaternion.Euler(-25f + rot.x, rot.y, rot.z);
            }
            else
            {
                // Hand mode: grabOffsetPos/Rot are the full local offset; no extra base.
                _propClone.transform.localPosition = pos;
                _propClone.transform.localRotation = Quaternion.Euler(rot.x, rot.y, rot.z);
            }
        }

        public void UpdateCameraPosition()
        {
            if (_cam == null || _previewHeadBone == null) return;
            var focusBone = (!_headMode && _previewHandBone != null)
                ? _previewHandBone : _previewHeadBone;

            var focusPos = focusBone.position;
            float rad    = _orbitAngleY * Mathf.Deg2Rad;
            var offset   = new Vector3(Mathf.Sin(rad) * _orbitDist, 0.15f, Mathf.Cos(rad) * _orbitDist);
            _cam.transform.position = focusPos + offset;
            _cam.transform.LookAt(focusPos + new Vector3(0f, 0.05f, 0f));
            if (_lightGO != null)
                _lightGO.transform.position = focusPos + new Vector3(0f, 0.5f, 1.0f);
        }

        public void ApplyHairShader(float amt, Hat hat)
        {
            if (_playerClone == null) return;
            var smr = _playerClone.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr == null) return;
            Material hairMat = null;
            foreach (var m in smr.materials)
                if (m != null && m.name.Contains("Hair2_lux")) { hairMat = m; break; }
            if (hairMat == null) return;
            var up = hat != null
                ? new Vector4(hat.hairlineUpVec.x, hat.hairlineUpVec.y, hat.hairlineUpVec.z, 0f)
                : new Vector4(0f, 1f, 1f, 0f);
            hairMat.SetFloat("_HatMax", amt);
            hairMat.SetVector("_HatUp", up);
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
            _previewHandBone = null;
            _target          = null;
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

        // Returns the group root for a grouped prop. Falls back to the target's actual scene
        // parent if the group root isn't registered in _groupRoots (e.g., after a game-mode
        // toggle that removed the physics root before the static root was recreated).
        static GameObject ResolveGroupRoot(LevelEditorObject target)
        {
            if (target == null || target.groupId <= 0) return null;
            var root = GroupManager.GetGroupRoot(target.groupId);
            if (root != null) return root;
            var parent = target.transform.parent?.gameObject;
            if (parent == null) return null;
            for (int i = 0; i < parent.transform.childCount; i++)
            {
                var sibling = parent.transform.GetChild(i).GetComponent<LevelEditorObject>();
                if (sibling != null && sibling != target && sibling.groupId == target.groupId)
                    return parent;
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
