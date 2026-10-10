using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Effects
{
    // Where each effect of Effects.cfg acts on the game. Percent effects are written as percents (10 = +10 %).
    internal static class EffectPatches
    {
        private static float Pct(Character character, string id) => Effects.Get(character, id) / 100f;
        private static bool Local(Character character) => character && character == Player.m_localPlayer;

        // --- Health, eitr, food ---

        [HarmonyPatch(typeof(Player), nameof(Player.GetTotalFoodValue))]
        private static class MaxPools
        {
            private static void Postfix(Player __instance, ref float hp, ref float eitr)
            {
                hp += Effects.Get(__instance, "HealthMax");
                eitr += Effects.Get(__instance, "EitrMax");
            }
        }

        [HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyHealthRegen))]
        private static class HealthRegen { private static void Postfix(SEMan __instance, ref float regenMultiplier) => regenMultiplier *= Mathf.Max(0, 1 + Pct(__instance.m_character, "HealthRegen")); }

        [HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyEitrRegen))]
        private static class EitrRegen { private static void Postfix(SEMan __instance, ref float eitrMultiplier) => eitrMultiplier *= Mathf.Max(0, 1 + Pct(__instance.m_character, "EitrRegen")); }

        // Flat change of each eitr cost (negative = cheaper), never below 0.
        [HarmonyPatch(typeof(Player), nameof(Player.UseEitr))]
        private static class EitrCost { private static void Prefix(Player __instance, ref float v) { if (v > 0) v = Mathf.Max(0, v + Effects.Get(__instance, "EitrCost")); } }

        // Food burns slower (or faster): the time each food lost during the update is divided by 1 + bonus.
        [HarmonyPatch(typeof(Player), "UpdateFood")]
        private static class FoodDuration
        {
            private static void Prefix(Player __instance, out float[] __state)
            {
                __state = null;
                if (Effects.Get(__instance, "FoodDuration") == 0) return;
                __state = new float[__instance.m_foods.Count];
                for (int i = 0; i < __state.Length; i++) __state[i] = __instance.m_foods[i].m_time;
            }
            private static void Postfix(Player __instance, float[] __state)
            {
                if (__state == null || __state.Length != __instance.m_foods.Count) return;
                float factor = Mathf.Max(.01f, 1 + Pct(__instance, "FoodDuration"));
                for (int i = 0; i < __state.Length; i++)
                {
                    var food = __instance.m_foods[i];
                    if (food.m_time < __state[i]) food.m_time = __state[i] - (__state[i] - food.m_time) / factor;
                }
            }
        }

        // --- Damage dealt: applied by the attacker before the hit is sent to the target ---

        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class Dealt
        {
            private static void Prefix(Character __instance, HitData hit)
            {
                if (hit == null || !(hit.GetAttacker() is Player player) || !Local(player) || __instance == player) return;
                if (hit.GetTotalDamage() <= 0) return;
                float fire = Effects.Get(player, "FireDmg"), frost = Effects.Get(player, "FrostDmg"), lightning = Effects.Get(player, "LightningDmg"),
                    poison = Effects.Get(player, "PoisonDmg"), spirit = Effects.Get(player, "SpiritDmg");
                hit.m_damage.m_fire += fire; hit.m_damage.m_frost += frost; hit.m_damage.m_lightning += lightning;
                hit.m_damage.m_poison += poison; hit.m_damage.m_spirit += spirit;
                float physical = Mathf.Max(0, 1 + Pct(player, "PhysicDmg"));
                hit.m_damage.m_blunt *= physical; hit.m_damage.m_slash *= physical; hit.m_damage.m_pierce *= physical;
                // -1 plays every effect variant: elemental damage (also from the weapon's own enchantments) needs the default one.
                var d = hit.m_damage;
                if (hit.m_variant < 0 && d.m_fire + d.m_frost + d.m_lightning + d.m_poison + d.m_spirit > 0) hit.m_variant = 0;
                hit.m_backstabBonus *= 1 + Pct(player, "BackstabBonus");
                hit.m_pushForce *= Mathf.Max(0, 1 + Pct(player, "Knockback"));
                if (Random.value < Pct(player, "CritChance")) Critical.Mark(hit);
                float steal = Pct(player, "LifeSteal");
                if (steal > 0) player.Heal(hit.GetTotalDamage() * steal, true);
            }
        }

        // CritChance triggers Valheim's own critical hit: on the target's machine, RPC_Damage doubles the damage and
        // plays the target's critical effects when the creature is staggering; a critical hit takes that path too
        // (players never take critical hits, like in vanilla). The attacker's roll travels in an unused bit of the
        // HitData flags (the leveling crits use another one).
        internal static class Critical
        {
            private const uint Flag = 0x40000000u;
            private sealed class Marked { }
            private static readonly ConditionalWeakTable<HitData, Marked> Hits = new ConditionalWeakTable<HitData, Marked>();
            internal static void Mark(HitData hit) { if (hit != null) Hits.GetValue(hit, _ => new Marked()); }
            private static bool Is(HitData hit) => hit != null && Hits.TryGetValue(hit, out _);

            private static bool StaggeringOrCritical(Character target, HitData hit) => target.IsStaggering() || Is(hit);

            [HarmonyPatch(typeof(Character), "RPC_Damage")]
            private static class Native
            {
                private static readonly System.Reflection.MethodInfo Target = AccessTools.Method(typeof(Character), "RPC_Damage");
                private static readonly System.Reflection.MethodInfo Staggering = AccessTools.Method(typeof(Character), nameof(Character.IsStaggering));
                private static readonly System.Reflection.MethodInfo Modifier = AccessTools.Method(typeof(HitData), nameof(HitData.ApplyModifier), new[] { typeof(float) });
                private static readonly System.Reflection.FieldInfo CritEffects = AccessTools.Field(typeof(Character), nameof(Character.m_critHitEffects));

                // When the game no longer has the native critical hit, the patch is skipped (missing members) or leaves
                // RPC_Damage untouched (missing test), with a clear warning: CritChance then does nothing.
                private static bool Prepare()
                {
                    string missing = Target == null ? "Character.RPC_Damage" : Staggering == null ? "Character.IsStaggering" : Modifier == null ? "HitData.ApplyModifier"
                        : CritEffects == null ? "Character.m_critHitEffects" : null;
                    if (missing != null) Disabled(missing);
                    return missing == null;
                }

                private static void Disabled(string missing) => Utility.Log.LogWarning("Native critical hit not found (" + missing + "): the CritChance effect is disabled");

                // The staggering test that leads to the doubled damage and the critical effects.
                private static int Find(List<CodeInstruction> codes)
                {
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (!codes[i].Calls(Staggering)) continue;
                        int end = Mathf.Min(codes.Count, i + 12);
                        for (int j = i + 1; j < end; j++) if (codes[j].Calls(Modifier) || codes[j].LoadsField(CritEffects)) return i;
                    }
                    return -1;
                }

                private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
                {
                    var codes = new List<CodeInstruction>(instructions);
                    int i = Find(codes);
                    if (i < 0) { Disabled("the critical hit test in Character.RPC_Damage"); return codes; }
                    codes.Insert(i, new CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_2));
                    codes[i + 1] = new CodeInstruction(System.Reflection.Emit.OpCodes.Call, AccessTools.Method(typeof(Critical), nameof(StaggeringOrCritical))).MoveLabelsFrom(codes[i + 1]);
                    return codes;
                }
            }

            [HarmonyPatch(typeof(HitData), nameof(HitData.Serialize))]
            private static class Send
            {
                private static void Prefix(ZPackage pkg, out int __state) => __state = pkg.GetPos();
                private static void Postfix(HitData __instance, ZPackage pkg, int __state)
                {
                    if (!Is(__instance)) return;
                    int end = pkg.GetPos(); pkg.SetPos(__state); uint flags = pkg.ReadUInt();
                    pkg.SetPos(__state); pkg.Write(flags | Flag); pkg.SetPos(end);
                }
            }

            [HarmonyPatch(typeof(HitData), nameof(HitData.Deserialize))]
            private static class Receive
            {
                private static void Prefix(HitData __instance, ZPackage pkg)
                {
                    int start = pkg.GetPos(); uint flags = pkg.ReadUInt(); pkg.SetPos(start);
                    Hits.Remove(__instance);
                    if ((flags & Flag) != 0) Mark(__instance);
                }
            }
        }

        // --- Damage taken: applied on the player's own machine ---

        [HarmonyPatch(typeof(Character), "RPC_Damage")]
        private static class Taken
        {
            private static void Prefix(Character __instance, HitData hit)
            {
                if (hit == null || !Local(__instance) || !__instance.m_nview || !__instance.m_nview.IsOwner() || __instance.IsDead()) return;
                Reflect(__instance, hit);
                hit.m_damage.m_fire *= Kept(__instance, "ResistFire");
                hit.m_damage.m_frost *= Kept(__instance, "ResistFrost");
                hit.m_damage.m_lightning *= Kept(__instance, "ResistLightning");
                hit.m_damage.m_poison *= Kept(__instance, "ResistPoison");
                hit.m_damage.m_spirit *= Kept(__instance, "ResistSpirit");
                hit.m_damage.m_blunt *= Kept(__instance, "ResistBlunt");
                hit.m_damage.m_slash *= Kept(__instance, "ResistSlash");
                hit.m_damage.m_pierce *= Kept(__instance, "ResistPierce");
            }

            private static float Kept(Character character, string id) => 1 - Mathf.Clamp01(Pct(character, id));

            // A share of the damage goes back to the attacker. The returned hit has no attacker, so it is never reflected again.
            private static void Reflect(Character player, HitData hit)
            {
                float share = Pct(player, "DamageReflect");
                Character attacker = hit.GetAttacker();
                if (share <= 0 || !attacker || attacker == player || attacker.IsDead() || hit.GetTotalDamage() <= 0) return;
                var back = new HitData { m_damage = hit.m_damage.Clone(), m_point = attacker.GetCenterPoint(), m_dir = (attacker.transform.position - player.transform.position).normalized };
                back.m_damage.Modify(share);
                attacker.Damage(back);
            }
        }

        [HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyTimedBlockBonus))]
        private static class Parry { private static void Postfix(SEMan __instance, ref float timedBlockBonus) => timedBlockBonus *= 1 + Pct(__instance.m_character, "ParryBonus"); }

        [HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyFallDamage))]
        private static class FallDamage { private static void Postfix(SEMan __instance, ref float damage) => damage *= Mathf.Max(0, 1 + Pct(__instance.m_character, "FallDmg")); }

        // --- Movement ---

        [HarmonyPatch(typeof(SEMan), nameof(SEMan.ApplyStatusEffectSpeedMods))]
        private static class MoveSpeed { private static void Postfix(SEMan __instance, ref float speed) => speed *= Mathf.Max(0, 1 + Pct(__instance.m_character, "MoveSpeed")); }

        // Slower fall: the falling speed is capped at a share of a free fall's speed (like the feather cape).
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyWalkVelocity))]
        private static class FallSpeed
        {
            private const float FreeFallSpeed = 20f, MinFallSpeed = 1f;
            private static void Postfix(SEMan __instance, ref Vector3 vel)
            {
                float change = Pct(__instance.m_character, "FallSpeed");
                if (change >= 0 || vel.y >= 0) return;
                float cap = Mathf.Max(MinFallSpeed, FreeFallSpeed * (1 + change));
                if (vel.y < -cap) vel.y = -cap;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.GetBodyArmor))]
        private static class BodyArmor { private static void Postfix(Player __instance, ref float __result) => __result += Effects.Get(__instance, "Armor"); }

        [HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyMaxCarryWeight))]
        private static class CarryWeight { private static void Postfix(SEMan __instance, ref float limit) => limit += Effects.Get(__instance.m_character, "CarryWeight"); }

        // --- Attack speed: melee and bows use AttackSpeed, magic weapons CastSpeed ---

        [HarmonyPatch(typeof(CharacterAnimEvent), nameof(CharacterAnimEvent.CustomFixedUpdate))]
        private static class AnimationSpeed
        {
            // The animator speed the game set last (attack events change it) and the one we set from it, so the
            // factor is applied once instead of compounding every frame.
            private sealed class State { internal float Base = 1, Set = float.NaN; }
            private static readonly ConditionalWeakTable<CharacterAnimEvent, State> States = new ConditionalWeakTable<CharacterAnimEvent, State>();

            private static void Postfix(CharacterAnimEvent __instance)
            {
                if (!(__instance.m_character is Humanoid humanoid) || !Local(humanoid) || !__instance.m_animator) return;
                var state = States.GetOrCreateValue(__instance);
                if (!humanoid.InAttack()) { state.Set = float.NaN; return; }
                float speed = __instance.m_animator.speed;
                if (float.IsNaN(state.Set) || !Mathf.Approximately(speed, state.Set)) state.Base = speed;
                float factor = Mathf.Max(.1f, 1 + Pct(humanoid, Magic(humanoid.GetCurrentWeapon()) ? "CastSpeed" : "AttackSpeed"));
                state.Set = state.Base * factor;
                __instance.m_animator.speed = state.Set;
            }
        }

        private static bool Magic(ItemDrop.ItemData weapon) =>
            weapon != null && (weapon.m_shared.m_skillType == Skills.SkillType.ElementalMagic || weapon.m_shared.m_skillType == Skills.SkillType.BloodMagic);

        // Bows draw (and aim) faster with CastSpeed.
        [HarmonyPatch(typeof(Player), nameof(Player.UpdateAttackBowDraw))]
        private static class BowDraw { private static void Prefix(Player __instance, ref float dt) => dt *= Mathf.Max(.01f, 1 + Pct(__instance, "CastSpeed")); }

        // --- Projectiles ---

        // Extra projectiles for each burst, removed again afterwards (by difference, so other mods changing the count are kept).
        [HarmonyPatch(typeof(Attack), "FireProjectileBurst")]
        private static class ProjectileCount
        {
            private static void Prefix(Attack __instance, out int __state)
            {
                __state = Local(__instance.m_character) && __instance.m_projectiles > 0 ? Mathf.RoundToInt(Effects.Get(__instance.m_character, "ProjectileCount")) : 0;
                __instance.m_projectiles += __state;
            }
            private static void Finalizer(Attack __instance, int __state) => __instance.m_projectiles -= __state;
        }

        // Projectiles without an area of effect explode on impact (the game's own area damage, around the hit point).
        [HarmonyPatch(typeof(Projectile), nameof(Projectile.Setup))]
        private static class Explosive
        {
            private const float Radius = 3f;
            // The weapon carrying ExplosiveProjectile (or a player-wide source) makes its projectiles explode.
            private static void Postfix(Projectile __instance, Character owner, ItemDrop.ItemData item)
            {
                if (Local(owner) && __instance.m_aoe <= 0 && (Rarity.Enchantments.ItemValue(item, "ExplosiveProjectile") >= 1 || Effects.Get(owner, "ExplosiveProjectile") >= 1))
                    __instance.m_aoe = Radius;
            }
        }

        // UnlimitedAmmo: a bow or crossbow carrying it still needs an arrow or bolt (it picks what is fired), but never
        // uses it up. The arrow removal of Attack.UseAmmo goes through Spend.
        [HarmonyPatch(typeof(Attack), "UseAmmo")]
        private static class UnlimitedAmmo
        {
            private static bool Spend(Inventory inventory, ItemDrop.ItemData ammo, int amount, Attack attack) =>
                Rarity.Enchantments.ItemValue(attack.m_weapon, "UnlimitedAmmo") >= 1 || inventory.RemoveItem(ammo, amount);

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var remove = AccessTools.Method(typeof(Inventory), nameof(Inventory.RemoveItem), new[] { typeof(ItemDrop.ItemData), typeof(int) });
                int count = 0;
                foreach (var code in instructions)
                {
                    if (!code.Calls(remove)) { yield return code; continue; }
                    count++;
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_0).MoveLabelsFrom(code).MoveBlocksFrom(code);
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, AccessTools.Method(typeof(UnlimitedAmmo), nameof(Spend)));
                }
                if (count == 0) Utility.Log.LogWarning("Ammo removal not found in Attack.UseAmmo: the UnlimitedAmmo effect is disabled");
            }
        }

        // --- Loot and harvest: rolled by the owner of the creature or object, with the player's published totals ---

        // Each stackable drop of a creature gets one more item with a LootBonus % chance.
        [HarmonyPatch(typeof(CharacterDrop), nameof(CharacterDrop.GenerateDropList))]
        private static class LootBonus
        {
            private static void Postfix(CharacterDrop __instance, List<KeyValuePair<GameObject, int>> __result)
            {
                Player player = __instance.m_character?.m_lastHit?.GetAttacker() as Player;
                if (!player || __instance.m_character.IsTamed()) return;
                float chance = Pct(player, "LootBonus");
                if (chance <= 0) return;
                for (int i = 0; i < __result.Count; i++)
                {
                    var item = __result[i].Key ? __result[i].Key.GetComponent<ItemDrop>() : null;
                    if (item && item.m_itemData.m_shared.m_maxStackSize > 1 && Random.value < chance)
                        __result[i] = new KeyValuePair<GameObject, int>(__result[i].Key, __result[i].Value + 1);
                }
            }
        }

        // Trees, logs, rocks, ore and other destructibles: the player hitting it is remembered while the hit is
        // handled, and each item it drops then is doubled with a HarvestBonus % chance.
        private static Player harvester;

        [HarmonyPatch]
        private static class Harvesting
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(Destructible), "RPC_Damage");
                yield return AccessTools.Method(typeof(TreeBase), "RPC_Damage");
                yield return AccessTools.Method(typeof(TreeLog), "RPC_Damage");
                yield return AccessTools.Method(typeof(MineRock), "RPC_Hit");
                yield return AccessTools.Method(typeof(MineRock5), "RPC_Damage");
            }
            private static void Prefix(HitData hit, out Player __state) { __state = harvester; harvester = hit?.GetAttacker() as Player; }
            private static void Finalizer(Player __state) => harvester = __state;
        }

        [HarmonyPatch(typeof(DropTable), nameof(DropTable.GetDropList), typeof(int))]
        private static class HarvestBonus
        {
            private static void Postfix(List<GameObject> __result)
            {
                if (!harvester || __result == null) return;
                float chance = Pct(harvester, "HarvestBonus");
                if (chance <= 0) return;
                int count = __result.Count;
                for (int i = 0; i < count; i++) if (Random.value < chance) __result.Add(__result[i]);
            }
        }

        // --- Durability: an item carrying Indestructible is kept at full durability ---

        [HarmonyPatch(typeof(Player), "Update")]
        private static class Indestructible
        {
            private static void Postfix(Player __instance)
            {
                if (!Local(__instance)) return;
                foreach (var item in __instance.GetInventory().GetEquippedItems())
                    if (item.m_shared.m_useDurability && item.m_durability < item.GetMaxDurability() && Rarity.Enchantments.ItemValue(item, "Indestructible") >= 1)
                        item.m_durability = item.GetMaxDurability();
            }
        }
    }
}
