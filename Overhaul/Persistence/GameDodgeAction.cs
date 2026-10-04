using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameDodgeAction
    {
        internal const string Dodge="movement.dodge";
        private sealed class Roll{internal Player Player;internal double Started;internal bool Animated;internal AnimatorCullingMode Culling;}
        private static readonly Dictionary<ZDOID,Roll> rolls=new Dictionary<ZDOID,Roll>();
        internal static void Forget(ZDOID actor)
        {
            if(!rolls.TryGetValue(actor,out var roll))return;rolls.Remove(actor);
            if(!roll.Player)return;
            roll.Player.m_inDodge=false;roll.Player.m_dodgeInvincible=false;roll.Player.m_dodgeInvincibleCached=false;
            if(roll.Player.m_animator)roll.Player.m_animator.cullingMode=roll.Culling;
            if(roll.Player.m_nview&&roll.Player.m_nview.IsValid())roll.Player.m_nview.GetZDO().Set(ZDOVars.s_dodgeinv,false);
        }
        internal static void Clear(){foreach(var actor in rolls.Keys.ToArray())Forget(actor);}
        internal static bool Active(Player player)=>player&&rolls.ContainsKey(player.GetZDOID());
        internal static void Tick(Player player)
        {
            if(!player||!rolls.TryGetValue(player.GetZDOID(),out var roll))return;
            bool animated=player.m_animator&&(player.m_animator.GetBool(Player.s_animatorTagDodge)||player.GetNextOrCurrentAnimHash()==Player.s_animatorTagDodge);
            double elapsed=Time.timeAsDouble-roll.Started;
            if(player.IsDead()||player.IsStaggering()||elapsed>5||!animated&&(roll.Animated||elapsed>.5)){Forget(player.GetZDOID());return;}
            roll.Animated|=animated;player.m_inDodge=true;player.m_dodgeInvincibleCached=animated;
            player.m_nview.GetZDO().Set(ZDOVars.s_dodgeinv,animated);
        }
        internal static PlayerChange[] Cost(PlayerSnapshot state,Player player)
        {
            float cost=0;GameCombatContext.Run(player,state,null,null,()=>cost=player.GetDodgeStaminaUse());
            // Native checks the unscaled cost, then UseStamina applies the world rate.
            if(PlayerResources.Read(state,"stamina")<=cost)throw new InvalidOperationException("Not enough stamina to dodge");
            var changes=PlayerCraftProgressGame.Raise(state,Skills.SkillType.Dodge,.1f).ToList();
            if(cost>0){changes.Add(PlayerResources.Row("stamina",Math.Max(0,PlayerResources.Read(state,"stamina")-cost*Game.m_staminaRate)));changes.Add(PlayerResources.Row(PlayerResources.StaminaDelay,player.m_staminaRegenDelay));}
            return changes.ToArray();
        }
        internal static PlayerActionPlan Prepare(ZDO actor,InventoryMoveRequest request,PlayerSnapshot state)
        {
            var instance=ZNetScene.instance.FindInstance(actor.m_uid);var player=instance?instance.GetComponent<Player>():null;
            var p=request.Gameplay.Position;var direction=new Vector3(p[0],p[1],p[2]);
            if(request.Action.Amount!=1||request.Gameplay.TargetId!=0||Mathf.Abs(direction.y)>.001f||Mathf.Abs(direction.sqrMagnitude-1)>.01f||!player||!player.m_body||!player.m_animator||!player.m_zanim||
                GameDeathProgress.IsDead(state)||PlayerResources.Read(state,"health")<=0||!player.IsOnGround()||player.IsTeleporting()||player.InIntro()||player.m_sleeping||player.InAttack()||player.IsStaggering()||Active(player)||GameGuardianPower.Active(actor.m_uid))
                throw new InvalidOperationException("Character cannot dodge");
            bool encumbered=false;GameCombatContext.Run(player,state,null,null,()=>encumbered=player.IsEncumbered());if(encumbered)throw new InvalidOperationException("Character is encumbered");
            var changes=Cost(state,player);
            return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(request.Action.Operation,state.Revision,changes),new Dictionary<long,ObjectRecord>()),()=>
            {
                if(!player||player.IsDead())return;
                rolls[actor.m_uid]=new Roll{Player=player,Started=Time.timeAsDouble,Culling=player.m_animator.cullingMode};player.m_animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
                player.ClearActionQueue();player.m_queuedDodgeTimer=0;player.m_inDodge=true;player.m_dodgeInvincible=true;player.m_beenHitWhileDodging=false;
                player.transform.rotation=Quaternion.LookRotation(direction);player.m_body.rotation=player.transform.rotation;
                player.m_zanim.SetTrigger("dodge");player.AddNoise(5);player.m_dodgeEffects.Create(player.transform.position,Quaternion.identity,player.transform,1,-1,actor.m_uid);
            });
        }
        [HarmonyPatch(typeof(Player),"Dodge")]
        private static class Intent
        {
            private static bool Prefix(Player __instance,Vector3 dodgeDir)
            {
                if(__instance!=Player.m_localPlayer||!PlayerSessionGame.Managed)return !GameMovementRuntime.Managed(__instance);
                dodgeDir.y=0;dodgeDir.Normalize();if(dodgeDir.sqrMagnitude>.99f)InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand
                {Kind=PlayerActionKind.UseOn,Definition=Dodge,Position=new[]{dodgeDir.x,0,dodgeDir.z}});return false;
            }
        }
        [HarmonyPatch(typeof(Player),"UpdateDodge")]
        private static class NativeQueue
        {
            private static bool Prefix(Player __instance)
            {
                if(GameMovementRuntime.Managed(__instance)){Tick(__instance);return false;}
                if(!PlayerSessionGame.Managed||__instance!=Player.m_localPlayer)return true;
                __instance.m_inDodge=__instance.m_animator.GetBool(Player.s_animatorTagDodge)||__instance.GetNextOrCurrentAnimHash()==Player.s_animatorTagDodge;return false;
            }
        }
        [HarmonyPatch(typeof(Player),nameof(Player.InDodge))]
        private static class State
        {private static bool Prefix(Player __instance,ref bool __result){if(!GameMovementRuntime.Managed(__instance))return true;__result=Active(__instance);return false;}}
        [HarmonyPatch(typeof(Player),nameof(Player.IsDodgeInvincible))]
        private static class Invincibility
        {private static bool Prefix(Player __instance,ref bool __result){if(!GameMovementRuntime.Managed(__instance))return true;__result=Active(__instance)&&__instance.m_dodgeInvincibleCached;return false;}}
        [HarmonyPatch(typeof(CharacterAnimEvent),"OnAnimatorMove")]
        private static class RootMotion
        {
            private static bool Prefix(CharacterAnimEvent __instance)
            {if(!(__instance.m_character is Player player)||!GameMovementRuntime.Managed(player))return true;player.AddRootMotion(__instance.m_animator.deltaPosition);return false;}
        }
    }
}
