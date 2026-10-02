using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Leveling
{
    // Death only: retain native loss rates and XP reset, then restore earned decades.
    // Use permanent skill levels, never temporary equipment/status-effect bonuses.
    [HarmonyPatch(typeof(Skills), nameof(Skills.OnDeath))]
    internal static class SkillDeathMilestones
    {
        private static void Prefix(Skills __instance, out Dictionary<Skills.Skill, float> __state)
        {
            __state = new Dictionary<Skills.Skill, float>();
            foreach (var skill in __instance.GetSkillList())
                __state[skill] = Mathf.Floor(skill.m_level / 10f) * 10f;
        }

        private static void Postfix(Dictionary<Skills.Skill, float> __state)
        {
            foreach (var entry in __state)
                entry.Key.m_level = Mathf.Max(entry.Key.m_level, entry.Value);
        }
    }
}
