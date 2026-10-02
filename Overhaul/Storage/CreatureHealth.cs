using HarmonyLib;
using UnityEngine;

namespace Overhaul.Storage
{
    // Change the base calculation, before stars and the dungeon boss multiplier.
    [HarmonyPatch(typeof(Character), nameof(Character.GetMaxHealthBase))]
    internal static class CreatureBaseHealthPatch
    {
        private static void Postfix(Character __instance, ref float __result)
        { if (!__instance.IsPlayer()) __result *= 1.5f; }
    }

    [HarmonyPatch(typeof(Character), "SetupMaxHealth")]
    internal static class CreatureHealthFractionPatch
    {
        private static void Prefix(Character __instance, out float __state)
        {
            float maximum = __instance.GetMaxHealth();
            __state = maximum > 0 ? Mathf.Clamp01(__instance.GetHealth() / maximum) : 1;
        }
        // BossModifiers.FinalHealth runs at Priority.Last and applies its bonus afterwards.
        [HarmonyPriority(Priority.Normal)]
        private static void Postfix(Character __instance, float __state)
        {
            if (!__instance.IsPlayer() && __instance.m_nview && __instance.m_nview.IsValid() && __instance.m_nview.IsOwner())
                __instance.SetHealth(__instance.GetMaxHealth() * __state);
        }
    }

    [HarmonyPatch(typeof(Character), "Awake")]
    internal static class LoadedCreatureHealthPatch
    {
        private static void Postfix(Character __instance)
        {
            // Native Awake skips max-health setup for wounded saved creatures.
            // Recalculate from the base, preserving injuries and avoiding accumulation.
            if (!__instance.IsPlayer() && __instance.m_nview && __instance.m_nview.IsValid() && __instance.m_nview.IsOwner())
                __instance.SetupMaxHealth();
        }
    }
}
