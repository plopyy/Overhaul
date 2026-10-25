using System.Linq;
using Overhaul.Storage;
using UnityEngine;

namespace Overhaul.Rarity
{
    // The enchanting table's inventory (a device store saved on the table, opened like a chest): slot 0 holds the item to
    // enchant (gear of the rarity system), slot 1 the trophy. The second inventory shows the catalysts of the item in slot 0:
    // filled from the item when it is put down, written back into the item when a catalyst is socketed or taken out, so the
    // catalysts always travel with their item and are never dropped on their own.
    internal sealed class EnchantInventory : DeviceAdapter
    {
        internal readonly CraftingStation Station;
        private ItemDrop.ItemData tracked;
        internal EnchantInventory(CraftingStation station) { Station = station; }
        internal override bool Dual => true;
        internal override int Width => 2;
        internal override int Height => 1;
        internal override int FuelWidth => 2;
        internal override int FuelHeight => 1;
        internal override bool SingleItems => true;
        internal override bool SingleFuel => true;
        // Trophies stack in their slot: enchanting uses one at a time, so the next try is ready.
        internal override bool Single(ItemDrop.ItemData item, bool fuel) => fuel || item == null || EnchantOdds.Of(item) == null;
        internal override string Title => Station.m_name;
        internal override string MaterialLabel => "";

        internal ItemDrop.ItemData Item => Store?.Items.GetItemAt(0, 0);
        internal ItemDrop.ItemData Trophy => Store?.Items.GetItemAt(1, 0);

        internal static bool Gear(ItemDrop.ItemData item) => item != null && !ItemRarity.IsCatalyst(item, out _) && Enchantments.Biome(item) >= 0;
        internal int CatalystSlots => Item == null ? 0 : ItemRarity.Catalysts(Item).Length;

        internal override bool Accepts(ItemDrop.ItemData item, bool fuel, Vector2i? pos = null)
        {
            if (item == null) return false;
            if (fuel)
            {
                if (!ItemRarity.IsCatalyst(item, out _)) return false;
                // The same catalyst only once per item.
                string prefab = item.m_dropPrefab ? item.m_dropPrefab.name : Enchantments.Prefab(item);
                if (Store.Fuel.GetAllItems().Any(other => other != item && (other.m_dropPrefab ? other.m_dropPrefab.name : null) == prefab)) return false;
                return pos.HasValue ? pos.Value.y == 0 && pos.Value.x < CatalystSlots : Enumerable.Range(0, CatalystSlots).Any(x => Store.Fuel.GetItemAt(x, 0) == null);
            }
            bool gear = Gear(item), trophy = EnchantOdds.Of(item) != null;
            if (!pos.HasValue) return gear || trophy;
            return pos.Value.y == 0 && (pos.Value.x == 0 ? gear : pos.Value.x == 1 && trophy);
        }
        internal override void Import() { }

        // An item put down or taken: its catalysts are shown; the same item: the catalysts shown are written into it.
        internal override void Publish()
        {
            var item = Item;
            if (item != tracked) { tracked = item; ShowCatalysts(); return; }
            if (item != null) KeepCatalysts();
        }
        internal void ShowCatalysts()
        {
            Store.Fuel.RemoveAll();
            if (Item == null) return;
            var slots = ItemRarity.Catalysts(Item);
            for (int i = 0; i < slots.Length; i++)
                if (slots[i].Length > 0 && ObjectDB.instance.GetItemPrefab(slots[i])) DeviceStore.ImportItem(Store.Fuel, DeviceStore.Item(slots[i]), i);
        }
        private void KeepCatalysts()
        {
            int count = CatalystSlots;
            var names = Enumerable.Range(0, count).Select(i => Store.Fuel.GetItemAt(i, 0)?.m_dropPrefab?.name ?? "").ToArray();
            if (names.All(n => n.Length == 0)) Item.m_customData.Remove(ItemRarity.CatalystKey);
            else Item.m_customData[ItemRarity.CatalystKey] = string.Join(",", names);
        }
        // The catalysts belong to the item: never dropped with the table.
        internal override void BeforeDestroyed() => Store.Fuel.RemoveAll();
    }
}
