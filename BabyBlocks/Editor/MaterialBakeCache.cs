using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using MelonLoader;
using MelonLoader.Utils;

namespace BabyBlocks
{
    // On-disk cache of MaterialBaker's output (baked mesh + atlas per renderer), so that
    // baking the same prop with the same material (see PropMetadataStore.GetMaterialCacheKey)
    // never has to repeat the GPU capture, even across different saves/sessions. All entries
    // live in a single Deflate-compressed-per-entry file, UserData/BabyBlocks/BakeCache.bin,
    // keyed by (prop id, material key) - mirroring PropLibrary's single-file GpuiCache.
    public static class MaterialBakeCache
    {
        const int FormatVersion = 1;

        static string CachePath =>
            Path.Combine(MelonEnvironment.UserDataDirectory, "BabyBlocks", "BakeCache.bin");

        static Dictionary<(string propId, string materialKey), (int rawLen, byte[] compressed)> _entries;

        public static List<MaterialBaker.BakedPartData> TryLoad(string propId, string materialKey)
        {
            EnsureLoaded();
            if (!_entries.TryGetValue((propId, materialKey), out var entry)) return null;

            try
            {
                using var compressed = new MemoryStream(entry.compressed);
                using var raw = new MemoryStream(entry.rawLen);
                using (var ds = new DeflateStream(compressed, CompressionMode.Decompress))
                    ds.CopyTo(raw);
                raw.Position = 0;

                using var br = new BinaryReader(raw);
                return MaterialBaker.ReadPartList(br);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[BakeCache] Failed to decode entry for \"{propId}\"/\"{materialKey}\": {e.Message}");
                return null;
            }
        }

        public static void Save(string propId, string materialKey, List<MaterialBaker.BakedPartData> parts)
        {
            if (parts == null || parts.Count == 0) return;
            EnsureLoaded();

            using var raw = new MemoryStream();
            using (var bw = new BinaryWriter(raw, System.Text.Encoding.UTF8, leaveOpen: true))
                MaterialBaker.WritePartList(bw, parts);

            using var compressed = new MemoryStream();
            raw.Position = 0;
            using (var ds = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                raw.CopyTo(ds);

            _entries[(propId, materialKey)] = ((int)raw.Length, compressed.ToArray());
            WriteAll();
        }

        static void EnsureLoaded()
        {
            if (_entries != null) return;
            _entries = new Dictionary<(string, string), (int, byte[])>();

            try
            {
                if (!File.Exists(CachePath)) return;
                using var fs = File.OpenRead(CachePath);
                using var br = new BinaryReader(fs);

                if (br.ReadInt32() != FormatVersion) return;

                int count = br.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    string propId = MaterialBaker.ReadStr(br);
                    string materialKey = MaterialBaker.ReadStr(br);
                    int rawLen = br.ReadInt32();
                    int compLen = br.ReadInt32();
                    var bytes = br.ReadBytes(compLen);
                    _entries[(propId, materialKey)] = (rawLen, bytes);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[BakeCache] Failed to load \"{CachePath}\": {e.Message}");
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
                    MaterialBaker.WriteStr(bw, kvp.Key.propId);
                    MaterialBaker.WriteStr(bw, kvp.Key.materialKey);
                    bw.Write(kvp.Value.rawLen);
                    bw.Write(kvp.Value.compressed.Length);
                    bw.Write(kvp.Value.compressed);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[BakeCache] Failed to save \"{CachePath}\": {e.Message}");
            }
        }
    }
}
