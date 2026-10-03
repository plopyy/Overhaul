using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Overhaul
{
    // Scale queries instead of shared prefab data: quality overrides, existing inventories
    // and repeated ObjectDB registration must all receive the same multiplier once.
    internal static class ItemBalance
    {
        internal static bool HasDoubleDurability(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null || !item.m_shared.m_useDurability) return false;
            switch (item.m_shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Bow:
                case ItemDrop.ItemData.ItemType.Tool:
                case ItemDrop.ItemData.ItemType.Torch:
                case ItemDrop.ItemData.ItemType.Shield:
                case ItemDrop.ItemData.ItemType.Helmet:
                case ItemDrop.ItemData.ItemType.Chest:
                case ItemDrop.ItemData.ItemType.Legs:
                case ItemDrop.ItemData.ItemType.Hands:
                case ItemDrop.ItemData.ItemType.Shoulder:
                    return true;
                default: return false;
            }
        }

        [HarmonyPatch]
        internal static class Weight
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(ItemDrop.ItemData), "GetWeight");
                yield return AccessTools.Method(typeof(ItemDrop.ItemData), "GetNonStackedWeight");
                yield return AccessTools.Method(typeof(Humanoid), "GetEquipmentWeight");
            }
            // EQS adds the extra equipped belts before this final multiplier.
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(ref float __result) => __result *= .5f;
        }

        [HarmonyPatch(typeof(ItemDrop.ItemData), "GetMaxDurability", new[] { typeof(int) })]
        internal static class Durability
        {
            private static void Postfix(ItemDrop.ItemData __instance, ref float __result)
            {
                if (HasDoubleDurability(__instance)) __result *= 2f;
            }
        }

        [HarmonyPatch]
        internal static class Megingjord
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(SE_Stats), "Setup");
                yield return AccessTools.Method(typeof(SE_Stats), "ModifyMaxCarryWeight");
                yield return AccessTools.Method(typeof(SE_Stats), "GetTooltipString");
            }
            private static void Prefix(SE_Stats __instance)
            {
                // The localization key also identifies cloned effects; no localized text lookup.
                if (__instance.m_name == "$item_beltstrength") __instance.m_addMaxCarryWeight = 400f;
            }
        }
    }
}
