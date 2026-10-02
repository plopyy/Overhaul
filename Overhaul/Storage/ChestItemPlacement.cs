using HarmonyLib;

namespace Overhaul.Storage
{
    [HarmonyPatch(typeof(Inventory), "TopFirst")]
    internal static class ChestItemPlacement
    {
        private static void Postfix(Inventory __instance, ref bool __result)
        {
            var gui = InventoryGui.instance;
            if (gui && gui.m_currentContainer &&
                __instance == gui.m_currentContainer.GetInventory())
                __result = true;
        }
    }
}
