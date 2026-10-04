using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameDashAction
    {
        internal const string Dash="movement.dash";
        internal static void Send(Player player)
        {
            var direction=player.m_moveDir;direction.y=0;
            if(direction.sqrMagnitude<.01f)direction=Vector3.ProjectOnPlane(player.transform.forward,Vector3.up);
            direction.Normalize();
            InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand{Kind=PlayerActionKind.UseOn,Definition=Dash,Position=new[]{direction.x,direction.y,direction.z}});
        }
        internal static PlayerActionPlan Prepare(ZDO actor,InventoryMoveRequest request,PlayerSnapshot state)
        {
            var instance=ZNetScene.instance.FindInstance(actor.m_uid);var player=instance?instance.GetComponent<Player>():null;
            if(request.Action.Amount!=1||request.Gameplay.TargetId!=0||!player||!player.m_body||!player.m_animator||!player.m_zanim||GameDeathProgress.IsDead(state)||PlayerResources.Read(state,"health")<=0||
                player.IsDead()||player.IsTeleporting()||player.InIntro()||player.m_sleeping||player.InAttack()||player.InDodge()||player.InMinorAction()||player.IsStaggering()||
                DynamicCombat.IsDashing(player)||GameGuardianPower.Active(actor.m_uid))throw new InvalidOperationException("Character cannot dash");
            bool canMove=false;GameCombatContext.Run(player,state,null,null,()=>canMove=player.CanMove());
            if(!canMove)throw new InvalidOperationException("Movement is blocked");
            if(GameCombatEquipment.Equipped(state).Any(item=>item.m_shared.m_name=="$item_hammer"||item.m_shared.m_name=="$item_cultivator"))throw new InvalidOperationException("Held tool prevents dash");
            var direction=new Vector3(request.Gameplay.Position[0],request.Gameplay.Position[1],request.Gameplay.Position[2]);float length=direction.sqrMagnitude;
            if(float.IsNaN(length)||float.IsInfinity(length)||Mathf.Abs(direction.y)>.001f||Mathf.Abs(length-1)>.01f)throw new InvalidOperationException("Invalid dash direction");
            float cost=global::Overhaul.Utility.OverhaulConfig.DashStaminaCost?.Value??10f;
            double stamina=PlayerResources.Read(state,"stamina");if(cost>0&&stamina<=cost)throw new InvalidOperationException("Insufficient dash stamina");
            var rows=new[]{PlayerResources.Row("stamina",Math.Max(0,stamina-cost)),PlayerResources.Row(PlayerResources.StaminaDelay,player.m_staminaRegenDelay)};
            return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(request.Action.Operation,state.Revision,rows),new Dictionary<long,ObjectRecord>()),()=>
            {if(player&&!player.IsDead()&&!player.IsTeleporting())DynamicCombat.BeginDash(player,direction);});
        }
    }
}

