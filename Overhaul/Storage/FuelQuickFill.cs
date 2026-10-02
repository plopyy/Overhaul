using System;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Storage
{
    internal static class FuelQuickFill
    {
        private static bool filling;
        internal static bool CanBegin(ZNetView view, Humanoid user) => !filling && user == Player.m_localPlayer &&
            view && view.IsValid() && SmelterQuickFill.ShiftHeld();
        internal static int Space(float capacity, float fuel) => Math.Max(0, Mathf.FloorToInt(capacity - fuel));
        internal static void Repeat(int remaining, Func<bool> add)
        {
            if (filling || remaining <= 0) return;
            filling = true;
            try
            {
                // Fixed budget from before the first RPC, even when the owner is remote.
                for (int i = 0; i < remaining; i++) if (!add()) break;
            }
            finally { filling = false; }
        }
    }

    [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Interact))]
    internal static class FireplaceQuickFillPatch
    {
        private static void Prefix(Fireplace __instance, Humanoid user, bool hold, ref bool alt, out int __state)
        {
            __state = 0;
            if (hold || !__instance.m_canRefill || __instance.m_infiniteFuel || !__instance.m_fuelItem || !FuelQuickFill.CanBegin(__instance.m_nview, user)) return;
            // On switchable lights, Shift fills instead of toggling the light off.
            alt = true;
            __state = FuelQuickFill.Space(__instance.m_maxFuel, __instance.m_nview.GetZDO().GetFloat(ZDOVars.s_fuel, 0));
        }
        private static void Postfix(Fireplace __instance, Humanoid user, bool __result, int __state)
        { if (__result) FuelQuickFill.Repeat(__state - 1, () => __instance.Interact(user, false, true)); }
    }
    [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.GetHoverText))]
    internal static class FireplaceQuickFillHoverPatch
    {
        private static void Postfix(Fireplace __instance, ref string __result)
        { if (__instance.m_canRefill && !__instance.m_infiniteFuel && __instance.m_fuelItem) __result += SmelterQuickFill.HoverHint(); }
    }

    [HarmonyPatch(typeof(CookingStation), "Awake")]
    internal static class CookingFuelCapacityPatch
    {
        private static void Prefix(CookingStation __instance)
        { if (__instance.m_useFuel && __instance.m_maxFuel > 0) __instance.m_maxFuel = Math.Max(100, __instance.m_maxFuel); }
    }
    [HarmonyPatch(typeof(CookingStation), "OnAddFuelSwitch")]
    internal static class CookingFuelQuickFillPatch
    {
        private static void Prefix(CookingStation __instance, Humanoid user, out int __state) => __state =
            FuelQuickFill.CanBegin(__instance.m_nview, user) ? FuelQuickFill.Space(__instance.m_maxFuel, __instance.GetFuel()) : 0;
        private static void Postfix(CookingStation __instance, Switch sw, Humanoid user, ItemDrop.ItemData item, bool __result, int __state)
        { if (__result) FuelQuickFill.Repeat(__state - 1, () => __instance.OnAddFuelSwitch(sw, user, item)); }
    }
    [HarmonyPatch(typeof(CookingStation), "OnHoverFuelSwitch")]
    internal static class CookingFuelQuickFillHoverPatch
    {
        private static void Postfix(ref string __result) => __result += SmelterQuickFill.HoverHint();
    }

    [HarmonyPatch(typeof(CookingStation), "OnInteract")]
    internal static class CookingInputQuickFillPatch
    {
        private static void Prefix(CookingStation __instance, Humanoid user, out int __state)
        {
            __state = 0;
            if (!FuelQuickFill.CanBegin(__instance.m_nview, user) || __instance.HaveDoneItem()) return;
            for (int i = 0; i < __instance.m_slots.Length; i++)
                if (__instance.m_nview.GetZDO().GetString("slot" + i, "") == "") __state++;
        }
        private static void Postfix(CookingStation __instance, Humanoid user, bool __result, int __state)
        {
            if (__result) FuelQuickFill.Repeat(__state - 1, () => !__instance.HaveDoneItem() &&
                __instance.FindCookableItem(user.GetInventory()) != null && __instance.OnInteract(user));
        }
    }
    [HarmonyPatch(typeof(CookingStation), "HoverText")]
    internal static class CookingInputQuickFillHoverPatch
    {
        private static void Postfix(CookingStation __instance, ref string __result)
        { if (!__instance.HaveDoneItem()) __result += SmelterQuickFill.HoverHint(); }
    }

    [HarmonyPatch(typeof(ShieldGenerator), "OnAddFuel")]
    internal static class ShieldFuelQuickFillPatch
    {
        private static void Prefix(ShieldGenerator __instance, Humanoid user, out int __state) => __state =
            FuelQuickFill.CanBegin(__instance.m_nview, user) ? FuelQuickFill.Space(__instance.m_maxFuel, __instance.GetFuel()) : 0;
        private static void Postfix(ShieldGenerator __instance, Switch sw, Humanoid user, ItemDrop.ItemData item, bool __result, int __state)
        { if (__result) FuelQuickFill.Repeat(__state - 1, () =>
            (item == null || user.GetInventory().HaveItem(item.m_shared.m_name, true)) && __instance.OnAddFuel(sw, user, item)); }
    }
    [HarmonyPatch(typeof(ShieldGenerator), "OnHoverAddFuel")]
    internal static class ShieldFuelQuickFillHoverPatch
    {
        private static void Postfix(ref string __result) => __result += SmelterQuickFill.HoverHint();
    }
}
