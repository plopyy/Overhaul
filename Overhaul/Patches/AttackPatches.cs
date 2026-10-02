using HarmonyLib;
using Overhaul.Utility;

namespace Overhaul.Patches
{
    [HarmonyPatch(typeof(Attack))]
    internal static class AttackPatches
    {
        [HarmonyPatch("GetAttackStamina")]
        [HarmonyPostfix]
        private static void GetAttackStamina_Postfix(Attack __instance, ref float __result)
        {
            if (__instance.m_character is Player && !AlterItemStat.HasAttack(__instance, "m_attackStamina"))
            {
                __result = OverhaulConfig.AttackUseStamina.Value
                    ? OverhaulConfig.AttackStaminaDrain.Value
                    : 0f;
            }
        }

        [HarmonyPatch("GetAttackEitr", new System.Type[0])]
        [HarmonyPostfix]
        private static void GetAttackEitr_Postfix(Attack __instance, ref float __result)
        {
            if (__instance.m_character is Player && !AlterItemStat.HasAttack(__instance, "m_attackEitr"))
            {
                __result *= OverhaulConfig.AttackEitrDrainRate.Value;
            }
        }
    }
}
