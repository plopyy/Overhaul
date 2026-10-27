using HarmonyLib;

namespace Overhaul.Rarity
{
    // Upgrading an item at a station: the game removes it and adds a brand new one, without its custom data (rarity,
    // enchantment lines, mythic, socketed catalysts, staff spells...). The new item gets the old one's data back.
    [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
    internal static class UpgradeKeepsData
    {
        private sealed class State
        {
            public ItemDrop.ItemData Old;
            public Vector2i Pos;
            public System.Collections.Generic.Dictionary<string, string> Data;
        }

        private static void Prefix(InventoryGui __instance, out State __state)
        {
            var old = __instance.m_craftUpgradeItem;
            __state = old?.m_customData != null && old.m_customData.Count > 0
                ? new State { Old = old, Pos = old.m_gridPos, Data = new System.Collections.Generic.Dictionary<string, string>(old.m_customData) }
                : null;
        }

        private static void Postfix(Player player, State __state)
        {
            if (__state == null || !player) return;
            var inventory = player.GetInventory();
            if (inventory.ContainsItem(__state.Old)) return; // not crafted (missing resources, full inventory...)
            var item = inventory.GetItemAt(__state.Pos.x, __state.Pos.y);
            if (item == null || item == __state.Old || item.m_shared.m_name != __state.Old.m_shared.m_name) return; // broken by an upgrader
            foreach (var pair in __state.Data) item.m_customData[pair.Key] = pair.Value;
            Effects.Effects.Invalidate();
        }
    }
}
