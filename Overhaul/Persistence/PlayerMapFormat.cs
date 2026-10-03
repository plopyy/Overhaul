using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Overhaul.Persistence
{
    // Native map conversion is for admission/import only; live exploration is stored as dirty bit blocks.
    internal static class PlayerMapFormat
    {
        internal const int CellsPerBlock = 4096, BytesPerBlock = CellsPerBlock / 8;
        internal static PlayerChange[] Decode(byte[] native)
        {
            if (native == null) return new PlayerChange[0];
            if (native.Length > 16 * 1024 * 1024) throw new InvalidDataException("Map import is too large");
            var rows = new List<PlayerChange>();
            using (var outer = NativeFormat.Reader(native))
            {
                if (outer.ReadInt32() != 8) throw new InvalidDataException("Unsupported native map version; expected 8");
                byte[] compressed = NativeFormat.Bytes(outer); NativeFormat.End(outer);
                using (var input = new MemoryStream(compressed, false))
                using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                using (var limited = new LimitedReadStream(gzip, 64 * 1024 * 1024))
                using (var buffered = new BufferedStream(limited, 32768))
                using (var r = new BinaryReader(buffered, Encoding.UTF8))
                {
                    int size = r.ReadInt32();
                    if (size < 1 || size > 4096) throw new InvalidDataException("Invalid map dimensions");
                    rows.Add(new PlayerChange("state", false, "map_size", size, null, null, null));
                    int cells = checked(size * size);
                    foreach (string layer in new[] { "self", "others" })
                        for (int start = 0; start < cells; start += CellsPerBlock)
                        {
                            var bits = new byte[BytesPerBlock]; bool nonzero = false;
                            for (int i = 0; i < Math.Min(CellsPerBlock, cells - start); i++)
                            {
                                byte value = r.ReadByte();
                                if (value > 1) throw new InvalidDataException("Invalid exploration bit");
                                if (value != 0) { bits[i / 8] |= (byte)(1 << (i % 8)); nonzero = true; }
                            }
                            if (nonzero) rows.Add(new PlayerChange("map", false, layer, start / CellsPerBlock, bits));
                        }
                    int pins = r.ReadInt32();
                    if (pins < 0 || pins > 100000) throw new InvalidDataException("Invalid map pin count");
                    for (int i = 0; i < pins; i++)
                    {
                        string label = r.ReadString(); float x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle();
                        int type = r.ReadInt32(); bool check = r.ReadBoolean(); long owner = r.ReadInt64(); string author = r.ReadString();
                        string id = i.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
                        rows.Add(new PlayerChange("pins", false, id, type, label, x, y, z, check, owner));
                        rows.Add(new PlayerChange("knowledge", false, "map_pin_author", id, author));
                    }
                    rows.Add(new PlayerChange("state", false, "map_public", r.ReadBoolean(), null, null, null));
                    if (r.BaseStream.ReadByte() != -1) throw new InvalidDataException("Trailing map data");
                }
            }
            return rows.ToArray();
        }
        internal static byte[] Encode(IEnumerable<PlayerChange> source)
        {
            var rows = source.Where(r => !r.Delete).ToArray();
            var state = rows.Where(r => r.Table == "state").ToDictionary(r => (string)r.Values[0], r => r.Values);
            if (!state.TryGetValue("map_size", out var dimension)) return null;
            int size = Convert.ToInt32(dimension[1]);
            if (size < 1 || size > 4096) throw new InvalidDataException("Invalid map dimensions");
            var blocks = rows.Where(r => r.Table == "map").Select(r => r.Values).ToDictionary(r => (string)r[0] + ":" + Convert.ToInt32(r[1]), r => (byte[])r[2]);
            var authors = rows.Where(r => r.Table == "knowledge" && (string)r.Values[0] == "map_pin_author").Select(r => r.Values).ToDictionary(r => (string)r[1], r => (string)r[2]);
            using (var raw = new MemoryStream())
            using (var w = new BinaryWriter(raw, Encoding.UTF8, true))
            {
                w.Write(size); int cells = checked(size * size);
                foreach (string layer in new[] { "self", "others" })
                    for (int start = 0; start < cells; start += CellsPerBlock)
                    {
                        blocks.TryGetValue(layer + ":" + start / CellsPerBlock, out var bits);
                        if (bits != null && bits.Length != BytesPerBlock) throw new InvalidDataException("Invalid map block");
                        for (int i = 0; i < Math.Min(CellsPerBlock, cells - start); i++)
                            w.Write(bits != null && (bits[i / 8] & (1 << (i % 8))) != 0);
                    }
                var pins = rows.Where(r => r.Table == "pins").Select(r => r.Values).OrderBy(r => (string)r[0], StringComparer.Ordinal).ToArray();
                w.Write(pins.Length);
                foreach (var pin in pins)
                {
                    w.Write((string)pin[2]); w.Write(Convert.ToSingle(pin[3])); w.Write(Convert.ToSingle(pin[4])); w.Write(Convert.ToSingle(pin[5]));
                    w.Write(Convert.ToInt32(pin[1])); w.Write(Convert.ToBoolean(pin[6])); w.Write(Convert.ToInt64(pin[7]));
                    w.Write(authors.TryGetValue((string)pin[0], out var author) ? author : "");
                }
                w.Write(state.TryGetValue("map_public", out var visible) && Convert.ToBoolean(visible[1])); w.Flush();
                byte[] compressed = NativeFormat.Deflate(raw.ToArray());
                using (var output = new MemoryStream())
                using (var result = new BinaryWriter(output))
                { result.Write(8); result.Write(compressed.Length); result.Write(compressed); result.Flush(); return output.ToArray(); }
            }
        }
        private sealed class LimitedReadStream : Stream
        {
            private readonly Stream input;
            private long remaining;
            internal LimitedReadStream(Stream input, long limit) { this.input = input; remaining = limit; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (count == 0) return 0;
                if (remaining == 0) { if (input.ReadByte() != -1) throw new InvalidDataException("Inflated map exceeds limit"); return 0; }
                int n = input.Read(buffer, offset, (int)Math.Min(remaining, count)); remaining -= n; return n;
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
