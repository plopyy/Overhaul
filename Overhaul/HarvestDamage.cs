using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Overhaul
{
    // Apply at the resource's damage entry point, never to the weapon or Character hit.
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
        }

        private static void Prefix(object __instance, ref HitData hit)
        {
            if (hit == null) return;
            bool tree = __instance is TreeBase || __instance is TreeLog ||
                (__instance is Destructible destructible && destructible.GetDestructibleType() == DestructibleType.Tree);
            if (tree && hit.m_skill == Skills.SkillType.Axes && hit.m_damage.m_chop > 0f)
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
        }
    }
}
