using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Overhaul.Persistence
{
    // Compare only consumed slots: concurrent changes to other slots remain valid.
    internal sealed class PlayerContainerAction
    {
        internal readonly PlayerBatch Before, After;
        private readonly HashSet<int> slots;
        internal PlayerContainerAction(IEnumerable<PlayerChange> before, PlayerBatch after)
        {
            if (after == null || after.Changes.Any(r => r.Table != "inventory" && r.Table != "item_data"))
                throw new InvalidDataException("Invalid world inventory action");
            After = after; slots = new HashSet<int>(ContainerVersions.Slots(after));
            Before = Canonical(before.Where(r => slots.Contains(Key(r))));
        }
        private static int Key(PlayerChange row) => Convert.ToInt32(row.Values[2])*256+Convert.ToInt32(row.Values[1]);
        private static PlayerBatch Canonical(IEnumerable<PlayerChange> rows) => new PlayerBatch("00000000000000000000000000000000",0,
            rows.OrderBy(Key).ThenBy(r => r.Table,StringComparer.Ordinal).ThenBy(r => r.Table == "item_data" ? (string)r.Values[3] : "",StringComparer.Ordinal));
        internal void Validate(SqliteDatabase db,long id)
        {
            if (PlayerTransferJournal.IsLocked(db,id) ||
                Canonical(WorldInventorySlots.ReadAll(db,id).Where(r => slots.Contains(Key(r)))).Digest() != Before.Digest())
                throw new InvalidOperationException("Crafting resource slot changed");
        }
        internal PlayerBatch Apply(SqliteDatabase db,long id)
        {
            long revision = ContainerVersions.Revision(db,id);
            foreach (var group in After.Changes.GroupBy(Key))
                WorldInventorySlots.Write(db,id,group.Key%256,group.Key/256,group.Where(r => !r.Delete));
            ContainerVersions.Advance(db,id,slots);
            return new PlayerBatch(After.Operation,revision,After.Changes);
        }
    }
}
