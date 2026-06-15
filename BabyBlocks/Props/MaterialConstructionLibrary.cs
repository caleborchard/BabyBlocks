using System;
using System.Collections.Generic;

namespace BabyBlocks
{
    // A user-defined material/surface combo, created in the Material Construction
    // tab of the debug Prop Details panel and then dragged onto placed props to
    // re-skin that single instance (see MaterialConstructionPanel). Persisted as
    // part of PropMetadataPanel's prop_metadata save (JSON, binary export, and
    // embedded baked-in resource).
    [Serializable]
    public class MaterialConstructionEntry
    {
        public int id = -1;
        public string name = "New Material";
        public string materialName = "";
        public string surfaceType = "";
    }

    // Thin facade over PropMetadataPanel's material construction storage, kept so
    // callers don't need to know it lives alongside the prop metadata.
    static class MaterialConstructionLibrary
    {
        public static List<MaterialConstructionEntry> Entries => PropMetadataStore.MaterialConstructions;

        public static void Save() => PropMetadataStore.SaveMaterialConstructions();
        public static void MarkDirty() => PropMetadataStore.MarkMaterialConstructionsDirty();

        public static MaterialConstructionEntry CreateNew() => PropMetadataStore.CreateMaterialConstruction();
        public static void Delete(MaterialConstructionEntry entry) => PropMetadataStore.DeleteMaterialConstruction(entry);
        public static MaterialConstructionEntry FindById(int id) => PropMetadataStore.FindMaterialConstructionById(id);
    }
}
