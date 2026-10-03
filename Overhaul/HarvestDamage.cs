using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Overhaul
{
    // Apply at the target's damage entry point, never to the weapon's shared stats.
    // Clone because one attack can reuse its HitData across several targets.
    [HarmonyPatch]
    internal static class HarvestDamage
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(TreeBase), "Damage");
            yield return AccessTools.Method(typeof(TreeLog), "Damage");
            yield return AccessTools.Method(typeof(Destructible), "Damage");
            yield return AccessTools.Method(typeof(MineRock), "Damage");
            yield return AccessTools.Method(typeof(MineRock5), "Damage");
            yield return AccessTools.Method(typeof(Character), "Damage");
        }

        private static void Prefix(object __instance, ref HitData hit)
        {
            if (hit == null) return;
            bool tree = __instance is TreeBase || __instance is TreeLog ||
                (__instance is Destructible destructible && destructible.GetDestructibleType() == DestructibleType.Tree);
            // Native axes report their special WoodCutting skill when the target is a tree.
            if (tree && (hit.m_skill == Skills.SkillType.Axes || hit.m_skill == Skills.SkillType.WoodCutting) && hit.m_damage.m_chop > 0f)
            {
                hit = hit.Clone();
                hit.m_damage.m_chop *= 2f;
            }
            else if ((__instance is MineRock || __instance is MineRock5) &&
                hit.m_skill == Skills.SkillType.Pickaxes && hit.m_damage.m_pickaxe > 0f)
            {
                hit = hit.Clone();
                hit.m_damage.m_pickaxe *= 2f;
            }
            else if (__instance is Character character && !character.IsPlayer() &&
                hit.m_skill == Skills.SkillType.Pickaxes && IsMiningCreature(character))
            {
                // Golems are the requested combat exception. Scale the entire pickaxe
                // hit here so its piercing component is doubled as well as mining damage.
                hit = hit.Clone();
                hit.m_damage.Modify(2f);
            }
        }

        private static bool IsMiningCreature(Character character)
        {
            if (Utils.GetPrefabName(character.gameObject) == "StoneGolem") return true;
            var modifier = character.m_damageModifiers.m_pickaxe;
            return modifier == HitData.DamageModifier.Weak || modifier == HitData.DamageModifier.VeryWeak ||
                modifier == HitData.DamageModifier.SlightlyWeak;
        }
    }
}
