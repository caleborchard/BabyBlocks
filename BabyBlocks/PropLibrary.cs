using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace BabyBlocks
{
    public class PropMeshPart
    {
        public Mesh       mesh;
        public Material[] materials;
        public Vector3    localPosition;
        public Quaternion localRotation;
        public Vector3    localScale;
    }

    public class PropInfo
    {
        public readonly string id;
        public          string displayName;

        public List<PropMeshPart> parts     = new();
        public bool               isLoaded;
        public bool               isInvalid;

        public int        gpuiIndex       = -1;
        public bool       IsGpui          => gpuiIndex >= 0;

        public bool HasMesh    => parts != null && parts.Count > 0;
        public bool IsPrimitive => id.StartsWith("primitive://", StringComparison.Ordinal);

        AsyncOperationHandle<GameObject> _handle;

        public PropInfo(string key, string name = null)
        {
            id          = key;
            displayName = name ?? Path.GetFileNameWithoutExtension(key)
                .Replace("_player", "").Replace("_Player", "");
        }

        public void SetHandle(AsyncOperationHandle<GameObject> h) => _handle = h;
    }

    public static class PropLibrary
    {
        public static bool IncludePlayerProps = false;
        public static bool IncludeMeshes      = true;
        public static bool IncludeAnimals     = false;
        public static bool IncludeHats        = false;
        public static bool IncludeNPCs        = false;

        static readonly List<PropInfo>               _all      = new();
        static readonly List<PropInfo>               _filtered = new();
        static readonly Dictionary<string, PropInfo> _byId     = new();

        public static IReadOnlyList<PropInfo> AllProps      => _all;
        public static IReadOnlyList<PropInfo> FilteredProps => _filtered;
        public static bool                    IsInitialized { get; private set; }

        public static PropInfo FindById(string id) => _byId.TryGetValue(id, out var p) ? p : null;

        static readonly string[] PrimitiveNames = { "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad" };

        public static void Init()
        {
            _all.Clear();
            _filtered.Clear();
            _byId.Clear();
            IsInitialized = false;

            // Always add Unity primitives first (pinned at top, not sorted).
            foreach (var name in PrimitiveNames)
                _all.Add(new PropInfo($"primitive://{name}", name));

            int primitiveCount = _all.Count;

            try { EnumerateFromCatalog(); }
            catch (Exception e) { MelonLogger.Error($"[PropLibrary] Catalog parse failed: {e.Message}"); }

            // Sort only the non-primitive entries.
            _all.Sort(primitiveCount, _all.Count - primitiveCount,
                Comparer<PropInfo>.Create((a, b) =>
                    string.Compare(a.displayName, b.displayName, StringComparison.OrdinalIgnoreCase)));

            foreach (var p in _all) _byId[p.id] = p;
            BuildFiltered();
            IsInitialized = true;
            MelonLogger.Msg($"[PropLibrary] {_all.Count} props enumerated ({primitiveCount} primitives), {_filtered.Count} after filter.");
        }

        // ── Catalog enumeration ──────────────────────────────────────────────

        static void EnumerateFromCatalog()
        {
            string catalogPath = Path.Combine(Application.streamingAssetsPath, "aa", "catalog.json");
            if (!File.Exists(catalogPath))
            {
                MelonLogger.Error($"[PropLibrary] catalog.json not found at {catalogPath}");
                return;
            }

            string json = File.ReadAllText(catalogPath);
            var    seen = new HashSet<string>(StringComparer.Ordinal);

            ScanTextForPaths(json, seen);

            int kdIdx = json.IndexOf("\"m_KeyDataString\"", StringComparison.Ordinal);
            if (kdIdx >= 0)
            {
                int valStart = json.IndexOf('"', kdIdx + 17) + 1;
                int valEnd   = json.IndexOf('"', valStart);
                if (valStart > 0 && valEnd > valStart)
                {
                    try
                    {
                        byte[] bytes       = Convert.FromBase64String(json.Substring(valStart, valEnd - valStart));
                        string decodedKeys = Encoding.UTF8.GetString(bytes);
                        ScanTextForPaths(decodedKeys, seen);
                    }
                    catch (Exception e)
                    {
                        MelonLogger.Warning($"[PropLibrary] m_KeyDataString decode failed: {e.Message}");
                    }
                }
            }

            MelonLogger.Msg($"[PropLibrary] Catalog scan → {_all.Count} props.");
        }

        static void ScanTextForPaths(string text, HashSet<string> seen)
        {
            int i = 0;
            while (i < text.Length)
            {
                int start = text.IndexOf("Assets/", i, StringComparison.Ordinal);
                if (start < 0) break;
                int end = start;
                while (end < text.Length)
                {
                    char c = text[end];
                    if (c == '"' || c == '\0' || c < ' ') break;
                    end++;
                }
                i = end + 1;
                string entry = text.Substring(start, end - start);
                if (!entry.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsLowerLodVariant(entry)) continue;
                if (!ShouldInclude(entry)) continue;
                if (!seen.Add(entry)) continue;
                _all.Add(new PropInfo(entry));
            }
        }

        // Strips "_LOD{digit}..." suffix: "Root1_LOD1" → "Root1", "Rock_LOD0-001" → "Rock"
        static string StripLodSuffix(string name)
        {
            int idx = name.IndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
            return idx >= 0 ? name.Substring(0, idx) : name;
        }

        static bool IsLowerLodVariant(string key)
        {
            string name  = Path.GetFileNameWithoutExtension(key);
            int    idx   = name.IndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            int after = idx + 4;
            if (after >= name.Length) return false;
            char c = name[after];
            return c >= '1' && c <= '9';
        }

        static bool ShouldInclude(string key)
        {
            if (IncludePlayerProps && key.StartsWith("Assets/_Props/_PlayerProps/", StringComparison.Ordinal)) return true;
            if (IncludeMeshes      && key.StartsWith("Assets/Meshes/",              StringComparison.Ordinal)) return true;
            if (IncludeAnimals     && key.StartsWith("Assets/ANIMALS/",             StringComparison.Ordinal)) return true;
            if (IncludeHats        && key.StartsWith("Assets/Prefabs/Hats/",        StringComparison.Ordinal)) return true;
            if (IncludeNPCs        && key.StartsWith("Assets/Prefabs/NPCS/",        StringComparison.Ordinal)) return true;
            return false;
        }

        static void BuildFiltered()
        {
            _filtered.Clear();
            foreach (var p in _all) _filtered.Add(p);
        }

        // ── GPUI prop scan ───────────────────────────────────────────────────

        // Called once BestRegionLoader is live. Identifies collision-only _player
        // prefabs in loadedProps (no MeshRenderer, only MeshCollider) and resolves
        // a visual mesh + material for each one.  The game no longer uses GPUI; it
        // uses a custom job-based system with three parallel arrays:
        //
        //   propLODIndex[propRefIdx]           → first LOD slot for that prop
        //   gcMeshIndex[lodSlot]               → index into gcMeshes/gcMaterials
        //   loadedGCMeshes[gcIdx] / gcMeshes[gcIdx]  → visual Mesh
        //   gcMaterials[gcIdx]                 → Material
        //
        // Source priority:
        //   1. gc arrays (covers all terrain props with correct MicroSplat materials)
        //   2. Addressable catalog prefab (wooden/construction props only)
        //   3. Collider-mesh fallback (grey geometry, always succeeds)
        public static void ScanGpuiProps()
        {
            var brl = BestRegionLoader.me;
            if (brl == null)
            {
                MelonLogger.Warning("[PropLibrary] BestRegionLoader not ready — call ScanGpuiProps later.");
                return;
            }

            var loaded = brl.loadedProps;
            if (loaded == null || loaded.Length == 0)
            {
                MelonLogger.Warning("[PropLibrary] loadedProps is null/empty.");
                return;
            }

            // ── GC rendering arrays — authoritative source of visual mesh + material ──
            //
            // The game's terrain prop renderer uses a three-level indirection:
            //   propLODIndex[i]       → lodSlot  (first LOD entry for prop i)
            //   gcMeshIndex[lodSlot]  → gcIdx    (index into mesh/material arrays)
            //   loadedGCMeshes[gcIdx] → Mesh     (actually-loaded Mesh asset)
            //   gcMaterials[gcIdx]    → Material
            //
            // propLODs[lodSlot] is a ScriptableObject with LOD config data (blend
            // distances etc.) — NOT the mesh/material source, so we don't load it.
            Il2CppStructArray<int>         propLODIdxArr     = null;
            Il2CppStructArray<int>         gcMeshIdxArr      = null;
            Il2CppReferenceArray<Mesh>     loadedGCMeshesArr = null;
            Il2CppReferenceArray<Mesh>     gcMeshesArr       = null;
            Il2CppReferenceArray<Material> gcMaterialsArr    = null;
            try
            {
                var rawIdx = ReflectGet(brl, "propLODIndex");
                if (rawIdx != null)
                {
                    propLODIdxArr = rawIdx as Il2CppStructArray<int>;
                    if (propLODIdxArr == null)
                        MelonLogger.Warning($"[PropLibrary] propLODIndex: unexpected type {rawIdx.GetType().FullName}");
                }

                var rawGcIdx = ReflectGet(brl, "gcMeshIndex");
                if (rawGcIdx != null)
                {
                    gcMeshIdxArr = rawGcIdx as Il2CppStructArray<int>;
                    if (gcMeshIdxArr == null)
                        MelonLogger.Warning($"[PropLibrary] gcMeshIndex: unexpected type {rawGcIdx.GetType().FullName}");
                }

                var rawLoadedGC = ReflectGet(brl, "loadedGCMeshes");
                if (rawLoadedGC != null)
                {
                    loadedGCMeshesArr = rawLoadedGC as Il2CppReferenceArray<Mesh>;
                    if (loadedGCMeshesArr == null)
                        MelonLogger.Warning($"[PropLibrary] loadedGCMeshes: unexpected type {rawLoadedGC.GetType().FullName}");
                }

                var rawGcMeshes = ReflectGet(brl, "gcMeshes");
                if (rawGcMeshes != null)
                {
                    gcMeshesArr = rawGcMeshes as Il2CppReferenceArray<Mesh>;
                    if (gcMeshesArr == null)
                        MelonLogger.Warning($"[PropLibrary] gcMeshes: unexpected type {rawGcMeshes.GetType().FullName}");
                }

                var rawGcMats = ReflectGet(brl, "gcMaterials");
                if (rawGcMats != null)
                {
                    gcMaterialsArr = rawGcMats as Il2CppReferenceArray<Material>;
                    if (gcMaterialsArr == null)
                        MelonLogger.Warning($"[PropLibrary] gcMaterials: unexpected type {rawGcMats.GetType().FullName}");
                }
            }
            catch (Exception e) { MelonLogger.Warning($"[PropLibrary] GC array reflect failed: {e.Message}"); }

            MelonLogger.Msg($"[PropLibrary] propLODIndex:{(propLODIdxArr == null ? "null" : propLODIdxArr.Length.ToString())}  " +
                            $"gcMeshIndex:{(gcMeshIdxArr == null ? "null" : gcMeshIdxArr.Length.ToString())}  " +
                            $"loadedGCMeshes:{(loadedGCMeshesArr == null ? "null" : loadedGCMeshesArr.Length.ToString())}  " +
                            $"gcMeshes:{(gcMeshesArr == null ? "null" : gcMeshesArr.Length.ToString())}  " +
                            $"gcMaterials:{(gcMaterialsArr == null ? "null" : gcMaterialsArr.Length.ToString())}");

            // ── Diagnostic: all in-memory materials ──────────────────────────────
            // This tells us which materials are actually loaded for name matching.
            try
            {
                var allMatsArr = Resources.FindObjectsOfTypeAll<Material>();
                var sbAllMat = new StringBuilder($"[PropLibrary] All loaded materials ({allMatsArr.Length}):");
                foreach (var m in allMatsArr)
                    if (m != null) sbAllMat.Append($"\n  \"{m.name}\"  shader={m.shader?.name}");
                MelonLogger.Msg(sbAllMat.ToString());
            }
            catch (Exception e) { MelonLogger.Warning($"[PropLibrary] Material dump failed: {e.Message}"); }

            // ── Diagnostic: GabeLOD + GabeLODLvl field dump ─────────────────────
            // Load propLODs[0], dump its IL2CPP fields, then reflect into lods[0]
            // to find where the material reference lives inside GabeLODLvl.
            var propLODsArr2 = ReflectGet(brl, "propLODs") as Il2CppReferenceArray<AssetReference>;
            if (propLODsArr2 != null && propLODsArr2.Length > 0 && propLODIdxArr != null)
            {
                try
                {
                    int slot0 = propLODIdxArr.Length > 0 ? propLODIdxArr[0] : 0;
                    if (slot0 >= 0 && slot0 < propLODsArr2.Length)
                    {
                        var lodRef0 = propLODsArr2[slot0];
                        if (lodRef0 != null)
                        {
                            var handle0 = Addressables.LoadAssetAsync<UnityEngine.Object>(lodRef0);
                            var asset0  = handle0.WaitForCompletion();
                            if (asset0 != null)
                            {
                                IntPtr nativeClass = IL2CPP.il2cpp_object_get_class(asset0.Pointer);
                                string cls = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(nativeClass));
                                string ns  = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(nativeClass));
                                var sbFld = new StringBuilder($"[PropLibrary] propLODs[{slot0}] type={ns}.{cls}  name=\"{asset0.name}\"");
                                IntPtr iter = IntPtr.Zero;
                                while (true)
                                {
                                    IntPtr fld = IL2CPP.il2cpp_class_get_fields(nativeClass, ref iter);
                                    if (fld == IntPtr.Zero) break;
                                    string fn = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_field_get_name(fld));
                                    IntPtr ft = IL2CPP.il2cpp_field_get_type(fld);
                                    string tn = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_type_get_name(ft));
                                    sbFld.Append($"\n  {tn,-40} {fn}");
                                }
                                MelonLogger.Msg(sbFld.ToString());

                                // ── Dive into GabeLODLvl[0] to find material field ───
                                try
                                {
                                    var lodsObj = ReflectGet(asset0, "lods");
                                    var lodsArr = lodsObj as System.Array;
                                    if (lodsArr == null && lodsObj != null)
                                    {
                                        // Try via Il2CppObjectBase
                                        var cast = lodsObj as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                                        if (cast != null)
                                        {
                                            IntPtr arrClass = IL2CPP.il2cpp_object_get_class(cast.Pointer);
                                            IntPtr elemClass = IL2CPP.il2cpp_class_get_element_class(arrClass);
                                            string elemName = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(elemClass));
                                            MelonLogger.Msg($"[PropLibrary] GabeLOD.lods element type: {elemName}");

                                            // Dump GabeLODLvl fields
                                            var sbLvl = new StringBuilder($"[PropLibrary] GabeLODLvl fields:");
                                            IntPtr iter2 = IntPtr.Zero;
                                            while (true)
                                            {
                                                IntPtr fld2 = IL2CPP.il2cpp_class_get_fields(elemClass, ref iter2);
                                                if (fld2 == IntPtr.Zero) break;
                                                string fn2 = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_field_get_name(fld2));
                                                IntPtr ft2 = IL2CPP.il2cpp_field_get_type(fld2);
                                                string tn2 = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_type_get_name(ft2));
                                                sbLvl.Append($"\n  {tn2,-40} {fn2}");
                                            }
                                            MelonLogger.Msg(sbLvl.ToString());
                                        }
                                    }
                                    else if (lodsArr != null)
                                    {
                                        MelonLogger.Msg($"[PropLibrary] GabeLOD.lods as System.Array, Length={lodsArr.Length}, elem type={lodsArr.GetType().GetElementType()?.Name ?? "?"}");
                                        if (lodsArr.Length > 0)
                                        {
                                            var elem = lodsArr.GetValue(0);
                                            if (elem != null)
                                            {
                                                var sbLvl = new StringBuilder($"[PropLibrary] GabeLODLvl[0] type={elem.GetType().Name}:");
                                                foreach (var f in elem.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                                                    sbLvl.Append($"\n  {f.FieldType.Name,-40} {f.Name} = {f.GetValue(elem)}");
                                                MelonLogger.Msg(sbLvl.ToString());
                                            }
                                        }
                                    }
                                }
                                catch (Exception e2) { MelonLogger.Warning($"[PropLibrary] GabeLODLvl dive failed: {e2.Message}"); }

                                // intentionally not releasing — keeps asset in memory
                            }
                            else MelonLogger.Warning($"[PropLibrary] propLODs[{slot0}] load returned null");
                        }
                    }
                }
                catch (Exception e) { MelonLogger.Warning($"[PropLibrary] propLODs field dump failed: {e.Message}"); }
            }

            // ── Diagnostic: dump all 10 gcMaterials ──────────────────────────────
            if (gcMaterialsArr != null)
            {
                var sbGcMat = new StringBuilder($"[PropLibrary] gcMaterials[{gcMaterialsArr.Length}]:");
                for (int k = 0; k < gcMaterialsArr.Length; k++)
                {
                    var m = gcMaterialsArr[k];
                    sbGcMat.Append($"\n  [{k}] \"{m?.name ?? "null"}\"  shader={m?.shader?.name ?? "null"}  mainTex={MatTexName(m, "_MainTex")}");
                }
                MelonLogger.Msg(sbGcMat.ToString());
            }

            // ── Pre-scan loadedProps for visual prefabs (have renderers) ──────────
            // A few props have both a collision _player prefab and a visual twin in
            // loadedProps.  Build a baseName → visual-GO map as a secondary fallback.
            var visualPrefabMap = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            var sbVis = new StringBuilder("[PropLibrary] Visual prefabs in loadedProps:");
            for (int i = 0; i < loaded.Length; i++)
            {
                var go = loaded[i];
                if (go == null) continue;
                bool hasRend = go.GetComponentInChildren<MeshRenderer>() != null
                            || go.GetComponentInChildren<SkinnedMeshRenderer>() != null;
                if (!hasRend) continue;
                string nm = go.name
                    .Replace("(Clone)", "")
                    .Replace("_player", "")
                    .Replace("_Player", "")
                    .Trim();
                sbVis.Append($"\n  raw=\"{go.name}\"  key=\"{nm}\"");
                if (!visualPrefabMap.ContainsKey(nm))
                    visualPrefabMap[nm] = go;
            }
            MelonLogger.Msg(sbVis + $"\n  Total: {visualPrefabMap.Count}");

            // ── Fallback terrain material (MicroSplat/Rock shader) ───────────────
            // Used only as last resort for props where LOD loading fails and the
            // prop has no catalog entry.
            Material terrainRockMat = null;
            try
            {
                var allMats = Resources.FindObjectsOfTypeAll<Material>();
                foreach (var m in allMats)
                    if (m != null && m.shader != null && m.shader.name == "MicroSplat/Rock")
                    { terrainRockMat = m; break; }
            }
            catch { }
            MelonLogger.Msg($"[PropLibrary] Terrain rock material: \"{terrainRockMat?.name ?? "null"}\"");

            // Catalog visual lookup — secondary fallback for props without a LOD entry.
            var visualLookup = BuildGpuiVisualLookup();
            MelonLogger.Msg($"[PropLibrary] Visual prop catalog lookup: {visualLookup.Count} entries.");

            // Scene material lookup — used to patch materials loaded from catalog whose
            // textures live in a separate bundle (mainTexture is null after prefab load).
            var sceneMats = BuildSceneMatByNameLookup();
            int sceneMatsWithTex = 0;
            foreach (var kv in sceneMats)
                if (kv.Value.mainTexture != null) sceneMatsWithTex++;
            MelonLogger.Msg($"[PropLibrary] Scene mat lookup: {sceneMats.Count} ({sceneMatsWithTex} with mainTexture).");

            // ── Diagnostic: dump all shader property names for BetterLit materials ─
            // Some BetterLit variants use non-standard texture property names, so we
            // need to know what slots actually hold textures in memory.
            try
            {
                bool dumpedBetterLit = false;
                foreach (var kv in sceneMats)
                {
                    var m = kv.Value;
                    if (m == null || m.shader == null) continue;
                    if (!m.shader.name.StartsWith("Better Lit", StringComparison.OrdinalIgnoreCase)) continue;

                    var sbShader = new StringBuilder($"[PropLibrary] BetterLit shader props for \"{m.name}\" ({m.shader.name}):");
                    int propCount = m.shader.GetPropertyCount();
                    for (int pi = 0; pi < propCount; pi++)
                    {
                        var ptype = m.shader.GetPropertyType(pi);
                        string pname = m.shader.GetPropertyName(pi);
                        if (ptype == UnityEngine.Rendering.ShaderPropertyType.Texture)
                        {
                            var tex = m.GetTexture(pname);
                            sbShader.Append($"\n  {pname,-36} = \"{tex?.name ?? "null"}\"");
                        }
                    }
                    MelonLogger.Msg(sbShader.ToString());

                    // Dump once for a material WITH a texture and once for one WITHOUT.
                    if (!dumpedBetterLit || m.mainTexture != null) { dumpedBetterLit = true; }
                    if (dumpedBetterLit) break;
                }
            }
            catch (Exception e) { MelonLogger.Warning($"[PropLibrary] BetterLit shader prop dump failed: {e.Message}"); }

            int insertAt = PrimitiveNames.Length;
            int added    = 0;
            int skipped  = 0;
            int gpuiIdx  = 0;

            // Step-resolution counters for the end-of-scan summary.
            int step1Count = 0, step2Count = 0, step3Count = 0, step4Count = 0, step5Count = 0;

            for (int i = 0; i < loaded.Length; i++)
            {
                var prefabGO = loaded[i];
                if (prefabGO == null) continue;

                bool hasRenderer = prefabGO.GetComponentInChildren<MeshRenderer>() != null
                                || prefabGO.GetComponentInChildren<SkinnedMeshRenderer>() != null;
                if (hasRenderer) continue;

                bool hasCollider = prefabGO.GetComponentInChildren<MeshCollider>() != null;
                if (!hasCollider) continue;

                int    gi     = gpuiIdx++;
                string gpuiId = $"gpui://{gi}";
                if (_byId.ContainsKey(gpuiId)) { skipped++; continue; }

                string baseName = prefabGO.name
                    .Replace("(Clone)", "")
                    .Replace("_player", "")
                    .Replace("_Player", "")
                    .Trim();

                var info = new PropInfo(gpuiId, baseName);
                info.gpuiIndex = gi;
                int resolvedStep = 0;

                // ── Step 1: gc arrays — propLODIndex → gcMeshIndex → Mesh + Material ──
                // This is the primary visual source for all GPU-instanced terrain props
                // (roots, rocks, trees, etc.).  The arrays are populated by the game's
                // own terrain streaming system, so materials are always the correct ones.
                if (!info.HasMesh && propLODIdxArr != null && gcMeshIdxArr != null && i < propLODIdxArr.Length)
                {
                    try
                    {
                        int lodSlot = propLODIdxArr[i];
                        if (lodSlot >= 0 && lodSlot < gcMeshIdxArr.Length)
                        {
                            int gcIdx = gcMeshIdxArr[lodSlot];
                            if (gcIdx >= 0)
                            {
                                Mesh mesh = null;
                                if (loadedGCMeshesArr != null && gcIdx < loadedGCMeshesArr.Length)
                                    mesh = loadedGCMeshesArr[gcIdx];
                                if (mesh == null && gcMeshesArr != null && gcIdx < gcMeshesArr.Length)
                                    mesh = gcMeshesArr[gcIdx];

                                Material mat = null;
                                if (gcMaterialsArr != null && gcIdx < gcMaterialsArr.Length)
                                    mat = gcMaterialsArr[gcIdx];

                                if (mesh != null)
                                {
                                    var mats = mat != null ? new[] { mat } : null;
                                    AddPart(info, mesh, mats, prefabGO.transform, prefabGO.transform);
                                    resolvedStep = 1;
                                    step1Count++;
                                }

                                if (gi < 60)
                                    MelonLogger.Msg($"[PropLibrary] GPUI[{gi}] \"{baseName}\" s1-gc[{gcIdx}] " +
                                                    $"mesh=\"{mesh?.name ?? "null"}\" " +
                                                    $"mat=\"{mat?.name ?? "null"}\" shader=\"{mat?.shader?.name ?? "null"}\" " +
                                                    $"mainTex={MatTexName(mat, "_MainTex")} " +
                                                    $"{(info.HasMesh ? "✓" : "✗")}");
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        if (gi < 60)
                            MelonLogger.Warning($"[PropLibrary] GPUI[{gi}] \"{baseName}\" s1-gc lookup failed: {e.Message}");
                    }
                }

                // ── Step 2: visual twin in loadedProps ───────────────────────────
                if (!info.HasMesh && visualPrefabMap.TryGetValue(baseName, out var visualTwinGO))
                {
                    try
                    {
                        ExtractParts(visualTwinGO, info);
                        if (info.HasMesh)
                        {
                            PatchMaterials(info, sceneMats);
                            resolvedStep = 2;
                            step2Count++;
                            if (gi < 60)
                                MelonLogger.Msg($"[PropLibrary] GPUI[{gi}] \"{baseName}\" s2-visual-twin ✓ parts={info.parts.Count} " +
                                                $"{PartMatSummary(info)}");
                        }
                        else if (gi < 60)
                            MelonLogger.Warning($"[PropLibrary] GPUI[{gi}] \"{baseName}\" s2-visual-twin found but no mesh extracted");
                    }
                    catch (Exception e)
                    {
                        if (gi < 60) MelonLogger.Warning($"[PropLibrary] GPUI[{gi}] \"{baseName}\" s2-visual-twin failed: {e.Message}");
                    }
                }

                // ── Step 3: addressable catalog visual prefab ─────────────────────
                if (!info.HasMesh && visualLookup.TryGetValue(baseName, out string visualPath))
                {
                    try
                    {
                        var handle   = Addressables.LoadAssetAsync<GameObject>(visualPath);
                        var visualGO = handle.WaitForCompletion();
                        if (visualGO != null)
                        {
                            ExtractParts(visualGO, info);
                            // Keep handle open so materials stay resident.
                            if (info.HasMesh)
                            {
                                PatchMaterials(info, sceneMats);
                                resolvedStep = 3;
                                step3Count++;
                                if (gi < 60)
                                    MelonLogger.Msg($"[PropLibrary] GPUI[{gi}] \"{baseName}\" s3-catalog ✓ \"{visualPath}\" " +
                                                    $"{PartMatSummary(info)}");
                            }
                            else if (gi < 60)
                                MelonLogger.Warning($"[PropLibrary] GPUI[{gi}] \"{baseName}\" s3-catalog loaded \"{visualPath}\" but no mesh extracted");
                        }
                        else if (gi < 60)
                            MelonLogger.Warning($"[PropLibrary] GPUI[{gi}] \"{baseName}\" s3-catalog null prefab for \"{visualPath}\"");
                    }
                    catch (Exception e)
                    {
                        if (gi < 60)
                            MelonLogger.Warning($"[PropLibrary] GPUI[{gi}] \"{baseName}\" s3-catalog load failed: {e.Message}");
                    }
                }

                // ── Step 4: terrain fallback — collision mesh + MicroSplat/Rock ──
                // Only for props with no catalog entry (large rocks, boulders, cliff
                // pieces whose visual LOD couldn't be loaded).
                if (!info.HasMesh && !visualLookup.ContainsKey(baseName))
                {
                    var colMeshes = new List<MeshCollider>();
                    CollectMeshColliders(prefabGO.transform, colMeshes);
                    var mats = terrainRockMat != null ? new[] { terrainRockMat } : null;
                    foreach (var mc in colMeshes)
                    {
                        if (mc.sharedMesh == null) continue;
                        AddPart(info, mc.sharedMesh, mats, mc.transform, prefabGO.transform);
                    }
                    if (info.HasMesh)
                    {
                        resolvedStep = 4;
                        step4Count++;
                        if (gi < 60)
                            MelonLogger.Msg($"[PropLibrary] GPUI[{gi}] \"{baseName}\" s4-terrain ✓ mat=\"{mats?[0]?.name ?? "grey"}\"");
                    }
                }

                // ── Step 5: grey geometry fallback ───────────────────────────────
                if (!info.HasMesh)
                {
                    var instance = UnityEngine.Object.Instantiate(
                        prefabGO, new Vector3(0f, -99999f, 0f), Quaternion.identity);
                    try   { ExtractPartsFromColliders(instance, info); }
                    catch (Exception e) { MelonLogger.Warning($"[PropLibrary] GPUI[{gi}] \"{baseName}\" s5-extract failed: {e.Message}"); }
                    UnityEngine.Object.Destroy(instance);
                    if (info.HasMesh)
                    {
                        resolvedStep = 5;
                        step5Count++;
                    }
                }

                info.isLoaded  = true;
                info.isInvalid = !info.HasMesh;

                if (info.HasMesh)
                {
                    _all.Insert(insertAt, info);
                    _byId[gpuiId] = info;
                    insertAt++;
                    added++;
                    // Always log the resolved step + material for every added prop so we
                    // can see which props got the wrong material without a gi < N cap.
                    MelonLogger.Msg($"[PropLibrary] GPUI[{gi}] \"{baseName}\" step={resolvedStep} parts={info.parts.Count} {PartMatSummary(info)}");
                }
                else
                {
                    skipped++;
                    MelonLogger.Warning($"[PropLibrary] GPUI[{gi}] \"{baseName}\" — no mesh, skipping (step={resolvedStep})");
                }
            }

            BuildFiltered();
            MelonLogger.Msg($"[PropLibrary] GPUI scan complete: {added} added, {skipped} skipped  " +
                            $"[s1={step1Count} s2={step2Count} s3={step3Count} s4={step4Count} s5={step5Count}]");
        }

        // Scans the addressable catalog for visual prop prefabs under Assets/_Props/
        // and returns a dictionary from basename (no extension, no _player suffix) to
        // the full addressable path.
        static Dictionary<string, string> BuildGpuiVisualLookup()
        {
            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string catalogPath = Path.Combine(Application.streamingAssetsPath, "aa", "catalog.json");
            if (!File.Exists(catalogPath)) return lookup;

            try
            {
                string json = File.ReadAllText(catalogPath);
                AddVisualPathsToLookup(json, lookup);

                int kdIdx = json.IndexOf("\"m_KeyDataString\"", StringComparison.Ordinal);
                if (kdIdx >= 0)
                {
                    int valStart = json.IndexOf('"', kdIdx + 17) + 1;
                    int valEnd   = json.IndexOf('"', valStart);
                    if (valStart > 0 && valEnd > valStart)
                    {
                        byte[] bytes   = Convert.FromBase64String(json.Substring(valStart, valEnd - valStart));
                        string decoded = Encoding.UTF8.GetString(bytes);
                        AddVisualPathsToLookup(decoded, lookup);
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropLibrary] GPUI visual lookup build failed: {e.Message}");
            }

            return lookup;
        }

        // Broad scan: all prefabs under Assets/ (not just _Props/ and Meshes/).
        // _player suffix and lower LOD variants are still filtered out below.
        static readonly string[] VisualScanPrefixes =
        {
            "Assets/",
        };

        static void AddVisualPathsToLookup(string text, Dictionary<string, string> lookup)
        {
            foreach (var prefix in VisualScanPrefixes)
            {
                int i = 0;
                while (i < text.Length)
                {
                    int start = text.IndexOf(prefix, i, StringComparison.Ordinal);
                    if (start < 0) break;
                    int end = start;
                    while (end < text.Length)
                    {
                        char c = text[end];
                        if (c == '"' || c == '\0' || c < ' ') break;
                        end++;
                    }
                    i = end + 1;
                    string path = text.Substring(start, end - start);
                    if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;

                    string name = Path.GetFileNameWithoutExtension(path);
                    // Skip _player collision prefabs and lower LOD variants.
                    if (name.EndsWith("_player", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.EndsWith("_Player", StringComparison.OrdinalIgnoreCase)) continue;
                    if (IsLowerLodVariant(path)) continue;

                    if (!lookup.ContainsKey(name))
                        lookup[name] = path;
                }
            }
        }

        // Returns the name of a named texture slot on a material, or "null"/"none" for logging.
        static string MatTexName(Material mat, string slot)
        {
            if (mat == null) return "null";
            try
            {
                var tex = mat.GetTexture(slot);
                return tex != null ? $"\"{tex.name}\"" : "none";
            }
            catch { return "?"; }
        }

        // One-line summary of the first part's material for per-prop logging.
        static string PartMatSummary(PropInfo info)
        {
            if (info.parts == null || info.parts.Count == 0) return "mat=none";
            var mats = info.parts[0].materials;
            if (mats == null || mats.Length == 0 || mats[0] == null) return "mat=null";
            var m = mats[0];
            return $"mat=\"{m.name}\" shader=\"{m.shader?.name ?? "null"}\" " +
                   $"mainTex={MatTexName(m, "_MainTex")} " +
                   $"baseTex={MatTexName(m, "_BaseColorMap")} " +
                   $"albedo={MatTexName(m, "_BaseMap")}";
        }

        // Builds name → material lookup from every material currently in memory,
        // preferring instances that have a mainTexture over ones that don't.
        static Dictionary<string, Material> BuildSceneMatByNameLookup()
        {
            var lookup = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var allMats = Resources.FindObjectsOfTypeAll<Material>();
                foreach (var m in allMats)
                {
                    if (m == null || string.IsNullOrEmpty(m.name)) continue;
                    if (!lookup.TryGetValue(m.name, out var existing) ||
                        (existing.mainTexture == null && m.mainTexture != null))
                        lookup[m.name] = m;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropLibrary] BuildSceneMatByNameLookup failed: {e.Message}");
            }
            return lookup;
        }

        // For each part whose material has no mainTexture, try to substitute the
        // scene-loaded instance of the same material name (which may have textures).
        static void PatchMaterials(PropInfo info, Dictionary<string, Material> sceneMats)
        {
            if (sceneMats == null || sceneMats.Count == 0) return;
            foreach (var part in info.parts)
            {
                if (part.materials == null) continue;
                for (int mi = 0; mi < part.materials.Length; mi++)
                {
                    var mat = part.materials[mi];
                    if (mat == null) continue;
                    if (mat.mainTexture != null) continue;
                    if (!sceneMats.TryGetValue(mat.name, out var sceneMat)) continue;
                    if (sceneMat.mainTexture == null) continue;
                    part.materials[mi] = sceneMat;
                }
            }
        }

        // Builds a lookup from mesh name → (Mesh, materials).
        //
        // Pass 1: MeshFilter + MeshRenderer components — these always have correct materials.
        // Pass 2: Resources.FindObjectsOfTypeAll<Mesh>() — catches every GPU-instanced mesh
        //         (roots, rocks, trees) that has no per-instance GameObject.  For these we
        //         attempt to match a material by name from the full loaded material list.
        static Dictionary<string, (Mesh mesh, Material[] mats)> BuildSceneMeshLookup()
        {
            var lookup = new Dictionary<string, (Mesh mesh, Material[] mats)>(StringComparer.OrdinalIgnoreCase);

            // ── Pass 1: MeshFilter scan (materials always correct) ───────────
            try
            {
                var mfs = Resources.FindObjectsOfTypeAll<MeshFilter>();
                foreach (var mf in mfs)
                {
                    if (mf == null || mf.sharedMesh == null) continue;
                    string n = mf.sharedMesh.name;
                    if (string.IsNullOrEmpty(n) || lookup.ContainsKey(n)) continue;

                    Material[] mats = null;
                    var mr = mf.GetComponent<MeshRenderer>();
                    if (mr != null) mats = mr.sharedMaterials;
                    lookup[n] = (mf.sharedMesh, mats);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropLibrary] BuildSceneMeshLookup pass1 failed: {e.Message}");
            }

            // ── Pass 2: all Meshes in memory (GPU-instanced props) ───────────
            // Build a flat material-by-name index first (cheap, one alloc).
            var matByName = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var allMats = Resources.FindObjectsOfTypeAll<Material>();
                foreach (var m in allMats)
                    if (m != null && !string.IsNullOrEmpty(m.name))
                        matByName.TryAdd(m.name, m);
            }
            catch { }

            try
            {
                var allMeshes = Resources.FindObjectsOfTypeAll<Mesh>();
                foreach (var mesh in allMeshes)
                {
                    if (mesh == null || string.IsNullOrEmpty(mesh.name)) continue;
                    if (lookup.ContainsKey(mesh.name)) continue; // pass1 already has it with material

                    // Try to find a matching material: exact mesh name, then stripped base name.
                    string meshBase = StripLodSuffix(mesh.name);
                    Material mat = null;
                    matByName.TryGetValue(mesh.name, out mat);
                    if (mat == null) matByName.TryGetValue(meshBase, out mat);

                    lookup[mesh.name] = (mesh, mat != null ? new[] { mat } : null);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropLibrary] BuildSceneMeshLookup pass2 failed: {e.Message}");
            }

            int withMat = 0;
            foreach (var kv in lookup)
                if (kv.Value.mats != null && kv.Value.mats.Length > 0 && kv.Value.mats[0] != null) withMat++;

            MelonLogger.Msg($"[PropLibrary] Scene mesh lookup: {lookup.Count} meshes ({withMat} with material).");
            return lookup;
        }

        // _player prefabs have MeshColliders but no MeshRenderers; extract their
        // shared meshes so we have geometry to display in the level editor.
        // Materials will be null (renders grey) until GPUI rendering is wired up.
        static void ExtractPartsFromColliders(GameObject root, PropInfo info)
        {
            var colliders = new List<MeshCollider>();
            CollectMeshColliders(root.transform, colliders);

            var rootT = root.transform;
            foreach (var mc in colliders)
            {
                if (mc.sharedMesh == null) continue;
                AddPart(info, mc.sharedMesh, null, mc.transform, rootT);
            }
        }

        static void CollectMeshColliders(Transform t, List<MeshCollider> result)
        {
            var mc = t.GetComponent<MeshCollider>();
            if (mc != null && mc.sharedMesh != null) result.Add(mc);
            for (int i = 0; i < t.childCount; i++)
                CollectMeshColliders(t.GetChild(i), result);
        }

        // ── GPUI pool template access ────────────────────────────────────────

        // Logs what BestRegionLoader exposes via reflection (fields AND properties),
        // and inspects the first few GPUI _player prefabs to find any rendering
        // components and their material fields.
        static void DiagnoseReflection(BestRegionLoader brl)
        {
            try
            {
                var t     = brl.GetType();
                var flags = System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Instance;

                var sb = new StringBuilder();

                var fields = t.GetFields(flags);
                sb.AppendLine($"[PropLibrary] BRL fields ({fields.Length}):");
                foreach (var f in fields)
                    sb.AppendLine($"  field  {f.FieldType.Name,-36} {f.Name}");

                var props = t.GetProperties(flags);
                sb.AppendLine($"[PropLibrary] BRL properties ({props.Length}):");
                foreach (var p in props)
                    sb.AppendLine($"  prop   {p.PropertyType.Name,-36} {p.Name}");

                MelonLogger.Msg(sb.ToString());
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropLibrary] DiagnoseReflection(BRL) failed: {e.Message}");
            }

            // Also dump all currently-loaded materials so we can see rock/terrain
            // material names — useful for matching by name later.
            try
            {
                var mats = Resources.FindObjectsOfTypeAll<Material>();
                if (mats != null)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"[PropLibrary] Loaded materials ({mats.Length}):");
                    foreach (var m in mats)
                        if (m != null) sb.AppendLine($"  \"{m.name}\"  shader={m.shader?.name}");
                    MelonLogger.Msg(sb.ToString());
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropLibrary] Material scan failed: {e.Message}");
            }
        }


        // Gets a named field OR property value from obj, trying both since IL2CPP
        // interop proxies sometimes expose C++ fields as C# properties.
        static object ReflectGet(object obj, string memberName)
        {
            if (obj == null) return null;
            var t = obj.GetType();

            var flags = System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance;

            var field = t.GetField(memberName, flags);
            if (field != null) return field.GetValue(obj);

            // IL2CppInterop proxy types: GetProperty(name, flags) silently returns null
            // even when the property exists.  Iterating GetProperties() is reliable.
            foreach (var p in t.GetProperties(flags))
            {
                if (p.Name != memberName) continue;
                try { return p.GetValue(obj); }
                catch { return null; }
            }

            return null;
        }

        // Like ReflectGet but with verbose per-step diagnostics and an IList return.
        // Logs exactly which step fails so we can distinguish: property not found,
        // GetValue throws, returns null, or the IList cast fails.
        static System.Collections.IList ReflectGetIList(object obj, string memberName, string logLabel)
        {
            if (obj == null) return null;
            var t     = obj.GetType();
            var flags = System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance;

            // Step 1 — find the property by iterating (GetProperty(name) is broken on IL2CppInterop proxies).
            System.Reflection.PropertyInfo found = null;
            foreach (var p in t.GetProperties(flags))
                if (p.Name == memberName) { found = p; break; }

            if (found == null)
            {
                MelonLogger.Warning($"[PropLibrary] {logLabel}: NOT FOUND in GetProperties()");
                return null;
            }

            // Step 2 — get the value, logging any exception.
            object val = null;
            try { val = found.GetValue(obj); }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropLibrary] {logLabel}: GetValue threw {e.GetType().Name}: {e.Message}");
                return null;
            }

            if (val == null)
            {
                MelonLogger.Warning($"[PropLibrary] {logLabel}: GetValue returned null (array not initialised yet?)");
                return null;
            }

            // Step 3 — cast to IList.
            var asList = val as System.Collections.IList;
            if (asList == null)
                MelonLogger.Warning($"[PropLibrary] {logLabel}: not IList — actual type={val.GetType().FullName}");
            else
                MelonLogger.Msg($"[PropLibrary] {logLabel}: OK — Count={asList.Count}");

            return asList;
        }

        // ── Addressable prop loading ─────────────────────────────────────────

        static void LoadPrimitive(PropInfo info)
        {
            try
            {
                var typeName = info.id.Substring("primitive://".Length);
                var pt       = (PrimitiveType)Enum.Parse(typeof(PrimitiveType), typeName);
                var go       = GameObject.CreatePrimitive(pt);
                go.transform.position = new Vector3(0f, -99999f, 0f);

                ExtractParts(go, info);
                GameObject.Destroy(go);

                info.isLoaded  = true;
                info.isInvalid = !info.HasMesh;
                if (info.HasMesh)
                    MelonLogger.Msg($"[PropLibrary] Loaded primitive: {info.displayName}");
                else
                    MelonLogger.Warning($"[PropLibrary] Primitive {info.displayName} yielded no mesh.");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[PropLibrary] Failed to load primitive {info.id}: {e.Message}");
                info.isLoaded  = true;
                info.isInvalid = true;
            }
        }

        public static void LoadPropData(PropInfo info)
        {
            if (info.isLoaded) return;

            if (info.IsPrimitive)
            {
                LoadPrimitive(info);
                return;
            }

            try
            {
                var handle = Addressables.LoadAssetAsync<GameObject>(info.id);
                var prefab = handle.WaitForCompletion();
                if (prefab == null)
                {
                    MelonLogger.Warning($"[PropLibrary] Null prefab for {info.id}");
                    info.isLoaded  = true;
                    info.isInvalid = true;
                    return;
                }

                var instance = UnityEngine.Object.Instantiate(
                    prefab, new Vector3(0f, -99999f, 0f), Quaternion.identity);

                ExtractParts(instance, info);

                if (!info.HasMesh)
                {
                    MelonLogger.Warning($"[PropLibrary] No mesh in {info.displayName} — dumping instance hierarchy:");
                    LogHierarchy(instance);
                }

                UnityEngine.Object.Destroy(instance);

                info.SetHandle(handle);
                info.isLoaded = true;

                if (!info.HasMesh)
                    info.isInvalid = true;
                else
                    MelonLogger.Msg($"[PropLibrary] Loaded {info.displayName} ({info.parts.Count} part(s))");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[PropLibrary] Failed to load {info.id}: {e.Message}");
                info.isLoaded  = true;
                info.isInvalid = true;
            }
        }

        // ── Hierarchy logging ────────────────────────────────────────────────

        static void LogHierarchy(GameObject root)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"  {root.name} (root)");
            for (int i = 0; i < root.transform.childCount; i++)
                AppendNode(root.transform.GetChild(i), sb, 1);
            MelonLogger.Msg($"[PropLibrary] {sb}");
        }

        static void AppendNode(Transform t, StringBuilder sb, int depth)
        {
            var indent = new string(' ', depth * 2);
            var go     = t.gameObject;

            var names = new StringBuilder();
            var comps = go.GetComponents<Component>();
            if (comps != null)
            {
                for (int i = 0; i < comps.Length; i++)
                {
                    if (comps[i] == null) continue;
                    if (names.Length > 0) names.Append(", ");
                    string n;
                    try   { n = comps[i].GetType().Name; }
                    catch { n = "?"; }
                    names.Append(n);
                }
            }

            var hints = new StringBuilder();
            if (go.GetComponent<MeshFilter>()          != null) hints.Append(" +MF");
            if (go.GetComponent<MeshRenderer>()        != null) hints.Append(" +MR");
            if (go.GetComponent<SkinnedMeshRenderer>() != null) hints.Append(" +SMR");
            var rend = go.GetComponent<Renderer>();
            if (rend != null && go.GetComponent<MeshRenderer>() == null && go.GetComponent<SkinnedMeshRenderer>() == null)
                hints.Append(" +Rend(unknown)");
            if (go.GetComponent<MeshCollider>()  != null) hints.Append(" +MeshCol");
            if (go.GetComponent<BoxCollider>()   != null) hints.Append(" +BoxCol");
            if (go.GetComponent<LODGroup>()      != null) hints.Append(" +LOD");

            sb.AppendLine($"{indent}{go.name}  [{names}]{hints}");
            for (int i = 0; i < t.childCount; i++)
                AppendNode(t.GetChild(i), sb, depth + 1);
        }

        // ── Mesh extraction helpers ──────────────────────────────────────────

        static void ExtractParts(GameObject root, PropInfo info)
        {
            var mrList  = new List<MeshRenderer>();
            var smrList = new List<SkinnedMeshRenderer>();
            CollectRenderers(root.transform, mrList, smrList);

            HashSet<int> lod0Ids = null;
            var lodGroup = root.GetComponent<LODGroup>()
                        ?? root.GetComponentInChildren<LODGroup>();
            if (lodGroup != null && (mrList.Count + smrList.Count) > 0)
            {
                try
                {
                    var lods = lodGroup.GetLODs();
                    if (lods != null && lods.Length > 0 && lods[0].renderers != null)
                    {
                        var lod0Rend = lods[0].renderers;
                        if (lod0Rend.Length > 0)
                        {
                            lod0Ids = new HashSet<int>();
                            for (int j = 0; j < lod0Rend.Length; j++)
                                if (lod0Rend[j] != null) lod0Ids.Add(lod0Rend[j].GetInstanceID());
                        }
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[PropLibrary] GetLODs() failed for {info.displayName}: {e.Message}");
                }
            }

            var rootT = root.transform;
            foreach (var mr in mrList)
            {
                if (lod0Ids != null && !lod0Ids.Contains(mr.GetInstanceID())) continue;
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                AddPart(info, mf.sharedMesh, mr.sharedMaterials, mr.transform, rootT);
            }
            foreach (var smr in smrList)
            {
                if (lod0Ids != null && !lod0Ids.Contains(smr.GetInstanceID())) continue;
                if (smr.sharedMesh == null) continue;
                AddPart(info, smr.sharedMesh, smr.sharedMaterials, smr.transform, rootT);
            }
        }

        static void AddPart(PropInfo info, Mesh mesh, Material[] materials, Transform t, Transform rootT)
        {
            // If this part belongs to a GPUI-derived prop we deliberately do NOT
            // keep its materials so the editor will render grey geometry only.
            // This prevents any GPUI-path code from assigning real materials.
            if (info != null && info.IsGpui)
            {
                materials = null;
            }

            var ws = t.lossyScale;
            var rs = rootT.lossyScale;
            info.parts.Add(new PropMeshPart
            {
                mesh          = mesh,
                materials     = materials,
                localPosition = rootT.InverseTransformPoint(t.position),
                localRotation = Quaternion.Inverse(rootT.rotation) * t.rotation,
                localScale    = new Vector3(
                    rs.x != 0f ? ws.x / rs.x : 1f,
                    rs.y != 0f ? ws.y / rs.y : 1f,
                    rs.z != 0f ? ws.z / rs.z : 1f),
            });
        }

        static void CollectRenderers(Transform t, List<MeshRenderer> mrList, List<SkinnedMeshRenderer> smrList)
        {
            var mr  = t.GetComponent<MeshRenderer>();
            var smr = t.GetComponent<SkinnedMeshRenderer>();
            if (mr  != null) mrList.Add(mr);
            if (smr != null) smrList.Add(smr);

            if (mr == null && smr == null)
            {
                var rend = t.GetComponent<Renderer>();
                if (rend != null)
                {
                    var mrCast  = rend.TryCast<MeshRenderer>();
                    var smrCast = rend.TryCast<SkinnedMeshRenderer>();
                    if      (mrCast  != null) mrList.Add(mrCast);
                    else if (smrCast != null) smrList.Add(smrCast);
                }
            }

            for (int i = 0; i < t.childCount; i++)
                CollectRenderers(t.GetChild(i), mrList, smrList);
        }
    }
}
