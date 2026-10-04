using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EquipmentAndQuickSlots;

namespace Overhaul.Persistence
{
    internal static class InventoryMovePresentation
    {
        // Resolve every prefab and validate both result sets before touching either live inventory.
        internal static Action Stage(Inventory playerInventory, Inventory container, InventoryMoveReply reply, Player player = null)
        {
            if (reply.Notification)
            {
                var update = Prepare(container, reply.Container, false);
                return () => { container.m_inventory.Clear(); container.m_inventory.AddRange(update); container.Changed(); };
            }
            var progression = PlayerCraftProgressGame.Presentation(reply.Player.Changes.Where(r => r.Table == "knowledge" || r.Table == "skills"),player);
            var food = PlayerFoodGame.Presentation(reply.Player.Changes.Where(r => r.Table == "food"),player);
            var potions = PlayerPotionGame.Presentation(reply.Player.Changes.Where(r => r.Table == "effects"),player);
            var guardian = PlayerStandGame.PowerPresentation(reply.Player.Changes.Where(r => r.Table == "state" && (string)r.Values[0]!=PlayerBuildGame.Debt && !PlayerResources.IsKey((string)r.Values[0])),player);
            var resources = PlayerResourceGame.Presentation(reply.Player.Changes.Where(r=>r.Table=="state" && PlayerResources.IsKey((string)r.Values[0])),player);
            var building = PlayerBuildGame.Presentation(reply.Player.Changes.Where(r => r.Table == "state" && (string)r.Values[0]==PlayerBuildGame.Debt),player);
            bool resize = reply.Player.Changes.Any(r => r.Table == "knowledge" && !r.Delete && (string)r.Values[0] == "uniques" &&
                ((string)r.Values[1]).StartsWith(Player.InventoryRowsKey+" ",StringComparison.OrdinalIgnoreCase));
            int rows = resize ? InventoryMoveGame.PlayerRows(reply.Player.Changes) : 0;
            int height = resize ? rows + Slots.ExtraRows + Slots.HiddenRows : playerInventory.m_height;
            var bag = PrepareSized(playerInventory, new PlayerBatch(reply.Player.Operation,reply.Player.ExpectedRevision,
                reply.Player.Changes.Where(r => r.Table != "knowledge" && r.Table != "skills" && r.Table != "food" && r.Table != "effects" && r.Table != "state")), reply.Snapshot,null,height);
            var chest = reply.ContainerAllowed ? Prepare(container, reply.Container, reply.Snapshot, reply.PreserveContainerSlots) : null;
            return () => PlayerEquipmentGame.Present(() =>
            {
                if (resize) { Slots.SetBaseRowsFromServer(rows); playerInventory.m_height = height; }
                var preserved = new HashSet<ItemDrop.ItemData>();
                if (player)
                {
                    foreach (var item in bag.Where(i => i.m_equipped).ToArray())
                    {
                        var existing = playerInventory.m_inventory.FirstOrDefault(old => !preserved.Contains(old) &&
                            player.IsItemEquiped(old) && old.m_dropPrefab == item.m_dropPrefab && old.m_quality == item.m_quality &&
                            old.m_variant == item.m_variant && old.m_crafterID == item.m_crafterID &&
                            old.m_customData.Count == item.m_customData.Count && old.m_customData.All(p => item.m_customData.TryGetValue(p.Key, out var v) && v == p.Value));
                        if (existing == null) continue;
                        preserved.Add(existing); existing.m_gridPos = item.m_gridPos; existing.m_stack = item.m_stack; existing.m_durability = item.m_durability;
                        bag[bag.IndexOf(item)] = existing;
                    }
                    foreach (var old in playerInventory.m_inventory.Where(player.IsItemEquiped).Where(i => !preserved.Contains(i)).ToArray())
                    { player.RemoveEquipAction(old); player.UnequipItem(old, false); }
                }
                playerInventory.m_inventory.Clear(); playerInventory.m_inventory.AddRange(bag);
                if (chest != null) { container.m_inventory.Clear(); container.m_inventory.AddRange(chest); }
                progression();
                food();
                potions();
                guardian();
                building();
                resources();
                // Both contents are installed before callbacks can observe either side of the move.
                playerInventory.Changed(); if (chest != null) container.Changed();
                if (player)
                    foreach (var item in bag.Where(i => i.m_equipped && !player.IsItemEquiped(i)).ToArray()) player.EquipItem(item, false);
                if (resize && InventoryGui.instance && InventoryGui.instance.m_player) InventoryGui.instance.SetInventorySize(rows+Slots.ExtraRows);
            });
        }
        internal static List<ItemDrop.ItemData> Prepare(Inventory inventory, PlayerBatch data, bool snapshot, HashSet<int> preserve = null)
            => PrepareSized(inventory,data,snapshot,preserve,inventory == null ? 0 : inventory.m_height);
        private static List<ItemDrop.ItemData> PrepareSized(Inventory inventory, PlayerBatch data, bool snapshot, HashSet<int> preserve,int height)
        {
            if (inventory == null || data == null) throw new InvalidDataException("Inventory view is unavailable");
            var items = snapshot ? new Dictionary<int, ItemDrop.ItemData>() : inventory.m_inventory.ToDictionary(i => i.m_gridPos.y * 256 + i.m_gridPos.x);
            if (snapshot && preserve != null)
                foreach (var item in inventory.m_inventory) { int key = item.m_gridPos.y * 256 + item.m_gridPos.x; if (preserve.Contains(key)) items.Add(key, item); }
            var changed = new HashSet<int>();
            foreach (var row in data.Changes)
            {
                if (row.Table != "inventory" && row.Table != "item_data" || snapshot && row.Delete) throw new InvalidDataException("Invalid inventory effect table");
                var values = row.Values; int x = Convert.ToInt32(values[1]), y = Convert.ToInt32(values[2]), key = y * 256 + x;
                if ((string)values[0] != "main" || x < 0 || x >= inventory.m_width || y < 0 || y >= height)
                    throw new InvalidDataException("Inventory effect is outside the layout");
                if (snapshot && preserve?.Contains(key) == true) continue;
                if (row.Table == "inventory")
                {
                    if (row.Delete) { items.Remove(key); changed.Add(key); }
                    else { var item = PlayerInventoryView.ReadItem(values, null, true); item.m_customData.Clear(); items[key] = item; changed.Add(key); }
                }
                else
                {
                    if (!items.TryGetValue(key, out var item)) throw new InvalidDataException("Inventory effect has orphan custom data");
                    if (!changed.Contains(key)) { item = item.Clone(); items[key] = item; changed.Add(key); }
                    if (row.Delete) item.m_customData.Remove((string)values[3]); else item.m_customData[(string)values[3]] = (string)values[4];
                }
            }
            return items.OrderBy(p => p.Key).Select(p => p.Value).ToList();
        }
    }
}
