using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace Overhaul.Persistence
{
    internal static class GameStatusRuntime
    {
        private sealed class Pending {internal bool Reset;internal int Level;internal float Skill;internal short Variant;}
        private static readonly Dictionary<Tuple<ZDOID,int>,Pending> pending=new Dictionary<Tuple<ZDOID,int>,Pending>();
        [ThreadStatic] private static int equipment;
        private static bool Presentation(Player player)=>GameStatusGame.Presenting||PlayerPotionGame.Presenting||PlayerEquipmentGame.Presenting||equipment>0||player.m_isLoading;
        internal static void Forget(ZDOID actor){foreach(var key in pending.Keys.Where(k=>k.Item1==actor).ToArray())pending.Remove(key);}
        internal static void Clear()=>pending.Clear();
        private static StatusEffect Add(Player player,int id,bool reset,int level,float skill,short variant)
        {
            if(id==0||!player.m_nview||!player.m_nview.IsValid())return null;
            var state=InventoryMoveGame.State(player.GetZDOID());if(state==null||GameDeathProgress.IsDead(state))return null;
            if(float.IsNaN(skill)||float.IsInfinity(skill)||level<0)return null;
            var saved=state.Rows.Where(r=>(r.Table=="status"||r.Table=="status_data")&&Convert.ToInt32(r.Values[0])==id).ToArray();
            var existing=saved.Length==0?null:GameStatusCodec.Restore(saved,player);
            if(existing&&!reset)return existing;
            var key=Tuple.Create(player.GetZDOID(),id);
            if(pending.TryGetValue(key,out var waiting))
            {waiting.Reset|=reset;waiting.Level=level;waiting.Skill=skill;waiting.Variant=variant;return existing;}
            var request=new Pending{Reset=reset,Level=level,Skill=skill,Variant=variant};pending.Add(key,request);
            if(!InventoryMoveGame.TimedAction(player,current=>
            {
                pending.Remove(key);
                if(GameDeathProgress.IsDead(current)||!request.Reset&&current.Rows.Any(r=>r.Table=="status"&&Convert.ToInt32(r.Values[0])==id))return null;
                var definition=ObjectDB.instance.GetStatusEffect(id);if(!definition)return null;
                bool allowed=false;GameCombatContext.Run(player,current,null,null,()=>allowed=definition.CanAdd(player));if(!allowed)return null;
                var rows=GameStatusImpact.Prepare(current,player,id,request.Level,request.Skill,request.Variant,ZDOID.None).ToList();
                if(request.Reset)
                {
                    var after=PlayerProgressService.Overlay(current,rows);var header=after.Rows.SingleOrDefault(r=>r.Table=="status"&&Convert.ToInt32(r.Values[0])==id);
                    if(header!=null&&Convert.ToInt16(header.Values[6])!=request.Variant)
                    {var values=header.Values;values[6]=(int)request.Variant;GamePlayerHit.Merge(rows,new[]{new PlayerChange("status",false,values)});}
                }
                if(rows.Count==0)return null;
                return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(Guid.NewGuid().ToString("N"),current.Revision,rows),new Dictionary<long,ObjectRecord>()),()=>{});
            }))pending.Remove(key);
            return existing;
        }
        [HarmonyPatch(typeof(SEMan),nameof(SEMan.AddStatusEffect),new[]{typeof(int),typeof(bool),typeof(int),typeof(float),typeof(short)})]
        private static class AddId
        {
            private static bool Prefix(SEMan __instance,int nameHash,bool resetTime,int itemLevel,float skillLevel,short variant,ref StatusEffect __result,bool __runOriginal)
            {
                if(!__runOriginal)return true;
                if(!(__instance.m_character is Player player)||Presentation(player))return true;
                if(GameCreatureAuthority.Enabled){__result=Add(player,nameHash,resetTime,itemLevel,skillLevel,variant);return false;}
                return !PlayerSessionGame.Managed;
            }
        }
        [HarmonyPatch(typeof(SEMan),nameof(SEMan.AddStatusEffect),new[]{typeof(StatusEffect),typeof(bool),typeof(int),typeof(float),typeof(short)})]
        private static class AddDefinition
        {
            private static bool Prefix(SEMan __instance,StatusEffect statusEffect,bool resetTime,int itemLevel,float skillLevel,short variant,ref StatusEffect __result,bool __runOriginal)
            {
                if(!__runOriginal)return true;
                if(!(__instance.m_character is Player player)||Presentation(player))return true;
                if(GameCreatureAuthority.Enabled){__result=statusEffect?Add(player,statusEffect.NameHash(),resetTime,itemLevel,skillLevel,variant):null;return false;}
                return !PlayerSessionGame.Managed;
            }
        }
        [HarmonyPatch(typeof(SEMan),"RPC_AddStatusEffect")]
        private static class Origin
        {
            private static bool Prefix(SEMan __instance,long sender,int nameHash,bool resetTime,int itemLevel,float skillLevel,int variant)
            {
                if(!(__instance.m_character is Player player))return true;
                if(GameCreatureAuthority.Enabled)
                {if(sender==ZNet.GetUID()&&variant>=short.MinValue&&variant<=short.MaxValue)Add(player,nameHash,resetTime,itemLevel,skillLevel,(short)variant);return false;}
                return !PlayerSessionGame.Managed;
            }
        }
        [HarmonyPatch(typeof(SEMan),nameof(SEMan.RemoveStatusEffect),new[]{typeof(int),typeof(bool)})]
        private static class Remove
        {
            private static bool Prefix(SEMan __instance,int nameHash,ref bool __result)
            {
                if(!(__instance.m_character is Player player)||Presentation(player))return true;
                if(GameCreatureAuthority.Enabled)
                {
                    __result=InventoryMoveGame.TimedAction(player,state=>
                    {
                        if(!state.Rows.Any(r=>r.Table=="status"&&Convert.ToInt32(r.Values[0])==nameHash))return null;
                        return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(Guid.NewGuid().ToString("N"),state.Revision,new[]{new PlayerChange("status",true,nameHash)}),new Dictionary<long,ObjectRecord>()),()=>{});
                    });return false;
                }
                return !PlayerSessionGame.Managed;
            }
        }
        [HarmonyPatch(typeof(Humanoid),"UpdateEquipmentStatusEffects")]
        private static class Equipment
        {
            private static void Prefix(out int __state){__state=equipment;equipment++;}
            private static void Finalizer(int __state)=>equipment=__state;
        }
    }
}
