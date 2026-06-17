using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;

namespace BabyBlocks
{
    // Unified most-recent-first history for props and materials.
    // File: UserData/BabyBlocks/history.txt — one line per entry, chronological:
    //   "prop:<addressableKey>" or "mat:<id>"
    static class PropHistory
    {
        const int MaxEntries = 32;

        // Single list preserving insertion order. Most-recently-used first.
        static readonly List<string> _history = new(); // "prop:..." or "mat:42"

        static readonly List<ResolvedHistoryEntry> _resolvedCache = new();
        static bool _resolvedDirty = true;
        static bool _loaded;

        static string SavePath =>
            Path.Combine(MelonEnvironment.UserDataDirectory, "BabyBlocks", "history.txt");

        public struct ResolvedHistoryEntry
        {
            public PropInfo Prop;
            public MaterialConstructionEntry Mat;
            public bool IsMat => Prop == null;
        }

        // ---- Recording ----

        public static void RecordUse(string propId)
        {
            if (string.IsNullOrEmpty(propId)) return;
            EnsureLoaded();
            var key = "prop:" + propId;
            _history.Remove(key);
            _history.Insert(0, key);
            Trim();
            _resolvedDirty = true;
            MelonLogger.Msg($"[PropHistory] RecordUse prop: {propId} (total: {_history.Count})");
            Save();
        }

        public static void RecordMaterialUse(int matId)
        {
            EnsureLoaded();
            var key = "mat:" + matId;
            _history.Remove(key);
            _history.Insert(0, key);
            Trim();
            _resolvedDirty = true;
            MelonLogger.Msg($"[PropHistory] RecordMaterialUse id={matId} (total: {_history.Count})");
            Save();
        }

        static void Trim()
        {
            while (_history.Count > MaxEntries)
                _history.RemoveAt(_history.Count - 1);
        }

        // ---- Retrieval ----

        // Returns entries in chronological order (most recent first), resolving each to
        // its PropInfo or MaterialConstructionEntry. Entries that can't be resolved yet
        // (e.g. materials not loaded) are omitted; the cache stays dirty so they retry.
        public static IReadOnlyList<ResolvedHistoryEntry> GetAllResolved()
        {
            EnsureLoaded();
            if (_resolvedDirty)
            {
                _resolvedCache.Clear();
                bool anyMissing = false;
                foreach (var raw in _history)
                {
                    if (raw.StartsWith("prop:"))
                    {
                        var info = PropLibrary.FindById(raw.Substring(5));
                        if (info != null) _resolvedCache.Add(new ResolvedHistoryEntry { Prop = info });
                        else anyMissing = true;
                    }
                    else if (raw.StartsWith("mat:") && int.TryParse(raw.Substring(4), out int id))
                    {
                        var entry = MaterialConstructionLibrary.FindById(id);
                        if (entry != null) _resolvedCache.Add(new ResolvedHistoryEntry { Mat = entry });
                        else anyMissing = true;
                    }
                }
                if (!anyMissing) _resolvedDirty = false;
            }
            return _resolvedCache;
        }

        // ---- Load / Save ----

        static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            MelonLogger.Msg($"[PropHistory] Loading from: {SavePath}");
            Load();
        }

        static void Load()
        {
            try
            {
                if (!File.Exists(SavePath))
                {
                    MelonLogger.Msg("[PropHistory] No history file found, starting fresh.");
                    return;
                }
                _history.Clear();
                foreach (var rawLine in File.ReadAllLines(SavePath))
                {
                    var line = rawLine.Trim();
                    if ((line.StartsWith("prop:") || line.StartsWith("mat:")) && _history.Count < MaxEntries)
                        _history.Add(line);
                }
                _resolvedDirty = true;
                MelonLogger.Msg($"[PropHistory] Loaded {_history.Count} entries.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropHistory] Load failed: {e.Message}");
            }
        }

        static void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(SavePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllLines(SavePath, _history);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropHistory] Save failed: {e.Message}");
            }
        }
    }
}
