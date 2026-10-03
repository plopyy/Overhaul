using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Overhaul.Persistence
{
    // Durable journal format, not an RPC granting clients arbitrary row updates.
    internal static class PlayerBatchFormat
    {
        private const int Limit = 16 * 1024 * 1024;
        internal static byte[] Encode(PlayerBatch batch)
        {
            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                w.Write(1); w.Write(batch.Operation); w.Write(batch.ExpectedRevision);
                var changes = batch.Changes.ToArray(); w.Write(changes.Length);
                foreach (var row in changes)
                {
                    w.Write(row.Table); w.Write(row.Delete);
                    foreach (var value in row.Values)
                    {
                        if (value == null) w.Write((byte)0);
                        else if (value is byte[] bytes) { w.Write((byte)1); w.Write(bytes.Length); w.Write(bytes); }
                        else if (value is string text) { w.Write((byte)2); w.Write(text); }
                        else if (value is float || value is double) { w.Write((byte)3); w.Write(Convert.ToDouble(value)); }
                        else { w.Write((byte)4); w.Write(Convert.ToInt64(value)); }
                    }
                    if (stream.Length > Limit) throw new InvalidDataException("Player journal batch exceeds limit");
                }
                w.Flush(); return stream.ToArray();
            }
        }
        internal static PlayerBatch Decode(byte[] payload)
        {
            if (payload == null || payload.Length > Limit) throw new InvalidDataException("Invalid player journal payload");
            using (var r = NativeFormat.Reader(payload))
            {
                if (r.ReadInt32() != 1) throw new InvalidDataException("Unsupported player journal version");
                string id = r.ReadString(); long revision = r.ReadInt64(); int count = r.ReadInt32();
                if (count < 0 || count > 65536) throw new InvalidDataException("Invalid journal row count");
                var changes = new PlayerChange[count];
                for (int i = 0; i < count; i++)
                {
                    string table = r.ReadString(); bool delete = r.ReadBoolean();
                    var definition = PlayerDatabase.Tables.SingleOrDefault(t => t.Name == table);
                    if (definition == null) throw new InvalidDataException("Unknown journal table");
                    var values = new object[delete ? definition.Keys : definition.Columns.Length];
                    for (int j = 0; j < values.Length; j++)
                        switch (r.ReadByte())
                        {
                            case 0: values[j] = null; break;
                            case 1: values[j] = NativeFormat.Bytes(r); break;
                            case 2: values[j] = r.ReadString(); break;
                            case 3: values[j] = r.ReadDouble(); break;
                            case 4: values[j] = r.ReadInt64(); break;
                            default: throw new InvalidDataException("Unknown journal value type");
                        }
                    changes[i] = new PlayerChange(table, delete, values);
                }
                NativeFormat.End(r); return new PlayerBatch(id, revision, changes);
            }
        }
    }
}
