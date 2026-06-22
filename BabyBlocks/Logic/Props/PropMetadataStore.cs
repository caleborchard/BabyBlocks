using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using MelonLoader;
using MelonLoader.Utils;

namespace BabyBlocks
{
    // Pure data layer for per-prop metadata: the in-memory _byId store, JSON/binary
    // persistence, per-prop accessor methods, and the material-construction list.
    // Has no UI or Unity-rendering dependencies beyond PropExtraInfo and
    // PropLibrary's id resolution.
    internal static class PropMetadataStore
    {
        public const string NoOverrideLabel = "(no override)";
        const byte PmdVersion = 3;

        internal static readonly Dictionary<string, PropExtraInfo> _byId = new(StringComparer.Ordinal);
        internal static bool _loaded;
        internal static int _nextIndex = 1;
        static bool _savePathLogged;
        internal static bool _loadedFromJson;

        static List<MaterialConstructionEntry> _materialConstructions = new();
        static int _nextMaterialConstructionId;

        static string SavePath =>
            Path.Combine(MelonEnvironment.UserDataDirectory, "BabyBlocks", "prop_metadata.json");

        public static string BinaryExportPath =>
            Path.Combine(MelonEnvironment.UserDataDirectory, "BabyBlocks", "prop_metadata.bin");

        internal static void EnsureLoaded()
        {
            if (_loaded) return;
            Load();
        }

        // ── Material constructions (persisted alongside prop metadata) ─────────────

        public static List<MaterialConstructionEntry> MaterialConstructions
        {
            get { EnsureLoaded(); return _materialConstructions; }
        }

        public static MaterialConstructionEntry CreateMaterialConstruction()
        {
            EnsureLoaded();
            var entry = new MaterialConstructionEntry
            {
                id = _nextMaterialConstructionId++,
                name = "New Material " + (_materialConstructions.Count + 1)
            };
            _materialConstructions.Add(entry);
            _loadedFromJson = true;
            Save();
            return entry;
        }

        public static void DeleteMaterialConstruction(MaterialConstructionEntry entry)
        {
            EnsureLoaded();
            if (entry == null) return;
            _materialConstructions.Remove(entry);
            _loadedFromJson = true;
            Save();
        }

        public static MaterialConstructionEntry FindMaterialConstructionById(int id)
        {
            EnsureLoaded();
            if (id < 0) return null;
            foreach (var e in _materialConstructions)
                if (e.id == id) return e;
            return null;
        }

        public static void MarkMaterialConstructionsDirty() => _loadedFromJson = true;

        public static void SaveMaterialConstructions()
        {
            EnsureLoaded();
            _loadedFromJson = true;
            Save();
        }

        internal static bool TryGetInfoById(string id, out PropExtraInfo info)
        {
            info = null;
            if (string.IsNullOrEmpty(id)) return false;
            if (_byId.TryGetValue(id, out info)) return true;

            string canonical = PropLibrary.ResolveCanonicalId(id);
            return !string.IsNullOrEmpty(canonical) && _byId.TryGetValue(canonical, out info);
        }

        static bool MergeItem(PropExtraInfo target, PropExtraInfo source)
        {
            if (target == null || source == null || ReferenceEquals(target, source)) return false;

            bool changed = false;

            if (target.index <= 0 && source.index > 0) { target.index = source.index; changed = true; }
            if (string.IsNullOrEmpty(target.displayName) && !string.IsNullOrEmpty(source.displayName)) { target.displayName = source.displayName; changed = true; }
            if (string.IsNullOrEmpty(target.category) && !string.IsNullOrEmpty(source.category)) { target.category = source.category; changed = true; }
            if (string.IsNullOrEmpty(target.colliderIgnoredSubmeshes) && !string.IsNullOrEmpty(source.colliderIgnoredSubmeshes)) { target.colliderIgnoredSubmeshes = source.colliderIgnoredSubmeshes; changed = true; }
            if (string.IsNullOrEmpty(target.overrideMaterialId) && !string.IsNullOrEmpty(source.overrideMaterialId)) { target.overrideMaterialId = source.overrideMaterialId; changed = true; }
            if (string.IsNullOrEmpty(target.nativeMaterialName) && !string.IsNullOrEmpty(source.nativeMaterialName)) { target.nativeMaterialName = source.nativeMaterialName; changed = true; }
            if (string.IsNullOrEmpty(target.materialSourcePropId) && !string.IsNullOrEmpty(source.materialSourcePropId)) { target.materialSourcePropId = source.materialSourcePropId; changed = true; }
            if (string.IsNullOrEmpty(target.surfaceType) && !string.IsNullOrEmpty(source.surfaceType)) { target.surfaceType = source.surfaceType; changed = true; }
            if (!target.useRenderMeshCollider && source.useRenderMeshCollider) { target.useRenderMeshCollider = true; changed = true; }
            if (!target.disableBaking && source.disableBaking) { target.disableBaking = true; changed = true; }
            if (target.forcedMaterialSlots <= 1 && source.forcedMaterialSlots > 1) { target.forcedMaterialSlots = source.forcedMaterialSlots; changed = true; }

            if ((target.disabledRenderers == null || target.disabledRenderers.Count == 0)
                && source.disabledRenderers != null && source.disabledRenderers.Count > 0)
            {
                target.disabledRenderers = new List<string>(source.disabledRenderers);
                changed = true;
            }

            if (!HasNonEmptySlot(target.perSlotMaterialOverrides) && HasNonEmptySlot(source.perSlotMaterialOverrides))
            {
                target.perSlotMaterialOverrides = new List<string>(source.perSlotMaterialOverrides);
                changed = true;
            }

            if (!target.excluded && source.excluded)
            {
                target.excluded = true;
                changed = true;
            }

            if (!target.isBush && source.isBush) { target.isBush = true; changed = true; }
            if (target.bushRadius <= 0f && source.bushRadius > 0f) { target.bushRadius = source.bushRadius; changed = true; }

            return changed;
        }

        public static void MigratePropIdsToCanonical()
        {
            EnsureLoaded();
            if (_byId.Count == 0) return;

            bool changed = false;
            var remaps = new List<(string fromId, string toId, PropExtraInfo item)>();
            foreach (var kvp in _byId)
            {
                string canonical = PropLibrary.ResolveCanonicalId(kvp.Key);
                if (string.IsNullOrEmpty(canonical) || string.Equals(canonical, kvp.Key, StringComparison.Ordinal))
                    continue;
                remaps.Add((kvp.Key, canonical, kvp.Value));
            }

            for (int i = 0; i < remaps.Count; i++)
            {
                var remap = remaps[i];
                if (!_byId.TryGetValue(remap.fromId, out var source))
                    continue;

                _byId.Remove(remap.fromId);
                changed = true;

                source.id = remap.toId;
                if (_byId.TryGetValue(remap.toId, out var existing))
                {
                    if (MergeItem(existing, source)) changed = true;
                }
                else
                {
                    _byId[remap.toId] = source;
                }
            }

            foreach (var item in _byId.Values)
            {
                if (item == null || string.IsNullOrEmpty(item.materialSourcePropId)) continue;
                string canonicalSource = PropLibrary.ResolveCanonicalId(item.materialSourcePropId);
                if (string.IsNullOrEmpty(canonicalSource)
                    || string.Equals(canonicalSource, item.materialSourcePropId, StringComparison.Ordinal))
                    continue;

                item.materialSourcePropId = canonicalSource;
                changed = true;
            }

            if (changed) Save();
        }

        public static bool GetUseRenderMeshCollider(string id)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return false;
            if (!_byId.TryGetValue(id, out var info))
            {
                var propInfo = PropLibrary.FindById(id);
                return propInfo == null || !propInfo.HasColliderParts;
            }
            return info.useRenderMeshCollider;
        }

        public static HashSet<int> GetColliderIgnoredSubmeshes(string id)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return null;
            if (!_byId.TryGetValue(id, out var info)) return null;
            return ParseIntSet(info.colliderIgnoredSubmeshes);
        }

        public static string GetSurfaceType(string id)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return string.Empty;
            if (!_byId.TryGetValue(id, out var info)) return string.Empty;
            return info.surfaceType ?? string.Empty;
        }

        public static string GetOverrideMaterialId(string id)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return string.Empty;
            if (!TryGetInfoById(id, out var info)) return string.Empty;
            return info.overrideMaterialId ?? string.Empty;
        }

        // A prop-level (not per-instance) setting: when true, MaterialBaker.Bake skips this
        // prop entirely and it keeps its plain/native materials when given physics, instead
        // of a baked mesh+atlas. Persisted alongside the other per-prop overrides (see
        // GetOverrideMaterialId/GetMaterialCacheKey) - i.e. it applies to every placed
        // instance of this prop, not saved per-instance in the level (.bbb) file.
        public static bool GetDisableBaking(string id)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return false;
            if (!TryGetInfoById(id, out var info)) return false;
            return info.disableBaking;
        }

        public static void SetDisableBaking(string id, bool value)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return;

            string key = PropLibrary.ResolveCanonicalId(id) ?? id;
            if (!_byId.TryGetValue(key, out var info))
            {
                if (!value) return;
                info = new PropExtraInfo { id = key, index = _nextIndex++ };
                _byId[key] = info;
            }

            if (info.disableBaking == value) return;
            info.disableBaking = value;
            Save();
        }

        // Stable string identifying the material configuration currently applied to prop
        // `id` (per-slot overrides, single override, or the native default) - used as part
        // of the on-disk bake-cache key (see MaterialBakeCache) so different materials
        // applied to the same prop get separate cached bakes.
        public static string GetMaterialCacheKey(string id)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id) || !TryGetInfoById(id, out var info))
                return "default";

            if (HasNonEmptySlot(info.perSlotMaterialOverrides))
            {
                var sb = new StringBuilder();
                for (int i = 0; i < info.perSlotMaterialOverrides.Count; i++)
                {
                    if (i > 0) sb.Append('+');
                    string slot = info.perSlotMaterialOverrides[i];
                    sb.Append(string.IsNullOrEmpty(slot) ? "_" : slot);
                }
                return sb.ToString();
            }

            if (!string.IsNullOrEmpty(info.overrideMaterialId)
                && !string.Equals(info.overrideMaterialId, NoOverrideLabel, StringComparison.OrdinalIgnoreCase))
                return info.overrideMaterialId;

            return "default";
        }

        public static bool GetIsBush(string id)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return false;
            return _byId.TryGetValue(id, out var info) && info.isBush;
        }

        public static bool GetKeepOriginalHierarchy(string id)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return false;
            return _byId.TryGetValue(id, out var info) && info.keepOriginalHierarchy;
        }

        public static bool HasMetadata(string id)
        {
            EnsureLoaded();
            return !string.IsNullOrEmpty(id) && _byId.ContainsKey(id);
        }

        public static bool IsExcluded(string id)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return false;
            return _byId.TryGetValue(id, out var info) && info.excluded;
        }

        // Returns true if the prop has been indexed but is only partially filled:
        // it has at least one but not all of displayName, category, surfaceType.
        public static bool IsPartiallyFilled(string id)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return false;
            if (!_byId.TryGetValue(id, out var info)) return false;
            if (info.excluded || info.index <= 0) return false;
            int count = 0;
            if (!string.IsNullOrEmpty(info.displayName)) count++;
            if (!string.IsNullOrEmpty(info.category)) count++;
            if (!string.IsNullOrEmpty(info.surfaceType)) count++;
            return count > 0 && count < 3;
        }

        internal static bool HasDuplicateDisplayName(string name, string excludeId)
        {
            foreach (var kvp in _byId)
            {
                if (string.Equals(kvp.Key, excludeId, StringComparison.Ordinal)) continue;
                if (string.Equals(kvp.Value.displayName, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        internal static bool TryGet(string id, out PropExtraInfo info)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id))
            {
                info = null;
                return false;
            }
            return _byId.TryGetValue(id, out info);
        }

        internal static bool HasNonEmptySlot(List<string> list)
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
                if (!string.IsNullOrEmpty(list[i])) return true;
            return false;
        }

        internal static PropExtraInfo Apply(string id, string displayName, string category,
            bool excluded, bool useRenderMeshCollider, string overrideMaterialName,
            string nativeMaterialName, string materialSourcePropId, string surfaceType,
            HashSet<string> disabledRenderers, string colliderIgnoredSubmeshes,
            List<string> perSlotOverrides = null, int forcedMaterialSlots = 0,
            bool isBush = false, float bushRadius = 0f, int soundGrassType = 1,
            bool keepOriginalHierarchy = false)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return null;

            bool any = !string.IsNullOrEmpty(displayName)
                    || !string.IsNullOrEmpty(category)
                    || excluded
                    || useRenderMeshCollider
                    || !string.IsNullOrEmpty(overrideMaterialName)
                    || !string.IsNullOrEmpty(surfaceType)
                    || (disabledRenderers != null && disabledRenderers.Count > 0)
                    || !string.IsNullOrEmpty(colliderIgnoredSubmeshes)
                    || HasNonEmptySlot(perSlotOverrides)
                    || forcedMaterialSlots > 1
                    || isBush
                    || keepOriginalHierarchy;

            if (!_byId.TryGetValue(id, out var info))
            {
                if (!any) return null;
                info = new PropExtraInfo { id = id };
                _byId[id] = info;
            }

            if (excluded)
            {
                info.displayName        = string.Empty;
                info.category           = string.Empty;
                info.excluded           = true;
                info.useRenderMeshCollider = false;
                info.colliderIgnoredSubmeshes = string.Empty;
                info.overrideMaterialId       = string.Empty;
                info.surfaceType              = string.Empty;
                info.disabledRenderers        = new List<string>();
                info.perSlotMaterialOverrides = null;
                info.forcedMaterialSlots      = 0;
                info.isBush                   = false;
                info.bushRadius               = 0f;
                info.soundGrassType           = 1;
                info.keepOriginalHierarchy    = false;
                info.disableBaking            = false;
                info.index                    = 0;
            }
            else
            {
                if (info.index <= 0) info.index = _nextIndex++;
                info.displayName        = displayName ?? string.Empty;
                info.category           = category ?? string.Empty;
                info.excluded           = false;
                info.useRenderMeshCollider = useRenderMeshCollider;
                info.colliderIgnoredSubmeshes = colliderIgnoredSubmeshes ?? string.Empty;
                info.overrideMaterialId = overrideMaterialName ?? string.Empty;
                // Store the true original and source only once — when a non-empty override is first applied.
                // Clear both when the override is removed so they don't linger.
                if (!string.IsNullOrEmpty(overrideMaterialName))
                {
                    // Only store nativeMaterialName when it differs from the override; if they match
                    // the renderer was contaminated and the value would be useless for un-contamination.
                    if (string.IsNullOrEmpty(info.nativeMaterialName) && !string.IsNullOrEmpty(nativeMaterialName)
                        && !string.Equals(nativeMaterialName, overrideMaterialName, StringComparison.OrdinalIgnoreCase))
                        info.nativeMaterialName = nativeMaterialName;
                    if (string.IsNullOrEmpty(info.materialSourcePropId) && !string.IsNullOrEmpty(materialSourcePropId))
                        info.materialSourcePropId = materialSourcePropId;
                }
                else
                {
                    info.nativeMaterialName    = string.Empty;
                    info.materialSourcePropId  = string.Empty;
                }
                info.surfaceType        = surfaceType ?? string.Empty;
                info.disabledRenderers  = new List<string>();
                if (disabledRenderers != null)
                    foreach (var path in disabledRenderers) info.disabledRenderers.Add(path);
                info.perSlotMaterialOverrides = HasNonEmptySlot(perSlotOverrides) ? perSlotOverrides : null;
                info.forcedMaterialSlots = forcedMaterialSlots;
                info.isBush = isBush;
                info.bushRadius = bushRadius;
                info.soundGrassType = soundGrassType;
                info.keepOriginalHierarchy = keepOriginalHierarchy;
            }

            Save();
            return info;
        }

        static void Load()
        {
            _loaded = true;
            _byId.Clear();
            _nextIndex = 1;
            _materialConstructions = new List<MaterialConstructionEntry>();
            _nextMaterialConstructionId = 0;

            try
            {
                if (Core.DebugMode)
                {
                    // Debug: try UserData JSON first (allows live editing)
                    if (File.Exists(SavePath))
                    {
                        var json = File.ReadAllText(SavePath);
                        if (!string.IsNullOrEmpty(json))
                        {
                            LoadFromJson(json);
                            return;
                        }
                    }
                    // Debug: fall back to UserData binary (useful for testing the export without rebuilding)
                    if (File.Exists(BinaryExportPath))
                    {
                        using var fs = File.OpenRead(BinaryExportPath);
                        LoadFromBinaryStream(fs);
                        BBLog.Msg("[PropMetadata] Loaded from UserData binary.");
                        return;
                    }
                }
                // Non-debug (or no UserData file found): use embedded binary
                LoadFromEmbedded();
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropMetadata] Load failed: {e.Message}");
            }
        }

        static void LoadFromJson(string json)
        {
            _loadedFromJson = true;
            var data = Deserialize(json);
            if (data == null || data.items == null) return;

            _nextIndex = Math.Max(1, data.nextIndex);
            int maxIndex = _nextIndex - 1;
            foreach (var item in data.items)
            {
                if (item == null || string.IsNullOrEmpty(item.id)) continue;
                if (!item.excluded && item.index <= 0) item.index = _nextIndex++;
                if (item.index > maxIndex) maxIndex = item.index;
                _byId[item.id] = item;
            }
            _nextIndex = Math.Max(_nextIndex, maxIndex + 1);

            _materialConstructions = data.materialConstructions ?? new List<MaterialConstructionEntry>();
            _nextMaterialConstructionId = Math.Max(0, data.nextMaterialConstructionId);
            ReconcileMaterialConstructionIds();

            // One-time cleanup: MicroSplat materials are runtime-generated so they can't have
            // a real source prop. Clear any that were incorrectly recorded.
            bool anyFixed = false;
            foreach (var item in _byId.Values)
            {
                if (string.IsNullOrEmpty(item.materialSourcePropId)) continue;
                if (string.IsNullOrEmpty(item.overrideMaterialId)) continue;
                if (!item.overrideMaterialId.StartsWith("[MicroSplat]", StringComparison.Ordinal)) continue;
                item.materialSourcePropId = string.Empty;
                anyFixed = true;
            }
            if (anyFixed) Save();
        }

        internal static void Save()
        {
            if (!_loadedFromJson) return;
            try
            {
                var dir = Path.GetDirectoryName(SavePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var data = new PropExtraInfoSave
                {
                    nextIndex = _nextIndex,
                    items = new List<PropExtraInfo>(_byId.Values),
                    nextMaterialConstructionId = _nextMaterialConstructionId,
                    materialConstructions = _materialConstructions
                };

                string json = Serialize(data);
                File.WriteAllText(SavePath, json);

                if (!_savePathLogged)
                {
                    _savePathLogged = true;
                    BBLog.Msg($"[PropMetadata] Saved to {SavePath}");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropMetadata] Save failed: {e.Message}");
            }
        }

        // ── Binary PMD format ─────────────────────────────────────────────────────

        internal static void SaveBinary(string path)
        {
            EnsureLoaded();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var fs = File.Open(path, FileMode.Create, FileAccess.Write);
            using var w = new BinaryWriter(fs, System.Text.Encoding.UTF8, leaveOpen: false);
            WriteBinary(w);
        }

        static void WriteBinary(BinaryWriter w)
        {
            // Header: magic "PMD" + version
            w.Write((byte)0x50); w.Write((byte)0x4D); w.Write((byte)0x44);
            w.Write(PmdVersion);

            var items = new List<PropExtraInfo>();
            foreach (var v in _byId.Values)
                if (v != null && !string.IsNullOrEmpty(v.id))
                    items.Add(v);

            w.Write(_nextIndex);
            w.Write(items.Count);

            foreach (var item in items)
            {
                w.Write(item.id);
                w.Write(item.index);

                bool hasDisplayName     = !item.excluded && !string.IsNullOrEmpty(item.displayName);
                bool hasCategory        = !item.excluded && !string.IsNullOrEmpty(item.category);
                bool hasColliderIgnored = !item.excluded && !string.IsNullOrEmpty(item.colliderIgnoredSubmeshes);
                bool hasOverrideMat     = !item.excluded && !string.IsNullOrEmpty(item.overrideMaterialId);

                byte flags1 = 0;
                if (item.excluded)                                flags1 |= 0x01;
                if (!item.excluded && item.useRenderMeshCollider) flags1 |= 0x02;
                if (!item.excluded && item.isBush)                flags1 |= 0x04;
                if (!item.excluded && item.keepOriginalHierarchy) flags1 |= 0x08;
                if (hasDisplayName)                               flags1 |= 0x10;
                if (hasCategory)                                  flags1 |= 0x20;
                if (hasColliderIgnored)                           flags1 |= 0x40;
                if (hasOverrideMat)                               flags1 |= 0x80;
                w.Write(flags1);

                if (item.excluded) continue;

                bool hasNativeMat   = !string.IsNullOrEmpty(item.nativeMaterialName);
                bool hasMatSource   = !string.IsNullOrEmpty(item.materialSourcePropId);
                bool hasSurface     = !string.IsNullOrEmpty(item.surfaceType);
                bool hasDisabled    = item.disabledRenderers != null && item.disabledRenderers.Count > 0;
                bool hasPerSlot     = HasNonEmptySlot(item.perSlotMaterialOverrides);
                bool hasForcedSlots = item.forcedMaterialSlots > 1;

                byte flags2 = 0;
                if (hasNativeMat)       flags2 |= 0x01;
                if (hasMatSource)       flags2 |= 0x02;
                if (hasSurface)         flags2 |= 0x04;
                if (hasDisabled)        flags2 |= 0x08;
                if (hasPerSlot)         flags2 |= 0x10;
                if (hasForcedSlots)     flags2 |= 0x20;
                if (item.disableBaking) flags2 |= 0x40;
                w.Write(flags2);

                if (hasDisplayName)     w.Write(item.displayName);
                if (hasCategory)        w.Write(item.category);
                if (hasColliderIgnored) w.Write(item.colliderIgnoredSubmeshes);
                if (hasOverrideMat)     w.Write(item.overrideMaterialId);
                if (hasNativeMat)       w.Write(item.nativeMaterialName);
                if (hasMatSource)       w.Write(item.materialSourcePropId);
                if (hasSurface)         w.Write(item.surfaceType);
                if (hasDisabled)
                {
                    var dr = item.disabledRenderers;
                    int cnt = Math.Min(dr.Count, 255);
                    w.Write((byte)cnt);
                    for (int i = 0; i < cnt; i++) w.Write(dr[i] ?? "");
                }
                if (hasPerSlot)
                {
                    var ps = item.perSlotMaterialOverrides;
                    int cnt = Math.Min(ps.Count, 255);
                    w.Write((byte)cnt);
                    for (int i = 0; i < cnt; i++) w.Write(ps[i] ?? "");
                }
                if (hasForcedSlots) w.Write(item.forcedMaterialSlots);
                if (item.isBush)
                {
                    w.Write(item.bushRadius);
                    w.Write(item.soundGrassType);
                }
            }

            w.Write(_nextMaterialConstructionId);
            w.Write(_materialConstructions.Count);
            foreach (var mc in _materialConstructions)
            {
                w.Write(mc.id);
                w.Write(mc.name ?? "");
                w.Write(mc.materialName ?? "");
                w.Write(mc.surfaceType ?? "");
                byte mcFlags = 0;
                if (mc.sunglassesNeeded) mcFlags |= 0x01;
                w.Write(mcFlags);
            }
        }

        static void LoadFromBinaryStream(Stream stream)
        {
            using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            byte b0 = r.ReadByte(), b1 = r.ReadByte(), b2 = r.ReadByte();
            if (b0 != 0x50 || b1 != 0x4D || b2 != 0x44)
                throw new InvalidDataException("Not a PMD file");
            byte version = r.ReadByte();
            if (version < 1 || version > PmdVersion)
                throw new InvalidDataException($"Unsupported PMD version {version}");

            _nextIndex = r.ReadInt32();
            int count = r.ReadInt32();

            for (int i = 0; i < count; i++)
            {
                string id = r.ReadString();
                int index = r.ReadInt32();
                byte flags1 = r.ReadByte();

                bool excluded = (flags1 & 0x01) != 0;

                if (string.IsNullOrEmpty(id))
                {
                    // Skip flags2 + payload for non-excluded entries to keep stream in sync
                    if (!excluded) r.ReadByte();
                    continue;
                }

                var item = new PropExtraInfo { id = id, index = index, excluded = excluded };

                if (!excluded)
                {
                    item.useRenderMeshCollider = (flags1 & 0x02) != 0;
                    item.isBush                = (flags1 & 0x04) != 0;
                    item.keepOriginalHierarchy = (flags1 & 0x08) != 0;
                    bool hasDisplayName     = (flags1 & 0x10) != 0;
                    bool hasCategory        = (flags1 & 0x20) != 0;
                    bool hasColliderIgnored = (flags1 & 0x40) != 0;
                    bool hasOverrideMat     = (flags1 & 0x80) != 0;

                    byte flags2 = r.ReadByte();
                    bool hasNativeMat   = (flags2 & 0x01) != 0;
                    bool hasMatSource   = (flags2 & 0x02) != 0;
                    bool hasSurface     = (flags2 & 0x04) != 0;
                    bool hasDisabled    = (flags2 & 0x08) != 0;
                    bool hasPerSlot     = (flags2 & 0x10) != 0;
                    bool hasForcedSlots = (flags2 & 0x20) != 0;
                    item.disableBaking  = (flags2 & 0x40) != 0;

                    if (hasDisplayName)     item.displayName              = r.ReadString();
                    if (hasCategory)        item.category                 = r.ReadString();
                    if (hasColliderIgnored) item.colliderIgnoredSubmeshes = r.ReadString();
                    if (hasOverrideMat)     item.overrideMaterialId       = r.ReadString();
                    if (hasNativeMat)       item.nativeMaterialName       = r.ReadString();
                    if (hasMatSource)       item.materialSourcePropId     = r.ReadString();
                    if (hasSurface)         item.surfaceType              = r.ReadString();
                    if (hasDisabled)
                    {
                        int cnt = r.ReadByte();
                        item.disabledRenderers = new List<string>(cnt);
                        for (int j = 0; j < cnt; j++) item.disabledRenderers.Add(r.ReadString());
                    }
                    if (hasPerSlot)
                    {
                        int cnt = r.ReadByte();
                        item.perSlotMaterialOverrides = new List<string>(cnt);
                        for (int j = 0; j < cnt; j++) item.perSlotMaterialOverrides.Add(r.ReadString());
                    }
                    if (hasForcedSlots) item.forcedMaterialSlots = r.ReadInt32();
                    if (item.isBush)
                    {
                        item.bushRadius     = r.ReadSingle();
                        item.soundGrassType = r.ReadInt32();
                    }
                }

                _byId[id] = item;
            }

            _materialConstructions = new List<MaterialConstructionEntry>();
            _nextMaterialConstructionId = 0;
            if (version >= 2)
            {
                _nextMaterialConstructionId = r.ReadInt32();
                int mcCount = r.ReadInt32();
                for (int i = 0; i < mcCount; i++)
                {
                    var mc = new MaterialConstructionEntry
                    {
                        id = r.ReadInt32(),
                        name = r.ReadString(),
                        materialName = r.ReadString(),
                        surfaceType = r.ReadString()
                    };
                    if (version >= 3)
                    {
                        byte mcFlags = r.ReadByte();
                        mc.sunglassesNeeded = (mcFlags & 0x01) != 0;
                    }
                    _materialConstructions.Add(mc);
                }
            }
            ReconcileMaterialConstructionIds();
        }

        static void ReconcileMaterialConstructionIds()
        {
            int maxId = -1;
            foreach (var e in _materialConstructions)
                if (e != null && e.id > maxId) maxId = e.id;
            _nextMaterialConstructionId = Math.Max(_nextMaterialConstructionId, maxId + 1);
            _materialConstructions.Sort((a, b) =>
                string.Compare(a?.name, b?.name, StringComparison.OrdinalIgnoreCase));
        }

        static void LoadFromEmbedded()
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("BabyBlocks.Props.prop_metadata.bin");
            if (stream == null)
            {
                MelonLogger.Warning("[PropMetadata] Embedded prop_metadata.bin not found.");
                return;
            }
            LoadFromBinaryStream(stream);
            BBLog.Msg("[PropMetadata] Loaded from embedded binary.");
        }

        // ─────────────────────────────────────────────────────────────────────────

        static string Serialize(PropExtraInfoSave data) => SerializeManual(data);

        static PropExtraInfoSave Deserialize(string json)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    IncludeFields = true
                };
                return JsonSerializer.Deserialize<PropExtraInfoSave>(json, options);
            }
            catch
            {
                return DeserializeManual(json);
            }
        }

        static string SerializeManual(PropExtraInfoSave data)
        {
            var sb = new System.Text.StringBuilder(1024);
            sb.Append("{\n  \"nextIndex\": ").Append(data.nextIndex).Append(",\n  \"items\": [\n");
            for (int i = 0; i < data.items.Count; i++)
            {
                var item = data.items[i];
                if (item == null) continue;
                sb.Append("    {\n");
                if (item.excluded)
                {
                    AppendJsonField(sb, "id", item.id, 6).Append(",\n");
                    sb.Append("      \"excluded\": true\n");
                }
                else
                {
                    AppendJsonField(sb, "id", item.id, 6).Append(",\n");
                    AppendJsonField(sb, "displayName", item.displayName, 6).Append(",\n");
                    AppendJsonField(sb, "category", item.category, 6).Append(",\n");
                    sb.Append("      \"excluded\": false,\n");
                    sb.Append("      \"useRenderMeshCollider\": ").Append(item.useRenderMeshCollider ? "true" : "false").Append(",\n");
                    AppendJsonField(sb, "colliderIgnoredSubmeshes", item.colliderIgnoredSubmeshes, 6).Append(",\n");
                    AppendJsonField(sb, "overrideMaterialId", item.overrideMaterialId, 6).Append(",\n");
                    AppendJsonField(sb, "nativeMaterialName", item.nativeMaterialName, 6).Append(",\n");
                    AppendJsonField(sb, "materialSourcePropId", item.materialSourcePropId, 6).Append(",\n");
                    AppendJsonField(sb, "surfaceType", item.surfaceType, 6).Append(",\n");
                    AppendJsonArray(sb, "disabledRenderers", item.disabledRenderers, 6).Append(",\n");
                    if (HasNonEmptySlot(item.perSlotMaterialOverrides))
                        AppendJsonArray(sb, "perSlotMaterialOverrides", item.perSlotMaterialOverrides, 6).Append(",\n");
                    if (item.forcedMaterialSlots > 1)
                        sb.Append("      \"forcedMaterialSlots\": ").Append(item.forcedMaterialSlots).Append(",\n");
                    if (item.isBush)
                    {
                        sb.Append("      \"isBush\": true,\n");
                        sb.Append("      \"bushRadius\": ").Append(item.bushRadius.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(",\n");
                        sb.Append("      \"soundGrassType\": ").Append(item.soundGrassType).Append(",\n");
                    }
                    if (item.keepOriginalHierarchy)
                        sb.Append("      \"keepOriginalHierarchy\": true,\n");
                    if (item.disableBaking)
                        sb.Append("      \"disableBaking\": true,\n");
                    sb.Append("      \"index\": ").Append(item.index).Append("\n");
                }
                sb.Append("    }");
                if (i < data.items.Count - 1) sb.Append(",");
                sb.Append("\n");
            }
            sb.Append("  ],\n");
            sb.Append("  \"nextMaterialConstructionId\": ").Append(data.nextMaterialConstructionId).Append(",\n");
            sb.Append("  \"materialConstructions\": [\n");
            for (int i = 0; i < data.materialConstructions.Count; i++)
            {
                var mc = data.materialConstructions[i];
                if (mc == null) continue;
                sb.Append("    {\n");
                sb.Append("      \"id\": ").Append(mc.id).Append(",\n");
                AppendJsonField(sb, "name", mc.name, 6).Append(",\n");
                AppendJsonField(sb, "materialName", mc.materialName, 6).Append(",\n");
                AppendJsonField(sb, "surfaceType", mc.surfaceType, 6);
                if (mc.sunglassesNeeded) sb.Append(",\n      \"sunglassesNeeded\": true");
                sb.Append("\n");
                sb.Append("    }");
                if (i < data.materialConstructions.Count - 1) sb.Append(",");
                sb.Append("\n");
            }
            sb.Append("  ]\n}");
            return sb.ToString();
        }

        static System.Text.StringBuilder AppendJsonField(System.Text.StringBuilder sb, string key, string value, int indent)
        {
            sb.Append(' ', indent).Append('"').Append(key).Append("\": ");
            AppendJsonString(sb, value ?? string.Empty);
            return sb;
        }

        static System.Text.StringBuilder AppendJsonArray(System.Text.StringBuilder sb, string key, List<string> values, int indent)
        {
            sb.Append(' ', indent).Append('"').Append(key).Append("\": [");
            if (values != null)
            {
                for (int i = 0; i < values.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    AppendJsonString(sb, values[i] ?? string.Empty);
                }
            }
            sb.Append(']');
            return sb;
        }

        static void AppendJsonString(System.Text.StringBuilder sb, string value)
        {
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        static PropExtraInfoSave DeserializeManual(string json)
        {
            var data = new PropExtraInfoSave { items = new List<PropExtraInfo>() };
            if (string.IsNullOrEmpty(json)) return data;

            data.nextIndex = ExtractInt(json, "nextIndex", 1);

            int itemsIdx = json.IndexOf("\"items\"", StringComparison.OrdinalIgnoreCase);
            if (itemsIdx < 0) return data;
            int arrStart = json.IndexOf('[', itemsIdx);
            if (arrStart < 0) return data;
            int arrEnd = FindMatching(json, arrStart, '[', ']');
            if (arrEnd < 0) return data;

            int i = arrStart + 1;
            while (i < arrEnd)
            {
                SkipWhitespace(json, ref i);
                if (i >= arrEnd) break;
                if (json[i] == ',') { i++; continue; }
                if (json[i] != '{') { i++; continue; }
                int objEnd = FindMatching(json, i, '{', '}');
                if (objEnd < 0) break;
                string obj = json.Substring(i, objEnd - i + 1);
                var item = new PropExtraInfo
                {
                    id = ExtractString(obj, "id"),
                    displayName = ExtractString(obj, "displayName"),
                    category = ExtractString(obj, "category"),
                    excluded = ExtractBool(obj, "excluded"),
                    useRenderMeshCollider = ExtractBool(obj, "useRenderMeshCollider"),
                    colliderIgnoredSubmeshes = ExtractString(obj, "colliderIgnoredSubmeshes"),
                    overrideMaterialId = ExtractString(obj, "overrideMaterialId"),
                    nativeMaterialName = ExtractString(obj, "nativeMaterialName"),
                    materialSourcePropId = ExtractString(obj, "materialSourcePropId"),
                    surfaceType = ExtractString(obj, "surfaceType"),
                    index = ExtractInt(obj, "index", 0),
                    disabledRenderers = ExtractStringArray(obj, "disabledRenderers"),
                    perSlotMaterialOverrides = ExtractStringArray(obj, "perSlotMaterialOverrides"),
                    forcedMaterialSlots = ExtractInt(obj, "forcedMaterialSlots", 0),
                    isBush = ExtractBool(obj, "isBush"),
                    bushRadius = ExtractFloat(obj, "bushRadius", 0f),
                    soundGrassType = ExtractInt(obj, "soundGrassType", 1),
                    keepOriginalHierarchy = ExtractBool(obj, "keepOriginalHierarchy"),
                    disableBaking = ExtractBool(obj, "disableBaking")
                };
                if (!string.IsNullOrEmpty(item.id))
                    data.items.Add(item);
                i = objEnd + 1;
            }

            data.nextMaterialConstructionId = ExtractInt(json, "nextMaterialConstructionId", 0);

            int mcIdx = json.IndexOf("\"materialConstructions\"", StringComparison.OrdinalIgnoreCase);
            if (mcIdx >= 0)
            {
                int mcArrStart = json.IndexOf('[', mcIdx);
                if (mcArrStart >= 0)
                {
                    int mcArrEnd = FindMatching(json, mcArrStart, '[', ']');
                    if (mcArrEnd >= 0)
                    {
                        int j = mcArrStart + 1;
                        while (j < mcArrEnd)
                        {
                            SkipWhitespace(json, ref j);
                            if (j >= mcArrEnd) break;
                            if (json[j] == ',') { j++; continue; }
                            if (json[j] != '{') { j++; continue; }
                            int mcObjEnd = FindMatching(json, j, '{', '}');
                            if (mcObjEnd < 0) break;
                            string mcObj = json.Substring(j, mcObjEnd - j + 1);
                            data.materialConstructions.Add(new MaterialConstructionEntry
                            {
                                id = ExtractInt(mcObj, "id", -1),
                                name = ExtractString(mcObj, "name"),
                                materialName = ExtractString(mcObj, "materialName"),
                                surfaceType = ExtractString(mcObj, "surfaceType"),
                                sunglassesNeeded = ExtractBool(mcObj, "sunglassesNeeded")
                            });
                            j = mcObjEnd + 1;
                        }
                    }
                }
            }

            return data;
        }

        static int FindMatching(string text, int start, char open, char close)
        {
            int depth = 0;
            bool inString = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"' && (i == 0 || text[i - 1] != '\\')) inString = !inString;
                if (inString) continue;
                if (c == open) depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        static HashSet<int> ParseIntSet(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var set = new HashSet<int>();
            var parts = text.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                if (int.TryParse(parts[i], out int value) && value >= 0)
                    set.Add(value);
            }
            return set.Count > 0 ? set : null;
        }

        static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        }

        static int ExtractInt(string json, string key, int fallback)
        {
            if (!TryFindKey(json, key, out int valueStart)) return fallback;
            int i = valueStart;
            SkipWhitespace(json, ref i);
            int sign = 1;
            if (i < json.Length && json[i] == '-') { sign = -1; i++; }
            int value = 0;
            bool found = false;
            while (i < json.Length && char.IsDigit(json[i]))
            {
                value = value * 10 + (json[i] - '0');
                i++;
                found = true;
            }
            return found ? value * sign : fallback;
        }

        static bool ExtractBool(string json, string key)
        {
            if (!TryFindKey(json, key, out int valueStart)) return false;
            int i = valueStart;
            SkipWhitespace(json, ref i);
            if (json.IndexOf("true", i, StringComparison.OrdinalIgnoreCase) == i) return true;
            return false;
        }

        static float ExtractFloat(string json, string key, float fallback)
        {
            if (!TryFindKey(json, key, out int valueStart)) return fallback;
            int i = valueStart;
            SkipWhitespace(json, ref i);
            int start = i;
            if (i < json.Length && (json[i] == '-' || json[i] == '+')) i++;
            while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '.')) i++;
            if (i == start) return fallback;
            string raw = json.Substring(start, i - start);
            return float.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;
        }

        static string ExtractString(string json, string key)
        {
            if (!TryFindKey(json, key, out int valueStart)) return string.Empty;
            int i = valueStart;
            SkipWhitespace(json, ref i);
            if (i >= json.Length || json[i] != '"') return string.Empty;
            i++;
            var sb = new System.Text.StringBuilder();
            while (i < json.Length)
            {
                char c = json[i++];
                if (c == '"') break;
                if (c == '\\' && i < json.Length)
                {
                    char esc = json[i++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 3 < json.Length)
                            {
                                string hex = json.Substring(i, 4);
                                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int code))
                                    sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(esc); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        static bool TryFindKey(string json, string key, out int valueStart)
        {
            valueStart = -1;
            int idx = json.IndexOf('"' + key + '"', StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            int colon = json.IndexOf(':', idx);
            if (colon < 0) return false;
            valueStart = colon + 1;
            return true;
        }

        static List<string> ExtractStringArray(string json, string key)
        {
            var result = new List<string>();
            if (!TryFindKey(json, key, out int valueStart)) return result;
            int i = valueStart;
            SkipWhitespace(json, ref i);
            if (i >= json.Length || json[i] != '[') return result;

            int end = FindMatching(json, i, '[', ']');
            if (end < 0) return result;
            int pos = i + 1;
            while (pos < end)
            {
                SkipWhitespace(json, ref pos);
                if (pos >= end) break;
                if (json[pos] == ',') { pos++; continue; }
                if (json[pos] != '"') { pos++; continue; }

                int start = pos;
                string val = ExtractString(json.Substring(start, end - start), string.Empty);
                if (!string.IsNullOrEmpty(val)) result.Add(val);

                int nextQuote = json.IndexOf('"', pos + 1);
                if (nextQuote < 0 || nextQuote >= end) break;
                pos = nextQuote + 1;
            }
            return result;
        }

        public static int GetMetaIndex(string id)
        {
            EnsureLoaded();
            return TryGetInfoById(id, out var info) ? info.index : 0;
        }

        public static string FindIdByIndex(int index)
        {
            EnsureLoaded();
            if (index <= 0) return null;
            foreach (var kvp in _byId)
                if (kvp.Value.index == index) return kvp.Key;
            return null;
        }

        public static string GetDisplayName(string id)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return null;
            return TryGetInfoById(id, out var info) && !string.IsNullOrEmpty(info.displayName)
                ? info.displayName : null;
        }

        public static string GetCategory(string id)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return "";
            return TryGetInfoById(id, out var info) ? (info.category ?? "") : "";
        }

        public static bool HasCategory(string id)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(id)) return false;
            return TryGetInfoById(id, out var info) && !string.IsNullOrEmpty(info.category);
        }

        public static List<string> GetAllCategories()
        {
            EnsureLoaded();
            var cats = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in _byId)
                if (!string.IsNullOrEmpty(kvp.Value.category))
                    cats.Add(kvp.Value.category);
            return new List<string>(cats);
        }
    }
}
