using System.Collections.Generic;
using Il2Cpp;
using UnityEngine;

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
                scale    = t.localScale,
                rotation = t.rotation,
            };

            public void Apply(Transform t)
            {
                t.position   = position;
                t.localScale = scale;
                t.rotation   = rotation;
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
                _live  = obj;
            }

            public void Undo()
            {
                if (LevelEditor.selectedObject == _live) LevelEditor.selectedObject = null;
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
                _live  = null;
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
                _obj    = obj;
                _before = before;
                _after  = after;
            }

            public void Undo() { if (_obj != null) { _before.Apply(_obj.transform); LevelEditor.Select(_obj); } }
            public void Redo() { if (_obj != null) { _after.Apply(_obj.transform);  LevelEditor.Select(_obj); } }
        }

        static readonly List<IAction> _undo = new();
        static readonly List<IAction> _redo = new();

        public static void PushSpawn(LevelEditorObject obj)  => Push(new SpawnAction(obj));
        public static void PushDelete(LevelEditorObject obj) => Push(new DeleteAction(obj));

        public static void PushTransform(LevelEditorObject obj,
            Vector3 posBefore, Vector3 scaleBefore, Quaternion rotBefore)
        {
            var before = new TransformState { position = posBefore, scale = scaleBefore, rotation = rotBefore };
            var after  = TransformState.Capture(obj.transform);
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
