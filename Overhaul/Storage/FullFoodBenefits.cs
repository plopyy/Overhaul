using HarmonyLib;

namespace Overhaul.Storage
{
    // Applies to every meal, without consulting progression or Nutrition.
    internal static class FullFoodBenefits
    {
        internal static void Apply(Player.Food food)
        {
            if (food.m_item?.m_shared == null) return;
            var shared = food.m_item.m_shared;
            bool active = food.m_time > 0;
            food.m_health = active ? shared.m_food : 0;
            food.m_stamina = active ? shared.m_foodStamina : 0;
            food.m_eitr = active ? shared.m_foodEitr : 0;
        }
        internal static void Apply(Player player)
        {
            foreach (var food in player.GetFoods()) Apply(food);
        }
    }

    [HarmonyPatch(typeof(Player), "GetTotalFoodValue")]
    internal static class FullFoodTotalsPatch
    {
        // UpdateFood calls this after its decay calculation and before setting
        // the player's maxima. Restore full benefits before the native sum.
        private static void Prefix(Player __instance) => FullFoodBenefits.Apply(__instance);
    }

    [HarmonyPatch(typeof(Player), nameof(Player.Load))]
    internal static class FullFoodLoadPatch
    {
        private static void Postfix(Player __instance) => FullFoodBenefits.Apply(__instance);
    }
}
