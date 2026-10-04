using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Overhaul.Persistence
{
    // One-time import and login reconstruction only. Never called for periodic writes.
    // Unsupported native versions fail explicitly instead of silently dropping progression.
    internal static class PlayerNativeFormat
    {
        internal sealed class Character
        {
            internal readonly List<PlayerChange> Rows = new List<PlayerChange>();
            internal byte[] Appearance;
        }
        private static int Count(BinaryReader r, int maximum = 65536)
        { int n = r.ReadInt32(); if (n < 0 || n > maximum) throw new InvalidDataException("Invalid character collection length"); return n; }
        private static string Text(BinaryReader r)
        { string s = r.ReadString(); if (s.Length > 1024 * 1024) throw new InvalidDataException("Character string too long"); return s; }
        private static float Number(BinaryReader r)
        { float f = r.ReadSingle(); if (float.IsNaN(f) || float.IsInfinity(f)) throw new InvalidDataException("Invalid character number"); return f; }
        internal static PlayerChange[] DecodeInventory(byte[] bytes)
        {
            var rows = new List<PlayerChange>();
            using (var reader = NativeFormat.Reader(bytes)) { ReadInventory(reader,rows,"main"); NativeFormat.End(reader); }
            return rows.ToArray();
        }
        private static void State(Character c, string key, object integer = null, object real = null, object text = null, object blob = null)
            => c.Rows.Add(new PlayerChange("state", false, key, integer, real, text, blob));
        internal static Character Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("Invalid character package size");
            var result = new Character();
            using (var r = NativeFormat.Reader(bytes))
            {
                if (r.ReadInt32() != 33) throw new InvalidDataException("Unsupported native player format; expected 33");
                foreach (string key in new[] { "max_health", "health", "max_stamina", "time_since_death" }) State(result, key, real: Number(r));
                State(result, "guardian_power", text: Text(r)); State(result, "guardian_cooldown", real: Number(r));
                ReadInventory(r, result.Rows, "main");
                foreach (string kind in new[] { "recipes", "stations", "materials", "tutorials", "uniques", "trophies", "biomes", "texts" })
                {
                    int count = Count(r);
                    for (int i = 0; i < count; i++)
                    {
                        string key = Text(r);
                        string value = kind == "stations" ? r.ReadInt32().ToString(System.Globalization.CultureInfo.InvariantCulture) : kind == "texts" ? Text(r) : "";
                        result.Rows.Add(new PlayerChange("knowledge", false, kind, key, value));
                    }
                }
                long start = r.BaseStream.Position;
                Text(r); Text(r); for (int i = 0; i < 6; i++) Number(r); r.ReadInt32();
                result.Appearance = bytes.Skip((int)start).Take((int)(r.BaseStream.Position - start)).ToArray();
                int food = Count(r, 32);
                for (int i = 0; i < food; i++) result.Rows.Add(new PlayerChange("food", false, i, Text(r), Number(r)));
                if (r.ReadInt32() != 2) throw new InvalidDataException("Unsupported skills version");
                int skills = Count(r, 1024);
                for (int i = 0; i < skills; i++) result.Rows.Add(new PlayerChange("skills", false, r.ReadInt32(), Number(r), Number(r)));
                int custom = Count(r);
                for (int i = 0; i < custom; i++) result.Rows.Add(new PlayerChange("custom_data", false, Text(r), Text(r)));
                foreach (string key in new[] { "stamina", "max_eitr", "eitr" }) State(result, key, real: Number(r));
                State(result, "build_ui", blob: NativeFormat.Bytes(r));
                NativeFormat.End(r);
            }
            // Duplicate keys must not turn a corrupt import into a silently truncated character.
            var keys = new HashSet<string>();
            foreach (var row in result.Rows)
            {
                var definition = PlayerDatabase.Tables.Single(t => t.Name == row.Table);
                var key = new StringBuilder(row.Table);
                foreach (var value in row.Values.Take(definition.Keys))
                { string s = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture); key.Append('|').Append(s.Length).Append(':').Append(s); }
                if (!keys.Add(key.ToString())) throw new InvalidDataException("Duplicate character record");
            }
            return result;
        }
        private static void ReadInventory(BinaryReader r, List<PlayerChange> rows, string bag)
        {
            if (r.ReadInt32() != 109) throw new InvalidDataException("Unsupported inventory format; expected 109");
            int count = r.ReadUInt16();
            for (int i = 0; i < count; i++)
            {
                double durability = r.ReadInt32() / 100.0;
                int x = r.ReadByte(), y = r.ReadByte(), level = r.ReadByte(), flags = r.ReadByte();
                int quality = (flags & 4) != 0 ? r.ReadUInt16() : 1;
                int stack = (flags & 8) != 0 ? r.ReadUInt16() : 1;
                int variant = (flags & 16) != 0 ? r.ReadInt32() : 0;
                long crafter = (flags & 32) != 0 ? r.ReadInt64() : 0;
                string crafterName = (flags & 32) != 0 ? Text(r) : "";
                int prefab = (flags & 64) != 0 ? r.ReadInt32() : 0;
                int custom = (flags & 128) != 0 ? NativeFormat.Count(r) : 0;
                var data = new List<PlayerChange>();
                for (int j = 0; j < custom; j++) data.Add(new PlayerChange("item_data", false, bag, x, y, Text(r), Text(r)));
                int extra = r.ReadByte();
                if ((extra & ~1) != 0) throw new InvalidDataException("Unsupported inventory flags");
                rows.Add(new PlayerChange("inventory", false, bag, x, y, prefab, stack, quality, durability,
                    (flags & 2) != 0, variant, crafter, crafterName, level, (flags & 1) != 0, (extra & 1) != 0));
                rows.AddRange(data);
            }
        }
        internal static byte[] Encode(IEnumerable<PlayerChange> rows, byte[] appearance)
        {
            var data = rows.Where(r => !r.Delete).GroupBy(r => r.Table).ToDictionary(g => g.Key, g => g.Select(r => r.Values).ToArray());
            Func<string, object[][]> table = name => data.TryGetValue(name, out var v) ? v : new object[0][];
            var state = table("state").ToDictionary(r => (string)r[0]);
            Func<string, float> number = name => Convert.ToSingle(state[name][2]);
            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                w.Write(33);
                foreach (string key in new[] { "max_health", "health", "max_stamina", "time_since_death" }) w.Write(number(key));
                w.Write((string)state["guardian_power"][3]); w.Write(number("guardian_cooldown"));
                WriteInventory(w, table("inventory"), table("item_data"), "main");
                foreach (string kind in new[] { "recipes", "stations", "materials", "tutorials", "uniques", "trophies", "biomes", "texts" })
                {
                    var list = table("knowledge").Where(r => (string)r[0] == kind).ToArray(); w.Write(list.Length);
                    foreach (var row in list)
                    {
                        w.Write((string)row[1]);
                        if (kind == "stations") w.Write(int.Parse((string)row[2], System.Globalization.CultureInfo.InvariantCulture));
                        else if (kind == "texts") w.Write((string)row[2]);
                    }
                }
                // Appearance belongs to the selected local character, never to imported inventory data.
                using (var ar = NativeFormat.Reader(appearance))
                { Text(ar); Text(ar); for (int i = 0; i < 6; i++) Number(ar); ar.ReadInt32(); NativeFormat.End(ar); }
                w.Write(appearance);
                var foods = table("food").OrderBy(r => Convert.ToInt32(r[0])).ToArray(); w.Write(foods.Length);
                foreach (var row in foods) { w.Write((string)row[1]); w.Write(Convert.ToSingle(row[2])); }
                var skills = table("skills"); w.Write(2); w.Write(skills.Length);
                foreach (var row in skills) { w.Write(Convert.ToInt32(row[0])); w.Write(Convert.ToSingle(row[1])); w.Write(Convert.ToSingle(row[2])); }
                var custom = table("custom_data"); w.Write(custom.Length);
                foreach (var row in custom) { w.Write((string)row[0]); w.Write((string)row[1]); }
                foreach (string key in new[] { "stamina", "max_eitr", "eitr" }) w.Write(number(key));
                var ui = (byte[])state["build_ui"][4]; w.Write(ui.Length); w.Write(ui);
                w.Flush(); return stream.ToArray();
            }
        }
        private static void WriteInventory(BinaryWriter w, object[][] items, object[][] custom, string bag)
        {
            var list = items.Where(r => (string)r[0] == bag).ToArray();
            w.Write(109); w.Write(checked((ushort)list.Length));
            foreach (var row in list)
            {
                int x = Convert.ToInt32(row[1]), y = Convert.ToInt32(row[2]);
                var values = custom.Where(r => (string)r[0] == bag && Convert.ToInt32(r[1]) == x && Convert.ToInt32(r[2]) == y).ToArray();
                int flags = (Convert.ToBoolean(row[12]) ? 1 : 0) | (Convert.ToBoolean(row[7]) ? 2 : 0) | 4 | 8 | 16 | 32 | 64;
                if (values.Length != 0) flags |= 128;
                w.Write(checked((int)Math.Round(Convert.ToDouble(row[6]) * 100)));
                w.Write(checked((byte)x)); w.Write(checked((byte)y)); w.Write(checked((byte)Convert.ToInt32(row[11]))); w.Write((byte)flags);
                w.Write(checked((ushort)Convert.ToInt32(row[5]))); w.Write(checked((ushort)Convert.ToInt32(row[4])));
                w.Write(Convert.ToInt32(row[8])); w.Write(Convert.ToInt64(row[9])); w.Write((string)row[10]); w.Write(Convert.ToInt32(row[3]));
                if (values.Length > 32767) throw new InvalidDataException("Too much item custom data");
                if (values.Length != 0)
                {
                    if (values.Length < 128) w.Write((byte)values.Length);
                    else { w.Write((byte)(128 | (values.Length >> 8))); w.Write((byte)(values.Length & 255)); }
                    foreach (var v in values) { w.Write((string)v[3]); w.Write((string)v[4]); }
                }
                w.Write((byte)(Convert.ToBoolean(row[13]) ? 1 : 0));
            }
        }
    }
}
