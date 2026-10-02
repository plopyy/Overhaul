using HarmonyLib;
using TMPro;
using UnityEngine;

namespace Overhaul.Leveling
{
    [HarmonyPatch(typeof(InventoryGui), "UpdateInventoryWeight")]
    internal static class UnburdenedWeightDisplay
    {
        internal static void Display(TMP_Text label, float weight)
        {
            // Avoid converting the unlimited carry capacity to an overflowing integer.
            // Plain text also removes the native red overload markup on the next update.
            label.text = Mathf.CeilToInt(weight).ToString() + "/\u221e";
        }

        private static bool Prefix(InventoryGui __instance, Player player)
        {
            if (!player || !__instance.m_weight || !LevelingEffects.Passive(player, "unburdened")) return true;
            Display(__instance.m_weight, player.GetInventory().GetTotalWeight());
            return false;
        }
    }
}
