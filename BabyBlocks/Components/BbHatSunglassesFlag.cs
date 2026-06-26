using System.Collections.Generic;
using Il2Cpp;

namespace BabyBlocks
{
    // Tracks which LevelEditorObject GOs have "Act as Sunglasses" enabled.
    // Uses a HashSet of GO instance IDs instead of AddComponent<T> because
    // Il2CppInterop's generic GetComponent<T> static constructor crashes for
    // mod defined types that aren't yet registered in the native Il2Cpp runtime.
    internal static class BbHatSunglassesFlag
    {
        static readonly HashSet<int> _ids = new();

        internal static void Clear() => _ids.Clear();

        internal static void Set(LevelEditorObject leo, bool on)
        {
            if (leo?.gameObject == null) return;
            int id = leo.gameObject.GetInstanceID();
            if (on) _ids.Add(id);
            else    _ids.Remove(id);
        }

        internal static bool Has(LevelEditorObject leo) =>
            leo?.gameObject != null && _ids.Contains(leo.gameObject.GetInstanceID());
    }
}
