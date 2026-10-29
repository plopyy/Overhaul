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

    // Universal repair works at any station, but the button only makes sense at stations that make
    // repairable gear: hide it at the preparation table, cauldron, mead ketill and the like.
    [HarmonyPatch(typeof(InventoryGui), "UpdateRepair")]
    internal static class RepairButtonStations
    {
        private static void Postfix(InventoryGui __instance)
        {
            var station = Player.m_localPlayer ? Player.m_localPlayer.GetCurrentCraftingStation() : null;
            var repairs = !station || AugaUnity.StationRecipes.OffersRepairs(station);
            // No repair button: the workbench window drops the notch cut out for it.
            if (station && AugaUnity.WorkbenchPanelController.instance)
                AugaUnity.WorkbenchPanelController.instance.SetRepairNotch(repairs);
            if (repairs) return;
            __instance.m_repairPanel.gameObject.SetActive(false);
            __instance.m_repairPanelSelection.gameObject.SetActive(false);
            __instance.m_repairButton.gameObject.SetActive(false);
        }
    }
}
