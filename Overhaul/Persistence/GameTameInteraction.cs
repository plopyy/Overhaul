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
        private static readonly int FollowId="overhaul_follow_player".GetStableHashCode();
        private const string FeedbackRpc="Overhaul_TameFeedback";
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
            Vector3 stopAt=data.GetPosition();bool command=tame.m_commandable;
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
                    world.Set(FollowId,follow?actor.GetLong(ZDOVars.s_playerID,0):0L);
                    if(!follow)world.Set(ZDOVars.s_patrolPoint,stopAt);
                }
                return world.Finish(new PlayerBatch(request.Action.Operation,snapshot.Revision,new[]{statistic}),()=>
                {
                    if(!tame)return;
                    if(command)
                    {
                        tame.m_monsterAI.SetFollowTarget(follow?playerObject:null);
                        if(follow)tame.m_monsterAI.ResetPatrolPoint();else tame.m_monsterAI.SetPatrolPoint(stopAt);
                        tame.m_unsummonTime=0;
                        int maximum=data.GetInt(ZDOVars.s_maxInstances,0);if(follow && maximum>0)tame.UnsummonMaxInstances(maximum);
                    }
                    tame.m_lastPetTime=Time.time;tame.m_nview.InvokeRPC(ZNetView.Everybody,FeedbackRpc,actor.m_uid,command,follow);
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

        [HarmonyPatch(typeof(Tameable),"Awake")]
        private static class FeedbackRegistration
        {
            private static void Postfix(Tameable __instance)
            {
                if(!__instance.m_nview || !__instance.m_nview.IsValid())return;
                __instance.m_nview.Register<ZDOID,bool,bool>(FeedbackRpc,(sender,actor,command,follow)=>Feedback(__instance,sender,actor,command,follow));
            }
        }
        internal static void Feedback(Tameable tame,long sender,ZDOID actor,bool command,bool follow)
        {
            if(!ZNet.instance || (!PlayerSessionGame.Managed && !GameCreatureAuthority.Enabled))return;
            long server=ZNet.instance.IsServer()?ZNet.GetUID():ZNet.instance.GetServerPeer()?.m_uid??0;
            if(server==0 || sender!=server)return;
            tame.m_petEffect.Create(tame.transform.position,tame.transform.rotation);
            var player=Player.m_localPlayer;if(!player || player.GetZDOID()!=actor)return;
            string message;
            if(command)message=tame.GetHoverName()+" "+(follow?"$hud_tamefollow":"$hud_tamestay");
            else
            {
                message=tame.m_tameTextGetter?.Invoke();
                if(string.IsNullOrEmpty(message))message=tame.m_nameBeforeText?tame.GetHoverName()+" "+tame.m_tameText:tame.m_tameText;
            }
            player.Message(MessageHud.MessageType.Center,message);
        }

        internal static void RestoreFollow(Tameable tame)
        {
            if(!tame.m_monsterAI || tame.m_monsterAI.GetFollowTarget() || !tame.m_nview || !tame.m_nview.IsValid() || !tame.m_nview.IsOwner())return;
            var data=tame.m_nview.GetZDO();if(GamePersistence.ActionReserved(data.m_uid))return;
            string name=data.GetString(ZDOVars.s_follow,"");if(string.IsNullOrEmpty(name))return;
            long id=data.GetLong(FollowId,0);ZDO match=null;
            foreach(var actor in PlayerSessionGame.ActiveActors())
            {
                bool matches=id!=0?actor.GetLong(ZDOVars.s_playerID,0)==id:
                    InventoryMoveGame.State(actor.m_uid)?.Rows.Any(r=>r.Table=="state" && (string)r.Values[0]=="player_name" && (string)r.Values[3]==name)==true;
                if(!matches)continue;
                // Do not pick an arbitrary player if a legacy name is ambiguous.
                if(match!=null)return;match=actor;
            }
            if(match!=null)
            {
                var player=ZNetScene.instance.FindInstance(match.m_uid);
                if(!player)return; // Player admitted, physical scene still loading.
                tame.m_monsterAI.SetFollowTarget(player);tame.m_monsterAI.ResetPatrolPoint();tame.m_unsummonTime=0;
                int maximum=data.GetInt(ZDOVars.s_maxInstances,0);if(maximum>0)tame.UnsummonMaxInstances(maximum);
                return;
            }
            if(tame.m_unsummonOnOwnerLogoutSeconds>0)
            {tame.m_unsummonTime+=Time.fixedDeltaTime;if(tame.m_unsummonTime>tame.m_unsummonOnOwnerLogoutSeconds)tame.UnSummon();}
        }
        [HarmonyPatch(typeof(Tameable),"UpdateSavedFollowTarget")]
        private static class Restore
        {private static bool Prefix(Tameable __instance){if(!GameCreatureAuthority.Enabled)return true;RestoreFollow(__instance);return false;}}
    }
}
