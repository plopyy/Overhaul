using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameVehicleRuntime
    {
        private sealed class Seat
        {
            internal Player Player;
            internal Component Controller;
            internal ZNetView View;
            internal long Sender;
            internal Vector3 Move,Look;
            internal bool Run,Block,SkillPending;
            internal double Seen,SkillTime;
            internal PlayerSnapshot SkillsFrom;
            internal float RideSkill;
        }
        private static readonly Dictionary<ZDOID,Seat> seats=new Dictionary<ZDOID,Seat>();
        private static Component clientController;
        private static double clientNext;
        private static ZNetView View(Component controller)=>controller is Sadle saddle?saddle.m_nview:((ShipControlls)controller).m_nview;
        private static Player Sender(long sender)
        {
            if(sender==ZNet.GetUID())return GameMovementRuntime.Managed(Player.m_localPlayer)?Player.m_localPlayer:null;
            var peer=ZNet.instance.GetPeer(sender);if(peer==null)return null;
            var actor=PlayerSessionGame.Actor(peer.m_rpc);var instance=actor==null?null:ZNetScene.instance.FindInstance(actor.m_uid);
            var player=instance?instance.GetComponent<Player>():null;return GameMovementRuntime.Managed(player)?player:null;
        }
        private static Seat User(Component controller,long sender)
        {
            var view=View(controller);if(!view||!view.IsValid()||!seats.TryGetValue(view.GetZDO().m_uid,out var seat)||seat.Controller!=controller||seat.Sender!=sender||!seat.Player)return null;
            return Sender(sender)==seat.Player&&!seat.Player.IsDead()&&seat.Player.m_doodadController==(IDoodadController)controller?seat:null;
        }
        internal static void Release(Player player)
        {
            if(!player)return;
            foreach(var pair in seats.Where(p=>p.Value.Player==player).ToArray())
            {
                var seat=pair.Value;seats.Remove(pair.Key);
                if(seat.View&&seat.View.IsValid())seat.View.GetZDO().Set(ZDOVars.s_user,0L);
                if(seat.Controller is Sadle saddle&&saddle)saddle.ResetControlls();
            }
            player.m_doodadController=null;
            if(player==Player.m_localPlayer){clientController=null;clientNext=0;}
        }
        private static void Request(Component controller,long sender,long user)
        {
            var view=View(controller);var player=Sender(sender);
            if(!player||!view||!view.IsValid()||GamePersistence.ActionReserved(view.GetZDO().m_uid))return;
            bool mount=controller is Sadle;var saddle=controller as Sadle;var ship=controller as ShipControlls;
            long expected=mount?player.GetZDOID().UserID:player.GetPlayerID();
            var point=mount?saddle.m_attachPoint:ship.m_attachPoint;float range=mount?saddle.m_maxUseRange:ship.m_maxUseRange;
            if(user!=expected||!controller.gameObject.activeInHierarchy||!point||Vector3.Distance(player.transform.position,point.position)>=range||
                player.IsDead()||player.InIntro()||player.IsTeleporting()||player.InAttack()||player.InDodge()||player.IsStaggering()||
                !Storage.ChestAccess.WardAccessAt(point.position,player.GetPlayerID()))return;
            if(User(controller,sender)!=null)return;
            if(player.IsAttached()||player.m_doodadController!=null||GameAttachmentRuntime.Occupied(point))return;
            if(mount&&(!saddle.m_character.IsTamed()||saddle.m_character.IsDead()||!view.GetZDO().GetBool(ZDOVars.s_haveSaddleHash,false)))return;
            if(!mount&&!ship.m_ship.IsPlayerInBoat(expected))return;
            var state=InventoryMoveGame.State(player.GetZDOID());bool encumbered=false;GameCombatContext.Run(player,state,null,null,()=>encumbered=player.IsEncumbered());if(encumbered)return;
            var key=view.GetZDO().m_uid;
            if(seats.TryGetValue(key,out var previous))
            {if(User(previous.Controller,previous.Sender)!=null)return;if(previous.Player)GameAttachmentRuntime.Detach(previous.Player);seats.Remove(key);}
            view.GetZDO().SetOwner(ZNet.GetUID());view.GetZDO().Set(ZDOVars.s_user,expected);
            if(mount)saddle.ResetControlls();
            var seat=new Seat{Player=player,Controller=controller,View=view,Sender=sender,Seen=Time.timeAsDouble,SkillTime=Time.timeAsDouble};seats[key]=seat;
            int index=mount?Array.IndexOf(view.GetComponentsInChildren<Sadle>(true),saddle):Array.IndexOf(view.GetComponentsInChildren<ShipControlls>(true),ship);
            GameAttachmentRuntime.Attach(player,view,mount?3:4,index);
        }
        private static void Receive(Component controller,long sender,Vector3 move,Vector3 look,int flags)
        {
            if(!GameCreatureAuthority.Enabled)return;var seat=User(controller,sender);if(seat==null)return;
            if((flags&~7)!=0||float.IsNaN(move.sqrMagnitude)||float.IsInfinity(move.sqrMagnitude)||move.sqrMagnitude>1.001f||Math.Abs(move.y)>.001f||
                float.IsNaN(look.sqrMagnitude)||float.IsInfinity(look.sqrMagnitude)||Math.Abs(look.sqrMagnitude-1)>.01f)return;
            seat.Move=move;seat.Look=look;seat.Run=(flags&1)!=0;seat.Block=(flags&4)!=0;seat.Seen=Time.timeAsDouble;
        }
        private static void Tick(Component controller,float dt)
        {
            var view=View(controller);if(!view||!view.IsValid()||!seats.TryGetValue(view.GetZDO().m_uid,out var seat))return;
            if(User(controller,seat.Sender)==null){if(seat.Player)GameAttachmentRuntime.Detach(seat.Player);seats.Remove(view.GetZDO().m_uid);return;}
            bool stale=Time.timeAsDouble-seat.Seen>.5;
            if(controller is ShipControlls ship){ship.m_ship.ApplyControlls(stale?Vector3.zero:seat.Move);return;}
            var saddle=(Sadle)controller;
            if(stale){saddle.ResetControlls();return;}
            if(seat.Block||seat.Move.z>.5||seat.Run){var direction=seat.Look;direction.y=0;saddle.m_controlDir=direction.normalized;}
            if(seat.Run)saddle.m_speed=Sadle.Speed.Run;
            else if(seat.Move.z>.5)saddle.m_speed=Sadle.Speed.Walk;
            else if(seat.Move.z<-.5)saddle.m_speed=Sadle.Speed.Stop;
            else if(seat.Block){if(saddle.m_speed!=Sadle.Speed.Walk&&saddle.m_speed!=Sadle.Speed.Run)saddle.m_speed=Sadle.Speed.Turn;}
            else if(saddle.m_speed==Sadle.Speed.Turn)saddle.m_speed=Sadle.Speed.Stop;
            var state=InventoryMoveGame.State(seat.Player.GetZDOID());
            if(!ReferenceEquals(state,seat.SkillsFrom))
            {seat.SkillsFrom=state;seat.RideSkill=Mathf.Clamp01(GameAttackResources.SkillLevel(state,Skills.SkillType.Ride,GameAttackResources.Effects(state))/100f);}
            saddle.m_rideSkill=seat.RideSkill;
            if(Time.timeAsDouble-seat.SkillTime<1||seat.SkillPending)return;
            seat.SkillTime=Time.timeAsDouble;
            if(saddle.m_speed!=Sadle.Speed.Run||!saddle.HaveStamina(0)||saddle.m_character.GetVelocity().sqrMagnitude<.01f)return;
            seat.SkillPending=true;
            if(!InventoryMoveGame.TimedAction(seat.Player,current=>new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(Guid.NewGuid().ToString("N"),current.Revision,
                PlayerCraftProgressGame.Raise(current,Skills.SkillType.Ride,1)),new Dictionary<long,ObjectRecord>()),()=>seat.SkillPending=false)))seat.SkillPending=false;
        }
        [HarmonyPatch]
        private static class Register
        {
            private static IEnumerable<MethodBase> TargetMethods(){yield return AccessTools.Method(typeof(Sadle),"Awake");yield return AccessTools.Method(typeof(ShipControlls),"Awake");}
            private static void Postfix(Component __instance)
            {var view=View(__instance);if(view&&view.IsValid())view.Register<Vector3,Vector3,int>("Overhaul_VehicleControls",(sender,move,look,flags)=>Receive(__instance,sender,move,look,flags));}
        }
        [HarmonyPatch]
        private static class Claim
        {
            private static IEnumerable<MethodBase> TargetMethods(){yield return AccessTools.Method(typeof(Sadle),"RPC_RequestControl");yield return AccessTools.Method(typeof(ShipControlls),"RPC_RequestControl");}
            private static bool Prefix(Component __instance,long sender,long playerID){if(!GameCreatureAuthority.Enabled)return true;Request(__instance,sender,playerID);return false;}
        }
        [HarmonyPatch]
        private static class Unclaim
        {
            private static IEnumerable<MethodBase> TargetMethods(){yield return AccessTools.Method(typeof(Sadle),"RPC_ReleaseControl");yield return AccessTools.Method(typeof(ShipControlls),"RPC_ReleaseControl");}
            private static bool Prefix(Component __instance,long sender){if(!GameCreatureAuthority.Enabled)return true;var seat=User(__instance,sender);if(seat!=null)GameAttachmentRuntime.Detach(seat.Player);return false;}
        }
        [HarmonyPatch]
        private static class Response
        {
            private static IEnumerable<MethodBase> TargetMethods(){yield return AccessTools.Method(typeof(Sadle),"RPC_RequestRespons");yield return AccessTools.Method(typeof(ShipControlls),"RPC_RequestRespons");}
            private static bool Prefix(long sender,bool granted)=>!PlayerSessionGame.Managed||!granted&&sender==(ZNet.instance.IsServer()?ZNet.GetUID():ZNet.instance.GetServerPeer()?.m_uid);
        }
        [HarmonyPatch]
        private static class Input
        {
            private static IEnumerable<MethodBase> TargetMethods(){yield return AccessTools.Method(typeof(Sadle),nameof(Sadle.ApplyControlls));yield return AccessTools.Method(typeof(ShipControlls),nameof(ShipControlls.ApplyControlls));}
            private static bool Prefix(Component __instance,Vector3 moveDir,Vector3 lookDir,bool run,bool autoRun,bool block)
            {
                if(!PlayerSessionGame.Managed)return true;
                if(clientController==__instance&&Time.timeAsDouble<clientNext)return false;
                clientController=__instance;clientNext=Time.timeAsDouble+.05;
                View(__instance).InvokeRPC("Overhaul_VehicleControls",moveDir,lookDir,(run?1:0)|(autoRun?2:0)|(block?4:0));return false;
            }
        }
        [HarmonyPatch(typeof(Sadle),"FixedUpdate")]
        private static class MountTick{private static void Prefix(Sadle __instance){if(GameCreatureAuthority.Enabled)Tick(__instance,Time.fixedDeltaTime);}}
        [HarmonyPatch(typeof(Ship),nameof(Ship.CustomFixedUpdate))]
        private static class ShipTick{private static void Prefix(Ship __instance,float fixedDeltaTime){if(GameCreatureAuthority.Enabled&&__instance.m_nview&&__instance.m_nview.IsValid()&&seats.TryGetValue(__instance.m_nview.GetZDO().m_uid,out var seat))Tick(seat.Controller,fixedDeltaTime);}}
        [HarmonyPatch(typeof(Sadle),"CalculateHaveValidUser")]
        private static class Rider{private static bool Prefix(Sadle __instance){if(!GameCreatureAuthority.Enabled)return true;__instance.m_haveValidUser=__instance.m_nview&&__instance.m_nview.IsValid()&&seats.TryGetValue(__instance.m_nview.GetZDO().m_uid,out var seat)&&User(__instance,seat.Sender)!=null;return false;}}
        [HarmonyPatch(typeof(Sadle),"UpdateRidingSkill")]
        private static class Skill{private static bool Prefix()=>!PlayerSessionGame.Managed&&!GameCreatureAuthority.Enabled;}
        [HarmonyPatch(typeof(Sadle),"RPC_Controls")]
        private static class LegacyMount{private static bool Prefix()=>!GameCreatureAuthority.Enabled;}
        [HarmonyPatch(typeof(Ship),"UpdateOwner")]
        private static class Ownership{private static bool Prefix()=>!GameCreatureAuthority.Enabled;}
        [HarmonyPatch]
        private static class LegacyShip
        {
            private static IEnumerable<MethodBase> TargetMethods(){foreach(string name in new[]{"RPC_Stop","RPC_Forward","RPC_Backward","RPC_Rudder"})yield return AccessTools.Method(typeof(Ship),name);}
            private static bool Prefix(long sender)=>!GameCreatureAuthority.Enabled||sender==ZNet.GetUID();
        }
    }
}
