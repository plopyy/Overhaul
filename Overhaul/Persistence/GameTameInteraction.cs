using System;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameTameInteraction
    {
        internal const string Use="tame.pet";
        private static readonly int Next="overhaul_next_pet".GetStableHashCode();
        internal static PlayerActionPlan Prepare(ZDO actor,GameObject target,InventoryMoveRequest request,PlayerSnapshot snapshot)
        {
            var tame=target.GetComponent<Tameable>();
            var playerObject=ZNetScene.instance.FindInstance(actor.m_uid);
            if(!tame || !tame.m_nview || !tame.m_nview.IsValid() || !tame.IsTamed() || request.Action.Amount!=1 ||
                !playerObject || !playerObject.GetComponent<Player>() || actor.GetBool(ZDOVars.s_dead,false) ||
                tame.m_character && tame.m_character.IsDead() ||
                !Storage.ChestAccess.WardAccessAt(target.transform.position,actor.GetLong(ZDOVars.s_playerID,0)))
                throw new InvalidOperationException("Tamed animal is unavailable");
            var data=tame.m_nview.GetZDO();long now=ZNet.instance.GetTime().Ticks;
            if(now<data.GetLong(Next,0))throw new InvalidOperationException("Animal interaction is still cooling down");
            bool command=tame.m_commandable;
            if(command && !tame.m_monsterAI)throw new InvalidOperationException("Animal cannot follow a player");
            bool follow=command && string.IsNullOrEmpty(data.GetString(ZDOVars.s_follow,"")) && !tame.m_monsterAI.GetFollowTarget();
            string name=(string)snapshot.Rows.Single(r=>r.Table=="state" && (string)r.Values[0]=="player_name").Values[3];
            var statistic=PlayerCraftProgressGame.Increment(snapshot,"statistics:0:values",((int)(command?PlayerStatType.TamedCommand:PlayerStatType.TamedPetting)).ToString(CultureInfo.InvariantCulture),1);
            using(var world=new PlayerActionObjectGame(data))
            {
                world.Set(Next,checked(now+TimeSpan.TicksPerSecond));
                if(command)
                {
                    world.Set(ZDOVars.s_follow,follow?name:"");world.Set(ZDOVars.s_patrol,follow?0:1);
                    if(!follow)world.Set(ZDOVars.s_patrolPoint,data.GetPosition());
                }
                return world.Finish(new PlayerBatch(request.Action.Operation,snapshot.Revision,new[]{statistic}),()=>
                {
                    if(!tame)return;
                    if(command)
                    {
                        tame.m_monsterAI.SetFollowTarget(follow?playerObject:null);
                        if(follow)tame.m_monsterAI.ResetPatrolPoint();else tame.m_monsterAI.SetPatrolPoint();
                        tame.m_unsummonTime=0;
                        int maximum=data.GetInt(ZDOVars.s_maxInstances,0);if(follow && maximum>0)tame.UnsummonMaxInstances(maximum);
                    }
                    tame.m_lastPetTime=Time.time;tame.m_petEffect.Create(target.transform.position,target.transform.rotation);
                });
            }
        }
        [HarmonyPatch(typeof(Tameable),nameof(Tameable.Interact))]
        private static class Intent
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Tameable __instance,Humanoid user,bool hold,bool alt,ref bool __result)
            {
                if(user!=Player.m_localPlayer || !PlayerSessionGame.Managed || alt)return true;
                __result=false;
                if(hold || !__instance.m_nview || !__instance.m_nview.IsValid() || !__instance.IsTamed() || user.IsTeleporting())return false;
                var id=__instance.m_nview.GetZDO().m_uid;
                __result=InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand{Kind=PlayerActionKind.UseOn,Definition=Use,TargetUser=id.UserID,TargetId=id.ID},0,0,1)==true;
                return false;
            }
        }
        [HarmonyPatch(typeof(Tameable),"RPC_Command")]
        private static class LegacyCommand
        {private static bool Prefix()=>!GameCreatureAuthority.Enabled;}
    }
}
