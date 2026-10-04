using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameGuardianPower
    {
        internal const string Start="guardian.start";
        private sealed class Cast{internal Player Player;internal string Power;internal double Deadline;internal bool Pending;internal AnimatorCullingMode Culling;}
        private static readonly Dictionary<ZDOID,Cast> casts=new Dictionary<ZDOID,Cast>();
        internal static bool Active(ZDOID actor)=>casts.ContainsKey(actor);
        internal static void Forget(ZDOID actor)
        {if(!casts.TryGetValue(actor,out var cast))return;casts.Remove(actor);if(cast.Player&&cast.Player.m_animator)cast.Player.m_animator.cullingMode=cast.Culling;}
        internal static void Clear(){foreach(var actor in casts.Keys.ToArray())Forget(actor);}
        internal static void Tick(){foreach(var pair in casts.ToArray())if(!pair.Value.Pending&&(!pair.Value.Player||pair.Value.Player.IsDead()||Time.timeAsDouble>pair.Value.Deadline))Forget(pair.Key);}
        private static StatusEffect Power(PlayerSnapshot state)
        {
            string name=state.Rows.Single(r=>r.Table=="state"&&(string)r.Values[0]=="guardian_power").Values[3] as string;
            var effect=string.IsNullOrEmpty(name)?null:ObjectDB.instance.GetStatusEffect(name.GetStableHashCode());
            if(!effect||PlayerResources.Read(state,"guardian_cooldown")>0)throw new InvalidOperationException("Guardian power is not ready");
            return effect;
        }
        internal static PlayerActionPlan Prepare(ZDO actor,InventoryMoveRequest request,PlayerSnapshot state)
        {
            var instance=ZNetScene.instance.FindInstance(actor.m_uid);var player=instance?instance.GetComponent<Player>():null;
            if(request.Action.Amount!=1||request.Gameplay.TargetId!=0||!player||!player.m_animator||!player.m_zanim||casts.ContainsKey(actor.m_uid)||GameStaffGuardRuntime.Blocked(state)||GameDeathProgress.IsDead(state)||
                player.IsTeleporting()||player.InIntro()||player.IsStaggering()||player.InDodge()||player.InMinorAction()||GameAttackRuntime.Active(actor.m_uid)||player.IsKnockedBack())
                throw new InvalidOperationException("Character cannot start guardian power");
            var effect=Power(state);
            var changes=new List<PlayerChange>{PlayerCraftProgressGame.Increment(state,"statistics:0:values",((int)PlayerStatType.UseGuardianPower).ToString(CultureInfo.InvariantCulture),1)};
            string suffix=effect.name.StartsWith("GP_")?effect.name.Substring(3):"";if(suffix=="TheElder")suffix="Elder";
            if(Enum.TryParse("UsePower"+suffix,out PlayerStatType stat))changes.Add(PlayerCraftProgressGame.Increment(state,"statistics:0:values",((int)stat).ToString(CultureInfo.InvariantCulture),1));
            return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(request.Action.Operation,state.Revision,changes),new Dictionary<long,ObjectRecord>()),()=>
            {
                if(!player||player.IsDead())return;
                casts[actor.m_uid]=new Cast{Player=player,Power=effect.name,Deadline=Time.timeAsDouble+10,Culling=player.m_animator.cullingMode};
                player.m_animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;player.m_zanim.SetTrigger("gpower");
            });
        }
        private static void Trigger(Player player)
        {
            if(!player.m_nview||!player.m_nview.IsValid()||!casts.TryGetValue(player.GetZDOID(),out var cast)||cast.Pending)return;
            if(Time.timeAsDouble>cast.Deadline){Forget(player.GetZDOID());return;}
            cast.Pending=true;
            if(!InventoryMoveGame.TimedAction(player,state=>
            {
                if(GameStaffGuardRuntime.Blocked(state)||GameDeathProgress.IsDead(state)||player.IsStaggering()){Forget(player.GetZDOID());return null;}
                if(PlayerResources.Read(state,"guardian_cooldown")>0||!state.Rows.Any(r=>r.Table=="state"&&(string)r.Values[0]=="guardian_power"&&(string)r.Values[3]==cast.Power))
                {Forget(player.GetZDOID());return null;}
                var effect=Power(state);
                var rows=GameStatusImpact.Prepare(state,player,effect.NameHash(),0,0,-1,ZDOID.None).ToList();
                GamePlayerHit.Merge(rows,GameAdrenaline.Change(PlayerProgressService.Overlay(state,rows),player,player.m_adrenalineGuardianPower));
                GamePlayerHit.Merge(rows,new[]{PlayerResources.Row("guardian_cooldown",effect.m_cooldown)});
                return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(Guid.NewGuid().ToString("N"),state.Revision,rows),new Dictionary<long,ObjectRecord>()),()=>
                {
                    Forget(player.GetZDOID());var nearby=new List<Player>();Player.GetPlayersInRange(player.transform.position,10,nearby);
                    foreach(var other in nearby)if(other!=player&&InventoryMoveGame.State(other.GetZDOID())!=null)other.GetSEMan().AddStatusEffect(effect.NameHash(),true,0,0,-1);
                });
            }))Forget(player.GetZDOID());
        }
        [HarmonyPatch(typeof(Player),nameof(Player.StartGuardianPower))]
        private static class Intent
        {
            private static bool Prefix(Player __instance,ref bool __result)
            {
                if(__instance!=Player.m_localPlayer||!PlayerSessionGame.Managed)return true;
                __result=InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand{Kind=PlayerActionKind.UseOn,Definition=Start})==true;return false;
            }
        }
        [HarmonyPatch(typeof(Player),nameof(Player.ActivateGuardianPower))]
        private static class Activation
        {
            private static bool Prefix(Player __instance,ref bool __result)
            {
                if(GameCreatureAuthority.Enabled){Trigger(__instance);__result=false;return false;}
                return !PlayerSessionGame.Managed;
            }
        }
        [HarmonyPatch(typeof(Player),"UpdateGuardianPower")]
        private static class Cooldown
        {private static bool Prefix()=>!PlayerSessionGame.Managed&&!GameCreatureAuthority.Enabled;}
    }
}

