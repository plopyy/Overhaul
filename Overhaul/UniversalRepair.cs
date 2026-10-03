using HarmonyLib;

namespace Overhaul
{
    // Both crafting and repair use this native usability gate.
    [HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.CheckUsable))]
    internal static class OpenAirCrafting
    {
        private static void Prefix(CraftingStation __instance) => __instance.m_craftRequireRoof = false;
    }

    [HarmonyPatch(typeof(InventoryGui), "CanRepair")]
    internal static class UniversalRepair
    {
        private static bool Prefix(ItemDrop.ItemData item, ref bool __result)
        {
            Player player = Player.m_localPlayer;
            __result = player && item?.m_shared != null && item.m_shared.m_canBeReparied &&
                (player.GetCurrentCraftingStation() || player.NoCostCheat());
            // RepairOneItem and HaveRepairableItems retain the station usability check,
            // native repair effects, skill gain and restoration to GetMaxDurability().
            return false;
        }
    }
}
