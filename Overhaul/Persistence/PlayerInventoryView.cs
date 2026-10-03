using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Overhaul.Persistence
{
    // Main-thread application of a confirmed server move. Build the result before changing
    // the live inventory, so a missing prefab or malformed response cannot apply half a move.
    internal sealed class PlayerInventoryView
    {
        private readonly Inventory inventory;
        private PlayerInventoryMove pending;
        internal long Revision { get; private set; }
        internal bool Busy => pending != null;
        internal string PendingOperation => pending?.Operation;

        internal PlayerInventoryView(Inventory inventory, long revision)
        {
            this.inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
            Revision = revision;
        }

        internal PlayerInventoryMove Begin(int fromX, int fromY, int toX, int toY, int amount)
        {
            if (Busy) throw new InvalidOperationException("An inventory action is already pending");
            pending = new PlayerInventoryMove(Guid.NewGuid().ToString("N"), Revision, fromX, fromY, toX, toY, amount);
            return pending;
        }

        // The transport must verify that this response came from the admitted server/session.
        internal bool Confirm(PlayerBatch effect)
        {
            if (pending == null || effect.Operation != pending.Operation || effect.ExpectedRevision != Revision) return false;
            long next = checked(Revision + 1);
            var items = inventory.m_inventory.ToDictionary(i => i.m_gridPos.y * 256 + i.m_gridPos.x);
            var changed = new HashSet<int>();
            foreach (var row in effect.Changes)
            {
                if (row.Table != "inventory" && row.Table != "item_data") throw new InvalidDataException("Unexpected inventory response table");
                var values = row.Values;
                int x = Convert.ToInt32(values[1]), y = Convert.ToInt32(values[2]);
                if ((string)values[0] != "main" || x < 0 || y < 0 || x >= inventory.m_width || y >= inventory.m_height ||
                    !((x == pending.FromX && y == pending.FromY) || (x == pending.ToX && y == pending.ToY)))
                    throw new InvalidDataException("Unexpected inventory response slot");
                int key = y * 256 + x;
                if (row.Table == "inventory")
                {
                    if (items.TryGetValue(key, out var previous) && previous.m_equipped)
                        throw new InvalidDataException("Move response would replace equipped item");
                    if (row.Delete) { items.Remove(key); changed.Add(key); continue; }
                    var item = ReadItem(values, previous, false);
                    items[key] = item; changed.Add(key);
                }
                else
                {
                    if (!items.TryGetValue(key, out var item)) throw new InvalidDataException("Item data has no destination item");
                    if (!changed.Contains(key)) { item = item.Clone(); items[key] = item; changed.Add(key); }
                    if (row.Delete) item.m_customData.Remove((string)values[3]);
                    else item.m_customData[(string)values[3]] = (string)values[4];
                }
            }
            inventory.m_inventory.Clear();
            inventory.m_inventory.AddRange(items.Values);
            Revision = next; pending = null;
            inventory.Changed();
            return true;
        }

        private static ItemDrop.ItemData ReadItem(object[] values, ItemDrop.ItemData previous, bool allowEquipped)
        {
            int prefabId = Convert.ToInt32(values[3]);
            var prefab = ObjectDB.instance.GetItemPrefab(prefabId);
            var template = prefab ? prefab.GetComponent<ItemDrop>() : null;
            if (!template) throw new InvalidDataException("Server inventory prefab is unavailable");
            int count = Convert.ToInt32(values[4]), quality = Convert.ToInt32(values[5]);
            float durability = Convert.ToSingle(values[6]);
            if (count <= 0 || count > template.m_itemData.m_shared.m_maxStackSize || quality < 1 ||
                durability < 0 || float.IsNaN(durability) || float.IsInfinity(durability) || (!allowEquipped && Convert.ToBoolean(values[7])))
                throw new InvalidDataException("Invalid server item values");
            bool same = previous != null && previous.m_dropPrefab && previous.m_dropPrefab.name.GetStableHashCode() == prefabId;
            var item = (same ? previous : template.m_itemData).Clone();
            if (!same) item.m_customData.Clear();
            item.m_dropPrefab = prefab; item.m_gridPos = new Vector2i(Convert.ToInt32(values[1]), Convert.ToInt32(values[2]));
            item.m_stack = count; item.m_quality = quality; item.m_durability = durability; item.m_equipped = Convert.ToBoolean(values[7]);
            item.m_variant = Convert.ToInt32(values[8]); item.m_crafterID = Convert.ToInt64(values[9]);
            item.m_crafterName = (string)values[10]; item.m_worldLevel = Convert.ToInt32(values[11]);
            item.m_pickedUp = Convert.ToBoolean(values[12]); item.m_cheated = Convert.ToBoolean(values[13]);
            return item;
        }
        internal bool Resynchronize(PlayerBatch snapshot)
        {
            if (pending == null || snapshot.Operation != pending.Operation || snapshot.ExpectedRevision < Revision) return false;
            var rows = snapshot.Changes.ToArray();
            if (rows.Any(r => r.Delete || (r.Table != "inventory" && r.Table != "item_data")))
                throw new InvalidDataException("Invalid inventory synchronization rows");
            var old = inventory.m_inventory.ToDictionary(i => i.m_gridPos.y * 256 + i.m_gridPos.x);
            var items = new Dictionary<int, ItemDrop.ItemData>();
            foreach (var row in rows.Where(r => r.Table == "inventory"))
            {
                var v = row.Values; int x = Convert.ToInt32(v[1]), y = Convert.ToInt32(v[2]);
                if ((string)v[0] != "main" || x < 0 || y < 0 || x >= inventory.m_width || y >= inventory.m_height)
                    throw new InvalidDataException("Invalid synchronized inventory slot");
                int key = y * 256 + x; old.TryGetValue(key, out var previous);
                var item = ReadItem(v, previous, true); item.m_customData.Clear();
                if (item.m_equipped && (previous == null || !previous.m_equipped || previous.m_dropPrefab != item.m_dropPrefab))
                    throw new InvalidDataException("Equipment state requires character synchronization");
                items.Add(key, item);
            }
            foreach (var row in rows.Where(r => r.Table == "item_data"))
            {
                var v = row.Values; int x = Convert.ToInt32(v[1]), y = Convert.ToInt32(v[2]);
                if ((string)v[0] != "main" || x < 0 || y < 0 || x >= inventory.m_width || y >= inventory.m_height ||
                    !items.TryGetValue(y * 256 + x, out var item)) throw new InvalidDataException("Orphan synchronized item data");
                item.m_customData.Add((string)v[3], (string)v[4]);
            }
            foreach (var pair in old.Where(p => p.Value.m_equipped))
                if (!items.TryGetValue(pair.Key, out var replacement) || !replacement.m_equipped || replacement.m_dropPrefab != pair.Value.m_dropPrefab)
                    throw new InvalidDataException("Equipment state requires character synchronization");
            // Keep Humanoid's equipped-item references valid. All validation above precedes any mutation.
            foreach (var pair in old.Where(p => p.Value.m_equipped))
            {
                var from = items[pair.Key]; var to = pair.Value;
                to.m_stack = from.m_stack; to.m_quality = from.m_quality; to.m_durability = from.m_durability;
                to.m_variant = from.m_variant; to.m_crafterID = from.m_crafterID; to.m_crafterName = from.m_crafterName;
                to.m_worldLevel = from.m_worldLevel; to.m_pickedUp = from.m_pickedUp; to.m_cheated = from.m_cheated;
                to.m_customData = from.m_customData; items[pair.Key] = to;
            }
            inventory.m_inventory.Clear(); inventory.m_inventory.AddRange(items.Values);
            Revision = snapshot.ExpectedRevision; pending = null; inventory.Changed(); return true;
        }
    }
}
