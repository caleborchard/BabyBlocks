using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Text;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace BabyBlocks
{
    // Binary .bbb format
    //
    // File header:
    //   Magic    : 3 bytes  { 0x42, 0x42, 0x42 }  ("BBB")
    //   Version  : 1 byte
    //   Count    : int32    (number of objects)
    //
    // Per object (version 5+):
    //   MetaIndex    : int32    (PropMetadataPanel index — the sole prop reference)
    //   ChunkIndex   : byte     (0..63 for chunked props, 255 for physics objects)
    //   Pos.x/y/z    : 3 × float32
    //   Rot.x/y/z    : 3 × float32 (w reconstructed)
    //   Scale.x/y/z  : 3 × float32
    //   PhysicsType  : byte     (0=static, 1=rigidbody, 2=grabable, 3=hat)
    //   GroupId      : byte     (0=no logical group)
    //   PhysGroupId  : byte     (0=no physics group)
    //   HatHairAmt   : float32  (hat hair-cut amount 0..1)
    //   GrabOffPos.xyz: 3 × float32  (hand-local grab offset)
    //   GrabOffRot.xyz: 3 × float32  (hand-local grab rotation, Euler degrees)
    //   HatOffPos.xyz : 3 × float32  (additive hat head offset)
    //   HatOffRot.xyz : 3 × float32  (additive hat head rotation, Euler degrees)
    //
    // After all object records (version 6+), a baked-render-data block:
    //   CompressedLen : int32    (0 if no objects have baked data)
    //   RawLen        : int32    (present only if CompressedLen > 0)
    //   CompressedData: byte[CompressedLen] (Deflate; see WriteBakedData/ReadBakedData)
    //
    // Decompressed baked-data layout:
    //   ObjectCount : int32
    //   For each:
    //     RecordIndex : int32 (index into the object records above)
    //     PartCount   : int32
    //     For each part (one per baked MeshRenderer):
    //       RendererPath : length-prefixed UTF8 string (sibling-index path from prop root)
    //       VertexCount  : int32
    //       Positions    : VertexCount × 3 × float32
    //       Normals      : VertexCount × 3 × float32
    //       UVs          : VertexCount × 2 × float32
    //       TriCount     : int32
    //       Triangles    : TriCount × int32
    //       AtlasLen     : int32
    //       AtlasJpg     : byte[AtlasLen]
    //
    // Version 4 (legacy): same without grab/hat offset fields.
    // Version 3 (legacy): same without PhysicsType/GroupId/HatHairAmt.
    // Version 2 (legacy): MetaIndex + full quaternion + scale.
    // Version 1 (legacy): PropId string + MetaIndex int32 + same transform.
    //
    // Version 7 adds, immediately after the format-version byte and before Count:
    //   BaseMapOff           : bool    (true if the base map was hidden when saved)
    //   DayWeatherPlaylist   : int32   (Menu.curChapter override applied while base map is off)
    //   RestoreDayWeatherPlaylist : int32 (Menu.curChapter to restore when base map is shown)
    //
    // Version 8 adds, at the end of each per-object record:
    //   MaterialConstructionId : length-prefixed UTF8 string (empty if no per-instance
    //                            material/surface override — see MaterialConstructionPanel)
    //
    // Version 9 adds, after RestoreDayWeatherPlaylist:
    //   WeatherPreset : int32 (-1 = Default, 0..N = locked preset index)
    //
    // Version 11 adds, after the baked-render-data block:
    //   GroupScaleCount : int32   (number of groups with a non-identity display scale)
    //   For each:
    //     GroupId  : int32
    //     Scale.xyz: 3 × float32
    //
    // Version 12 adds, at the end of each per-object record (after instanceFlags):
    //   TintR : byte    (material tint red,   0-255; 255=no tint)
    //   TintG : byte    (material tint green, 0-255; 255=no tint)
    //   TintB : byte    (material tint blue,  0-255; 255=no tint)
    static class LevelSaveLoad
    {
        static readonly byte[] Magic = { 0x42, 0x42, 0x42 };
        const byte FormatVersion = 12;

        // Reserved MetaIndex value identifying the Spawn Point — it isn't registered in
        // PropMetadataPanel (no per-instance metadata needed), so a sentinel outside the
        // valid (>0) PropMetadataPanel index range is used instead.
        const int SpawnPointMetaIndex = -1;

        struct SaveRecord
        {
            public LevelEditorObject obj;
            public int metaIndex;
            public int chunkIndex;
            public Vector3 position;
            public Quaternion rotation;
            public Vector3 scale;
            public PhysicsMode physicsType;
            public int groupId;
            public int physicsGroupId;
            public float hatHairAmt;
            public Vector3 grabOffsetPos;
            public Vector3 grabOffsetRot;
            public Vector3 hatOffsetPos;
            public Vector3 hatOffsetRot;
            public int materialConstructionId;
            public byte instanceFlags; // 0x01=sunglassesNeeded  0x02=playerPassthrough  0x04=hatSunglasses
            public Vector3 materialTint; // RGB 0-255; (255,255,255) = no tint
        }

        public static bool Save(string path)
        {
            var mgr = LevelEditorManager.Instance;
            if (mgr == null)
            {
                MelonLogger.Warning("[SaveLoad] LevelEditorManager not ready.");
                return false;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var fs = File.Open(path, FileMode.Create, FileAccess.Write);
                using var w  = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);

                w.Write(Magic);
                w.Write(FormatVersion);
                w.Write(!BaseMapController.BaseMapEnabled);
                w.Write(BaseMapController.DayWeatherPlaylist);
                w.Write(BaseMapController.RestoreDayWeatherPlaylist);
                w.Write(BaseMapController.WeatherPreset);

                var records = BuildSortedRecords(mgr);
                w.Write(records.Count);

                for (int i = 0; i < records.Count; i++)
                    WriteRecord(w, records[i]);

                WriteBakedData(w, records);
                WriteGroupScales(w);

                MelonLogger.Msg($"[SaveLoad] Saved {records.Count} object(s) → {path}");
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[SaveLoad] Save failed: {e.Message}");
                return false;
            }
        }

        public static (bool ok, int count, string error) Load(string path)
        {
            var mgr = LevelEditorManager.Instance;
            if (mgr == null)
                return (false, 0, "Level editor not ready.");

            if (!File.Exists(path))
                return (false, 0, "File not found.");

            try
            {
                using var fs = File.Open(path, FileMode.Open, FileAccess.Read);
                using var r  = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);
                return LoadFromReader(r, mgr, path);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[SaveLoad] Load failed: {e.Message}");
                return (false, 0, e.Message);
            }
        }

        // Deserializes a level from a BBB byte buffer (e.g. received over the network).
        // Behaves identically to Load() except baked render data is not expected/read,
        // and the spawn-point teleport is skipped (we don't teleport on behalf of a
        // peer's load — that would trigger Menu.me.Teleport for every connected player).
        public static (bool ok, int count, string error) LoadFromNetworkData(byte[] data)
        {
            if (data == null || data.Length == 0) return (false, 0, "No data.");
            var mgr = LevelEditorManager.Instance;
            if (mgr == null) return (false, 0, "Level editor not ready.");
            try
            {
                using var ms = new MemoryStream(data);
                using var r  = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);
                return LoadFromReader(r, mgr, "[network]", teleportOnLoad: false);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[SaveLoad] LoadFromNetworkData failed: {e.Message}");
                return (false, 0, e.Message);
            }
        }

        // Serializes the current level to a BBB byte buffer suitable for network
        // transmission — same format as Save() but with no baked render data block,
        // since baked data is large and re-baking on load is fast enough.
        // Returns null if the level manager is unavailable or the scene is empty.
        public static byte[] SerializeForNetwork()
        {
            var mgr = LevelEditorManager.Instance;
            if (mgr == null) return null;
            var records = BuildSortedRecords(mgr);
            if (records.Count == 0) return null;
            try
            {
                using var ms = new MemoryStream();
                using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: false);
                w.Write(Magic);
                w.Write(FormatVersion);
                w.Write(!BaseMapController.BaseMapEnabled);
                w.Write(BaseMapController.DayWeatherPlaylist);
                w.Write(BaseMapController.RestoreDayWeatherPlaylist);
                w.Write(BaseMapController.WeatherPreset);
                w.Write(records.Count);
                foreach (var rec in records)
                    WriteRecord(w, rec);
                w.Write(0); // CompressedLen = 0 — no baked data
                WriteGroupScales(w);
                return ms.ToArray();
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[SaveLoad] SerializeForNetwork failed: {e.Message}");
                return null;
            }
        }

        private static (bool ok, int count, string error) LoadFromReader(BinaryReader r, LevelEditorManager mgr, string sourceName, bool teleportOnLoad = true)
        {
            LevelEditor.Select(null);

            var magic = r.ReadBytes(3);
            if (magic.Length < 3 || magic[0] != 0x42 || magic[1] != 0x42 || magic[2] != 0x42)
                return (false, 0, "Not a .bbb file.");

            byte version = r.ReadByte();
            if (version > FormatVersion)
                return (false, 0, $"Unsupported format version {version}.");

            bool baseMapOff = false;
            int dayWeatherPlaylist = 0;
            int restoreDayWeatherPlaylist = 0;
            int weatherPreset = -1;
            if (version >= 7)
            {
                baseMapOff = r.ReadBoolean();
                dayWeatherPlaylist = r.ReadInt32();
                restoreDayWeatherPlaylist = r.ReadInt32();
            }
            if (version >= 9)
                weatherPreset = r.ReadInt32();

            int count = r.ReadInt32();
            int spawned = 0;

            mgr.RemoveAll();
            LevelEditor.ClearAllSelectionState();
            BabyBlocks.Networking.ModNetworking.ClearNetworkedObjects();
            BbHatSunglassesFlag.Clear();

            var leos = new LevelEditorObject[count];

            for (int i = 0; i < count; i++)
            {
                string propId;
                int chunkIndex = -1;
                if (version == 1)
                {
                    // Legacy: string propId + metaIndex (discarded — use propId directly).
                    propId = ReadLegacyString(r);
                    r.ReadInt32();
                }
                else if (version == 2)
                {
                    int metaIndex = r.ReadInt32();
                    propId = PropMetadataStore.FindIdByIndex(metaIndex);
                    if (string.IsNullOrEmpty(propId))
                    {
                        MelonLogger.Warning($"[SaveLoad] No prop for index {metaIndex}");
                        r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); // pos
                        r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); // rot
                        r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); // scale
                        continue;
                    }
                }
                else
                {
                    int metaIndex = r.ReadInt32();
                    chunkIndex = r.ReadByte();
                    propId = metaIndex == SpawnPointMetaIndex
                        ? PropLibrary.SpawnPointPropId
                        : PropMetadataStore.FindIdByIndex(metaIndex);
                    if (string.IsNullOrEmpty(propId))
                    {
                        MelonLogger.Warning($"[SaveLoad] No prop for index {metaIndex}");
                        r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); // pos
                        r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); // rot (compact)
                        r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); // scale
                        if (version >= 4)
                        {
                            r.ReadByte(); // physicsType
                            r.ReadByte(); // groupId
                            r.ReadByte(); // physicsGroupId
                            r.ReadSingle(); // hatHairAmt
                        }
                        if (version >= 5)
                        {
                            for (int k = 0; k < 12; k++) r.ReadSingle(); // grab/hat offsets
                        }
                        if (version >= 8) r.ReadInt32();  // materialConstructionId
                        if (version >= 10) r.ReadByte();  // instanceFlags
                        if (version >= 12) { r.ReadByte(); r.ReadByte(); r.ReadByte(); } // tint
                        continue;
                    }
                }

                var pos   = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var rot   = (version >= 3) ? ReadCompactRotation(r)
                                           : new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var scale = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

                PhysicsMode physicsType      = PhysicsMode.Static;
                int recordGroupId            = 0;
                int recordPhysicsGroupId     = 0;
                float hatHairAmt             = 0f;
                var grabOffsetPos = Vector3.zero;
                var grabOffsetRot = Vector3.zero;
                var hatOffsetPos  = Vector3.zero;
                var hatOffsetRot  = Vector3.zero;
                if (version >= 4)
                {
                    byte rawPhysics = r.ReadByte();
                    physicsType = Enum.IsDefined(typeof(PhysicsMode), (int)rawPhysics)
                        ? (PhysicsMode)rawPhysics : PhysicsMode.Static;
                    recordGroupId        = r.ReadByte();
                    recordPhysicsGroupId = r.ReadByte();
                    hatHairAmt           = Mathf.Clamp01(r.ReadSingle());
                }
                if (version >= 5)
                {
                    grabOffsetPos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    grabOffsetRot = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    hatOffsetPos  = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    hatOffsetRot  = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                }

                int  materialConstructionId = version >= 8  ? r.ReadInt32() : -1;
                byte instanceFlags          = version >= 10 ? r.ReadByte()  : (byte)0;
                var  materialTint           = version >= 12
                    ? new Vector3(r.ReadByte(), r.ReadByte(), r.ReadByte())
                    : new Vector3(255f, 255f, 255f);

                var info = PropLibrary.FindById(propId);
                if (info == null)
                {
                    MelonLogger.Warning($"[SaveLoad] Prop not found: {propId}");
                    continue;
                }

                var leo = mgr.SpawnFromPropInfo(info, pos);
                if (leo == null) continue;

                leo.transform.rotation   = rot;
                // The spawn point marker is fixed-size — ignore any saved scale.
                leo.transform.localScale = PropLibrary.IsSpawnPointProp(propId) ? Vector3.one : scale;
                mgr.SyncLoopBase(leo);
                if (version >= 3 && chunkIndex >= 0)
                {
                    if (chunkIndex == 255)
                    {
                        leo.chunkIndex = 255;
                        leo.chunkCoord = new Vector2Int(-1, -1);
                    }
                    else
                    {
                        int safeChunk = Mathf.Clamp(chunkIndex, 0, 63);
                        leo.chunkIndex = safeChunk;
                        leo.chunkCoord = new Vector2Int(safeChunk % 8, safeChunk / 8);
                    }
                }
                leo.physicsMode      = physicsType;
                leo.groupId          = recordGroupId;
                leo.physicsGroupId   = recordPhysicsGroupId;
                leo.hatHairAmt       = hatHairAmt;
                leo.grabOffsetPos    = grabOffsetPos;
                leo.grabOffsetRot    = grabOffsetRot;
                leo.hatOffsetPos     = hatOffsetPos;
                leo.hatOffsetRot     = hatOffsetRot;
                leo.materialConstructionId = materialConstructionId;
                if (materialConstructionId >= 0)
                {
                    var construction = MaterialConstructionLibrary.FindById(materialConstructionId);
                    if (construction != null)
                        MaterialConstructionPanel.ApplyToInstance(leo, construction, pushHistory: false);
                    else
                    {
                        MelonLogger.Warning($"[SaveLoad] Material construction {materialConstructionId} not found for {leo.addressableKey}");
                        leo.materialConstructionId = -1;
                    }
                }

                leo.sunglassesNeeded  = (instanceFlags & 0x01) != 0;
                leo.playerPassthrough = (instanceFlags & 0x02) != 0;
                if (leo.sunglassesNeeded && leo.GetComponent<BbSunglassesChecker>() == null)
                    leo.gameObject.AddComponent<BbSunglassesChecker>();
                if (leo.playerPassthrough)
                    PropInstanceServices.SetBushPassthrough(leo.gameObject, true);
                if ((instanceFlags & 0x04) != 0)
                    BbHatSunglassesFlag.Set(leo, true);
                PropInstanceServices.ApplyTint(leo, materialTint);
                leos[i] = leo;
                spawned++;

                // Both clients load the same .bbb in the same record order, so the
                // record index is a netId both sides agree on without any negotiation -
                // lets selection-highlight/transform/delete sync work for props that
                // came from a shared save file rather than being placed live this session.
                BabyBlocks.Networking.ModNetworking.RegisterLoadedNetworkedObject((ulong)(i + 1), leo);
            }

            if (version >= 6)
                ReadBakedData(r, leos);

            GroupManager.ApplyGroups();
            PhysicsObjectManager.SyncLoadedHatHairValues();

            if (version >= 11)
                ReadGroupScales(r);

            // Teleport BEFORE applying the saved base-map state: SetBaseMapEnabled's
            // chunk-hide scan and brl.off toggling need to happen at the player's
            // final position, and Teleport itself needs BRL active to stream in
            // chunks at the destination — doing this after brl.off=true left the
            // player ungrounded with stale chunks and stuck Menu.me.teleporting.
            // Skipped for network loads: Menu.me.Teleport() would execute on every
            // connected client, not just the joining one, teleporting everyone.
            if (teleportOnLoad)
                TeleportToSpawnPoint(leos);

            // Menu.Teleport's coroutine runs across many frames, so defer applying
            // the base-map state until it finishes (see ApplyLoadedBaseMapStateDelayed).
            if (version >= 7)
                MelonCoroutines.Start(
                    BaseMapController.ApplyLoadedBaseMapStateDelayed(baseMapOff, dayWeatherPlaylist, restoreDayWeatherPlaylist));
            BaseMapController.SetWeatherPreset(weatherPreset);

            MelonLogger.Msg($"[SaveLoad] Loaded {spawned}/{count} object(s) from {sourceName}");
            return (true, spawned, null);
        }

        // Teleports the player to the level's Spawn Point (if one was loaded). Only
        // triggered by loading a .bbb level file — never by game saves or other
        // teleport actions.
        static void TeleportToSpawnPoint(LevelEditorObject[] leos)
        {
            foreach (var leo in leos)
            {
                if (leo == null || !PropLibrary.IsSpawnPointProp(leo.addressableKey)) continue;

                var player = PlayerMovement.me;
                if (player == null || Menu.me == null) return;

                // Show the "Teleporting..." overlay and temporarily step out of cursor/editor
                // mode (if active) so the editor UI stays out of the way during the teleport.
                FlyCamController.BeginLevelLoadTeleport();

                // Face the player the same way the spawn point's arrow points (local +Z)
                // before handing off to Teleport — TeleportCo preserves whatever rotation
                // is on player.anim.transform when it places the player.
                player.anim.transform.rotation = Quaternion.Euler(0f, leo.transform.eulerAngles.y, 0f);
                Menu.me.Teleport(leo.transform.position);
                MelonCoroutines.Start(SnapFlyCamToPlayer(player));
                return;
            }
        }

        // Mirrors FarTeleportCo's fly-cam snap: once Menu.me.Teleport's coroutine
        // settles, move the fly cam to the player's new body position so the editor
        // camera follows the player instead of staying at its pre-load position.
        static IEnumerator SnapFlyCamToPlayer(PlayerMovement player)
        {
            // Wait one frame so Menu.me.Teleport's coroutine has had a chance to
            // set Menu.me.teleporting = true before we start polling it.
            yield return null;
            while (Menu.me != null && Menu.me.teleporting) yield return null;
            FlyCamController.EndLevelLoadTeleport();
            if (player != null && player.flyCam != null && player.torsoRbs != null && player.torsoRbs.Length > 0)
                player.flyCam.transform.position = player.torsoRbs[0].transform.position;
        }

        // Version 1 backward-compat: int32 byte-length followed by UTF-8 bytes.
        static string ReadLegacyString(BinaryReader r)
        {
            int len = r.ReadInt32();
            if (len <= 0) return "";
            return Encoding.UTF8.GetString(r.ReadBytes(len));
        }

        static List<SaveRecord> BuildSortedRecords(LevelEditorManager mgr)
        {
            var records = new List<SaveRecord>();
            if (mgr == null) return records;

            foreach (var leo in mgr.Objects)
            {
                if (leo == null || string.IsNullOrEmpty(leo.addressableKey)) continue;

                int metaIndex;
                if (PropLibrary.IsSpawnPointProp(leo.addressableKey))
                {
                    metaIndex = SpawnPointMetaIndex;
                }
                else
                {
                    metaIndex = PropMetadataStore.GetMetaIndex(leo.addressableKey);
                    if (metaIndex <= 0) continue;
                }

                var position = leo.hasLoopBasePosition ? leo.loopBasePosition : leo.transform.position;
                records.Add(new SaveRecord
                {
                    obj = leo,
                    metaIndex = metaIndex,
                    chunkIndex = leo.physicsMode == PhysicsMode.Static
                        ? LevelEditorManager.GetChunkIndex(position)
                        : 255,
                    position = position,
                    rotation = CanonicalizeRotation(leo.transform.rotation),
                    scale = leo.transform.localScale,
                    physicsType      = leo.physicsMode,
                    groupId          = leo.groupId,
                    physicsGroupId   = leo.physicsGroupId,
                    hatHairAmt       = leo.hatHairAmt,
                    grabOffsetPos    = leo.grabOffsetPos,
                    grabOffsetRot    = leo.grabOffsetRot,
                    hatOffsetPos     = leo.hatOffsetPos,
                    hatOffsetRot     = leo.hatOffsetRot,
                    materialConstructionId = leo.materialConstructionId,
                    instanceFlags = (byte)((leo.sunglassesNeeded ? 0x01 : 0) | (leo.playerPassthrough ? 0x02 : 0) | (BbHatSunglassesFlag.Has(leo) ? 0x04 : 0)),
                    materialTint  = leo.materialTint,
                });
            }

            records.Sort(CompareRecords);
            return records;
        }

        static int CompareRecords(SaveRecord a, SaveRecord b)
        {
            int chunkCompare = a.chunkIndex.CompareTo(b.chunkIndex);
            if (chunkCompare != 0) return chunkCompare;

            int zCompare = a.position.z.CompareTo(b.position.z);
            if (zCompare != 0) return zCompare;

            int xCompare = a.position.x.CompareTo(b.position.x);
            if (xCompare != 0) return xCompare;

            int yCompare = a.position.y.CompareTo(b.position.y);
            if (yCompare != 0) return yCompare;

            return string.Compare(a.obj?.addressableKey, b.obj?.addressableKey, StringComparison.Ordinal);
        }

        static void WriteRecord(BinaryWriter w, SaveRecord record)
        {
            w.Write(record.metaIndex);
            w.Write((byte)Mathf.Clamp(record.chunkIndex, 0, 255));
            w.Write(record.position.x);  w.Write(record.position.y);  w.Write(record.position.z);
            WriteCompactRotation(w, record.rotation);
            w.Write(record.scale.x); w.Write(record.scale.y); w.Write(record.scale.z);
            w.Write((byte)record.physicsType);
            w.Write((byte)Mathf.Clamp(record.groupId, 0, 255));
            w.Write((byte)Mathf.Clamp(record.physicsGroupId, 0, 255));
            w.Write(Mathf.Clamp01(record.hatHairAmt));
            w.Write(record.grabOffsetPos.x); w.Write(record.grabOffsetPos.y); w.Write(record.grabOffsetPos.z);
            w.Write(record.grabOffsetRot.x); w.Write(record.grabOffsetRot.y); w.Write(record.grabOffsetRot.z);
            w.Write(record.hatOffsetPos.x);  w.Write(record.hatOffsetPos.y);  w.Write(record.hatOffsetPos.z);
            w.Write(record.hatOffsetRot.x);  w.Write(record.hatOffsetRot.y);  w.Write(record.hatOffsetRot.z);
            w.Write(record.materialConstructionId);
            w.Write(record.instanceFlags);
            w.Write((byte)Mathf.Clamp(Mathf.RoundToInt(record.materialTint.x), 0, 255));
            w.Write((byte)Mathf.Clamp(Mathf.RoundToInt(record.materialTint.y), 0, 255));
            w.Write((byte)Mathf.Clamp(Mathf.RoundToInt(record.materialTint.z), 0, 255));
        }

        // Persists every physics-enabled object's already-baked mesh+atlas (see
        // MaterialBaker.ExportBakedData) so Load() can skip MaterialBaker.Bake's GPU
        // capture entirely. Static objects are never baked, so they're skipped here.
        // The whole block is Deflate-compressed - the per-vertex float arrays compress
        // well, and the cost on top of the already-compressed JPG atlases is negligible.
        static void WriteBakedData(BinaryWriter w, List<SaveRecord> records)
        {
            var entries = new List<(int index, List<MaterialBaker.BakedPartData> parts)>();
            int physicsRecordCount = 0;
            for (int i = 0; i < records.Count; i++)
            {
                if (records[i].physicsType == PhysicsMode.Static) continue;
                physicsRecordCount++;
                var parts = MaterialBaker.ExportBakedData(records[i].obj.gameObject);
                if (parts.Count > 0) entries.Add((i, parts));
            }

            MelonLogger.Msg($"[SaveLoad] WriteBakedData: {entries.Count}/{physicsRecordCount} physics object(s) had exportable bake data");

            if (entries.Count == 0)
            {
                w.Write(0);
                return;
            }

            using var raw = new MemoryStream();
            using (var bw = new BinaryWriter(raw, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(entries.Count);
                foreach (var (index, parts) in entries)
                {
                    bw.Write(index);
                    MaterialBaker.WritePartList(bw, parts);
                }
            }

            using var compressed = new MemoryStream();
            raw.Position = 0;
            using (var ds = new DeflateStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                raw.CopyTo(ds);

            var compressedBytes = compressed.ToArray();
            w.Write(compressedBytes.Length);
            w.Write((int)raw.Length);
            w.Write(compressedBytes);
        }

        // Reads the block written by WriteBakedData and applies each part to its
        // corresponding spawned object via MaterialBaker.ImportBakedData, BEFORE
        // GroupManager.ApplyGroups() runs - so Bake()'s "already baked" guard skips the GPU
        // capture for every renderer covered by the saved data.
        static void ReadBakedData(BinaryReader r, LevelEditorObject[] leos)
        {
            int compressedLen = r.ReadInt32();
            if (compressedLen <= 0) return;
            int rawLen = r.ReadInt32();
            var compressedBytes = r.ReadBytes(compressedLen);

            using var compressed = new MemoryStream(compressedBytes);
            using var raw = new MemoryStream(rawLen);
            using (var ds = new DeflateStream(compressed, CompressionMode.Decompress))
                ds.CopyTo(raw);
            raw.Position = 0;

            using var br = new BinaryReader(raw, Encoding.UTF8, leaveOpen: true);
            int objCount = br.ReadInt32();
            for (int o = 0; o < objCount; o++)
            {
                int index = br.ReadInt32();
                var parts = MaterialBaker.ReadPartList(br);

                if (index >= 0 && index < leos.Length && leos[index] != null
                    && !PropMetadataStore.GetDisableBaking(leos[index].addressableKey))
                    MaterialBaker.ImportBakedData(leos[index].gameObject, parts);
            }
        }

        static void WriteGroupScales(BinaryWriter w)
        {
            var scales = GroupManager.GetAllGroupDisplayScales().ToList();
            w.Write(scales.Count);
            foreach (var kv in scales)
            {
                w.Write(kv.Key);
                w.Write(kv.Value.x); w.Write(kv.Value.y); w.Write(kv.Value.z);
            }
        }

        static void ReadGroupScales(BinaryReader r)
        {
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                int groupId = r.ReadInt32();
                var scale   = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                GroupManager.SetGroupDisplayScale(groupId, scale);
            }
        }

        static void WriteCompactRotation(BinaryWriter w, Quaternion rotation)
        {
            rotation = CanonicalizeRotation(rotation);
            w.Write(rotation.x);
            w.Write(rotation.y);
            w.Write(rotation.z);
        }

        static Quaternion ReadCompactRotation(BinaryReader r)
        {
            float x = r.ReadSingle();
            float y = r.ReadSingle();
            float z = r.ReadSingle();
            float w = 1f - (x * x + y * y + z * z);
            w = w > 0f ? Mathf.Sqrt(w) : 0f;
            return new Quaternion(x, y, z, w);
        }

        static Quaternion CanonicalizeRotation(Quaternion rotation)
        {
            if (rotation.w < 0f)
                rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
            return rotation;
        }
    }
}
