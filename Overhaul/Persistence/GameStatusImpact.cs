using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameStatusImpact
    {
        private sealed class Frame {internal PlayerSnapshot State;internal readonly List<PlayerChange> Changes=new List<PlayerChange>();}
        [ThreadStatic] private static Frame current;
        internal static PlayerChange[] Prepare(PlayerSnapshot state,Player player,int id,int level,float skill,short variant,ZDOID attacker,float frost=0)
        {
            if(id==0||PlayerResources.Read(state,"health")<=0)return Array.Empty<PlayerChange>();
            var definition=ObjectDB.instance.GetStatusEffect(id);if(!definition)return Array.Empty<PlayerChange>();
            var type=definition.GetType();
            if(type!=typeof(StatusEffect)&&type!=typeof(SE_Stats)&&type!=typeof(SE_Shield)&&type!=typeof(SE_React)&&type!=typeof(SE_Frost)&&type!=typeof(SE_Burning)&&type!=typeof(SE_Poison)&&type!=typeof(SE_Wet)&&type!=typeof(SE_Smoke))
                throw new InvalidOperationException("Server impact status is not implemented: "+definition.name);
            var rows=state.Rows.Where(r=>(r.Table=="status"||r.Table=="status_data")&&Convert.ToInt32(r.Values[0])==id).ToArray();
            bool existing=rows.Any(r=>r.Table=="status");var effect=existing?GameStatusCodec.Restore(rows,player):definition.Clone();
            effect.m_character=player;effect.m_startEffectInstances=null;
            if(!existing){effect.m_time=0;effect.m_hitVariant=variant;}
            var frame=new Frame{State=state};var previous=current;current=frame;
            try
            {
                GameCombatContext.Run(player,state,null,null,()=>
                {
                    if(!existing)effect.Setup(player);
                    if(frost>0&&effect is SE_Frost frozen)frozen.AddDamage(frost);
                    else {if(existing)effect.ResetTime();effect.SetLevel(level,skill);}
                });
                frame.Changes.AddRange(GameStatusCodec.Delta(frame.State,effect,attacker));return frame.Changes.ToArray();
            }
            finally{current=previous;}
        }
        [HarmonyPatch(typeof(StatusEffect),nameof(StatusEffect.Setup))]
        private static class Setup
        {private static bool Prefix(StatusEffect __instance,Character character){if(current==null)return true;__instance.m_character=character;return false;}}
        [HarmonyPatch(typeof(StatusEffect),"TriggerStartEffects")]
        private static class Visuals
        {private static bool Prefix()=>current==null;}
        [HarmonyPatch(typeof(Terminal),nameof(Terminal.Log))]
        private static class Logging
        {private static bool Prefix()=>current==null;}
        [HarmonyPatch(typeof(SE_Stats),nameof(SE_Stats.StartupEffects))]
        private static class Startup
        {
            private static bool Prefix(SE_Stats __instance)
            {
                if(current==null)return true;
                var changes=PlayerResources.Restore(current.State,Math.Max(0,__instance.m_healthUpFront),Math.Max(0,__instance.m_staminaUpFront),Math.Max(0,__instance.m_eitrUpFront));
                GamePlayerHit.Merge(current.Changes,changes);current.State=PlayerProgressService.Overlay(current.State,changes);
                if(__instance.m_adrenalineUpFront>0)
                {
                    var adrenaline=GameAdrenaline.Change(current.State,(Player)__instance.m_character,__instance.m_adrenalineUpFront);
                    GamePlayerHit.Merge(current.Changes,adrenaline);current.State=PlayerProgressService.Overlay(current.State,adrenaline);
                }
                return false;
            }
        }
    }
}
