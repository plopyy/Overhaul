using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.AI
{
    [HarmonyPatch(typeof(BaseAI),"Awake")]
    internal static class MobIntelligenceSpawn
    {
        private static void Postfix(BaseAI __instance){if(__instance.m_character)MobBehaviorConfig.Rule(__instance.m_character);}
    }
    [HarmonyPatch(typeof(MonsterAI),nameof(MonsterAI.UpdateAI))]
    internal static class MobCombatPatch
    {
        internal sealed class Snapshot
        {
            internal float View,Hear,Alert,Circle,Minimum;
            internal bool Circulate,Flying;
            internal ItemDrop.ItemData Weapon;
            internal float WeaponTime;
        }
        private static void Prefix(MonsterAI __instance,out Snapshot __state)
        {
            __state=null;
            if(MobBehavior.States.TryGetValue(__instance,out var old)&&(old.Blocking||old.EvadeUntil!=0)&&(!MobBehavior.Active(__instance)||MobBehavior.Now>=Math.Max(old.BlockUntil,old.EvadeUntil)))MobBehavior.ReleaseBlock(__instance.m_character as Humanoid,old);
            if(!MobBehavior.Active(__instance))return;var rule=MobBehaviorConfig.Rule(__instance.m_character);if(rule.Vanilla)return;
            var ai=__instance;
            __state=new Snapshot{View=ai.m_viewRange,Hear=ai.m_hearRange,Alert=ai.m_alertRange,Circle=ai.m_circleTargetInterval,Minimum=ai.m_minAttackInterval,Circulate=ai.m_circulateWhileCharging,Flying=ai.m_circulateWhileChargingFlying};
            if(rule.Intelligence==Intelligence.Dumb){ai.m_viewRange*=.6f;ai.m_hearRange*=.6f;ai.m_alertRange*=.6f;if(ai.m_character is Humanoid body)body.m_blocking=false;}
            var state=MobBehavior.States.GetOrCreateValue(ai);
            if(rule.Group==GroupBehavior.Pack)MobBehavior.RefreshPack(ai,state);
            if(rule.Intelligence==Intelligence.Dumb||rule.Aggression!=Aggression.Normal||rule.Group==GroupBehavior.Pack&&state.Pack.Count>=3){ai.m_circleTargetInterval=0;ai.m_circulateWhileCharging=false;ai.m_circulateWhileChargingFlying=false;}
            if(MobSpecies.Mosquito(ai)&&rule.Intelligence!=Intelligence.Dumb){ai.m_circulateWhileCharging=__state.Circulate;ai.m_circulateWhileChargingFlying=__state.Flying;}
            if(rule.Intelligence==Intelligence.Smart&&ai.m_targetCreature&&ai.m_targetCreature.m_nview&&ai.m_targetCreature.m_nview.IsValid())
            {
                double miss=(ai.m_targetCreature.m_nview.GetZDO().GetLong(MobBehavior.MissKey,0)/1000d);
                if(miss>state.LastMiss&&miss<=MobBehavior.Now&&MobBehavior.Now-miss<1.2&&ai.CanSeeTarget(ai.m_targetCreature))
                {
                    ai.m_minAttackInterval=0;ai.m_circleTargetInterval=0;ai.m_circulateWhileCharging=false;ai.m_circulateWhileChargingFlying=false;
                    var weapon=(ai.m_character as Humanoid)?.GetCurrentWeapon();
                    if(weapon!=null){__state.Weapon=weapon;__state.WeaponTime=weapon.m_lastAttackTime;weapon.m_lastAttackTime=-1000000;}
                }
            }
        }
        private static void Finalizer(MonsterAI __instance,Snapshot __state)
        {
            if(__state==null)return;var ai=__instance;ai.m_viewRange=__state.View;ai.m_hearRange=__state.Hear;ai.m_alertRange=__state.Alert;ai.m_circleTargetInterval=__state.Circle;ai.m_minAttackInterval=__state.Minimum;ai.m_circulateWhileCharging=__state.Circulate;ai.m_circulateWhileChargingFlying=__state.Flying;
            if(__state.Weapon!=null&&__state.Weapon.m_lastAttackTime==-1000000)__state.Weapon.m_lastAttackTime=__state.WeaponTime;
        }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,ILGenerator generator)
        {
            var codes=new List<CodeInstruction>(instructions);int index=codes.FindIndex(c=>c.LoadsField(AccessTools.Field(typeof(MonsterAI),nameof(MonsterAI.m_circleTargetInterval))));
            if(index<1||codes[index-1].opcode!=OpCodes.Ldarg_0)throw new InvalidOperationException("Monster combat entry changed");
            index--;var resume=generator.DefineLabel();var entry=new CodeInstruction(OpCodes.Ldarg_0);entry.labels.AddRange(codes[index].labels);codes[index].labels.Clear();codes[index].labels.Add(resume);
            codes.InsertRange(index,new[]{entry,new CodeInstruction(OpCodes.Ldarg_1),new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(MobBehavior),nameof(MobBehavior.Combat))),new CodeInstruction(OpCodes.Brfalse,resume),new CodeInstruction(OpCodes.Ldc_I4_1),new CodeInstruction(OpCodes.Ret)});
            return codes;
        }
    }    [HarmonyPatch(typeof(MonsterAI),nameof(MonsterAI.DoAttack))]
    internal static class MobAttackDecision
    {
        private static bool Prefix(MonsterAI __instance,Character target,bool isFriend,ref bool __result)
        {
            if(!MobBehavior.Active(__instance))return true;
            if(MobBehaviorConfig.Rule(__instance.m_character).Intelligence==Intelligence.Dumb&&!isFriend&&DumbFollowup.Starting!=__instance&&!MobBehavior.CanRetaliate(__instance,target)){__result=false;return false;}
            if(isFriend||!target)return true;var rule=MobBehaviorConfig.Rule(__instance.m_character);if(rule.Vanilla)return true;
            var state=MobBehavior.States.GetOrCreateValue(__instance);bool allowed=MobBehavior.Now>=state.ReactUntil&&MobBehavior.Now>=state.BlockUntil&&MobBehavior.Now>=state.EvadeUntil;
            if(rule.Group==GroupBehavior.Pack)allowed&=MobPack.Allowed(__instance,state);
            if(allowed)return true;__result=false;return false;
        }
        private static void Postfix(MonsterAI __instance,Character target,bool isFriend,bool __result)
        {
            if(!__result||!MobBehavior.Owner(__instance)||MobBehaviorConfig.Rule(__instance.m_character).Vanilla)return;
            if(!isFriend)DumbFollowup.Record(__instance,target);
            var state=MobBehavior.States.GetOrCreateValue(__instance);state.FlankUntil=MobBehavior.Now+2.5;state.RushUntil=0;
            if(__instance.m_targetCreature&&__instance.m_targetCreature.m_nview&&__instance.m_targetCreature.m_nview.IsValid())state.LastMiss=(__instance.m_targetCreature.m_nview.GetZDO().GetLong(MobBehavior.MissKey,0)/1000d);
        }
    }
    [HarmonyPatch(typeof(AnimalAI),nameof(AnimalAI.UpdateAI))]
    internal static class AnimalIntelligence
    {
        private static void Prefix(AnimalAI __instance,out float[] __state)
        {
            __state=null;if(!MobBehavior.Owner(__instance)||MobBehaviorConfig.Rule(__instance.m_character).Intelligence!=Intelligence.Dumb)return;
            if(!MobBehavior.CanRetaliate(__instance,__instance.m_target)){__instance.m_target=null;__instance.m_nview.GetZDO().Set(MobBehavior.RetaliationKey,ZDOID.None);__instance.SetAlerted(false);}
            __state=new[]{__instance.m_viewRange,__instance.m_hearRange};__instance.m_viewRange*=.6f;__instance.m_hearRange*=.6f;
        }
        private static void Finalizer(AnimalAI __instance,float[] __state){if(__state!=null){__instance.m_viewRange=__state[0];__instance.m_hearRange=__state[1];}}
    }
    [HarmonyPatch(typeof(Humanoid),nameof(Humanoid.IsBlocking))]
    internal static class DumbNoBlock
    {
        private static void Postfix(Humanoid __instance,ref bool __result)
        {if(!(__instance is Player)&&MobBehaviorConfig.Rule(__instance).Intelligence==Intelligence.Dumb)__result=false;}
    }
    [HarmonyPatch(typeof(BaseAI),nameof(BaseAI.SetAlerted))]
    internal static class DumbAlert
    {
        private static bool Prefix(BaseAI __instance,bool alert)
        {
            if(!alert||!MobBehavior.Owner(__instance)||MobBehaviorConfig.Rule(__instance.m_character).Intelligence!=Intelligence.Dumb)return true;
            return __instance.m_nview.GetZDO().GetZDOID(MobBehavior.RetaliationKey)!=ZDOID.None;
        }
    }
    [HarmonyPatch(typeof(BaseAI),nameof(BaseAI.FindEnemy))]
    internal static class DumbFindEnemy
    {
        private static void Postfix(BaseAI __instance,ref Character __result)
        {
            var ai=__instance;if(!MobBehavior.Owner(ai)||MobBehaviorConfig.Rule(ai.m_character).Intelligence!=Intelligence.Dumb)return;
            var current=ai is MonsterAI monster?monster.m_targetCreature:ai is AnimalAI animal?animal.m_target:null;
            if(!MobBehavior.CanRetaliate(ai,__result))__result=MobBehavior.CanRetaliate(ai,current)?current:null;
        }
    }
    [HarmonyPatch(typeof(MonsterAI),nameof(MonsterAI.UpdateTarget))]
    internal static class DumbTargets
    {
        private static void Postfix(MonsterAI __instance,ref bool canHearTarget,ref bool canSeeTarget)
        {
            var ai=__instance;if(!MobBehavior.Owner(ai)||MobBehaviorConfig.Rule(ai.m_character).Intelligence!=Intelligence.Dumb)return;
            ai.m_targetStatic=null;
            if(MobBehavior.CanRetaliate(ai,ai.m_targetCreature))return;
            ai.m_targetCreature=null;canHearTarget=canSeeTarget=false;ai.m_nview.GetZDO().Set(MobBehavior.RetaliationKey,ZDOID.None);ai.SetAlerted(false);
            ai.SetTargetInfo(ZDOID.None);
        }
    }
    internal static class MissedAttacks
    {
        internal sealed class Swing { internal Player Player;internal bool Hit; }
        internal static Swing Current;
        [HarmonyPatch(typeof(Attack),"DoMeleeAttack")]
        private static class Melee
        {
            private static void Prefix(Attack __instance,out Swing __state)
            {
                __state=Current;Current=null;var player=__instance.m_character as Player;
                if(player&&player.m_nview&&player.m_nview.IsValid()&&player.m_nview.IsOwner())Current=new Swing{Player=player};
            }
            private static void Postfix(){if(Current!=null&&!Current.Hit)Current.Player.m_nview.GetZDO().Set(MobBehavior.MissKey,(long)(MobBehavior.Now*1000));}
            private static void Finalizer(Swing __state)=>Current=__state;
        }
        [HarmonyPatch(typeof(Character),nameof(Character.Damage))]
        private static class Hit
        {
            private static void Prefix(Character __instance,HitData hit){if(Current!=null&&hit.GetAttacker()==Current.Player&&BaseAI.IsEnemy(Current.Player,__instance))Current.Hit=true;}
        }
    }
}
