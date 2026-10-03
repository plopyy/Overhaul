using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Overhaul.Persistence
{
    internal static class NativeFormat
    {
        internal const int WorldVersion = 41;
        internal static readonly string[] Types = { "float", "vector3", "quaternion", "int", "long", "string", "bytes" };
        internal static BinaryReader Reader(byte[] data) => new BinaryReader(new MemoryStream(data, false), Encoding.UTF8);
        internal static float[] Vector(BinaryReader r, int count = 3)
        { var value = new float[count]; for (int i = 0; i < count; i++) value[i] = r.ReadSingle(); return value; }
        internal static int Count(BinaryReader r) { int n = r.ReadByte(); return (n & 128) == 0 ? n : ((n & 127) << 8) | r.ReadByte(); }
        internal static byte[] Bytes(BinaryReader r)
        {
            int count = r.ReadInt32();
            if (count < 0 || count > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Invalid native byte array size");
            return r.ReadBytes(count);
        }
        internal static byte[] Inflate(byte[] data)
        {
            using (var input = new MemoryStream(data, false))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream()) { gzip.CopyTo(output); return output.ToArray(); }
        }
        internal static byte[] Deflate(byte[] data)
        {
            using (var output = new MemoryStream())
            { using (var gzip = new GZipStream(output, CompressionMode.Compress, true)) gzip.Write(data, 0, data.Length); return output.ToArray(); }
        }
        internal static void End(BinaryReader r)
        { if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Unexpected trailing world data"); }

        internal static ObjectRecord ReadObject(BinaryReader r, long id, int chunk, int order, Func<int,string> prefab, Func<int,string> key)
        {
            var record = new ObjectRecord { Id = id, User = 1, NetworkId = checked((uint)id), Chunk = chunk, Order = order, Flags = r.ReadUInt16() };
            record.Position = (record.Flags & 8192) != 0 ? new float[] { r.ReadInt16(), 0, r.ReadInt16() } : Vector(r);
            record.Prefab = r.ReadInt32(); record.Name = prefab(record.Prefab);
            record.Rotation = new float[3];
            if ((record.Flags & 4096) != 0)
            {
                uint packed = r.ReadUInt16();
                if ((packed & 32768) != 0) record.Rotation[1] = (packed & 32767) * .5f;
                else { packed = (packed << 16) | r.ReadUInt16(); record.Rotation = new[] { (packed & 1023) * .5f, ((packed >> 10) & 1023) * .5f, ((packed >> 20) & 1023) * .5f }; }
            }
            if ((record.Flags & 1) != 0) { record.ConnectionType = r.ReadByte(); record.ConnectionHash = r.ReadInt32(); }
            for (int type = 0; type < 7; type++) if ((record.Flags & (2 << type)) != 0)
            {
                int count = Count(r);
                for (int i = 0; i < count; i++)
                {
                    var p = new PropertyRecord { Key = r.ReadInt32(), Type = Types[type] }; p.Name = key(p.Key);
                    switch (type)
                    {
                        case 0: p.Value = r.ReadSingle(); break;
                        case 1: p.Value = Vector(r); break;
                        case 2: p.Value = Vector(r, 4); break;
                        case 3: p.Value = r.ReadInt32(); break;
                        case 4: p.Value = r.ReadInt64(); break;
                        case 5: p.Value = r.ReadString(); break;
                        case 6: p.Value = Bytes(r); break;
                    }
                    record.Properties.Add(p);
                }
            }
            return record;
        }
        internal static int Hash(string value)
        {
            unchecked
            {
                int a = 5381, b = 5381;
                for (int i = 0; i < value.Length; i += 2) { a = a * 33 ^ value[i]; if (i + 1 < value.Length) b = b * 33 ^ value[i + 1]; }
                return a + b * 1566083941;
            }
        }
    }
}
