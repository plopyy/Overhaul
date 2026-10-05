using System.Collections.Generic;
using System.Linq;
using EquipmentAndQuickSlots;

namespace Overhaul.Storage
{
    internal static class DeviceEquipmentSwap
    {
        internal static bool Exchange(DeviceStore store, Player player)
        {
            var armor = (ArmorInventory)store.Adapter;
            var bag = player.GetInventory();
            var worn = bag.GetEquippedItems().ToArray();
            // Only the top (vanilla) utility item takes part; extra utility cells keep their accessory.
            // An incoming accessory is then equipped through vanilla's branch, back into that top cell.
            var exchangeable = worn.Where(item => item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Utility || item == player.m_utilityItem).ToArray();
            var outgoing = new ItemDrop.ItemData[7];
            var incoming = new ItemDrop.ItemData[7];
            for (int i = 0; i < 7; i++)
            {
                var cell = new Vector2i(i, 0);
                outgoing[i] = exchangeable.FirstOrDefault(item => item.m_stack == 1 && !outgoing.Contains(item) && armor.Accepts(item, false, cell));
                incoming[i] = store.Items.GetItemAt(i, 0);
            }

            // Move the existing objects, retaining durability, quality and custom data. Delay
            // inventory notifications until both sides and the native equipment state agree.
            var previousBag = bag.GetAllItems().ToArray();
            var previousStand = store.Items.GetAllItems().ToArray();
            var positions = previousBag.Concat(previousStand).ToDictionary(item => item, item => item.m_gridPos);
            bool complete = false;
            store.Change(() =>
            {
                try
                {
                    foreach (var item in outgoing.Where(item => item != null))
                    {
                        player.RemoveEquipAction(item);
                        player.UnequipItem(item, false);
                        bag.GetAllItems().Remove(item);
                    }
                    for (int i = 0; i < 7; i++)
                    {
                        var item = incoming[i];
                        if (item != null)
                        {
                            var cell = PlayerCell(bag, item, outgoing[i], positions);
                            if (cell.x < 0) return;
                            store.Items.GetAllItems().Remove(item);
                            item.m_gridPos = cell;
                            item.m_equipped = false;
                            bag.GetAllItems().Add(item);
                        }
                        if (outgoing[i] != null)
                        {
                            outgoing[i].m_gridPos = new Vector2i(i, 0);
                            store.Items.GetAllItems().Add(outgoing[i]);
                        }
                    }
                    Slots.cachedItems.Clear();
                    foreach (var item in incoming.Where(item => item != null))
                        if (!player.EquipItem(item, false)) return;

                    // Native hand rules can unequip a shield when equipping a two-handed weapon.
                    // An incompatible set must not turn an exchange into a partial withdrawal.
                    if (incoming.Any(item => item != null && !player.IsItemEquiped(item)) ||
                        worn.Any(item => !outgoing.Contains(item) && !player.IsItemEquiped(item))) return;
                    complete = true;
                }
                finally
                {
                    if (!complete)
                    {
                        foreach (var item in bag.GetEquippedItems().ToArray()) player.UnequipItem(item, false);
                        bag.GetAllItems().Clear(); bag.GetAllItems().AddRange(previousBag);
                        store.Items.GetAllItems().Clear(); store.Items.GetAllItems().AddRange(previousStand);
                        foreach (var pair in positions) pair.Key.m_gridPos = pair.Value;
                        Slots.cachedItems.Clear();
                        foreach (var item in worn) player.EquipItem(item, false);
                    }
                    bag.Changed();
                    store.Items.Changed();
                }
            });
            return complete;
        }

        private static Vector2i PlayerCell(Inventory bag, ItemDrop.ItemData item, ItemDrop.ItemData outgoing,
            Dictionary<ItemDrop.ItemData, Vector2i> positions)
        {
            // Prefer the matching EQS equipment cell, even with a full backpack. An occupied
            // cosmetic/quick slot is never overwritten. Native FindEmptySlot honors EQS limits.
            foreach (var slot in Slots.slots)
                if (slot != null && slot.IsEquipmentSlot && !Slots.IsExtraUtilitySlot(slot) && slot.ItemFits(item) &&
                    slot.GridPosition.y < bag.GetHeight() && slot.GridPosition.x >= 0 &&
                    bag.GetItemAt(slot.GridPosition.x, slot.GridPosition.y) == null)
                    return slot.GridPosition;
            if (outgoing != null)
            {
                var cell = positions[outgoing];
                if (bag.GetItemAt(cell.x, cell.y) == null) return cell;
            }
            return bag.FindEmptySlot(true);
        }
    }
}
