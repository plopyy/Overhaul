using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static partial class GameCombatContext
    {
        [HarmonyPatch(typeof(Player),nameof(Player.IsEncumbered))]
        private static class MovementWeight
        {
            private static bool Prefix(Player __instance,ref bool __result)
            {
                if(!Matches(__instance))return true;
                if(!Current.InventoryWeight.HasValue)Current.InventoryWeight=Current.State.Rows.Where(r=>r.Table=="inventory").Sum(row=>PlayerInventoryView.ReadItem(row.Values,null,true).GetWeight());
                __result=Current.InventoryWeight.Value>__instance.GetMaxCarryWeight();return false;
            }
        }
        [HarmonyPatch(typeof(SEMan),nameof(SEMan.ModifyMaxCarryWeight))]
        private static class MovementCarry
        {
            private static bool Prefix(SEMan __instance,float baseLimit,ref float limit)
            {if(!Matches(__instance.m_character))return true;foreach(var effect in Current.Effects)effect.ModifyMaxCarryWeight(baseLimit,ref limit);return false;}
        }
        // Native movement often reads Skills directly rather than Player's wrapper.
        [HarmonyPatch(typeof(Skills),nameof(Skills.GetSkillLevel))]
        private static class NativeSkill
        {
            private static bool Prefix(Skills __instance,Skills.SkillType skillType,ref float __result)
            {
                if(Current==null || Current.Player.m_skills!=__instance)return true;
                __result=skillType==Skills.SkillType.None?0:GameAttackResources.SkillLevel(Current.State,skillType,Current.Effects);return false;
            }
        }
        [HarmonyPatch]
        private static class MovementEquipment
        {
            private static readonly Dictionary<string,Func<ItemDrop.ItemData.SharedData,float>> fields=new Dictionary<string,Func<ItemDrop.ItemData.SharedData,float>>
            {
                {nameof(Player.GetEquipmentMovementModifier),s=>s.m_movementModifier},
                {nameof(Player.GetEquipmentJumpStaminaModifier),s=>s.m_jumpStaminaModifier},
                {nameof(Player.GetEquipmentDodgeStaminaModifier),s=>s.m_dodgeStaminaModifier},
                {nameof(Player.GetEquipmentSwimStaminaModifier),s=>s.m_swimStaminaModifier},
                {nameof(Player.GetEquipmentSneakStaminaModifier),s=>s.m_sneakStaminaModifier},
                {nameof(Player.GetEquipmentRunStaminaModifier),s=>s.m_runStaminaModifier}
            };
            private static IEnumerable<MethodBase> TargetMethods()=>fields.Keys.Select(name=>(MethodBase)AccessTools.Method(typeof(Player),name));
            private static bool Prefix(Player __instance,MethodBase __originalMethod,ref float __result)
            {if(!Matches(__instance))return true;var read=fields[__originalMethod.Name];__result=Current.Equipment.Sum(item=>read(item.m_shared));return false;}
        }
        [HarmonyPatch(typeof(SEMan),nameof(SEMan.ApplyStatusEffectSpeedMods))]
        private static class MovementSpeed
        {
            private static bool Prefix(SEMan __instance,ref float speed,Vector3 dir)
            {if(!Matches(__instance.m_character))return true;float original=speed;foreach(var effect in Current.Effects)effect.ModifySpeed(original,ref speed,__instance.m_character,dir);return false;}
        }
        [HarmonyPatch(typeof(SEMan),nameof(SEMan.ApplyStatusEffectJumpMods))]
        private static class MovementJump
        {
            private static bool Prefix(SEMan __instance,ref Vector3 jump)
            {if(!Matches(__instance.m_character))return true;var original=jump;foreach(var effect in Current.Effects)effect.ModifyJump(original,ref jump);return false;}
        }
        [HarmonyPatch(typeof(SEMan),nameof(SEMan.ModifyWalkVelocity))]
        private static class MovementFallSpeed
        {
            private static bool Prefix(SEMan __instance,ref Vector3 vel)
            {if(!Matches(__instance.m_character))return true;foreach(var effect in Current.Effects)effect.ModifyWalkVelocity(ref vel);return false;}
        }
        [HarmonyPatch(typeof(SEMan),nameof(SEMan.ModifyFallDamage))]
        private static class MovementFallDamage
        {
            private static bool Prefix(SEMan __instance,float baseDamage,ref float damage)
            {if(!Matches(__instance.m_character))return true;foreach(var effect in Current.Effects)effect.ModifyFallDamage(baseDamage,ref damage);return false;}
        }
        [HarmonyPatch(typeof(SEMan),nameof(SEMan.ModifyNoise))]
        private static class MovementNoise
        {
            private static bool Prefix(SEMan __instance,float baseNoise,ref float noise)
            {if(!Matches(__instance.m_character))return true;foreach(var effect in Current.Effects)effect.ModifyNoise(baseNoise,ref noise);return false;}
        }
        [HarmonyPatch(typeof(Character),nameof(Character.AddNoise))]
        private static class NoiseSource
        {
            private static bool Prefix(Character __instance,float range)
            {
                if(!(__instance is Player player)||!GameMovementRuntime.Managed(player)||Matches(player))return true;
                var state=InventoryMoveGame.State(player.GetZDOID());
                GameCombatContext.Run(player,state,null,null,()=>player.AddNoise(range));return false;
            }
        }
        [HarmonyPatch(typeof(Character),nameof(Character.GetNoiseRange))]
        private static class NoiseRange
        {
            private static bool Prefix(Character __instance,ref float __result)
            {if(!(__instance is Player player)||!GameMovementRuntime.Managed(player))return true;__result=player.m_noiseRange;return false;}
        }
        [HarmonyPatch(typeof(SEMan),nameof(SEMan.ModifyRunStaminaDrain))]
        private static class MovementRunDrain
        {
            private static bool Prefix(SEMan __instance,float baseDrain,ref float drain,Vector3 dir,bool minZero)
            {if(!Matches(__instance.m_character))return true;foreach(var effect in Current.Effects)effect.ModifyRunStaminaDrain(baseDrain,ref drain,dir);if(minZero)drain=Mathf.Max(0,drain);return false;}
        }
        [HarmonyPatch]
        private static class MovementStamina
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {foreach(string name in new[]{"ModifyJumpStaminaUsage","ModifyDodgeStaminaUsage","ModifySwimStaminaUsage","ModifySneakStaminaUsage"})yield return AccessTools.Method(typeof(SEMan),name);}
            private static bool Prefix(SEMan __instance,float baseStaminaUse,ref float staminaUse,bool minZero,MethodBase __originalMethod)
            {
                if(!Matches(__instance.m_character))return true;
                foreach(var effect in Current.Effects)switch(__originalMethod.Name)
                {
                    case "ModifyJumpStaminaUsage":effect.ModifyJumpStaminaUsage(baseStaminaUse,ref staminaUse);break;
                    case "ModifyDodgeStaminaUsage":effect.ModifyDodgeStaminaUsage(baseStaminaUse,ref staminaUse);break;
                    case "ModifySwimStaminaUsage":effect.ModifySwimStaminaUsage(baseStaminaUse,ref staminaUse);break;
                    case "ModifySneakStaminaUsage":effect.ModifySneakStaminaUsage(baseStaminaUse,ref staminaUse);break;
                }
                if(minZero)staminaUse=Mathf.Max(0,staminaUse);return false;
            }
        }
    }
}
