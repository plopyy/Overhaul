using HarmonyLib;

namespace Overhaul.Storage
{
    [HarmonyPatch(typeof(ArmorStand), "UseItem")]
    internal static class ArmorStandHammer
    {
        // Return unhandled so Humanoid.UseItem continues to native equipment use.
        // Reject before access checks too: equipping your own tool needs no ward access.
        private static bool Prefix(ItemDrop.ItemData item, ref bool __result)
        {
            if (item?.m_shared?.m_name != "$item_hammer" &&
                !(item?.m_dropPrefab && item.m_dropPrefab.name == "Hammer")) return true;
            __result = false;
            return false;
        }
    }
}
