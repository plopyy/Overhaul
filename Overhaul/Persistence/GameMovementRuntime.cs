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
        }
        private static readonly Dictionary<ZDOID,Motion> motions=new Dictionary<ZDOID,Motion>();
        [ThreadStatic] private static Player simulating;
        private sealed class Scope
        {
            internal Player Previous;
            internal GameCombatContext.Frame Frame;
            internal bool Entered;
        }
        internal static bool Managed(Player player)=>GameCreatureAuthority.Enabled&&player&&player.m_nview&&player.m_nview.IsValid()&&InventoryMoveGame.State(player.GetZDOID())!=null;
        internal static void Forget(ZDOID actor)=>motions.Remove(actor);
        internal static void Clear(){motions.Clear();simulating=null;}
        private static Motion Remember(Player player)
        {
            var id=player.GetZDOID();if(!motions.TryGetValue(id,out var motion))motions.Add(id,motion=new Motion());
            motion.Position=player.m_body?player.m_body.position:player.transform.position;motion.Rotation=player.m_body?player.m_body.rotation:player.transform.rotation;
            motion.Velocity=player.m_body?player.m_body.linearVelocity:Vector3.zero;motion.Angular=player.m_body?player.m_body.angularVelocity:Vector3.zero;return motion;
        }
        internal static void Protect(ZDO actor)
        {
            if(!motions.TryGetValue(actor.m_uid,out var motion))return;
            actor.SetPosition(motion.Position);actor.SetRotation(motion.Rotation);
            actor.Set(ZDOVars.s_velHash,motion.Velocity);actor.Set(ZDOVars.s_bodyVelHash,motion.Velocity);actor.Set(ZDOVars.s_bodyAVelHash,motion.Angular);
        }
        private static Scope Enter(Player player,bool context)
        {
            var scope=new Scope{Previous=simulating,Frame=GameCombatContext.Current,Entered=true};
            if(context)
            {
                var motion=Remember(player);var state=InventoryMoveGame.State(player.GetZDOID());
                if(motion.Frame==null||!ReferenceEquals(motion.Frame.State,state))motion.Frame=new GameCombatContext.Frame
                {Player=player,State=state,Equipment=GameCombatEquipment.Equipped(state),Effects=GameAttackResources.Effects(state)};
                motion.Frame.Weapon=motion.Frame.Equipment.FirstOrDefault(item=>item.IsWeapon());
                GameCombatContext.Current=motion.Frame;
            }
            simulating=player;return scope;
        }
        private static void Leave(Scope scope)
        {if(scope?.Entered==true){simulating=scope.Previous;GameCombatContext.Current=scope.Frame;}}
        [HarmonyPatch(typeof(Character),nameof(Character.CustomFixedUpdate))]
        private static class PhysicsStep
        {
            private static void Prefix(Character __instance,float dt,out Scope __state)
            {
                __state=null;if(!(__instance is Player player)||!Managed(player))return;
                __state=Enter(player,true);
                var input=GameMovementControl.Read(player.GetZDOID());
                player.m_moveDir=input?.Move??Vector3.zero;player.m_run=input?.Run??false;player.m_walk=input?.Walk??false;player.m_crouchToggled=input?.Crouch??false;
                player.m_debugFly=false;
                if(input!=null)player.SetLookDir(input.Look);
                if(player!=Player.m_localPlayer)player.UpdateCrouch(dt);
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
            {if(simulating!=__instance)return true;__result=__instance.GetCurrentAnimHash()==Player.s_animatorTagCutscene||__instance.InIntro()||__instance.m_sleeping;return false;}
        }
        [HarmonyPatch(typeof(Player),nameof(Player.HaveStamina))]
        private static class Stamina
        {private static bool Prefix(Player __instance,float amount,ref bool __result){if(simulating!=__instance)return true;__result=InventoryMoveGame.Stamina(__instance.GetZDOID())>amount;return false;}}
        [HarmonyPatch(typeof(Player),nameof(Player.UseStamina))]
        private static class StaminaUse
        {
            private static bool Prefix(Player __instance,float v,bool __runOriginal)
            {
                if(!__runOriginal||simulating!=__instance)return true;
                InventoryMoveGame.SpendStamina(__instance.GetZDOID(),v*Game.m_staminaRate,__instance.m_staminaRegenDelay);return false;
            }
        }
        [HarmonyPatch(typeof(Player),"CheckRun")]
        private static class Running
        {
            private static bool Prefix(Player __instance,Vector3 moveDir,float dt,ref bool __result)
            {
                if(simulating!=__instance)return true;
                __result=false;
                if(!__instance.m_run||moveDir.magnitude<.1f||__instance.IsCrouching()||__instance.IsEncumbered()||__instance.InDodge())return false;
                float drain=__instance.m_runStaminaDrain*Mathf.Lerp(1,.5f,__instance.GetSkillFactor(Skills.SkillType.Run));
                drain*=1-__instance.GetEquipmentMovementModifier()+__instance.GetEquipmentRunStaminaModifier();
                __instance.m_seman.ModifyRunStaminaDrain(drain,ref drain,moveDir,true);
                __instance.UseStamina(dt*drain*Game.m_moveStaminaRate);
                if(!__instance.HaveStamina(0))return false;
                __instance.m_runSkillImproveTimer+=dt;
                if(__instance.m_runSkillImproveTimer>1){__instance.m_runSkillImproveTimer=0;__instance.RaiseSkill(Skills.SkillType.Run,1);}
                __instance.ClearActionQueue();__result=true;return false;
            }
        }
    }
}
