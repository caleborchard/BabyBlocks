using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using UnityEngine;

namespace BabyBlocks
{
    static class PropHistory
    {
        const int MaxEntries = 24;

        static readonly List<string>   _ids   = new();
        static readonly List<PropInfo> _cache = new();
        static bool _cacheDirty = true;
        static bool _loaded;

        static string SavePath =>
            Path.Combine(Application.persistentDataPath, "BabyBlocks_history.txt");

        public static void RecordUse(string propId)
        {
            if (string.IsNullOrEmpty(propId)) return;
            EnsureLoaded();
            _ids.Remove(propId);
            _ids.Insert(0, propId);
            if (_ids.Count > MaxEntries) _ids.RemoveAt(_ids.Count - 1);
            _cacheDirty = true;
            Save();
        }

        public static IReadOnlyList<PropInfo> GetHistoryProps()
        {
            EnsureLoaded();
            if (_cacheDirty)
            {
                _cache.Clear();
                foreach (var id in _ids)
                {
                    var info = PropLibrary.FindById(id);
                    if (info != null) _cache.Add(info);
                }
                _cacheDirty = false;
            }
            return _cache;
        }

        static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            Load();
        }

        static void Load()
        {
            try
            {
                if (!File.Exists(SavePath)) return;
                _ids.Clear();
                foreach (var line in File.ReadAllLines(SavePath))
                {
                    var trimmed = line.Trim();
                    if (!string.IsNullOrEmpty(trimmed) && _ids.Count < MaxEntries)
                        _ids.Add(trimmed);
                }
                _cacheDirty = true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropHistory] Load failed: {e.Message}");
            }
        }

        static void Save()
        {
            try { File.WriteAllLines(SavePath, _ids); }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropHistory] Save failed: {e.Message}");
            }
        }
    }
}
