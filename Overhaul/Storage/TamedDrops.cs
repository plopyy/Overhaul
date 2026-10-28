using System;
using HarmonyLib;

namespace Overhaul.Storage
{
    // Loot of a tamed creature never expires on the ground (GroundItemExpiry).
    internal static class TamedDrops
    {
        internal static readonly int ProtectedKey = "overhaul_tamed_drop_v1".GetStableHashCode();
        private static readonly int CorpseKey = "overhaul_tamed_corpse_v1".GetStableHashCode();
        [ThreadStatic] private static int depth;

        // Creatures without a corpse drop their loot during OnDeath.
        [HarmonyPatch(typeof(CharacterDrop), "OnDeath")]
        private static class DeathScope
        {
            private static void Prefix(CharacterDrop __instance, out int __state)
            {
                __state = depth;
                if (__instance.m_character && __instance.m_character.IsTamed()) depth++;
            }
            private static void Finalizer(int __state) => depth = __state;
        }

        // Creatures with a corpse drop it when the corpse vanishes: the corpse remembers it was tamed.
        [HarmonyPatch(typeof(Ragdoll), nameof(Ragdoll.Setup))]
        private static class CorpseRemember
        {
            private static void Postfix(Ragdoll __instance, CharacterDrop characterDrop)
            {
                var character = characterDrop ? characterDrop.GetComponent<Character>() : null;
                if (character && character.IsTamed() && __instance.m_nview && __instance.m_nview.IsValid())
                    __instance.m_nview.GetZDO().Set(CorpseKey, true);
            }
        }

        [HarmonyPatch(typeof(Ragdoll), "SpawnLoot")]
        private static class CorpseScope
        {
            private static void Prefix(Ragdoll __instance, out int __state)
            {
                __state = depth;
                if (__instance.m_nview && __instance.m_nview.IsValid() && __instance.m_nview.GetZDO().GetBool(CorpseKey, false)) depth++;
            }
            private static void Finalizer(int __state) => depth = __state;
        }

        [HarmonyPatch(typeof(ItemDrop), "Awake")]
        private static class MarkDrop
        {
            private static void Postfix(ItemDrop __instance)
            {
                if (depth <= 0 || !__instance.m_nview || !__instance.m_nview.IsValid() || !__instance.m_nview.IsOwner()) return;
                __instance.m_nview.GetZDO().Set(ProtectedKey, true);
            }
        }
    }
}
