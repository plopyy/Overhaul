using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Overhaul.Effects;
using RootEffect = Overhaul.Effects.Root;
using UnityEngine;

namespace Overhaul.Rarity
{
    // Catalyst effects (Catalyst sections of RaritySystem.cfg): every catalyst socketed in the local player's worn gear acts,
    // each one once whatever the gear and however many pieces carry it. Its Bonus effects join the player's effect totals;
    // its Effect is one of the behaviours below, with the section's fixed values.
    internal static class CatalystEffects
    {
        internal static void Initialize() => Effects.Effects.Register(Bonuses);

        // The catalysts socketed in the player's worn gear, each once.
        internal static List<CatalystDef> Active(Player player)
        {
            var list = new List<CatalystDef>();
            if (!player) return list;
            var data = RarityConfig.Current; var seen = new HashSet<string>();
            foreach (var item in player.GetInventory().GetEquippedItems())
                foreach (var name in ItemRarity.Catalysts(item))
                    if (name.Length > 0 && seen.Add(name) && data.CatalystEffects.TryGetValue(name, out var def)) list.Add(def);
            return list;
        }
        // What a catalyst does, in the player's language: its effect's text with the section's values.
        internal static string Describe(string prefab)
        {
            if (prefab == null || !RarityConfig.Current.CatalystEffects.TryGetValue(prefab, out var def) || string.IsNullOrEmpty(def.Effect)) return "";
            string text = Localization.instance.Localize("$overhaul_catalyst_effect_" + def.Effect.ToLowerInvariant());
            foreach (var pair in def.Values) text = text.Replace("{" + pair.Key + "}", pair.Value.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture));
            if (def.Dot != null) text = text.Replace("{Dot}", Localization.instance.Localize("$overhaul_dot_" + def.Dot.ToLowerInvariant()));
            if (def.Bonuses.Length > 0) text = text.Replace("{Bonus}", def.Bonuses[0].Value.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture));
            return text;
        }
        static CatalystDef Find(Player player, string effect) => Active(player).FirstOrDefault(d => string.Equals(d.Effect, effect, StringComparison.OrdinalIgnoreCase));
        static bool Local(Character c) => c && c == Player.m_localPlayer;

        static void Bonuses(Player player, Dictionary<string, float> totals)
        {
            foreach (var def in Active(player)) foreach (var (effect, value) in def.Bonuses) Effects.Effects.Add(totals, effect, value);
        }

        // ------------------------------------------------------------------ attacks of the local player
        static int attacks;
        static bool thunderPending, dotAttack;
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
        private static class Attacks
        {
            private static void Postfix(Humanoid __instance, bool __result)
            {
                if (!__result || !(__instance is Player player) || !Local(player)) return;
                attacks++;
                var thunder = Find(player, "ThunderStrike"); thunderPending = thunder != null && attacks % Mathf.Max(1, (int)thunder.Get("Count", 4)) == 0;
                var dot = Find(player, "DotEvery"); dotAttack = dot != null && attacks % Mathf.Max(1, (int)dot.Get("Count", 3)) == 0;
                // EitrSurge: a spell (an attack spending eitr) may refill all the eitr.
                var surge = Find(player, "EitrSurge"); var attack = player.m_currentAttack;
                if (surge != null && attack != null && attack.m_attackEitr > 0 && UnityEngine.Random.value * 100f < surge.Get("Chance", 15))
                    player.AddEitr(player.GetMaxEitr());
            }
        }

        static float killFrenzyUntil; static int killFrenzyStacks;

        // The local player's hits: extra damage, life steal, root, bolts and DoTs.
        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class Hits
        {
            private static void Prefix(Character __instance, HitData hit)
            {
                if (hit == null || Dots.IsDot(hit) || !(hit.GetAttacker() is Player player) || !Local(player) || __instance == player || __instance.IsDead()) return;
                if (hit.GetTotalDamage() <= 0) return;
                var active = Active(player); if (active.Count == 0) return;
                float bonus = 0;
                foreach (var def in active)
                {
                    switch (def.Effect)
                    {
                        case "FirstStrike": if (__instance.GetHealthPercentage() >= .999f) bonus += def.Get("Value"); break;
                        case "Execute": if (__instance.GetHealthPercentage() < def.Get("Threshold") / 100f) bonus += def.Get("Value"); break;
                        case "KillFrenzy": if (Time.time < killFrenzyUntil) bonus += def.Get("Value") * killFrenzyStacks; break;
                    }
                }
                if (bonus != 0) hit.ApplyModifier(1 + bonus / 100f);
                foreach (var def in active)
                {
                    switch (def.Effect)
                    {
                        case "DesperateLifeSteal":
                            if (player.GetHealthPercentage() < def.Get("Threshold") / 100f) player.Heal(hit.GetTotalDamage() * def.Get("Value") / 100f, true);
                            break;
                        case "Entangle":
                            if (UnityEngine.Random.value * 100f < def.Get("Chance")) Network.Entangle(__instance, def.Get("Duration", 3));
                            break;
                        case "ThunderStrike":
                            if (!thunderPending) break;
                            thunderPending = false;
                            var bolt = new HitData { m_point = __instance.GetCenterPoint(), m_dir = Vector3.down, m_dodgeable = false, m_blockable = false };
                            bolt.m_damage.m_lightning = def.Get("Damage", 10); bolt.SetAttacker(player);
                            __instance.Damage(bolt);
                            break;
                        case "DotEvery":
                            if (dotAttack && Enum.TryParse(def.Dot, true, out DotType type)) Network.Dot(__instance, type, def.Get("Damage", 4), player.GetZDOID());
                            break;
                    }
                }
            }
        }

        // ------------------------------------------------------------------ hits the local player takes
        static float lastStandReady;
        [HarmonyPatch(typeof(Character), "RPC_Damage")]
        private static class Taken
        {
            [HarmonyPriority(Priority.High)]
            private static void Prefix(Character __instance, HitData hit)
            {
                if (!(__instance is Player player) || !Local(player) || hit == null || Dots.IsDot(hit) || player.IsDead()) return;
                var active = Active(player); if (active.Count == 0) return;
                var lastStand = active.FirstOrDefault(d => d.Effect == "LastStand");
                if (lastStand != null && Time.time >= lastStandReady && player.GetHealthPercentage() < lastStand.Get("Threshold", 25) / 100f && hit.GetTotalDamage() > 0)
                {
                    hit.m_damage = new HitData.DamageTypes(); hit.m_staggerMultiplier = 0; hit.m_pushForce = 0;
                    lastStandReady = Time.time + lastStand.Get("Cooldown", 120);
                    player.Message(MessageHud.MessageType.TopLeft, "$overhaul_catalyst_laststand_triggered");
                }
                var thorns = active.FirstOrDefault(d => d.Effect == "FireThorns");
                var attacker = hit.GetAttacker();
                if (thorns != null && attacker && attacker != player && !hit.m_ranged && !attacker.IsDead())
                {
                    var burn = new HitData { m_point = attacker.GetCenterPoint(), m_dir = (attacker.transform.position - player.transform.position).normalized, m_dodgeable = false, m_blockable = false };
                    burn.m_damage.m_fire = thorns.Get("Damage", 10); burn.SetAttacker(player);
                    attacker.Damage(burn);
                }
            }
        }

        // Crystal: a parry sends back part of the parried damage to the attacker.
        [HarmonyPatch(typeof(Humanoid), "BlockAttack")]
        private static class Parry
        {
            private static void Prefix(Humanoid __instance, HitData hit, Character attacker, out float __state)
            {
                __state = 0;
                if (!(__instance is Player player) || !Local(player) || !attacker || hit == null) return;
                var def = Find(player, "ParryReflect"); var blocker = player.GetCurrentBlocker();
                bool parry = blocker != null && blocker.m_shared.m_timedBlockBonus > 1f && player.m_blockTimer != -1f && player.m_blockTimer < .25f;
                if (def != null && parry) __state = hit.GetTotalBlockableDamage() * def.Get("Value", 50) / 100f;
            }
            private static void Postfix(Humanoid __instance, Character attacker, bool __result, float __state)
            {
                if (!__result || __state <= 0 || !attacker || attacker.IsDead()) return;
                var back = new HitData { m_point = attacker.GetCenterPoint(), m_dir = (attacker.transform.position - __instance.transform.position).normalized, m_dodgeable = false, m_blockable = false };
                back.m_damage.m_blunt = __state; back.SetAttacker(__instance);
                attacker.Damage(back);
            }
        }

        // ------------------------------------------------------------------ kills of the local player
        internal static void Killed(Vector3 position)
        {
            var player = Player.m_localPlayer; if (!player) return;
            MythicEffects.Killed();
            foreach (var def in Active(player))
            {
                if (def.Effect == "KillFrenzy")
                {
                    killFrenzyStacks = Time.time < killFrenzyUntil ? Mathf.Min((int)def.Get("Stacks", 3), killFrenzyStacks + 1) : 1;
                    killFrenzyUntil = Time.time + def.Get("Duration", 10);
                }
                else if (def.Effect == "KillExplosion") Explosion(player, position, def.Get("Damage", 20));
            }
        }

        // Yagluth's drop: the Staff of Embers fireball's burst on the corpse, fire damage to the creatures around.
        static void Explosion(Player player, Vector3 point, float damage)
        {
            var model = EffectPatches.Explosive.Fireball(); if (!model) return;
            model.m_hitEffects.Create(point, Quaternion.identity, null, 1f);
            var seen = new HashSet<Character>();
            foreach (var collider in Physics.OverlapSphere(point, Mathf.Max(2f, model.m_aoe), Projectile.s_rayMaskSolids, QueryTriggerInteraction.UseGlobal))
            {
                var c = collider.GetComponentInParent<Character>();
                if (!c || c == player || c.IsDead() || !seen.Add(c) || !BaseAI.IsEnemy(player, c)) continue;
                var hit = new HitData { m_point = collider.ClosestPointOnBounds(point), m_dir = (c.transform.position - point).normalized, m_dodgeable = false, m_blockable = false };
                hit.m_damage.m_fire = damage; hit.SetAttacker(player);
                c.Damage(hit);
            }
        }

        // ------------------------------------------------------------------ movement
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.ApplyStatusEffectSpeedMods))]
        private static class ColdWalker
        {
            private static void Postfix(SEMan __instance, ref float speed)
            {
                if (!(__instance.m_character is Player player) || !Local(player)) return;
                var def = Find(player, "ColdWalker"); if (def == null) return;
                var biome = player.GetCurrentBiome();
                if (biome == Heightmap.Biome.Mountain || biome == Heightmap.Biome.DeepNorth) speed *= 1 + def.Get("Value", 10) / 100f;
            }
        }
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.ApplyStatusEffectJumpMods))]
        private static class HighJump
        {
            private static void Postfix(SEMan __instance, ref Vector3 jump)
            {
                if (!(__instance.m_character is Player player) || !Local(player)) return;
                var def = Find(player, "HighJump"); if (def != null) jump.y *= 1 + def.Get("Value", 25) / 100f;
            }
        }
        // Molten core: no heat damage from lava or the hot water around it.
        [HarmonyPatch(typeof(Character), "UpdateHeatDamage")]
        private static class LavaWalker
        {
            private static bool Prefix(Character __instance) => !(__instance is Player player && Local(player) && Find(player, "LavaWalker") != null);
        }

        // ------------------------------------------------------------------ network: kills, roots and DoTs reach the right machine
        internal static class Network
        {
            const string DotRpc = "Overhaul_CatalystDot", RootRpc = "Overhaul_CatalystRoot", KillRpc = "Overhaul_CatalystKill";
            internal static void Dot(Character target, DotType type, float baseDamage, ZDOID attacker)
            {
                if (target.m_nview.IsOwner()) Dots.Apply(target, type, baseDamage, attacker);
                else target.m_nview.InvokeRPC(DotRpc, (int)type, baseDamage, attacker);
            }
            internal static void Entangle(Character target, float seconds)
            {
                if (target.m_nview.IsOwner()) RootEffect.Apply(target, seconds);
                else target.m_nview.InvokeRPC(RootRpc, seconds);
            }
            [HarmonyPatch(typeof(Character), "Awake")]
            private static class Register
            {
                private static void Postfix(Character __instance)
                {
                    var view = __instance.m_nview; if (!view || !view.IsValid()) return;
                    view.Register<int, float, ZDOID>(DotRpc, (sender, type, damage, attacker) => { if (view.IsOwner()) Dots.Apply(__instance, (DotType)type, damage, attacker); });
                    view.Register<float>(RootRpc, (sender, seconds) => { if (view.IsOwner()) RootEffect.Apply(__instance, seconds); });
                    if (__instance is Player) view.Register<Vector3>(KillRpc, (sender, position) => { if (__instance == Player.m_localPlayer) Killed(position); });
                }
            }
            // On the creature's owner: a kill by a player reaches that player.
            [HarmonyPatch(typeof(Character), "RPC_Damage")]
            private static class Kill
            {
                private static void Prefix(Character __instance, out bool __state) => __state = __instance.IsDead() || __instance.GetHealth() <= 0;
                private static void Postfix(Character __instance, HitData hit, bool __state)
                {
                    if (__state || __instance.IsPlayer() || __instance.GetHealth() > 0 || !(hit?.GetAttacker() is Player player)) return;
                    if (player == Player.m_localPlayer) Killed(__instance.GetCenterPoint());
                    else player.m_nview.InvokeRPC(KillRpc, __instance.GetCenterPoint());
                }
            }
        }
    }
}
