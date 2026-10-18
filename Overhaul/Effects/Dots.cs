using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Overhaul.Effects
{
    internal enum DotType { Burn, Frostbite, Shock, Poison, Bleed }

    // Damage over time, replacing every DoT of the game (burning, poison, frost, lightning, spirit), on players and creatures alike.
    // Fire, frost, lightning and poison hits put their DoT on the target (Burn, Frostbite, Shock, Poison); Bleed comes from
    // effects. A DoT lasts 3 s and deals its base damage every second. A new one of the same type adds a stack (5 at most)
    // and restarts the 3 s; it ends entirely when the time runs out. Damage per second:
    //   base × (1 + 0.5 × (stacks − 1)) × (1 + attacker's BurnDamage/FrostbiteDamage/... bonus / 100)
    // A hit's DoT base is a fifth of its raw element damage, rounded down, at least 1. DoT damage never crits, goes through the
    // target's resistances; an immune target never gets the DoT. Side effects, whatever the resistances (normal / boss):
    //   Burn: +50 % / +20 % chance to take a critical hit. Frostbite: 50 % / 30 % slower. Shock: stunned once every 10 s / 60 s.
    //   Poison: deals 20 % / 10 % less damage. Bleed: no health regeneration / half.
    // The state lives in the character's ZDO (stacks, base, end time, attacker), so every machine shows the icons under the
    // health bars and the DoT survives a change of owner; the owner ticks it.
    internal static class Dots
    {
        internal const float Duration = 3f;
        internal const int MaxStacks = 5;
        internal static readonly string[] Names = { "Burn", "Frostbite", "Shock", "Poison", "Bleed" };
        static readonly HitData.DamageType[] Types = { HitData.DamageType.Fire, HitData.DamageType.Frost, HitData.DamageType.Lightning, HitData.DamageType.Poison, HitData.DamageType.Slash };
        static readonly int[] StackKeys = Keys("stacks"), BaseKeys = Keys("base"), EndKeys = Keys("end");
        static readonly string[] AttackerKeys = { "overhaul_dot_burn_attacker", "overhaul_dot_frostbite_attacker", "overhaul_dot_shock_attacker", "overhaul_dot_poison_attacker", "overhaul_dot_bleed_attacker" };
        static readonly int StunKey = "overhaul_dot_stun".GetStableHashCode();
        // DoT tick hits carry their own hit type (a byte the game sends as is): never a new DoT, never a critical hit.
        const byte FirstHitType = 200;
        static int[] Keys(string what) { var keys = new int[Names.Length]; for (int i = 0; i < keys.Length; i++) keys[i] = ("overhaul_dot_" + Names[i].ToLowerInvariant() + "_" + what).GetStableHashCode(); return keys; }

        static double Now => ZNet.instance ? ZNet.instance.GetTimeSeconds() : Time.time;
        static ZDO Zdo(Character c) => c && c.m_nview && c.m_nview.IsValid() ? c.m_nview.GetZDO() : null;

        internal static bool IsDot(HitData hit) => hit != null && (byte)hit.m_hitType >= FirstHitType && (byte)hit.m_hitType < FirstHitType + Names.Length;

        internal static int Stacks(Character c, DotType type)
        {
            var zdo = Zdo(c); int t = (int)type;
            if (zdo == null) return 0;
            int stacks = zdo.GetInt(StackKeys[t], 0);
            return stacks > 0 && Now < zdo.GetFloat(EndKeys[t], 0) ? stacks : 0;
        }

        static float BossOr(Character c, float normal, float boss) => c.IsBoss() ? boss : normal;

        // A hit's DoT base: a fifth of its raw element damage, rounded down, at least 1.
        internal static float BaseOf(float raw) => Mathf.Max(1, Mathf.Floor(raw / 5f));

        // Puts a DoT (or one more stack) on the target; only on the target's owner.
        internal static void Apply(Character target, DotType type, float baseDamage, ZDOID attacker)
        {
            var zdo = Zdo(target); int t = (int)type;
            if (zdo == null || !target.m_nview.IsOwner() || target.IsDead() || baseDamage <= 0) return;
            if (target.GetDamageModifiers().GetModifier(Types[t]) == HitData.DamageModifier.Immune) return;
            int stacks = Mathf.Min(MaxStacks, Stacks(target, type) + 1);
            zdo.Set(StackKeys[t], stacks);
            zdo.Set(BaseKeys[t], stacks > 1 ? Mathf.Max(baseDamage, zdo.GetFloat(BaseKeys[t], 0)) : baseDamage);
            zdo.Set(EndKeys[t], (float)(Now + Duration));
            zdo.Set(AttackerKeys[t], attacker);
            if (stacks == 1) nextTick[(target.GetZDOID(), t)] = Now + 1;
            if (type == DotType.Shock && Now - zdo.GetFloat(StunKey, -9999) >= BossOr(target, 10, 60))
            {
                zdo.Set(StunKey, (float)Now);
                target.Stagger(-target.transform.forward);
            }
        }

        static readonly Dictionary<(ZDOID, int), double> nextTick = new Dictionary<(ZDOID, int), double>();
        static readonly List<Character> owned = new List<Character>();
        static float nextScan;

        // Every frame: the owned characters' DoTs deal their damage each second, and end when their time is up.
        internal static void Tick()
        {
            if (!ZNet.instance) return;
            if (Time.time >= nextScan)
            {
                nextScan = Time.time + .25f; owned.Clear();
                foreach (var c in Character.GetAllCharacters()) if (c && c.m_nview && c.m_nview.IsValid() && c.m_nview.IsOwner()) owned.Add(c);
                foreach (var c in owned) for (int t = 0; t < Names.Length; t++) Step(c, t);
                PlayerIcons();
            }
        }

        static void Step(Character c, int t)
        {
            var zdo = Zdo(c); if (zdo == null) return;
            int stacks = zdo.GetInt(StackKeys[t], 0); if (stacks <= 0) return;
            var key = (c.GetZDOID(), t);
            if (Now >= zdo.GetFloat(EndKeys[t], 0) || c.IsDead()) { zdo.Set(StackKeys[t], 0); nextTick.Remove(key); return; }
            if (!nextTick.TryGetValue(key, out double due)) { nextTick[key] = Now + 1; return; }
            if (Now < due) return;
            nextTick[key] = due + 1;
            var attackerId = zdo.GetZDOID(AttackerKeys[t]);
            var attacker = ZNetScene.instance ? ZNetScene.instance.FindInstance(attackerId)?.GetComponent<Character>() : null;
            float bonus = attacker ? Effects.Get(attacker, Names[t] + "Damage") : 0;
            float damage = zdo.GetFloat(BaseKeys[t], 0) * (1 + .5f * (stacks - 1)) * (1 + bonus / 100f);
            var hit = new HitData { m_point = c.GetCenterPoint(), m_dir = Vector3.zero, m_dodgeable = false, m_blockable = false, m_hitType = (HitData.HitType)(FirstHitType + t) };
            hit.m_attacker = attackerId;
            switch ((DotType)t)
            {
                case DotType.Burn: hit.m_damage.m_fire = damage; break;
                case DotType.Frostbite: hit.m_damage.m_frost = damage; break;
                case DotType.Shock: hit.m_damage.m_lightning = damage; break;
                case DotType.Poison: hit.m_damage.m_poison = damage; break;
                case DotType.Bleed: hit.m_damage.m_slash = damage; break;
            }
            c.Damage(hit);
        }

        // The local player's DoTs in their status effect bar: one status per DoT, its stacks as the icon text.
        static readonly Dictionary<int, SE_Dot> shown = new Dictionary<int, SE_Dot>();
        static void PlayerIcons()
        {
            var player = Player.m_localPlayer; if (!player) return;
            var seman = player.GetSEMan();
            for (int t = 0; t < Names.Length; t++)
            {
                int stacks = Stacks(player, (DotType)t);
                shown.TryGetValue(t, out var se);
                if (stacks > 0 && (se == null || !seman.HaveStatusEffect(se.NameHash())))
                {
                    var template = ScriptableObject.CreateInstance<SE_Dot>();
                    template.name = "Overhaul_Dot_" + Names[t]; template.m_name = "$overhaul_dot_" + Names[t].ToLowerInvariant(); template.m_icon = Icon(t);
                    se = (SE_Dot)seman.AddStatusEffect(template, false);
                    shown[t] = se;
                }
                if (se != null) se.Stacks = stacks;
                if (stacks <= 0 && se != null) { seman.RemoveStatusEffect(se, true); shown.Remove(t); }
            }
        }

        // The icons come from the health bar template (AugaEnemyHud prefab, OverhaulDots row).
        internal static Sprite Icon(int t)
        {
            var hud = EnemyHud.instance ? EnemyHud.instance.m_baseHud : null;
            var image = hud ? hud.transform.Find("OverhaulDots/" + Names[t])?.GetComponent<Image>() : null;
            return image ? image.sprite : null;
        }

        internal sealed class SE_Dot : StatusEffect
        {
            internal int Stacks;
            public override string GetIconText() => Stacks > 1 ? Stacks.ToString() : "";
        }

        // ------------------------------------------------------------------ the game's DoTs replaced
        [ThreadStatic] static HitData current;
        [ThreadStatic] static float rawFire, rawFrost, rawLightning, rawPoison;

        [HarmonyPatch(typeof(Character), "RPC_Damage")]
        private static class Received
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(Character __instance, HitData hit)
            {
                current = hit;
                rawFire = hit.m_damage.m_fire; rawFrost = hit.m_damage.m_frost; rawLightning = hit.m_damage.m_lightning; rawPoison = hit.m_damage.m_poison;
                // Poison: a poisoned attacker deals less damage (its DoT ticks excepted).
                if (!IsDot(hit) && hit.GetAttacker() is Character attacker && Stacks(attacker, DotType.Poison) > 0) hit.m_damage.Modify(1 - BossOr(attacker, .2f, .1f));
            }
            private static void Finalizer() => current = null;
        }

        // The game's elemental status effects never start: fire, poison and spirit (which the game kept out of the hit to deal over
        // time) are dealt at once, and the element's DoT goes on the target instead.
        static void Instant(Character target, float damage, Action<HitData> set)
        {
            if (damage <= 0 || current == null) return;
            var hit = new HitData { m_point = current.m_point, m_dir = current.m_dir, m_attacker = current.m_attacker, m_hitType = current.m_hitType };
            set(hit);
            target.ApplyDamage(hit, true, false);
        }
        static void Start(Character target, DotType type, float raw)
        {
            if (current == null || IsDot(current) || raw <= 0) return;
            Apply(target, type, BaseOf(raw), current.m_attacker);
        }

        [HarmonyPatch(typeof(Character), "AddFireDamage")]
        private static class Fire { private static bool Prefix(Character __instance, float damage) { Instant(__instance, damage, h => h.m_damage.m_fire = damage); Start(__instance, DotType.Burn, rawFire); return false; } }
        [HarmonyPatch(typeof(Character), "AddPoisonDamage")]
        private static class PoisonHit { private static bool Prefix(Character __instance, float damage) { Instant(__instance, damage, h => h.m_damage.m_poison = damage); Start(__instance, DotType.Poison, rawPoison); return false; } }
        [HarmonyPatch(typeof(Character), "AddSpiritDamage")]
        private static class Spirit { private static bool Prefix(Character __instance, float damage) { Instant(__instance, damage, h => h.m_damage.m_spirit = damage); return false; } }
        [HarmonyPatch(typeof(Character), "AddFrostDamage")]
        private static class Frost { private static bool Prefix(Character __instance) { Start(__instance, DotType.Frostbite, rawFrost); return false; } }
        [HarmonyPatch(typeof(Character), "AddLightningDamage")]
        private static class Lightning { private static bool Prefix(Character __instance) { Start(__instance, DotType.Shock, rawLightning); return false; } }

        // No status effect of the game's own DoTs ever starts (attacks carrying one, the Ashlands lava...). The lava's burning
        // becomes a Burn: a fifth of the lava damage the character is taking at that heat.
        static readonly HashSet<int> Replaced = new HashSet<int> { SEMan.s_statusEffectBurning, SEMan.s_statusEffectPoison, SEMan.s_statusEffectFrost, SEMan.s_statusEffectLightning, SEMan.s_statusEffectSpirit };
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.AddStatusEffect), typeof(StatusEffect), typeof(bool), typeof(int), typeof(float), typeof(short))]
        private static class GameDots
        {
            private static bool Prefix(SEMan __instance, StatusEffect statusEffect, ref StatusEffect __result)
            {
                if (!statusEffect || !Replaced.Contains(statusEffect.NameHash())) return true;
                var c = __instance.m_character;
                if (c && statusEffect.NameHash() == SEMan.s_statusEffectBurning && c.m_lavaHeatLevel > 0 && c.m_nview && c.m_nview.IsOwner())
                    Apply(c, DotType.Burn, BaseOf(c.m_lavaHeatLevel >= 1 ? c.m_lavaFullDamage : c.m_lavaFirstDamage), ZDOID.None);
                __result = null;
                return false;
            }
        }

        // ------------------------------------------------------------------ immunities (on/off effects of the player)
        // FireImmunity: no fire damage, so never a Burn. FrostImmunity: no frost damage, never a Frostbite, and no Cold or
        // Freezing from the weather.
        static bool Has(Character c, string effect) => c is Player && Effects.Get(c, effect) >= 1;
        [HarmonyPatch(typeof(Character), nameof(Character.GetDamageModifiers))]
        private static class Immunities
        {
            private static void Postfix(Character __instance, ref HitData.DamageModifiers __result)
            {
                if (Has(__instance, "FireImmunity")) __result.m_fire = HitData.DamageModifier.Immune;
                if (Has(__instance, "FrostImmunity")) __result.m_frost = HitData.DamageModifier.Immune;
            }
        }
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.AddStatusEffect), typeof(StatusEffect), typeof(bool), typeof(int), typeof(float), typeof(short))]
        private static class NoCold
        {
            private static bool Prefix(SEMan __instance, StatusEffect statusEffect, ref StatusEffect __result)
            {
                if (!statusEffect || (statusEffect.NameHash() != SEMan.s_statusEffectCold && statusEffect.NameHash() != SEMan.s_statusEffectFreezing)) return true;
                if (!Has(__instance.m_character, "FrostImmunity")) return true;
                __result = null; return false;
            }
        }

        // ------------------------------------------------------------------ side effects
        // Frostbite slows.
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.ApplyStatusEffectSpeedMods))]
        private static class Slow
        {
            private static void Postfix(SEMan __instance, ref float speed)
            {
                var c = __instance.m_character;
                if (c && Stacks(c, DotType.Frostbite) > 0) speed *= 1 - BossOr(c, .5f, .3f);
            }
        }
        // Bleed stops (bosses: halves) health regeneration and healing.
        [HarmonyPatch(typeof(Character), "RPC_Heal")]
        private static class NoRegen
        {
            private static void Prefix(Character __instance, ref float hp)
            {
                if (Stacks(__instance, DotType.Bleed) > 0) hp *= BossOr(__instance, 0, .5f);
            }
        }
        // Burn: the attacker's hit is more likely to be critical (rolled on the attacker's machine, like CritChance).
        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class BurnCritical
        {
            private static void Prefix(Character __instance, HitData hit)
            {
                if (hit == null || IsDot(hit) || __instance.IsPlayer() || Stacks(__instance, DotType.Burn) <= 0) return;
                if (!(hit.GetAttacker() is Player player) || player != Player.m_localPlayer) return;
                if (UnityEngine.Random.value < BossOr(__instance, .5f, .2f)) EffectPatches.Critical.Mark(hit);
            }
        }

        // ------------------------------------------------------------------ health bars
        // Shown for every creature within ShowDistance, aimed at or not, with its DoT icons and stacks under the bar.
        internal const float ShowDistance = 15f;
        [HarmonyPatch(typeof(EnemyHud), "Awake")]
        private static class Distance { private static void Postfix(EnemyHud __instance) => __instance.m_maxShowDistance = Mathf.Max(__instance.m_maxShowDistance, ShowDistance); }
        [HarmonyPatch(typeof(EnemyHud), "UpdateHuds")]
        private static class Bars
        {
            private static void Prefix(EnemyHud __instance)
            {
                foreach (var hud in __instance.m_huds.Values)
                    if (hud.m_character && Vector3.Distance(hud.m_character.transform.position, __instance.m_refPoint) <= ShowDistance) hud.m_hoverTimer = 0;
            }
            private static void Postfix(EnemyHud __instance)
            {
                foreach (var hud in __instance.m_huds.Values)
                {
                    if (!hud.m_gui || !hud.m_character) continue;
                    var row = hud.m_gui.transform.Find("OverhaulDots"); if (!row) continue;
                    for (int t = 0; t < Names.Length && t < row.childCount; t++)
                    {
                        int stacks = Stacks(hud.m_character, (DotType)t);
                        var icon = row.GetChild(t);
                        if (icon.gameObject.activeSelf != stacks > 0) icon.gameObject.SetActive(stacks > 0);
                        if (stacks > 0) { var text = icon.Find("Stacks")?.GetComponent<TMP_Text>(); if (text) text.text = stacks > 1 ? stacks.ToString() : ""; }
                    }
                }
            }
        }
    }
}
