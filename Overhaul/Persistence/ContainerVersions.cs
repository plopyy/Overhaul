using System;
using System.Collections.Generic;
using System.Linq;

namespace Overhaul.Persistence
{
    internal static class ContainerVersions
    {
        internal static long Revision(SqliteDatabase db, long objectId)
        { using (var row = db.Query("SELECT revision FROM container_revisions WHERE object_id=?", objectId)) return row.Read() ? row.Long(0) : 0; }
        internal static void Validate(SqliteDatabase db, long objectId, InventoryMoveAction request, IEnumerable<int> slots)
        {
            long revision = Revision(db, objectId);
            if (request.ContainerRevision > revision) throw new InvalidOperationException("Unknown container revision");
            foreach (int slot in slots)
                using (var row = db.Query("SELECT revision FROM container_slot_revisions WHERE object_id=? AND slot=?", objectId, slot))
                    if (request.SlotVersion(slot) > revision || row.Read() && row.Long(0) > request.SlotVersion(slot))
                        throw new InvalidOperationException("Container slot changed");
        }
        internal static long Advance(SqliteDatabase db, long objectId, IEnumerable<int> changed)
        {
            long revision = checked(Revision(db, objectId) + 1);
            new DatabaseRow("container_revisions", "object_id,revision", 1, objectId, revision).Write(db);
            foreach (int key in changed.Distinct()) new DatabaseRow("container_slot_revisions", "object_id,slot,revision", 2, objectId, key, revision).Write(db);
            return revision;
        }
        internal static Dictionary<int,string> Fingerprints(SqliteDatabase db, long objectId)
        {
            using (var found = db.Query("SELECT 1 FROM containers WHERE object_id=? AND version=109 AND decode_status='complete'", objectId))
                if (!found.Read()) return new Dictionary<int,string>();
            return WorldInventorySlots.ReadAll(db, objectId).GroupBy(r => Convert.ToInt32(r.Values[2]) * 256 + Convert.ToInt32(r.Values[1]))
                .ToDictionary(g => g.Key, g => new PlayerBatch("00000000000000000000000000000000", 0,
                    g.OrderBy(r => r.Table, StringComparer.Ordinal).ThenBy(r => r.Table == "item_data" ? (string)r.Values[3] : "", StringComparer.Ordinal)).Digest());
        }
        internal static void RecordChanges(SqliteDatabase db, long objectId, Dictionary<int,string> before)
        {
            var after = Fingerprints(db, objectId);
            var changed = before.Keys.Union(after.Keys).Where(k => !before.TryGetValue(k, out string a) || !after.TryGetValue(k, out string b) || a != b).ToArray();
            if (changed.Length != 0) Advance(db, objectId, changed);
        }
        internal static int[] Slots(PlayerBatch batch) => batch.Changes.Select(r => Convert.ToInt32(r.Values[2]) * 256 + Convert.ToInt32(r.Values[1])).Distinct().ToArray();
    }
}
