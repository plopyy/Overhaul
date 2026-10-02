using System;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Storage
{
    [HarmonyPatch(typeof(Smelter), "Awake")]
    internal static class CharcoalKilnWoods
    {
        internal static readonly string[] Woods = { "Wood", "FineWood", "RoundLog", "ElderBark", "YggdrasilWood", "Blackwood", "Frostwood" };

        private static void Prefix(Smelter __instance) => Configure(__instance, ObjectDB.instance);

        internal static void Configure(Smelter kiln, ObjectDB database)
        {
            if (!database || Utils.GetPrefabName(kiln.gameObject) != "charcoal_kiln") return;
            var coal = database.GetItemPrefab("Coal");
            if (!coal) return;
            // Own the list before extending it, preserving existing and modded recipes.
            kiln.m_conversion = new System.Collections.Generic.List<Smelter.ItemConversion>(kiln.m_conversion);
            foreach (var name in Woods)
            {
                if (kiln.m_conversion.Exists(c => c.m_from && c.m_from.gameObject.name == name)) continue;
                var wood = database.GetItemPrefab(name);
                if (wood) kiln.m_conversion.Add(new Smelter.ItemConversion {
                    m_from = wood.GetComponent<ItemDrop>(), m_to = coal.GetComponent<ItemDrop>() });
            }
            int normal = kiln.m_conversion.FindIndex(c => c.m_from && c.m_from.gameObject.name == "Wood");
            if (normal > 0)
            {
                var conversion = kiln.m_conversion[normal];
                kiln.m_conversion.RemoveAt(normal);
                kiln.m_conversion.Insert(0, conversion);
            }
        }
    }

    // Extend the normal interaction; native recipes, inventory removal, RPCs,
    // animation and effects remain responsible for each accepted item.
    internal static class SmelterQuickFill
    {
        private static bool filling;
        internal static string HoverHint() => Localization.instance.Localize(
            "\n[<color=yellow><b>" + Leveling.LevelingText.Get("overhaul_bulk_fill_shift") +
            " + $KEY_Use</b></color>] " + Leveling.LevelingText.Get("overhaul_bulk_fill_hint"));
        internal static bool ShiftHeld() => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        internal static int Available(Smelter smelter, bool fuel) => fuel
            ? Math.Max(0, Mathf.FloorToInt(smelter.m_maxFuel - smelter.GetFuel()))
            : Math.Max(0, smelter.m_maxOre - smelter.GetQueueSize());
        internal static int Begin(Smelter smelter, Humanoid user, bool fuel)
        {
            if (filling || user != Player.m_localPlayer || !ShiftHeld() || !smelter.m_nview || !smelter.m_nview.IsValid()) return 0;
            return Available(smelter, fuel);
        }
        internal static int FillMore(Smelter smelter, Switch sw, Humanoid user, ItemDrop.ItemData selected, bool fuel, int remaining)
        {
            if (filling || remaining <= 0) return 0;
            filling = true; int added = 0;
            try
            {
                // Bound the loop by capacity observed BEFORE the first RPC.
                // Remote ownership may leave the replicated count unchanged until the next network update.
                for (int i = 0; i < remaining; i++)
                {
                    if (Available(smelter, fuel) <= 0) break;
                    var inventory = user.GetInventory();
                    ItemDrop.ItemData item = null;
                    if (fuel)
                    {
                        if (!smelter.m_fuelItem || !inventory.HaveItem(smelter.m_fuelItem.m_itemData.m_shared.m_name, true)) break;
                    }
                    else
                    {
                        item = selected == null ? smelter.FindCookableItem(inventory) : inventory.GetAllItems().Find(x =>
                            x.m_dropPrefab == selected.m_dropPrefab && x.m_worldLevel >= Game.m_worldLevel);
                        if (item == null) break;
                    }
                    bool accepted = fuel ? smelter.OnAddFuel(sw, user, null) : smelter.OnAddOre(sw, user, item);
                    if (!accepted) break;
                    added++;
                }
            }
            finally { filling = false; }
            return added;
        }
    }
    [HarmonyPatch(typeof(Smelter), "Awake")]
    internal static class SmelterCapacityPatch
    {
        private static void Prefix(Smelter __instance)
        {
            // Apply to the production component, including later biomes and modded
            // machines. Never lower an existing capacity or enable absent inputs.
            if (__instance.m_maxOre > 0) __instance.m_maxOre = Math.Max(100, __instance.m_maxOre);
            if (__instance.m_maxFuel > 0) __instance.m_maxFuel = Math.Max(100, __instance.m_maxFuel);
        }
    }
    [HarmonyPatch(typeof(Smelter), "OnHoverAddOre")]
    internal static class SmelterQuickOreHoverPatch
    {
        private static void Postfix(ref string __result) => __result += SmelterQuickFill.HoverHint();
    }
    [HarmonyPatch(typeof(Smelter), "OnHoverAddFuel")]
    internal static class SmelterQuickFuelHoverPatch
    {
        private static void Postfix(ref string __result) => __result += SmelterQuickFill.HoverHint();
    }
    [HarmonyPatch(typeof(Smelter), "OnAddOre")]
    internal static class SmelterQuickOrePatch
    {
        private static void Prefix(Smelter __instance, Humanoid user, ref ItemDrop.ItemData item, out int __state)
        {
            __state = SmelterQuickFill.Begin(__instance, user, false);
            // Capture the chosen wood before native consumption, so a bulk fill
            // cannot switch species when its last stack runs out.
            if (item == null && Utils.GetPrefabName(__instance.gameObject) == "charcoal_kiln")
                item = __instance.FindCookableItem(user.GetInventory());
        }
        private static void Postfix(Smelter __instance, Switch sw, Humanoid user, ItemDrop.ItemData item, bool __result, int __state)
        { if (__result && __state > 1) SmelterQuickFill.FillMore(__instance, sw, user, item, false, __state - 1); }
    }
    [HarmonyPatch(typeof(Smelter), "OnAddFuel")]
    internal static class SmelterQuickFuelPatch
    {
        private static void Prefix(Smelter __instance, Humanoid user, out int __state) => __state = SmelterQuickFill.Begin(__instance, user, true);
        private static void Postfix(Smelter __instance, Switch sw, Humanoid user, bool __result, int __state)
        { if (__result && __state > 1) SmelterQuickFill.FillMore(__instance, sw, user, null, true, __state - 1); }
    }
}

