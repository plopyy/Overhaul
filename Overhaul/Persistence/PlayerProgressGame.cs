using System;
using System.Globalization;
using HarmonyLib;

namespace Overhaul.Persistence
{
    internal static class PlayerProgressGame
    {
        private static Character actor;
        [HarmonyPatch(typeof(FishingFloat),"FixedUpdate")]
        private static class FishingSimulation
        {
            private static void Prefix(FishingFloat __instance,out Character __state)
            {
                __state=actor;
                if(PlayerFishingCastGame.Enabled && PlayerFishingCastGame.Managed(__instance) && ZNet.instance.IsServer() && __instance.m_nview.IsOwner())actor=__instance.GetOwner();
            }
            private static Exception Finalizer(Character __state,Exception __exception){actor=__state;return __exception;}
        }
        [HarmonyPatch(typeof(Player),nameof(Player.RaiseSkill))]
        private static class Skill
        {
            [HarmonyPriority(Priority.First+300)]
            private static bool Prefix(Player __instance,Skills.SkillType skill,float value)
            {
                if(!actor || actor!=__instance)return true;
                if(skill!=Skills.SkillType.None && value>0 && !float.IsNaN(value) && !float.IsInfinity(value))
                    InventoryMoveGame.Progress(actor.GetZDOID(),state=>PlayerCraftProgressGame.Raise(state,skill,value));
                return false;
            }
        }
        [HarmonyPatch(typeof(Game),nameof(Game.IncrementPlayerStat))]
        private static class Statistics
        {
            [HarmonyPriority(Priority.First+300)]
            private static bool Prefix(PlayerStatType stat,float amount)
            {
                if(!actor)return true;
                if(stat!=PlayerStatType.None && !float.IsNaN(amount) && !float.IsInfinity(amount))
                    InventoryMoveGame.Progress(actor.GetZDOID(),state=>new[]{PlayerCraftProgressGame.Increment(state,"statistics:0:values",((int)stat).ToString(CultureInfo.InvariantCulture),amount)});
                return false;
            }
        }
    }
}
