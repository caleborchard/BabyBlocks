using System.Collections.Generic;
using Il2Cpp;
using UnityEngine;
using System.Linq;

namespace BabyBlocks
{
    static class LevelEditorHistory
    {
        struct TransformState
        {
            public Vector3    position, scale;
            public Quaternion rotation;

            public static TransformState Capture(Transform t) => new()
            {
                position = t.position,
                scale = t.localScale,
                rotation = t.rotation,
            };

            public void Apply(Transform t)
            {
                t.position = position;
                t.localScale = scale;
                t.rotation = rotation;
            }
        }

        interface IAction { void Undo(); void Redo(); }

        class SpawnAction : IAction
        {
            // One of these is set depending on whether this is a primitive or addressable prop.
            readonly string        _addressableKey;
            readonly PrimitiveType _primType;

            TransformState    _state;
            LevelEditorObject _live;

            public SpawnAction(LevelEditorObject obj)
            {
                _addressableKey = obj.addressableKey;
                if (string.IsNullOrEmpty(_addressableKey))
                    System.Enum.TryParse(obj.objectType, out _primType);
                _state = TransformState.Capture(obj.transform);
                _live = obj;
            }

            public void Undo()
            {
                // Clear selection before Remove so GizmoRenderer never reads
                // the destroyed object's transform and jumps to world origin.
                LevelEditor.ClearAllSelectionState();
                LevelEditorManager.Instance?.Remove(_live);
                _live = null;
            }

            public void Redo()
            {
                _live = Spawn(_state.position);
                if (_live != null) { _state.Apply(_live.transform); LevelEditor.Select(_live); }
            }

            LevelEditorObject Spawn(Vector3 pos)
            {
                if (!string.IsNullOrEmpty(_addressableKey))
                {
                    var info = PropLibrary.FindById(_addressableKey);
                    return info != null ? LevelEditorManager.Instance?.SpawnFromPropInfo(info, pos) : null;
                }
                return LevelEditorManager.Instance?.SpawnPrimitive(_primType, pos);
            }
        }

        class DeleteAction : IAction
        {
            readonly string        _addressableKey;
            readonly PrimitiveType _primType;

            TransformState    _state;
            LevelEditorObject _live;

            public DeleteAction(LevelEditorObject obj)
            {
                _addressableKey = obj.addressableKey;
                if (string.IsNullOrEmpty(_addressableKey))
                    System.Enum.TryParse(obj.objectType, out _primType);
                _state = TransformState.Capture(obj.transform);
                _live = null;
            }

            public void Undo()
            {
                _live = Spawn(_state.position);
                if (_live != null) { _state.Apply(_live.transform); LevelEditor.Select(_live); }
            }

            public void Redo()
            {
                if (_live == null) return;
                if (LevelEditor.selectedObject == _live) LevelEditor.selectedObject = null;
                LevelEditorManager.Instance?.Remove(_live);
                _live = null;
            }

            LevelEditorObject Spawn(Vector3 pos)
            {
                if (!string.IsNullOrEmpty(_addressableKey))
                {
                    var info = PropLibrary.FindById(_addressableKey);
                    return info != null ? LevelEditorManager.Instance?.SpawnFromPropInfo(info, pos) : null;
                }
                return LevelEditorManager.Instance?.SpawnPrimitive(_primType, pos);
            }
        }

        class TransformAction : IAction
        {
            readonly LevelEditorObject _obj;
            readonly TransformState    _before, _after;

            public TransformAction(LevelEditorObject obj, TransformState before, TransformState after)
            {
                _obj = obj;
                _before = before;
                _after = after;
            }

            public void Undo() { if (_obj != null) { _before.Apply(_obj.transform); LevelEditorManager.Instance?.SyncLoopBase(_obj); LevelEditor.Select(_obj); } }
            public void Redo() { if (_obj != null) { _after.Apply(_obj.transform);  LevelEditorManager.Instance?.SyncLoopBase(_obj); LevelEditor.Select(_obj); } }
        }

        class MaterialAction : IAction
        {
            readonly LevelEditorObject _obj;
            readonly Renderer[]        _renderers;
            readonly Material[][]      _matsBefore, _matsAfter;
            readonly GameObject[]      _tagObjs;
            readonly string[]          _tagsBefore, _tagsAfter;
            readonly int               _idBefore, _idAfter;

            public MaterialAction(LevelEditorObject obj, Renderer[] renderers,
                Material[][] matsBefore, Material[][] matsAfter,
                GameObject[] tagObjs, string[] tagsBefore, string[] tagsAfter,
                int idBefore, int idAfter)
            {
                _obj = obj;
                _renderers = renderers;
                _matsBefore = matsBefore;
                _matsAfter = matsAfter;
                _tagObjs = tagObjs;
                _tagsBefore = tagsBefore;
                _tagsAfter = tagsAfter;
                _idBefore = idBefore;
                _idAfter = idAfter;
            }

            public void Undo() => Apply(_matsBefore, _tagsBefore, _idBefore);
            public void Redo() => Apply(_matsAfter, _tagsAfter, _idAfter);

            void Apply(Material[][] mats, string[] tags, int id)
            {
                if (_obj == null) return;
                for (int i = 0; i < _renderers.Length; i++)
                    if (_renderers[i] != null) _renderers[i].sharedMaterials = mats[i];
                for (int i = 0; i < _tagObjs.Length; i++)
                    if (_tagObjs[i] != null) { try { _tagObjs[i].tag = tags[i]; } catch { } }
                _obj.materialConstructionId = id;
                LevelEditor.Select(_obj);
            }
        }

        class GroupScaleAction : IAction
        {
            readonly int                  _groupId;
            readonly GameObject           _groupRoot;
            readonly LevelEditorObject[]  _members;
            readonly Vector3              _rootPosBefore,      _rootPosAfter;
            readonly Vector3              _displayScaleBefore, _displayScaleAfter;
            readonly Vector3[]            _scalesBefore,       _scalesAfter;
            readonly Vector3[]            _localPosBefore,     _localPosAfter;

            public GroupScaleAction(int groupId, GameObject groupRoot, LevelEditorObject[] members,
                Vector3 rootPosBefore, Vector3 displayScaleBefore,
                Vector3[] scalesBefore, Vector3[] localPosBefore)
            {
                _groupId = groupId;
                _groupRoot = groupRoot;
                _members = members;
                _rootPosBefore = rootPosBefore;
                _displayScaleBefore = displayScaleBefore;
                _scalesBefore = scalesBefore;
                _localPosBefore = localPosBefore;

                // Capture after-state at construction time (called immediately after the change).
                _rootPosAfter = groupRoot?.transform.position ?? Vector3.zero;
                _displayScaleAfter = GroupManager.GetGroupDisplayScale(groupId);
                _scalesAfter = new Vector3[members.Length];
                _localPosAfter = new Vector3[members.Length];
                for (int i = 0; i < members.Length; i++)
                {
                    if (members[i] == null) continue;
                    _scalesAfter[i] = members[i].transform.localScale;
                    _localPosAfter[i] = members[i].transform.localPosition;
                }
            }

            public bool HasChanges()
            {
                if (_displayScaleBefore != _displayScaleAfter) return true;
                for (int i = 0; i < _members.Length; i++)
                    if (_scalesBefore[i] != _scalesAfter[i]) return true;
                return false;
            }

            public void Undo() => Apply(_scalesBefore, _localPosBefore, _rootPosBefore, _displayScaleBefore);
            public void Redo() => Apply(_scalesAfter,  _localPosAfter,  _rootPosAfter,  _displayScaleAfter);

            void Apply(Vector3[] scales, Vector3[] localPositions, Vector3 rootPos, Vector3 displayScale)
            {
                if (_groupRoot != null) _groupRoot.transform.position = rootPos;
                for (int i = 0; i < _members.Length; i++)
                {
                    if (_members[i] == null) continue;
                    _members[i].transform.localScale = scales[i];
                    _members[i].transform.localPosition = localPositions[i];
                }
                GroupManager.SetGroupDisplayScale(_groupId, displayScale);
                // Re-sync each member's loop base pose to the restored world positions, or
                // LevelEditorManager.Update's chunk-loop snaps them back to the pre-undo base
                // (same revert that broke text-box group scaling). Matches TransformAction.
                var lem = LevelEditorManager.Instance;
                if (lem != null)
                    foreach (var m in _members)
                        if (m != null) lem.SyncLoopBase(m);
                // Re-select a live member so the gizmo refreshes.
                var live = _members.FirstOrDefault(m => m != null);
                if (live != null) LevelEditor.Select(live);
            }
        }

        static readonly List<IAction> _undo = new();
        static readonly List<IAction> _redo = new();

        public static void PushSpawn(LevelEditorObject obj)  => Push(new SpawnAction(obj));
        public static void PushDelete(LevelEditorObject obj) => Push(new DeleteAction(obj));

        class GroupRotateAction : IAction
        {
            readonly int                 _groupId;
            readonly GameObject          _groupRoot;
            readonly LevelEditorObject[] _members;
            readonly Vector3             _rootPosBefore, _rootPosAfter;
            readonly Quaternion          _rootRotBefore, _rootRotAfter;

            public GroupRotateAction(int groupId, GameObject groupRoot, LevelEditorObject[] members,
                Vector3 rootPosBefore, Quaternion rootRotBefore)
            {
                _groupId = groupId;
                _groupRoot = groupRoot;
                _members = members;
                _rootPosBefore = rootPosBefore;
                _rootRotBefore = rootRotBefore;
                _rootPosAfter = groupRoot?.transform.position ?? Vector3.zero;
                _rootRotAfter = groupRoot?.transform.rotation ?? Quaternion.identity;
            }

            public bool HasChanges() => _rootPosBefore != _rootPosAfter || _rootRotBefore != _rootRotAfter;

            void Apply(Vector3 pos, Quaternion rot)
            {
                if (_groupRoot == null) return;
                _groupRoot.transform.SetPositionAndRotation(pos, rot);
                var live = _members.FirstOrDefault(m => m != null);
                if (live != null) LevelEditor.Select(live);
            }

            public void Undo() => Apply(_rootPosBefore, _rootRotBefore);
            public void Redo() => Apply(_rootPosAfter,  _rootRotAfter);
        }

        public static void PushGroupRotate(int groupId, GameObject groupRoot, LevelEditorObject[] members,
            Vector3 rootPosBefore, Quaternion rootRotBefore)
        {
            var action = new GroupRotateAction(groupId, groupRoot, members, rootPosBefore, rootRotBefore);
            if (action.HasChanges()) Push(action);
        }

        // Collects member state and pushes an undo entry for a group scale change.
        // Call AFTER the scale has been applied; the before-state is supplied by the caller.
        public static void PushGroupScale(int groupId, GameObject groupRoot,
            LevelEditorObject[] members,
            Vector3 rootPosBefore, Vector3 displayScaleBefore,
            Vector3[] memberScalesBefore, Vector3[] memberLocalPosBefore)
        {
            var action = new GroupScaleAction(groupId, groupRoot, members,
                rootPosBefore, displayScaleBefore, memberScalesBefore, memberLocalPosBefore);
            if (action.HasChanges()) Push(action);
        }

        public static void PushMaterial(LevelEditorObject obj, Renderer[] renderers, Material[][] matsBefore,
            GameObject[] tagObjs, string[] tagsBefore, int idBefore)
        {
            var matsAfter = new Material[renderers.Length][];
            for (int i = 0; i < renderers.Length; i++)
                matsAfter[i] = renderers[i] != null ? renderers[i].sharedMaterials : null;

            var tagsAfter = new string[tagObjs.Length];
            for (int i = 0; i < tagObjs.Length; i++)
                tagsAfter[i] = tagObjs[i] != null ? tagObjs[i].tag : null;

            Push(new MaterialAction(obj, renderers, matsBefore, matsAfter, tagObjs, tagsBefore, tagsAfter,
                idBefore, obj.materialConstructionId));
        }

        public static void PushTransform(LevelEditorObject obj,
            Vector3 posBefore, Vector3 scaleBefore, Quaternion rotBefore)
        {
            var before = new TransformState { position = posBefore, scale = scaleBefore, rotation = rotBefore };
            var after = TransformState.Capture(obj.transform);
            if (before.position != after.position || before.scale != after.scale || before.rotation != after.rotation)
                Push(new TransformAction(obj, before, after));
        }

        public static void Undo()
        {
            if (_undo.Count == 0) return;
            var a = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            a.Undo();
            _redo.Add(a);
        }

        public static void Redo()
        {
            if (_redo.Count == 0) return;
            var a = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            a.Redo();
            _undo.Add(a);
        }

        static void Push(IAction action)
        {
            _undo.Add(action);
            _redo.Clear();
        }
    }
}
