using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Leveling
{
    internal static class NutritionDuration
    {
        internal const string SaveKey = "Overhaul.NutritionDuration";
        private static readonly MethodInfo Memberwise = AccessTools.Method(typeof(object), "MemberwiseClone");
        private sealed class Meal { internal ItemDrop.ItemData.SharedData Original; }
        private static readonly ConditionalWeakTable<ItemDrop.ItemData, Meal> Meals = new ConditionalWeakTable<ItemDrop.ItemData, Meal>();
        private static readonly ConditionalWeakTable<Player, object> Legacy = new ConditionalWeakTable<Player, object>();
        private static bool SavedMultiplier(Player player, out float saved)
        {
            saved = 1;
            return player && player.m_customData.TryGetValue(SaveKey, out var value) &&
                float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out saved) && saved >= 1 && !float.IsInfinity(saved);
        }
        internal static float Multiplier(Player player)
        {
            var state = OverhaulCharacter.Get(player);
            if (state != null && !state.Ready) return SavedMultiplier(player, out var saved) ? saved : 1;
            return 1 + Mathf.Max(0, LevelingEffects.Bonus(player, "food"));
        }
        internal static float Preview(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return 0;
            return Meals.TryGetValue(item, out _) ? item.m_shared.m_foodBurnTime : item.m_shared.m_foodBurnTime * Multiplier(Player.m_localPlayer);
        }
        internal static ItemDrop.ItemData CopyMeal(ItemDrop.ItemData item, float multiplier)
        {
            var original = Meals.TryGetValue(item, out var meal) ? meal.Original : item.m_shared;
            var copy = item.Clone();
            copy.m_shared = (ItemDrop.ItemData.SharedData)Memberwise.Invoke(original, null);
            copy.m_shared.m_foodBurnTime = original.m_foodBurnTime * multiplier;
            Meals.Add(copy, new Meal { Original = original });
            return copy;
        }
        internal static void Sync(Player player, float savedMultiplier = 1)
        {
            float multiplier = Multiplier(player);
            bool legacy = Legacy.TryGetValue(player, out _) && OverhaulCharacter.Get(player).Ready;
            bool changed = false;
            var foods = player.GetFoods();
            for (int i = foods.Count - 1; i >= 0; i--)
            {
                var food = foods[i];
                if (food.m_item?.m_shared == null) continue;
                float previousDuration;
                if (Meals.TryGetValue(food.m_item, out var meal))
                {
                    previousDuration = food.m_item.m_shared.m_foodBurnTime;
                    food.m_item.m_shared.m_foodBurnTime = meal.Original.m_foodBurnTime * multiplier;
                }
                else
                {
                    previousDuration = food.m_item.m_shared.m_foodBurnTime * savedMultiplier;
                    food.m_item = CopyMeal(food.m_item, multiplier);
                }
                float duration = food.m_item.m_shared.m_foodBurnTime;
                if (previousDuration > 0 && duration != previousDuration)
                {
                    // A stat change adds/removes the full duration difference, keeping
                    // elapsed time. Only migration from the old slow timer preserves its fraction.
                    food.m_time = legacy ? food.m_time * duration / previousDuration : food.m_time + duration - previousDuration;
                    changed = true;
                    if (food.m_time <= 0) { foods.RemoveAt(i); continue; }
                    Storage.FullFoodBenefits.Apply(food);
                }
            }
            if (legacy) Legacy.Remove(player);
            if (changed && player.m_nview && player.m_nview.IsValid())
            {
                player.GetTotalFoodValue(out var health, out var stamina, out var eitr);
                player.SetMaxHealth(health, true); player.SetMaxStamina(stamina, true); player.SetMaxEitr(eitr, true);
            }
        }
        internal static void Save(Player player)
        {
            Sync(player);
            if (Legacy.TryGetValue(player, out _) && !OverhaulCharacter.Get(player).Ready) return;
            player.m_customData[SaveKey] = Multiplier(player).ToString("R", CultureInfo.InvariantCulture);
        }
        internal static void RefreshTooltip(Player player)
        {
            if (player == Player.m_localPlayer && UITooltip.m_tooltip)
                UITooltip.m_tooltip.GetComponent<AugaUnity.ComplexTooltip>()?.RefreshFoodDuration();
        }
        internal static void Load(Player player)
        {
            bool hasSaved = SavedMultiplier(player, out var saved);
            Legacy.Remove(player);
            if (!hasSaved) { saved = 1; Legacy.Add(player, new object()); }
            // Legacy saves contain unscaled seconds because the old patch slowed time.
            Sync(player, saved);
        }
    }
    [HarmonyPatch(typeof(Player), "UpdateFood")]
    internal static class NutritionUpdatePatch
    {
        private static void Prefix(Player __instance) => NutritionDuration.Sync(__instance);
    }
    [HarmonyPatch(typeof(Player), nameof(Player.EatFood))]
    internal static class NutritionEatPatch
    {
        private static void Prefix(Player __instance, ref ItemDrop.ItemData item)
        {
            NutritionDuration.Sync(__instance);
            if (item?.m_shared != null) item = NutritionDuration.CopyMeal(item, NutritionDuration.Multiplier(__instance));
        }
    }
    [HarmonyPatch(typeof(Player), nameof(Player.Save))]
    internal static class NutritionSavePatch
    {
        private static void Prefix(Player __instance) => NutritionDuration.Save(__instance);
    }
    [HarmonyPatch(typeof(Player), nameof(Player.Load))]
    internal static class NutritionLoadPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Player __instance) => NutritionDuration.Load(__instance);
    }
    // Native tooltips also use an isolated copy; item stats in bags/prefabs stay shared and unchanged.
    [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool) })]
    internal static class NutritionNativeTooltipPatch
    {
        private static void Prefix(ref ItemDrop.ItemData item)
        {
            if (item?.m_shared != null && item.m_shared.m_foodBurnTime > 0)
                item = NutritionDuration.CopyMeal(item, NutritionDuration.Multiplier(Player.m_localPlayer));
        }
    }
}
