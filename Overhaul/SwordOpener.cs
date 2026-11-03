using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Overhaul
{
    // Opening thrust (one-handed swords): the special attack of a sword that has not attacked for Rest s (that sword itself: switching
    // from another weapon does not count) plays Speed faster; if it lands on a creature, the next Hits light attacks of the same sword
    // deal Damage more, within Window s. The first-attack lunge of the weapon rework still carries it forward.
    internal static class SwordOpener
    {
        const float Rest = 3f, Speed = .5f, Damage = .25f, Window = 5f;
        const int Hits = 3;

        static readonly ConditionalWeakTable<ItemDrop.ItemData, StrongBox<float>> lastUse = new ConditionalWeakTable<ItemDrop.ItemData, StrongBox<float>>();
        static Attack opener;
        static ItemDrop.ItemData boosted;
        static int boostedHits;
        static float boostedUntil;
        static Attack boostCounted;

        static bool IsSword(ItemDrop.ItemData item) => item != null && Utility.WeaponAttackSpeeds.Category(item) == "Swords1H";

        internal static float SpeedFactor(Player player) => player.m_currentAttack != null && player.m_currentAttack == opener ? 1 + Speed : 1;

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
        private static class Start
        {
            private static void Prefix(Humanoid __instance, bool secondaryAttack, out (bool fast, ItemDrop.ItemData weapon) __state)
            {
                __state = (false, null);
                if (!(__instance is Player player) || player != Player.m_localPlayer) return;
                var weapon = player.GetCurrentWeapon();
                if (!IsSword(weapon)) return;
                bool rested = !lastUse.TryGetValue(weapon, out var last) || Time.time - last.Value >= Rest;
                __state = (secondaryAttack && rested, weapon);
            }
            private static void Postfix(Humanoid __instance, bool __result, (bool fast, ItemDrop.ItemData weapon) __state)
            {
                if (!__result || __state.weapon == null) return;
                lastUse.GetOrCreateValue(__state.weapon).Value = Time.time;
                opener = __state.fast ? ((Player)__instance).m_currentAttack : null;
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class Hit
        {
            private static void Prefix(Character __instance, HitData hit)
            {
                var player = Player.m_localPlayer;
                if (!player || hit.GetAttacker() != player || __instance == player || __instance.IsDead()) return;
                var attack = player.m_currentAttack;
                if (attack == null) return;
                // The opener landed: the light attacks that follow are boosted.
                if (attack == opener) { boosted = attack.m_weapon; boostedHits = Hits; boostedUntil = Time.time + Window; boostCounted = null; return; }
                if (player.m_currentAttackIsSecondary || Time.time > boostedUntil || attack.m_weapon != boosted) return;
                // One of the boosted attacks per light attack, however many creatures it hits.
                if (attack != boostCounted) { if (boostedHits <= 0) return; boostCounted = attack; boostedHits--; }
                hit.m_damage.Modify(1 + Damage);
            }
        }
    }
}
