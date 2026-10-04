using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameJumpAction
    {
        internal const string Jump="movement.jump";
        [ThreadStatic] private static Player publishing;
        internal sealed class Result
        {internal Vector3 Velocity;internal PlayerChange[] Changes;internal bool Tired;}
        internal static Result Calculate(PlayerSnapshot state,Player player,Vector3 velocity,Vector3 direction)
        {
            Result result=null;
            GameCombatContext.Run(player,state,null,null,()=>
            {
                float speed=player.m_speed;player.m_seman.ApplyStatusEffectSpeedMods(ref speed,player.m_currentVel);
                bool tired=PlayerResources.Read(state,"stamina")<=player.m_jumpStaminaUsage||speed<=0;
                float factor=1+player.GetSkillFactor(Skills.SkillType.Jump)*.4f;
                // Overhaul deliberately uses a vertical impulse on every slope.
                velocity.y=Mathf.Max(velocity.y,player.m_jumpForce*factor);
                velocity+=direction*player.m_jumpForceForward*factor;
                if(tired)velocity*=player.m_jumpForceTiredFactor;
                player.m_seman.ApplyStatusEffectJumpMods(ref velocity);
                if(float.IsNaN(velocity.sqrMagnitude)||float.IsInfinity(velocity.sqrMagnitude))throw new InvalidOperationException("Invalid server jump velocity");
                if(velocity.x<=0&&velocity.y<=0&&velocity.z<=0)throw new InvalidOperationException("Jump is prevented by status effects");
                float baseCost=global::Overhaul.Utility.OverhaulConfig.JumpUseStamina?.Value==false?0:global::Overhaul.Utility.OverhaulConfig.JumpStaminaDrain?.Value??player.m_jumpStaminaUsage;
                float cost=baseCost*(1-player.GetEquipmentMovementModifier()+player.GetEquipmentJumpStaminaModifier());
                player.m_seman.ModifyJumpStaminaUsage(cost,ref cost,true);cost*=Game.m_moveStaminaRate*Game.m_staminaRate;
                var changes=new List<PlayerChange>();
                if(cost>0){changes.Add(PlayerResources.Row("stamina",Math.Max(0,PlayerResources.Read(state,"stamina")-cost)));changes.Add(PlayerResources.Row(PlayerResources.StaminaDelay,player.m_staminaRegenDelay));}
                if(!tired)GamePlayerHit.Merge(changes,PlayerCraftProgressGame.Raise(state,Skills.SkillType.Jump,1));
                GamePlayerHit.Merge(changes,new[]{PlayerCraftProgressGame.Increment(state,"statistics:0:values",((int)PlayerStatType.Jumps).ToString(CultureInfo.InvariantCulture),1)});
                result=new Result{Velocity=velocity,Changes=changes.ToArray(),Tired=tired};
            });
            return result;
        }
        internal static PlayerActionPlan Prepare(ZDO actor,InventoryMoveRequest request,PlayerSnapshot state)
        {
            var instance=ZNetScene.instance.FindInstance(actor.m_uid);var player=instance?instance.GetComponent<Player>():null;
            if(request.Action.Amount!=1||request.Gameplay.TargetId!=0||!player||!player.m_body||!player.m_animator||!player.m_zanim||GameStaffGuardRuntime.Blocked(state)||GameDeathProgress.IsDead(state)||PlayerResources.Read(state,"health")<=0||
                player.IsTeleporting()||player.InIntro()||player.m_sleeping||player.InDodge()||player.IsKnockedBack()||player.IsStaggering()||player.InAttack()||GameGuardianPower.Active(actor.m_uid)||
                !player.IsOnGround()&&(!player.InLiquidSwimDepth()||player.m_hitWorldTime>=.25f))throw new InvalidOperationException("Character cannot jump");
            bool encumbered=false;GameCombatContext.Run(player,state,null,null,()=>encumbered=player.IsEncumbered());
            if(encumbered)throw new InvalidOperationException("Character is encumbered");
            var direction=player.m_slipping?player.m_currentVel.normalized:GameMovementControl.Read(actor.m_uid)?.Move??Vector3.zero;
            var result=Calculate(state,player,player.m_body.linearVelocity,direction);
            return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(request.Action.Operation,state.Revision,result.Changes),new Dictionary<long,ObjectRecord>()),()=>
            {
                if(!player||player.IsDead())return;
                var previous=publishing;publishing=player;
                try{player.ForceJump(result.Velocity,true);}finally{publishing=previous;}
            });
        }
        [HarmonyPatch(typeof(Character),nameof(Character.Jump))]
        private static class Intent
        {
            private static bool Prefix(Character __instance)
            {
                if(!(__instance is Player player))return true;
                if(player==Player.m_localPlayer&&PlayerSessionGame.Managed)
                {return true;}
                return !GameMovementRuntime.Managed(player);
            }
        }
        // This is a notification after the local jump, not a permission request.
        [HarmonyPatch(typeof(Player),"OnJump")]
        private static class NotifyJump
        {
            private static void Postfix(Player __instance)
            {if(__instance==Player.m_localPlayer&&PlayerSessionGame.Managed&&publishing!=__instance)InventoryMoveGame.Client?.Jumped();}
        }
        internal static PlayerActionPlan Observe(Player player,PlayerSnapshot state)
        {
            if(!player||GameDeathProgress.IsDead(state)||player.IsTeleporting()||player.InIntro())return null;
            var result=Calculate(state,player,Vector3.zero,Vector3.zero);
            return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(Guid.NewGuid().ToString("N"),state.Revision,result.Changes),new Dictionary<long,ObjectRecord>()),()=>{});
        }
        [HarmonyPatch(typeof(Player),"OnJump")]
        private static class AlreadyPaid
        {
            [HarmonyPriority(Priority.First+400)]
            private static bool Prefix(Player __instance)
            {if(publishing!=__instance)return true;__instance.ClearActionQueue();return false;}
        }
    }
}

