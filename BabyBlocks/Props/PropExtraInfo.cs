using System;
using System.Collections.Generic;

namespace BabyBlocks
{
    [Serializable]
    public class PropExtraInfo
    {
        public string id;
        public string displayName;
        public string category;
        public bool excluded;
        public bool useRenderMeshCollider;
        public string colliderIgnoredSubmeshes;
        public string overrideMaterialId;
        public string nativeMaterialName;       // true original material, stored when first override is applied
        public string materialSourcePropId;     // prop whose asset contains the override material
        public string surfaceType;
        public int index;
        public List<string> disabledRenderers = new();
        public List<string> perSlotMaterialOverrides;
        public int forcedMaterialSlots;         // 0 = auto-detect; >1 = manual multi-slot
        public bool isBush;
        public float bushRadius;
        public int soundGrassType = 1;
        public bool keepOriginalHierarchy;
        public bool disableBaking;
    }

    [Serializable]
    class PropExtraInfoSave
    {
        public int nextIndex = 1;
        public List<PropExtraInfo> items = new();
    }
}
