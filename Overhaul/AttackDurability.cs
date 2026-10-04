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
            internal bool HitResource;
            internal float Charged;

            internal void Charge()
            {
                float due = Pending * (HitCreature ? 1f : HitResource ? 0.5f : 0f);
                float cost = due - Charged;
                if (cost <= 0f) return;
                Charged = due;
                if(Player && Player.m_nview && Player.m_nview.IsValid())
                {
                    Weapon.m_customData.TryGetValue(Persistence.GameEquipmentWear.Identity,out string identity);
                    if(Persistence.InventoryMoveGame.Wear(Player.GetZDOID(),identity,cost))return;
                    if(Player==global::Player.m_localPlayer && Persistence.PlayerSessionGame.Managed)return;
                }
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
        // until a creature or harvest resource is touched. Mixed hits pay at most the full debit.
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

        [HarmonyPatch]
        internal static class ResourceHit
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var type in new[] { typeof(TreeBase), typeof(TreeLog), typeof(Destructible), typeof(MineRock), typeof(MineRock5) })
                    yield return AccessTools.Method(type, "Damage");
            }
            private static void Prefix(object __instance, HitData hit)
            {
                if (Current == null || hit == null) return;
                bool tree = __instance is TreeBase || __instance is TreeLog ||
                    (__instance is Destructible d && d.GetDestructibleType() == DestructibleType.Tree);
                bool harvest = tree
                    ? (hit.m_skill == Skills.SkillType.Axes || hit.m_skill == Skills.SkillType.WoodCutting) && hit.m_damage.m_chop > 0f
                    : (__instance is MineRock || __instance is MineRock5 ||
                        (__instance is Destructible resource && !resource.GetComponent<RandomFlyingBird>())) &&
                        hit.m_skill == Skills.SkillType.Pickaxes && hit.m_damage.m_pickaxe > 0f;
                if (!harvest) return;
                Current.HitResource = true;
                Current.Charge();
            }
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
                if (Current == null || !__instance.GetComponent<RandomFlyingBird>()) return;
                Current.HitCreature = true;
                Current.Charge();
            }
        }
    }
}
