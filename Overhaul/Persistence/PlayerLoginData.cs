using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Overhaul.Persistence
{
    internal static class PlayerLoginData
    {
        internal static PlayerChange[] Fresh(string name, float health, float stamina)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || health <= 0 || stamina <= 0)
                throw new InvalidDataException("Invalid initial character defaults");
            var bytes = new byte[8]; using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            long id = BitConverter.ToInt64(bytes, 0) & long.MaxValue; if (id == 0) id = 1;
            var rows = new List<PlayerChange>();
            void Number(string key, float value) => rows.Add(new PlayerChange("state", false, key, null, value, null, null));
            Number("health", health); Number("max_health", health); Number("max_stamina", stamina); Number("stamina", stamina);
            Number("time_since_death", 999999); Number("guardian_cooldown", 0); Number("max_eitr", 0); Number("eitr", 0);
            rows.Add(new PlayerChange("state", false, "guardian_power", null, null, "", null));
            rows.Add(new PlayerChange("state", false, "build_ui", null, null, null, new byte[0]));
            rows.Add(new PlayerChange("state", false, "player_id", id, null, null, null));
            rows.Add(new PlayerChange("state", false, "player_name", null, null, name, null));
            rows.Add(new PlayerChange("state", false, "first_spawn", true, null, null, null));
            rows.Add(new PlayerChange("state", false, "created_at", DateTime.UtcNow.Ticks, null, null, null));
            return rows.ToArray();
        }

        // Import is available only during an authorized first admission. No gameplay RPC uses this format.
        internal static byte[] PackImport(byte[] native, IEnumerable<PlayerChange> profile)
        {
            if (native == null || native.Length == 0) return null;
            byte[] metadata = PlayerBatchFormat.Encode(new PlayerBatch(Guid.Empty.ToString("N"), 0, profile));
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(1); writer.Write(native.Length); writer.Write(native); writer.Write(metadata.Length); writer.Write(metadata);
                writer.Flush();
                if (stream.Length > PlayerAdmissionChannel.Limit) throw new InvalidDataException("Character import exceeds limit");
                return stream.ToArray();
            }
        }
        internal static PlayerChange[] UnpackImport(byte[] bytes)
        {
            if (bytes == null || bytes.Length > PlayerAdmissionChannel.Limit) throw new InvalidDataException("Invalid character import size");
            using (var reader = NativeFormat.Reader(bytes))
            {
                if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported character import");
                byte[] Read()
                {
                    int size = reader.ReadInt32();
                    if (size < 1 || size > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Truncated character import");
                    return reader.ReadBytes(size);
                }
                var character = PlayerNativeFormat.Decode(Read());
                var profile = PlayerBatchFormat.Decode(Read()); NativeFormat.End(reader);
                if (profile.ExpectedRevision != 0 || profile.Operation != Guid.Empty.ToString("N") || profile.Changes.Any(r => r.Delete))
                    throw new InvalidDataException("Invalid initial character profile");
                var rows = character.Rows.Concat(profile.Changes).ToArray();
                var keys = new HashSet<string>();
                foreach (var row in rows)
                {
                    var table = PlayerDatabase.Tables.Single(t => t.Name == row.Table);
                    string key = row.Table;
                    foreach (var value in row.Values.Take(table.Keys)) { string text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture); key += ":" + text.Length + ":" + text; }
                    if (!keys.Add(key)) throw new InvalidDataException("Duplicate imported character row");
                }
                var id = rows.SingleOrDefault(r => r.Table == "state" && (string)r.Values[0] == "player_id");
                if (id == null || Convert.ToInt64(id.Values[1]) == 0) throw new InvalidDataException("Missing imported character identity");
                return rows;
            }
        }
    }
}
