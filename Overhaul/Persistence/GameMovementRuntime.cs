using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameMovementRuntime
    {
        private sealed class Motion
        {
            internal Vector3 Position,Velocity,Angular;
            internal Quaternion Rotation;
            internal GameCombatContext.Frame Frame;
            internal bool PositionPending,HaveSavedPosition;
            internal Vector3 SavedPosition;
            internal double NextView;
            internal long ViewSequence;
            internal Player Player;
            internal bool Suspended,WasKinematic;
            internal Vector2s CheckedZone;
            internal bool HaveCheckedZone,AreaReady;
            internal double NextAreaCheck;
            internal PlayerChange[] DerivedRows;
        }
        private static readonly Dictionary<ZDOID,Motion> motions=new Dictionary<ZDOID,Motion>();
        internal static void InvalidateEffects(ZDOID actor)
        {if(motions.TryGetValue(actor,out var motion)){motion.DerivedRows=null;if(motion.Frame!=null)motion.Frame.State=null;}}
        [ThreadStatic] private static Player simulating;
        private sealed class Scope
        {
            internal Player Previous;
            internal GameCombatContext.Frame Frame;
            internal bool Entered;
        }
        internal static bool Managed(Player player)=>GameCreatureAuthority.Enabled&&player&&player.m_nview&&player.m_nview.IsValid()&&InventoryMoveGame.State(player.GetZDOID())!=null;
        internal static void Forget(ZDOID actor)
        {if(motions.TryGetValue(actor,out var motion)&&motion.Suspended&&motion.Player&&motion.Player.m_body)motion.Player.m_body.isKinematic=motion.WasKinematic;motions.Remove(actor);if(motion?.Player)DynamicCombat.ForgetDash(motion.Player);GameDodgeAction.Forget(actor);GameEnvironmentRuntime.Forget(actor);}
        internal static void Clear(){foreach(var actor in motions.Keys.ToArray())Forget(actor);simulating=null;GameDodgeAction.Clear();GameEnvironmentRuntime.Clear();}
        internal static void SavePosition(ZDOID actor,bool final=false)
        {
            if(GameArrivalRuntime.Active(actor))return;
            if(!motions.TryGetValue(actor,out var motion)||motion.PositionPending&&!final)return;
            var point=motion.Position;
            if(!motion.PositionPending&&motion.HaveSavedPosition&&motion.SavedPosition==point)return;
            var state=InventoryMoveGame.State(actor);if(state==null||GameDeathProgress.IsDead(state)||PlayerResources.Read(state,"health")<=0)return;
            // One outstanding position update per actor. Slow disks cannot grow
            // an unbounded queue of intermediate physics coordinates.
            motion.PositionPending=true;
            if(!InventoryMoveGame.Progress(actor,current=>
                {
                    if(GameDeathProgress.IsDead(current)||PlayerResources.Read(current,"health")<=0)return Array.Empty<PlayerChange>();
                    var rows=new List<PlayerChange>{new PlayerChange("spawn",false,"logout",(double)point.x,(double)point.y,(double)point.z)};
                    var first=current.Rows.FirstOrDefault(row=>row.Table=="state"&&(string)row.Values[0]=="first_spawn");
                    if(first!=null&&Convert.ToBoolean(first.Values[1]))
                    {
                        rows.Add(new PlayerChange("state",false,"first_spawn",false,null,null,null));
                        if(!current.Rows.Any(row=>row.Table=="spawn"&&(string)row.Values[0]=="home"))rows.Add(new PlayerChange("spawn",false,"home",(double)point.x,(double)point.y,(double)point.z));
                    }
                    return rows.ToArray();
                },()=>
                {motion.PositionPending=false;motion.HaveSavedPosition=true;motion.SavedPosition=point;}))motion.PositionPending=false;
        }
        internal static ZPackage View(ZDOID actor)
        {
            if(!motions.TryGetValue(actor,out var motion)||Time.timeAsDouble<motion.NextView)return null;
            motion.NextView=Time.timeAsDouble+.05;
            var view=GameMovementView.Encode(actor,++motion.ViewSequence,GameMovementControl.Read(actor)?.Sequence??0,motion.Position,motion.Rotation,motion.Velocity,motion.Player&&motion.Player.m_teleporting,motion.Player&&motion.Player.m_distantTeleport);
            view.Write(motion.Player&&motion.Player.InIntro());return view;
        }
        internal static void Record(Player player){Remember(player);if(player.m_nview&&player.m_nview.IsValid())Protect(player.m_nview.GetZDO());}
        private static Motion Remember(Player player)
        {
            var id=player.GetZDOID();if(!motions.TryGetValue(id,out var motion))motions.Add(id,motion=new Motion());
            motion.Player=player;
            motion.Position=player.m_body?player.m_body.position:player.transform.position;motion.Rotation=player.m_body?player.m_body.rotation:player.transform.rotation;
            motion.Velocity=player.m_body?player.m_body.linearVelocity:Vector3.zero;motion.Angular=player.m_body?player.m_body.angularVelocity:Vector3.zero;return motion;
        }
        internal static void Protect(ZDO actor)
        {
            if(!motions.TryGetValue(actor.m_uid,out var motion))
            {
                actor.SetRotation(Quaternion.identity);
                actor.Set(ZDOVars.s_velHash,Vector3.zero);actor.Set(ZDOVars.s_bodyVelHash,Vector3.zero);actor.Set(ZDOVars.s_bodyAVelHash,Vector3.zero);return;
            }
            actor.SetPosition(motion.Position);actor.SetRotation(motion.Rotation);
            actor.Set(ZDOVars.s_velHash,motion.Velocity);actor.Set(ZDOVars.s_bodyVelHash,motion.Velocity);actor.Set(ZDOVars.s_bodyAVelHash,motion.Angular);
        }
        internal static bool Position(ZDOID actor,out Vector3 point)
        {if(motions.TryGetValue(actor,out var motion)){point=motion.Position;return true;}point=Vector3.zero;return false;}
        private static Scope Enter(Player player,bool context)
        {
            var scope=new Scope{Previous=simulating,Frame=GameCombatContext.Current,Entered=true};
            if(context)
            {
                var motion=Remember(player);var state=InventoryMoveGame.State(player.GetZDOID());
                if(motion.Frame==null)motion.Frame=new GameCombatContext.Frame{Player=player};
                if(!ReferenceEquals(motion.Frame.State,state))
                {
                    // Position/resource commits retain immutable inventory/effect
                    // rows. Avoid rebuilding item definitions on every such commit.
                    var relevant=state.Rows.Where(Derived);
                    if(motion.DerivedRows==null||!relevant.SequenceEqual(motion.DerivedRows))
                    {
                        motion.DerivedRows=relevant.ToArray();motion.Frame.Equipment=GameCombatEquipment.Equipped(state);motion.Frame.Effects=GameCombatContext.Effects(state,player);
                        motion.Frame.InventoryWeight=null;motion.Frame.Weapon=motion.Frame.Equipment.FirstOrDefault(item=>item.IsWeapon());
                    }
                    motion.Frame.State=state;
                }
                GameCombatContext.Current=motion.Frame;
            }
            simulating=player;return scope;
        }
        private static bool Derived(PlayerChange row)=>row.Table=="inventory"||row.Table=="item_data"||row.Table=="effects"||row.Table=="status"||row.Table=="status_data";
        private static bool AreaReady(Player player,Motion motion)
        {
            if(!ZNetScene.instance)return true;
            var zone=ZoneSystem.GetZone(player.transform.position);
            if(!motion.HaveCheckedZone||!motion.CheckedZone.Equals(zone))
            {motion.HaveCheckedZone=true;motion.CheckedZone=zone;motion.AreaReady=false;motion.NextAreaCheck=0;}
            if(!motion.AreaReady&&Time.timeAsDouble>=motion.NextAreaCheck)
            {motion.NextAreaCheck=Time.timeAsDouble+.1;motion.AreaReady=ZNetScene.instance.IsAreaReady(player.transform.position);}
            return motion.AreaReady;
        }
        private static void Leave(Scope scope)
        {if(scope?.Entered==true){simulating=scope.Previous;GameCombatContext.Current=scope.Frame;}}
        [HarmonyPatch(typeof(Character),nameof(Character.CustomFixedUpdate))]
        private static class PhysicsStep
        {
            private static bool Prefix(Character __instance,float dt,out Scope __state)
            {
                __state=null;if(!(__instance is Player player)||!Managed(player))return true;
                if(!global::Overhaul.Leveling.OverhaulCharacter.Get(player).Ready)GameLeveling.Refresh(player,InventoryMoveGame.State(player.GetZDOID()));
                if(GameArrivalRuntime.Active(player.GetZDOID())){Record(player);return false;}
                if(GameCatapultPassengers.Hold(player))return false;
                GameTeleportAction.Tick(player,dt);
                GameAttachmentRuntime.Tick(player);
                if(DynamicCombat.IsDashing(player)&&(player.IsDead()||player.IsTeleporting()||player.IsStaggering()||player.InDodge()))DynamicCombat.CancelDash(player);
                var motion=Remember(player);
                if(player.m_body&&!AreaReady(player,motion))
                {if(!motion.Suspended){motion.WasKinematic=player.m_body.isKinematic;motion.Suspended=true;}player.m_body.isKinematic=true;return false;}
                if(motion.Suspended&&player.m_body){player.m_body.isKinematic=motion.WasKinematic;motion.Suspended=false;}
                __state=Enter(player,true);
                GameHarpoonRuntime.Tick(player,dt);
                GameDodgeAction.Tick(player);
                var input=GameMovementControl.Read(player.GetZDOID());
                player.m_moveDir=input?.Move??Vector3.zero;player.m_run=input?.Run??false;player.m_walk=input?.Walk??false;player.m_crouchToggled=input?.Crouch??false;
                player.m_debugFly=false;
                if(input!=null)player.SetLookDir(input.Look);
                if(player!=Player.m_localPlayer)player.UpdateCrouch(dt);
                if(StealthSystem.instance)player.UpdateStealth(dt);
                player.EdgeOfWorldKill(dt);
                return true;
            }
            private static void Finalizer(Character __instance,Scope __state)
            {try{if(__state?.Entered==true)Remember((Player)__instance);}finally{Leave(__state);}}
        }
        [HarmonyPatch(typeof(ZDO),nameof(ZDO.IsOwner))]
        private static class Owner
        {private static bool Prefix(ZDO __instance,ref bool __result){if(!simulating||simulating.GetZDOID()!=__instance.m_uid)return true;__result=true;return false;}}
        [HarmonyPatch(typeof(ZSyncTransform),"OwnerSync")]
        private static class Sync
        {
            private static void Prefix(ZSyncTransform __instance,out Scope __state)
            {
                __state=null;if(!(__instance.m_character is Player player)||!Managed(player))return;
                __state=Enter(player,false);
                // This is a simulation scope, not an ownership handover: never
                // reset the server body from a peer's last transform packet.
                __instance.m_wasOwner=true;
            }
            private static void Finalizer(ZSyncTransform __instance,Scope __state)
            {try{if(__state?.Entered==true)Remember((Player)__instance.m_character);}finally{Leave(__state);}}
        }
        [HarmonyPatch(typeof(ZSyncTransform),"ClientSync")]
        private static class IgnorePeerTransform
        {private static bool Prefix(ZSyncTransform __instance)=>!(__instance.m_character is Player player)||!Managed(player);}
        [HarmonyPatch(typeof(SEMan),nameof(SEMan.Update))]
        private static class EffectClock
        {private static bool Prefix(SEMan __instance)=>!simulating||__instance.m_character!=simulating;}
        [HarmonyPatch(typeof(Player),nameof(Player.InCutscene))]
        private static class Cutscene
        {
            private static bool Prefix(Player __instance,ref bool __result)
            {if(simulating!=__instance&&!GameCombatContext.Matches(__instance))return true;__result=__instance.GetCurrentAnimHash()==Player.s_animatorTagCutscene||__instance.InIntro()||__instance.m_sleeping;return false;}
        }
        [HarmonyPatch(typeof(Player),"UpdateStealth")]
        private static class StealthClock
        {private static bool Prefix(Player __instance)=>simulating==__instance||!Managed(__instance);}
        [HarmonyPatch(typeof(Player),"EdgeOfWorldKill")]
        private static class WorldEdge
        {private static bool Prefix(Player __instance)=>simulating==__instance||!Managed(__instance);}
        [HarmonyPatch(typeof(Player),nameof(Player.HaveStamina))]
        private static class Stamina
        {private static bool Prefix(Player __instance,float amount,bool __runOriginal,ref bool __result){if(!__runOriginal)return true;if(simulating==__instance){__result=InventoryMoveGame.Stamina(__instance.GetZDOID())>amount;return false;}if(!GameCombatContext.Matches(__instance))return true;__result=PlayerResources.Read(GameCombatContext.Current.State,"stamina")>amount;return false;}}
        [HarmonyPatch(typeof(Player),nameof(Player.UseStamina))]
        private static class StaminaUse
        {
            private static bool Prefix(Player __instance,float v,bool __runOriginal)
            {
                if(!__runOriginal||simulating!=__instance)return true;
                InventoryMoveGame.SpendStamina(__instance.GetZDOID(),v*Game.m_staminaRate,__instance.m_staminaRegenDelay);return false;
            }
        }
    }
}



