using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace BabyBlocks
{
    // Single-file cache of 128x128 JPG prop thumbnails keyed by prop ID.
    // Format: Version(int32) | Count(int32) | [propId(str), jpgLen(int32), jpgBytes...]
    static class PropPreviewCache
    {
        const int FormatVersion = 20;

        static string CachePath =>
            Path.Combine(MelonEnvironment.UserDataDirectory, "BabyBlocks", "PropPreviews.bin");

        static Dictionary<string, byte[]> _entries;

        public static Texture2D TryLoadTexture(string propId)
        {
            EnsureLoaded();
            if (!_entries.TryGetValue(propId, out var jpg)) return null;
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
                tex.filterMode = FilterMode.Bilinear;
                ImageConversion.LoadImage(tex, jpg, false);
                return tex;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropPreviews] Decode failed for \"{propId}\": {e.Message}");
                return null;
            }
        }

        public static void Save(string propId, byte[] jpg)
        {
            if (jpg == null || jpg.Length == 0) return;
            EnsureLoaded();
            _entries[propId] = jpg;
            WriteAll();
        }

        static void EnsureLoaded()
        {
            if (_entries != null) return;
            _entries = new Dictionary<string, byte[]>();
            try
            {
                if (!File.Exists(CachePath)) return;
                using var fs = File.OpenRead(CachePath);
                using var br = new BinaryReader(fs);
                if (br.ReadInt32() != FormatVersion) return;
                int count = br.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    string id = MaterialBaker.ReadStr(br);
                    int jpgLen = br.ReadInt32();
                    var bytes = br.ReadBytes(jpgLen);
                    _entries[id] = bytes;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropPreviews] Load failed from \"{CachePath}\": {e.Message}");
                _entries.Clear();
            }
        }

        static void WriteAll()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath));
                using var fs = File.Create(CachePath);
                using var bw = new BinaryWriter(fs);
                bw.Write(FormatVersion);
                bw.Write(_entries.Count);
                foreach (var kvp in _entries)
                {
                    MaterialBaker.WriteStr(bw, kvp.Key);
                    bw.Write(kvp.Value.Length);
                    bw.Write(kvp.Value);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PropPreviews] Save failed to \"{CachePath}\": {e.Message}");
            }
        }
    }
}
