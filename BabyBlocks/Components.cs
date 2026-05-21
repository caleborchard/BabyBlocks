using System;
using UnityEngine;

namespace BabyBlocks
{
    public class LevelEditorObject : MonoBehaviour
    {
        public LevelEditorObject(IntPtr ptr) : base(ptr) { }
        public string objectType    = "Cube"; // primitive type name, or "Addressable"
        public string addressableKey = "";    // non-empty for game asset props; stable ID for save/load
        public int chunkIndex = -1;
        public Vector2Int chunkCoord = new Vector2Int(-1, -1);
        public Vector3 loopBasePosition;
        public bool hasLoopBasePosition;
    }

    public class GizmoHandle : MonoBehaviour
    {
        public GizmoHandle(IntPtr ptr) : base(ptr) { }
        // 0=X, 1=Y, 2=Z, 3=free sphere, 4=XY, 5=YZ, 6=XZ
        public int axisIndex;
    }
}
