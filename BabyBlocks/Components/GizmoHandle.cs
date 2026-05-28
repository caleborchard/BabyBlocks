using System;
using UnityEngine;

namespace BabyBlocks
{
    public class GizmoHandle : MonoBehaviour
    {
        public GizmoHandle(IntPtr ptr) : base(ptr) { }
        // 0=X, 1=Y, 2=Z, 3=free sphere, 4=XY, 5=YZ, 6=XZ
        public int axisIndex;
    }
}
