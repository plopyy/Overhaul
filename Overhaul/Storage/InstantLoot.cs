using HarmonyLib;
using UnityEngine;

namespace Overhaul.Storage
{
    internal static class InstantLoot
    {
        internal static void ShortenDeath(Character character)
        {
            // Start native corpse creation at death instead of waiting for a long animation.
            if (character && !character.IsPlayer()) character.m_deathAnimation = false;
        }
        internal static void ShortenCorpse(Character character, Ragdoll corpse)
        {
            if (!character || character.IsPlayer() || !corpse) return;
            corpse.m_ttl = .5f;
            corpse.CancelInvoke("DestroyNow");
            // Keep native DestroyNow: effects, loot, then network destruction, together.
            corpse.InvokeRepeating("DestroyNow", .5f, .1f);
        }
    }
    [HarmonyPatch(typeof(Character),"CheckDeath")]
    internal static class ShortDeathAnimation
    {
        static void Prefix(Character __instance)
        {
            if (!__instance.IsDead() && __instance.GetHealth() <= 0) InstantLoot.ShortenDeath(__instance);
        }
    }
    [HarmonyPatch]
    internal static class ShortDeathCorpse
    {
        static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.DeclaredMethod(typeof(Character), "OnRagdollCreated");
            yield return AccessTools.DeclaredMethod(typeof(Humanoid), "OnRagdollCreated");
        }
        static void Postfix(Character __instance, Ragdoll ragdoll) => InstantLoot.ShortenCorpse(__instance,ragdoll);
    }
}
