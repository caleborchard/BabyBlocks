using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MelonLoader;
using UnityEngine;

namespace BabyBlocks
{
    // -------------------------------------------------------------------------
    // Binary .bbb format
    //
    // File header:
    //   Magic    : 3 bytes  { 0x42, 0x42, 0x42 }  ("BBB")
    //   Version  : 1 byte
    //   Count    : int32    (number of objects)
    //
    // Per object (version 4):
    //   MetaIndex  : int32    (PropMetadataPanel index — the sole prop reference)
    //   ChunkIndex : byte     (0..63 for chunked props, 255 for physics objects)
    //   Pos.x/y/z  : 3 × float32
    //   Rot.x/y/z  : 3 × float32 (w reconstructed)
    //   Scale.x/y/z: 3 × float32
    //   PhysicsType: byte     (0=static, 1=rigidbody, 2=grabable, 3=hat)
    //   GroupId    : byte     (0=no logical group)
    //   PhysGroupId: byte     (0=no physics group)
    //   HatHairAmt : float32  (hat hair-cut amount 0..1)
    //
    // Version 3 (legacy): same without PhysicsType/GroupId/HatHairAmt.
    // Version 2 (legacy): MetaIndex + full quaternion + scale.
    // Version 1 (legacy): PropId string + MetaIndex int32 + same transform.
    // -------------------------------------------------------------------------
    static class LevelSaveLoad
    {
        static readonly byte[] Magic = { 0x42, 0x42, 0x42 };
        const byte FormatVersion = 4;

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

                var records = BuildSortedRecords(mgr);
                w.Write(records.Count);

                for (int i = 0; i < records.Count; i++)
                    WriteRecord(w, records[i]);

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
                LevelEditor.Select(null);
                using var fs = File.Open(path, FileMode.Open, FileAccess.Read);
                using var r  = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);

                var magic = r.ReadBytes(3);
                if (magic.Length < 3 || magic[0] != 0x42 || magic[1] != 0x42 || magic[2] != 0x42)
                    return (false, 0, "Not a .bbb file.");

                byte version = r.ReadByte();
                if (version > 5)
                    return (false, 0, $"Unsupported format version {version}.");

                int count = r.ReadInt32();
                int spawned = 0;

                mgr.RemoveAll();

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
                        propId = PropMetadataPanel.FindIdByIndex(metaIndex);
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
                        propId = PropMetadataPanel.FindIdByIndex(metaIndex);
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
                    if (version >= 4)
                    {
                        byte rawPhysics = r.ReadByte();
                        physicsType = Enum.IsDefined(typeof(PhysicsMode), (int)rawPhysics)
                            ? (PhysicsMode)rawPhysics : PhysicsMode.Static;
                        recordGroupId        = r.ReadByte();
                        recordPhysicsGroupId = r.ReadByte();
                        hatHairAmt           = Mathf.Clamp01(r.ReadSingle());
                    }

                    var info = PropLibrary.FindById(propId);
                    if (info == null)
                    {
                        MelonLogger.Warning($"[SaveLoad] Prop not found: {propId}");
                        continue;
                    }

                    var leo = mgr.SpawnFromPropInfo(info, pos);
                    if (leo == null) continue;

                    leo.transform.rotation   = rot;
                    leo.transform.localScale = scale;
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
                    spawned++;
                }

                mgr.ApplyPhysicsGroups();
                mgr.SyncLoadedHatHairValues();

                MelonLogger.Msg($"[SaveLoad] Loaded {spawned}/{count} object(s) from {path}");
                return (true, spawned, null);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[SaveLoad] Load failed: {e.Message}");
                return (false, 0, e.Message);
            }
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
                int metaIndex = PropMetadataPanel.GetMetaIndex(leo.addressableKey);
                if (metaIndex <= 0) continue;

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

    // -------------------------------------------------------------------------
    // Windows native open/save file dialogs via comdlg32 P/Invoke.
    // -------------------------------------------------------------------------
    static class NativeFileDialog
    {
        [StructLayout(LayoutKind.Sequential)]
        struct OPENFILENAME
        {
            public int    lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public IntPtr lpstrFilter;
            public IntPtr lpstrCustomFilter;
            public int    nMaxCustFilter;
            public int    nFilterIndex;
            public IntPtr lpstrFile;
            public int    nMaxFile;
            public IntPtr lpstrFileTitle;
            public int    nMaxFileTitle;
            public IntPtr lpstrInitialDir;
            public IntPtr lpstrTitle;
            public int    Flags;
            public short  nFileOffset;
            public short  nFileExtension;
            public IntPtr lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public IntPtr lpTemplateName;
            public IntPtr pvReserved;
            public int    dwReserved;
            public int    FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetOpenFileNameW(ref OPENFILENAME ofn);

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetSaveFileNameW(ref OPENFILENAME ofn);

        const int OFN_EXPLORER       = 0x00080000;
        const int OFN_FILEMUSTEXIST  = 0x00001000;
        const int OFN_PATHMUSTEXIST  = 0x00000800;
        const int OFN_OVERWRITEPROMPT= 0x00000002;
        const int OFN_NOCHANGEDIR    = 0x00000008;
        const int OFN_HIDEREADONLY   = 0x00000004;

        // Filter string uses \0 between name/pattern pairs and \0\0 to terminate.
        static readonly string FilterStr =
            "BabyBlocks Level (*.bbb)\0*.bbb\0All Files (*.*)\0*.*\0\0";

        public static string OpenDialog(string initialPath)
            => RunDialog(initialPath, open: true);

        public static string SaveDialog(string initialPath)
            => RunDialog(initialPath, open: false);

        static string RunDialog(string initialPath, bool open)
        {
            const int BufSize = 32768;

            string initDir  = null;
            string initFile = null;
            if (!string.IsNullOrEmpty(initialPath))
            {
                initDir  = Path.GetDirectoryName(initialPath);
                initFile = Path.GetFileName(initialPath);
            }

            var fileBuf   = Marshal.AllocHGlobal(BufSize * 2);
            var filterBuf = WriteWString(FilterStr);
            var defExtBuf = WriteWString("bbb");
            var titleBuf  = WriteWString(open ? "Open Level" : "Save Level");
            var dirBuf    = initDir  != null ? WriteWString(initDir)  : IntPtr.Zero;

            try
            {
                // Zero buffer then optionally pre-fill with initial filename.
                for (int i = 0; i < BufSize * 2; i++) Marshal.WriteByte(fileBuf, i, 0);
                if (!string.IsNullOrEmpty(initFile))
                {
                    var fb = Encoding.Unicode.GetBytes(initFile);
                    int copyLen = Math.Min(fb.Length, (BufSize - 1) * 2);
                    Marshal.Copy(fb, 0, fileBuf, copyLen);
                }

                var ofn = new OPENFILENAME
                {
                    lStructSize    = Marshal.SizeOf<OPENFILENAME>(),
                    lpstrFilter    = filterBuf,
                    nFilterIndex   = 1,
                    lpstrFile      = fileBuf,
                    nMaxFile       = BufSize,
                    lpstrTitle     = titleBuf,
                    lpstrDefExt    = defExtBuf,
                    lpstrInitialDir= dirBuf,
                    Flags          = OFN_EXPLORER | OFN_NOCHANGEDIR | OFN_HIDEREADONLY |
                                     (open ? OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST
                                           : OFN_OVERWRITEPROMPT),
                };

                bool ok = open ? GetOpenFileNameW(ref ofn) : GetSaveFileNameW(ref ofn);
                if (!ok) return null;

                string result = Marshal.PtrToStringUni(fileBuf);
                // Append .bbb extension if the user didn't type one and it's a save dialog.
                if (!open && !string.IsNullOrEmpty(result)
                    && !result.EndsWith(".bbb", StringComparison.OrdinalIgnoreCase))
                    result += ".bbb";
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(fileBuf);
                Marshal.FreeHGlobal(filterBuf);
                Marshal.FreeHGlobal(defExtBuf);
                Marshal.FreeHGlobal(titleBuf);
                if (dirBuf != IntPtr.Zero) Marshal.FreeHGlobal(dirBuf);
            }
        }

        // Write a Unicode string (including any embedded \0 chars) to unmanaged memory.
        static IntPtr WriteWString(string s)
        {
            var bytes = Encoding.Unicode.GetBytes(s);
            var buf   = Marshal.AllocHGlobal(bytes.Length + 2); // +2 for null terminator
            Marshal.Copy(bytes, 0, buf, bytes.Length);
            Marshal.WriteInt16(buf, bytes.Length, 0);           // null terminator
            return buf;
        }
    }

    // -------------------------------------------------------------------------
    // Draggable Save / Load window — visible in both debug and non-debug modes.
    // -------------------------------------------------------------------------
    static class SaveLoadWindow
    {
        const float WinW   = 310f;
        const float WinH   = 160f;
        const float HeaderH= 30f;
        const float Pad    = 7f;

        static Rect   _windowRect;
        static bool   _initialized;
        static bool   _dragging;
        static Vector2 _dragOffset;

        static string _filePath = "";
        static string _status   = "";
        static float  _statusTime;

        const float StatusDuration = 4f;

        // Ensure initial position is set after Screen dimensions are known.
        static void EnsureInit()
        {
            if (_initialized) return;
            _initialized = true;

            string saved = Core.LastSavePath;
            _filePath = !string.IsNullOrEmpty(saved)
                ? saved
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "BabyBlocks", "level.bbb");

            _windowRect = new Rect(Screen.width - WinW - 10f, Screen.height - WinH - 40f, WinW, WinH);
        }

        public static bool ContainsPoint(Vector2 guiPoint) =>
            _initialized && _windowRect.Contains(guiPoint);

        public static void DrawGUI(Event e)
        {
            EnsureInit();

            // Clamp to screen
            _windowRect.x = Mathf.Clamp(_windowRect.x, 0f, Screen.width  - WinW);
            _windowRect.y = Mathf.Clamp(_windowRect.y, 0f, Screen.height - WinH);

            GUI.color = new Color(0f, 0f, 0f, 0.72f);
            GUI.Box(_windowRect, "");
            GUI.color = Color.white;

            // Header
            var headerRect = new Rect(_windowRect.x, _windowRect.y, WinW, HeaderH);
            GUI.Label(new Rect(_windowRect.x + Pad, _windowRect.y + 6f, WinW - Pad * 2f, 20f),
                "Save / Load");

            // Drag handling
            if (e.type == EventType.MouseDown && e.button == 0 && headerRect.Contains(e.mousePosition))
            {
                _dragging   = true;
                _dragOffset = e.mousePosition - new Vector2(_windowRect.x, _windowRect.y);
                e.Use();
            }
            if (_dragging)
            {
                if (e.type == EventType.MouseDrag)
                {
                    _windowRect.x = e.mousePosition.x - _dragOffset.x;
                    _windowRect.y = e.mousePosition.y - _dragOffset.y;
                    e.Use();
                }
                if (e.type == EventType.MouseUp)
                    _dragging = false;
            }

            float contentY = _windowRect.y + HeaderH + Pad;
            float contentX = _windowRect.x + Pad;
            float innerW   = WinW - Pad * 2f;

            // Save / Load buttons
            float btnW = (innerW - Pad) * 0.5f;
            if (GUI.Button(new Rect(contentX, contentY, btnW, 22f), "Save"))
                DoSave();
            if (GUI.Button(new Rect(contentX + btnW + Pad, contentY, btnW, 22f), "Load"))
                DoLoad();

            contentY += 26f;

            if (GUI.Button(new Rect(contentX, contentY, innerW, 22f), "Clear"))
                DoClear();

            contentY += 26f;

            bool newLooping = GUI.Toggle(new Rect(contentX, contentY, innerW, 20f),
                LevelEditorManager.ChunkLoopingEnabled, "Chunk looping");
            if (newLooping != LevelEditorManager.ChunkLoopingEnabled)
                LevelEditorManager.ChunkLoopingEnabled = newLooping;

            contentY += 24f;

            // File path text field + browse button
            float browseW = 26f;
            float fieldW  = innerW - browseW - Pad;
            _filePath = GUI.TextField(new Rect(contentX, contentY, fieldW, 20f), _filePath ?? "");
            if (GUI.Button(new Rect(contentX + fieldW + Pad, contentY, browseW, 20f), "…"))
                BrowseForFile();

            // Status message (fades after a few seconds)
            if (!string.IsNullOrEmpty(_status) && Time.realtimeSinceStartup - _statusTime < StatusDuration)
            {
                float alpha = Mathf.Clamp01(StatusDuration - (Time.realtimeSinceStartup - _statusTime));
                GUI.color = new Color(1f, 1f, 0.4f, alpha);
                GUI.Label(new Rect(contentX, contentY + 22f, innerW, 18f), _status);
                GUI.color = Color.white;
            }
        }

        static void DoSave()
        {
            if (string.IsNullOrWhiteSpace(_filePath))
            {
                SetStatus("No file path set.");
                return;
            }
            bool ok = LevelSaveLoad.Save(_filePath);
            if (ok) Core.LastSavePath = _filePath;
            SetStatus(ok ? "Saved." : "Save failed — see log.");
        }

        static void DoClear()
        {
            var mgr = LevelEditorManager.Instance;
            if (mgr == null) { SetStatus("Level editor not ready."); return; }
            mgr.RemoveAll();
            SetStatus("Cleared.");
        }

        static void DoLoad()
        {
            if (string.IsNullOrWhiteSpace(_filePath))
            {
                SetStatus("No file path set.");
                return;
            }
            var (ok, count, error) = LevelSaveLoad.Load(_filePath);
            if (ok) Core.LastSavePath = _filePath;
            SetStatus(ok ? $"Loaded {count} object(s)." : $"Load failed: {error}");
        }

        static void BrowseForFile()
        {
            string picked = NativeFileDialog.OpenDialog(_filePath);
            if (picked != null)
            {
                _filePath = picked;
                Core.LastSavePath = picked;
            }
        }

        public static void TriggerSave()
        {
            EnsureInit();
            DoSave();
        }

        static void SetStatus(string msg)
        {
            _status     = msg;
            _statusTime = Time.realtimeSinceStartup;
        }
    }
}
