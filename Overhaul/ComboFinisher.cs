using HarmonyLib;
using UnityEngine;

namespace Overhaul
{
    // Combo finisher (dual axes): each light attack of a combo that lands on a creature counts; a special attack
    // started while the combo goes on plays twice as fast and hits harder, the more hits landed the more (Bonus), and knocks
    // the creatures it hits back. The combo is lost after Window s without a landed hit, and spent by the special attack.
    internal static class ComboFinisher
    {
        const float Window = 1.5f, PushPerBonus = 4f;
        // Bonus after 2, 3 and 4 landed hits (and more).
        static readonly float[] Bonus = { 0, 0, .25f, .4f, .6f };

        static int hits;
        static float lastHit;
        static Attack counted, finisher;
        static float finisherBonus;

        static bool Fits(Player player)
        {
            var category = Utility.WeaponAttackSpeeds.Category(player.GetCurrentWeapon());
            return category == "DualAxes";
        }

        // The animation speed factor of the attack playing (1 outside a finisher).
        // The finisher plays twice as fast whatever its level; the level only sets the damage and knockback bonus.
        const float FinisherSpeed = 2f;
        internal static float SpeedFactor(Player player) => player.m_currentAttack != null && player.m_currentAttack == finisher ? FinisherSpeed : 1;

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
        private static class Start
        {
            private static void Postfix(Humanoid __instance, bool secondaryAttack, bool __result)
            {
                if (!__result || !(__instance is Player player) || player != Player.m_localPlayer) return;
                if (!secondaryAttack) return;
                bool combo = Fits(player) && Time.time - lastHit <= Window;
                finisherBonus = combo ? Bonus[Mathf.Min(hits, Bonus.Length - 1)] : 0;
                finisher = finisherBonus > 0 ? player.m_currentAttack : null;
                // Feedback: which finisher level started (1 = 2 hits, 2 = 3 hits, 3 = 4 hits and more).
                if (finisher != null)
                {
                    int level = Mathf.Clamp(hits - 1, 1, 3);
                    player.Message(MessageHud.MessageType.TopLeft, Localization.instance.Localize("$overhaul_combo_finisher", level.ToString(), Mathf.RoundToInt(finisherBonus * 100).ToString()));
                    Utility.Log.LogInfo($"Combo finisher level {level}: {hits} hits, +{finisherBonus * 100:0}% damage");
                }
                hits = 0; counted = null;
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class Hits
        {
            private static void Prefix(Character __instance, HitData hit)
            {
                var player = Player.m_localPlayer;
                if (!player || hit.GetAttacker() != player || __instance == player || __instance.IsDead()) return;
                var attack = player.m_currentAttack;
                if (attack == null) return;
                if (attack == finisher)
                {
                    hit.m_damage.Modify(1 + finisherBonus);
                    hit.m_pushForce *= 1 + PushPerBonus * finisherBonus;
                    return;
                }
                // A light attack of the combo: one count per attack, however many creatures it hits.
                if (player.m_currentAttackIsSecondary || !Fits(player) || attack == counted) return;
                if (Time.time - lastHit > Window) hits = 0;
                counted = attack; hits++; lastHit = Time.time;
            }
        }
    }
}
