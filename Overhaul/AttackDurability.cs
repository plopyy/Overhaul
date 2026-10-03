using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Overhaul
{
    internal static class AttackDurability
    {
        internal sealed class Attempt
        {
            internal Player Player;
            internal ItemDrop.ItemData Weapon;
            internal float Pending;
            internal bool HitCreature;

            internal void Charge()
            {
                if (!HitCreature || Pending <= 0f) return;
                float cost = Pending;
                Pending = 0f;
                // A projectile can outlive its owner or the item being dropped/destroyed.
                if (!Player || !Player.GetInventory().ContainsItem(Weapon)) return;
                float reduction = Leveling.LevelingEffects.Passive(Player, "artisan") ? 1f
                    : Mathf.Clamp01(Leveling.LevelingEffects.Bonus(Player, "durability"));
                Weapon.m_durability = Mathf.Max(0f, Weapon.m_durability - cost * (1f - reduction));
            }
        }

        private static readonly ConditionalWeakTable<Attack, Attempt> Attacks = new ConditionalWeakTable<Attack, Attempt>();
        private static readonly ConditionalWeakTable<object, Attempt> Projectiles = new ConditionalWeakTable<object, Attempt>();
        [ThreadStatic] internal static Attempt Current;

        private static Attempt Get(Attack attack)
        {
            if (!(attack.m_character is Player player) || attack.m_weapon == null) return null;
            return Attacks.GetValue(attack, _ => new Attempt { Player = player, Weapon = attack.m_weapon });
        }

        // Receives exactly the native debit (including world modifiers). No write occurs
        // until a creature is touched. All projectiles in one attack share this debit.
        internal static void Debit(ItemDrop.ItemData item, float value, Attack attack)
        {
            Attempt attempt = Get(attack);
            if (attempt == null || value >= item.m_durability)
            {
                item.m_durability = value;
                return;
            }
            attempt.Pending += item.m_durability - value;
            attempt.Charge();
        }

        [HarmonyPatch]
        internal static class NativeDebit
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (string name in new[] { "ProjectileAttackTriggered", "DoNonAttack", "DoAreaAttack", "DoMeleeAttack" })
                    yield return AccessTools.Method(typeof(Attack), name);
            }
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var field = AccessTools.Field(typeof(ItemDrop.ItemData), "m_durability");
                int replaced = 0;
                foreach (var instruction in instructions)
                {
                    if (instruction.opcode == OpCodes.Stfld && Equals(instruction.operand, field))
                    {
                        // Retain branch labels and exception markers at the original instruction.
                        instruction.opcode = OpCodes.Ldarg_0;
                        instruction.operand = null;
                        yield return instruction;
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(AttackDurability), nameof(Debit)));
                        replaced++;
                    }
                    else yield return instruction;
                }
                if (replaced != 1) throw new InvalidOperationException("Overhaul: expected one native attack durability debit, found " + replaced);
            }
        }

        [HarmonyPatch]
        internal static class AttackScope
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (string name in new[] { "OnAttackTrigger", "FireProjectileBurst", "ProjectileAttackTriggered", "DoNonAttack", "DoAreaAttack", "DoMeleeAttack" })
                    yield return AccessTools.Method(typeof(Attack), name);
            }
            private static void Prefix(Attack __instance, MethodBase __originalMethod, out Attempt __state)
            {
                __state = Current;
                if (__originalMethod.Name == "OnAttackTrigger") Attacks.Remove(__instance);
                Current = Get(__instance);
            }
            private static void Finalizer(Attempt __state) => Current = __state;
        }

        [HarmonyPatch]
        internal static class ProjectileSetup
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(Projectile), "Setup");
                yield return AccessTools.Method(typeof(Aoe), "Setup");
            }
            private static void Prefix(object __instance, Character owner)
            {
                Projectiles.Remove(__instance);
                if (Current != null && owner == Current.Player) Projectiles.Add(__instance, Current);
            }
        }

        [HarmonyPatch]
        internal static class ProjectileScope
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (string name in new[] { "OnHit", "DoAOE", "SpawnOnHit" })
                    yield return AccessTools.Method(typeof(Projectile), name);
                yield return AccessTools.Method(typeof(Aoe), "OnHit");
                yield return AccessTools.Method(typeof(Aoe), "CustomFixedUpdate");
            }
            private static void Prefix(object __instance, out Attempt __state)
            {
                __state = Current;
                Projectiles.TryGetValue(__instance, out var attempt);
                Current = attempt;
            }
            private static void Finalizer(Attempt __state) => Current = __state;
        }

        [HarmonyPatch(typeof(Character), "Damage")]
        internal static class CreatureHit
        {
            private static void Prefix(Character __instance)
            {
                if (Current == null || __instance.IsPlayer()) return;
                Current.HitCreature = true;
                Current.Charge();
            }
        }

        [HarmonyPatch(typeof(Destructible), "Damage")]
        internal static class BirdHit
        {
            private static void Prefix(Destructible __instance)
            {
                if (Current == null || !__instance.GetComponent<Bird>()) return;
                Current.HitCreature = true;
                Current.Charge();
            }
        }
    }
}
