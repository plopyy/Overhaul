using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Overhaul.Persistence
{
    // Runs exclusively on the world writer, with the container reserved by the action coordinator.
    // The normalized rows are authoritative for format109; ObjectSql.Read reconstructs native bytes.
    internal static class WorldInventorySlots
    {
        internal static PlayerChange[] Read(SqliteDatabase db, long objectId, int x, int y)
        {
            ValidateSlot(objectId, x, y);
            using (var container = db.Query("SELECT version,decode_status FROM containers WHERE object_id=?", objectId))
                if (!container.Read() || container.Long(0) != 109 || container.Text(1) != "complete")
                    throw new InvalidDataException("Container inventory is not decoded");
            var result = new List<PlayerChange>();
            long itemId;
            using (var row = db.Query("SELECT id,prefab_hash,stack,quality,durability,equipped,variant,crafter_id,crafter_name,world_level,picked_up,cheated FROM inventory WHERE object_id=? AND x=? AND y=?", objectId, x, y))
            {
                if (!row.Read()) return result.ToArray();
                itemId = row.Long(0);
                result.Add(new PlayerChange("inventory", false, "main", x, y, row.Value(1), row.Value(2), row.Value(3),
                    row.Value(4), row.Value(5), row.Value(6), row.Value(7), row.Value(8), row.Value(9), row.Value(10), row.Value(11)));
            }
            using (var custom = db.Query("SELECT key,value FROM item_data WHERE item_id=? ORDER BY key", itemId))
                while (custom.Read()) result.Add(new PlayerChange("item_data", false, "main", x, y, custom.Text(0), custom.Text(1)));
            return result.ToArray();
        }

        // Must be inside the same world transaction as the durable player-transfer intent.
        // Accepts a complete replacement for one occupied slot, or no rows to delete it.
        internal static void Write(SqliteDatabase db, long objectId, int x, int y, IEnumerable<PlayerChange> replacement)
        {
            ValidateSlot(objectId, x, y);
            var rows = replacement.ToArray();
            if (rows.Any(r => r.Delete || (r.Table != "inventory" && r.Table != "item_data") ||
                (string)r.Values[0] != "main" || Convert.ToInt32(r.Values[1]) != x || Convert.ToInt32(r.Values[2]) != y))
                throw new InvalidDataException("Invalid container slot replacement");
            using (var container = db.Query("SELECT version,decode_status FROM containers WHERE object_id=?", objectId))
                if (!container.Read() || container.Long(0) != 109 || container.Text(1) != "complete")
                    throw new InvalidDataException("Container inventory is not decoded");
            var item = rows.SingleOrDefault(r => r.Table == "inventory");
            var custom = rows.Where(r => r.Table == "item_data").Select(r => r.Values).ToArray();
            if (item == null && rows.Length != 0 || custom.Select(r => (string)r[3]).Distinct().Count() != custom.Length)
                throw new InvalidDataException("Invalid item custom data");
            long slot = checked(objectId * 65536 + x * 256 + y);
            // Existing imported IDs may differ; always locate the occupied slot by its unique position.
            using (var existing = db.Query("SELECT id FROM inventory WHERE object_id=? AND x=? AND y=?", objectId, x, y))
                if (existing.Read()) slot = existing.Long(0);
            if (item == null) db.Write("DELETE FROM inventory WHERE object_id=? AND x=? AND y=?", objectId, x, y);
            else
            {
                var v = item.Values;
                if (Convert.ToInt32(v[4]) <= 0 || Convert.ToInt32(v[5]) <= 0 || Convert.ToDouble(v[6]) < 0)
                    throw new InvalidDataException("Invalid container item values");
                int flags = (Convert.ToBoolean(v[12]) ? 1 : 0) | (Convert.ToBoolean(v[7]) ? 2 : 0) | 4 | 8 | 16 | 32 | 64 | (custom.Length > 0 ? 128 : 0);
                new DatabaseRow("inventory", "id,object_id,item_order,prefab_hash,prefab,stack,quality,durability,x,y,equipped,picked_up,variant,crafter_id,crafter_name,world_level,cheated,flags,extra_flags", 1,
                    slot, objectId, y * 256 + x, v[3], ObjectSql.PrefabName(Convert.ToInt32(v[3])), v[4], v[5], v[6], x, y,
                    v[7], v[12], v[8], v[9], v[10], v[11], v[13], flags, Convert.ToBoolean(v[13]) ? 1 : 0).Write(db);
                db.Write("DELETE FROM item_data WHERE item_id=?", slot);
                foreach (var entry in custom) db.Write("INSERT INTO item_data(item_id,key,value) VALUES(?,?,?)", slot, entry[3], entry[4]);
            }
            db.Write("UPDATE containers SET item_count=(SELECT count(*) FROM inventory WHERE object_id=?) WHERE object_id=?", objectId, objectId);
        }

        private static void ValidateSlot(long objectId, int x, int y)
        {
            if (objectId <= 0 || objectId > (long.MaxValue - 65535) / 65536 || x < 0 || y < 0 || x > 255 || y > 255)
                throw new ArgumentOutOfRangeException("Invalid world inventory slot");
        }
    }
}
