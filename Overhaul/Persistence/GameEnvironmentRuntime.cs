using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameEnvironmentRuntime
    {
        internal sealed class Conditions
        {internal bool Cold,Freezing,Rain,Roof,Shelter,Fire,Warm,Shield,Sensed,Sitting;internal int Comfort,BaseValue;}
        private sealed class Clock{internal double Next,NextCover;internal bool Pending;}
        private static readonly Dictionary<ZDOID,Clock> clocks=new Dictionary<ZDOID,Clock>();
        internal static void Forget(ZDOID actor){clocks.Remove(actor);GameEnvironmentWeather.Forget(actor);}
        internal static void Clear(){clocks.Clear();GameEnvironmentWeather.Clear();}
        internal static void Tick(ZDO actor)
        {
            if(actor==null||!EnvMan.instance||WorldGenerator.instance==null)return;
            var instance=ZNetScene.instance.FindInstance(actor.m_uid);var player=instance?instance.GetComponent<Player>():null;
            if(!GameMovementRuntime.Managed(player)||!player.m_collider||player.IsDead()||player.InIntro())return;
            if(!clocks.TryGetValue(actor.m_uid,out var clock))clocks.Add(actor.m_uid,clock=new Clock());
            if(clock.Pending||Time.timeAsDouble<clock.Next)return;clock.Next=Time.timeAsDouble+.5;clock.Pending=true;
            if(!InventoryMoveGame.TimedAction(player,state=>
            {
                if(!player||player.IsDead()){clock.Pending=false;return null;}
                var weather=GameEnvironmentWeather.Read(player);if(weather==null){clock.Pending=false;return null;}
                if(Time.timeAsDouble>=clock.NextCover)
                {
                    clock.NextCover=Time.timeAsDouble+1;
                    Cover.GetCoverForPoint(player.GetCenterPoint(),out player.m_coverPercentage,out player.m_underRoof,.5f);
                    player.m_baseValue=EffectArea.GetBaseValue(player.transform.position,20);
                    player.m_comfortLevel=SE_Rested.CalculateComfortLevel(player.InShelter(),player.transform.position);
                    actor.Set(ZDOVars.s_baseValue,player.m_baseValue);
                }
                var point=player.transform.position;
                var sample=new Conditions{Cold=weather.m_isCold||weather.m_isColdAtNight&&!EnvMan.IsDay(),Freezing=weather.m_isFreezing||weather.m_isFreezingAtNight&&!EnvMan.IsDay(),Rain=weather.m_isWet,
                    Roof=player.m_underRoof,Shelter=player.InShelter(),Fire=EffectArea.IsPointInsideArea(player.GetCenterPoint(),EffectArea.Type.Heat,player.GetRadius()),
                    Warm=EffectArea.IsPointInsideArea(point,EffectArea.Type.WarmCozyArea,1),Shield=ShieldGenerator.IsInsideShield(point),Sensed=player.IsSensed(),Sitting=player.IsSitting(),Comfort=player.m_comfortLevel,BaseValue=player.m_baseValue};
                var rows=Prepare(state,player,sample);if(rows.Length==0){clock.Pending=false;return null;}
                return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(Guid.NewGuid().ToString("N"),state.Revision,rows),new Dictionary<long,ObjectRecord>()),()=>clock.Pending=false);
            }))clock.Pending=false;
        }
        internal static PlayerChange[] Prepare(PlayerSnapshot state,Player player,Conditions sample)
        {
            if(PlayerResources.Read(state,"health")<=0||GameDeathProgress.IsDead(state))return Array.Empty<PlayerChange>();
            var changes=new List<PlayerChange>();var current=state;
            bool Has(int id)=>current.Rows.Any(row=>(row.Table=="status"||row.Table=="effects")&&Convert.ToInt32(row.Values[0])==id);
            void Change(IEnumerable<PlayerChange> rows){var delta=rows.ToArray();GamePlayerHit.Merge(changes,delta);current=PlayerProgressService.Overlay(current,delta);}
            bool Remove(int id){if(!Has(id))return false;Change(new[]{new PlayerChange("status",true,id)});return true;}
            void Add(int id,bool refresh=false){if(!Has(id)||refresh)Change(GameStatusImpact.Prepare(current,player,id,0,0,-1,ZDOID.None));}
            void Set(int id,bool value){if(value)Add(id);else Remove(id);}
            bool burning=Has(SEMan.s_statusEffectBurning),wet=Has(SEMan.s_statusEffectWet);
            HitData.DamageModifier frost=HitData.DamageModifier.Normal;GameCombatContext.Run(player,state,null,null,()=>frost=player.GetDamageModifiers().GetModifier(HitData.DamageType.Frost));
            bool freezing=sample.Freezing&&!sample.Fire&&!sample.Shelter;
            bool cold=sample.Cold&&!sample.Fire||sample.Freezing&&sample.Fire&&!sample.Shelter||sample.Freezing&&!sample.Fire&&sample.Shelter;
            if(sample.Warm||frost==HitData.DamageModifier.Resistant||frost==HitData.DamageModifier.VeryResistant||frost==HitData.DamageModifier.SlightlyResistant){freezing=false;cold=false;}
            if(sample.Rain&&!sample.Roof&&!sample.Shield)Add(SEMan.s_statusEffectWet,true);
            Set(SEMan.s_statusEffectShelter,sample.Shelter);Set(SEMan.s_statusEffectCampFire,sample.Fire);
            bool resting=!sample.Sensed&&(sample.Sitting||sample.Shelter)&&!cold&&!freezing&&(!wet||sample.Warm)&&!burning&&sample.Fire;
            Set(SEMan.s_statusEffectResting,resting);player.m_safeInHome=resting&&sample.Shelter&&sample.BaseValue>=1;
            if(freezing){if(!Remove(SEMan.s_statusEffectCold))Add(SEMan.s_statusEffectFreezing);}
            else if(cold){if(!Remove(SEMan.s_statusEffectFreezing))Add(SEMan.s_statusEffectCold);}
            else{Remove(SEMan.s_statusEffectCold);Remove(SEMan.s_statusEffectFreezing);}
            string statistic=((int)PlayerStatType.MaxComfort).ToString(CultureInfo.InvariantCulture);
            var known=state.Rows.FirstOrDefault(row=>row.Table=="knowledge"&&(string)row.Values[0]=="statistics:0:values"&&(string)row.Values[1]==statistic);
            float maximum=known==null?0:float.Parse((string)known.Values[2],CultureInfo.InvariantCulture);
            if(sample.Comfort>maximum)Change(new[]{PlayerCraftProgressGame.Increment(current,"statistics:0:values",statistic,sample.Comfort-maximum)});
            return changes.ToArray();
        }
        [HarmonyPatch(typeof(Player),"UpdateEnvStatusEffects")]
        private static class NativeEnvironment
        {private static bool Prefix(Player __instance)=>!GameMovementRuntime.Managed(__instance)&&!(PlayerSessionGame.Managed&&__instance==Player.m_localPlayer);}
        [HarmonyPatch(typeof(Player),"UpdateCover")]
        private static class NativeCover
        {private static bool Prefix(Player __instance)=>!GameMovementRuntime.Managed(__instance);}
        [HarmonyPatch(typeof(Player),"UpdateBaseValue")]
        private static class NativeComfort
        {private static bool Prefix(Player __instance)=>!GameMovementRuntime.Managed(__instance);}
    }
}
