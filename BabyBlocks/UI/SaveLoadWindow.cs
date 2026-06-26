using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MelonLoader.Utils;
using UnityEngine;

namespace BabyBlocks
{
    // Windows native open/save file dialogs via comdlg32 P/Invoke.
    static class NativeFileDialog
    {
        [StructLayout(LayoutKind.Sequential)]
        struct OPENFILENAME
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public IntPtr lpstrFilter;
            public IntPtr lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;
            public int nMaxFile;
            public IntPtr lpstrFileTitle;
            public int nMaxFileTitle;
            public IntPtr lpstrInitialDir;
            public IntPtr lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public IntPtr lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public IntPtr lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetOpenFileNameW(ref OPENFILENAME ofn);

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetSaveFileNameW(ref OPENFILENAME ofn);

        const int OFN_EXPLORER = 0x00080000;
        const int OFN_FILEMUSTEXIST = 0x00001000;
        const int OFN_PATHMUSTEXIST = 0x00000800;
        const int OFN_OVERWRITEPROMPT = 0x00000002;
        const int OFN_NOCHANGEDIR = 0x00000008;
        const int OFN_HIDEREADONLY = 0x00000004;

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

            string initDir = null;
            string initFile = null;
            if (!string.IsNullOrEmpty(initialPath))
            {
                initDir = Path.GetDirectoryName(initialPath);
                initFile = Path.GetFileName(initialPath);
            }

            var fileBuf = Marshal.AllocHGlobal(BufSize * 2);
            var filterBuf = WriteWString(FilterStr);
            var defExtBuf = WriteWString("bbb");
            var titleBuf = WriteWString(open ? "Open Level" : "Save Level");
            var dirBuf = initDir != null ? WriteWString(initDir) : IntPtr.Zero;

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
                    lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                    lpstrFilter = filterBuf,
                    nFilterIndex = 1,
                    lpstrFile = fileBuf,
                    nMaxFile = BufSize,
                    lpstrTitle = titleBuf,
                    lpstrDefExt = defExtBuf,
                    lpstrInitialDir = dirBuf,
                    Flags = OFN_EXPLORER | OFN_NOCHANGEDIR | OFN_HIDEREADONLY |
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
            var buf = Marshal.AllocHGlobal(bytes.Length + 2); // +2 for null terminator
            Marshal.Copy(bytes, 0, buf, bytes.Length);
            Marshal.WriteInt16(buf, bytes.Length, 0);           // null terminator
            return buf;
        }
    }

    // Draggable Save / Load window
    static class SaveLoadWindow
    {
        const float WinW = 310f;
        const float BaseWinH = 232f;
        const float HeaderH = 30f;
        const float Pad = 7f;
        const float DropdownItemH = 18f;

        static Rect _windowRect;
        static bool _initialized;
        static bool _dragging;
        static Vector2 _dragOffset;
        static bool _weatherDropdownOpen;

        // Clear/Load both wipe the current scene — the first click swaps that
        // button's label to "Are you sure?" and a second click within
        // ConfirmTimeout actually performs the action, unless the scene is
        // already empty.
        static bool  _confirmClear;
        static bool  _confirmLoad;
        static float _confirmClearTime;
        static float _confirmLoadTime;
        const float ConfirmTimeout = 3f;

        static string _filePath = "";
        static string _status = "";
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
                : Path.Combine(MelonEnvironment.UserDataDirectory, "BabyBlocks", "levels", "level.bbb");

            _windowRect = new Rect(Screen.width - WinW - 10f, Screen.height - BaseWinH - 40f, WinW, BaseWinH);
        }

        public static bool ContainsPoint(Vector2 guiPoint) =>
            _initialized && _windowRect.Contains(guiPoint);

        public static void DrawGUI(Event e)
        {
            EnsureInit();

            int playlistCount = BaseMapController.DayWeatherPlaylistCount;
            if (BaseMapController.BaseMapEnabled) _weatherDropdownOpen = false;
            float dropdownExtra = (_weatherDropdownOpen && playlistCount > 0) ? playlistCount * DropdownItemH : 0f;
            float winH = BaseWinH + dropdownExtra;

            // Clamp to screen
            _windowRect.x = Mathf.Clamp(_windowRect.x, 0f, Screen.width  - WinW);
            _windowRect.y = Mathf.Clamp(_windowRect.y, 0f, Screen.height - winH);
            _windowRect.height = winH;

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
                _dragging = true;
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
            float innerW = WinW - Pad * 2f;

            float btnW = (innerW - Pad) * 0.5f;

            // Auto-revert a pending confirmation if it's not acted on promptly.
            if (_confirmClear && Time.realtimeSinceStartup - _confirmClearTime > ConfirmTimeout) _confirmClear = false;
            if (_confirmLoad && Time.realtimeSinceStartup - _confirmLoadTime > ConfirmTimeout) _confirmLoad = false;

            // Save / Load buttons
            if (GUI.Button(new Rect(contentX, contentY, btnW, 22f), "Save"))
            {
                _confirmLoad = false;
                DoSave();
            }
            if (GUI.Button(new Rect(contentX + btnW + Pad, contentY, btnW, 22f), _confirmLoad ? "Are you sure?" : "Load"))
                RequestLoad();

            contentY += 26f;

            if (GUI.Button(new Rect(contentX, contentY, innerW, 22f), _confirmClear ? "Are you sure?" : "Clear"))
                RequestClear();

            contentY += 26f;

            bool newLooping = GUI.Toggle(new Rect(contentX, contentY, innerW, 20f),
                LevelEditorManager.ChunkLoopingEnabled, "Chunk looping");
            if (newLooping != LevelEditorManager.ChunkLoopingEnabled)
                LevelEditorManager.ChunkLoopingEnabled = newLooping;

            contentY += 24f;

            bool newBaseMap = GUI.Toggle(new Rect(contentX, contentY, innerW, 20f),
                BaseMapController.BaseMapEnabled, "Base map");
            if (newBaseMap != BaseMapController.BaseMapEnabled)
            {
                BaseMapController.SetBaseMapEnabled(newBaseMap);
                BabyBlocks.Networking.ModNetworking.SendBaseMapState(newBaseMap);
            }

            contentY += 24f;

            // Day Weather Playlist dropdown — only usable while the base map is
            // hidden. SetBaseMapEnabled captures/restores Menu.curChapter around it.
            bool weatherEnabled = !BaseMapController.BaseMapEnabled && playlistCount > 0;
            GUI.enabled = weatherEnabled;
            string playlistLabel = playlistCount > 0
                ? $"Weather playlist: {BaseMapController.DayWeatherPlaylist}"
                : "Weather playlist: n/a";
            if (GUI.Button(new Rect(contentX, contentY, innerW, 20f),
                playlistLabel + (_weatherDropdownOpen ? " ▲" : " ▼")))
                _weatherDropdownOpen = !_weatherDropdownOpen;
            GUI.enabled = true;

            contentY += 24f;

            if (weatherEnabled && _weatherDropdownOpen)
            {
                for (int i = 0; i < playlistCount; i++)
                {
                    string lbl = (i == BaseMapController.DayWeatherPlaylist ? "> " : "") + $"Playlist {i}";
                    if (GUI.Button(new Rect(contentX, contentY, innerW, DropdownItemH), lbl))
                    {
                        BaseMapController.SetDayWeatherPlaylist(i);
                        _weatherDropdownOpen = false;
                    }
                    contentY += DropdownItemH;
                }
            }

            if (GUI.Button(new Rect(contentX, contentY, innerW, 22f), "Open Levels Folder"))
                OpenLevelsFolder();

            contentY += 26f;

            // File path text field + browse button
            float browseW = 26f;
            float fieldW = innerW - browseW - Pad;
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

        // Scene is already empty — clearing it would have no effect, so skip the
        // "Are you sure?" prompt. Otherwise the first click arms the prompt and
        // the second (within ConfirmTimeout) actually clears.
        static void RequestClear()
        {
            var mgr = LevelEditorManager.Instance;
            if (mgr == null) { SetStatus("Level editor not ready."); return; }
            if (mgr.Objects.Count == 0) { SetStatus("Nothing to clear."); return; }

            if (_confirmClear)
            {
                _confirmClear = false;
                DoClear();
            }
            else
            {
                _confirmClear = true;
                _confirmClearTime = Time.realtimeSinceStartup;
            }
        }

        static void DoClear()
        {
            var mgr = LevelEditorManager.Instance;
            if (mgr == null) { SetStatus("Level editor not ready."); return; }
            mgr.RemoveAll();
            LevelEditor.ClearAllSelectionState();
            BabyBlocks.Networking.ModNetworking.SendLevelCleared();
            SetStatus("Cleared.");
        }

        // Loading replaces the whole scene — only prompt if there's something to lose.
        static void RequestLoad()
        {
            if (string.IsNullOrWhiteSpace(_filePath))
            {
                SetStatus("No file path set.");
                return;
            }

            var mgr = LevelEditorManager.Instance;
            if (mgr == null || mgr.Objects.Count == 0)
            {
                DoLoad();
                return;
            }

            if (_confirmLoad)
            {
                _confirmLoad = false;
                DoLoad();
            }
            else
            {
                _confirmLoad = true;
                _confirmLoadTime = Time.realtimeSinceStartup;
            }
        }

        static void DoLoad()
        {
            if (string.IsNullOrWhiteSpace(_filePath))
            {
                SetStatus("No file path set.");
                return;
            }
            var (ok, count, error) = LevelSaveLoad.Load(_filePath);
            if (ok)
            {
                Core.LastSavePath = _filePath;
                BabyBlocks.Networking.ModNetworking.BroadcastLevelLoad();
            }
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

        static void OpenLevelsFolder()
        {
            string folder = Path.Combine(MelonEnvironment.UserDataDirectory, "BabyBlocks", "levels");
            try { Directory.CreateDirectory(folder); } catch { }
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    Process.Start("explorer.exe", folder);
                else
                    Process.Start(new ProcessStartInfo("xdg-open", folder) { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                SetStatus($"Could not open folder: {ex.Message}");
            }
        }

        public static void TriggerSave()
        {
            EnsureInit();
            DoSave();
        }

        // Called from the UniverseLib file dropdown — opens a native save dialog then saves.
        public static void TriggerSaveDialog()
        {
            EnsureInit();
            string picked = NativeFileDialog.SaveDialog(_filePath);
            if (string.IsNullOrEmpty(picked)) return;
            _filePath = picked;
            DoSave();
        }

        // Called from the UniverseLib file dropdown — opens a native open dialog then loads.
        public static void TriggerLoadDialog()
        {
            EnsureInit();
            string picked = NativeFileDialog.OpenDialog(_filePath);
            if (string.IsNullOrEmpty(picked)) return;
            _filePath = picked;
            DoLoad();
        }

        // Called from the UniverseLib file dropdown — clears without the two-click guard
        // (the dropdown button itself acts as the confirmation layer).
        public static void TriggerClear() => DoClear();

        public static bool HasObjects =>
            LevelEditorManager.Instance != null && LevelEditorManager.Instance.Objects.Count > 0;

        static void SetStatus(string msg)
        {
            _status = msg;
            _statusTime = Time.realtimeSinceStartup;
        }
    }
}
