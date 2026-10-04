using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class PlayerFurnitureGame
    {
        internal const string BedUse="bed.use",ChairUse="chair.use",Leave="furniture.leave";
        internal static PlayerActionPlan Prepare(ZDO actor,GameObject target,InventoryMoveRequest request,PlayerSnapshot state)
        {
            var player=ZNetScene.instance.FindInstance(actor.m_uid)?.GetComponent<Player>();
            if(!player||request.Action.Amount!=1||player.IsTeleporting()||player.InIntro()||player.IsAttached()||player.InAttack()||player.InDodge()||player.IsStaggering())
                throw new InvalidOperationException("Character cannot use furniture now");
            long id=actor.GetLong(ZDOVars.s_playerID,0);
            if(!Storage.ChestAccess.WardAccessAt(target.transform.position,id))throw new InvalidOperationException("Furniture access is denied");
            if(request.Gameplay.Definition==BedUse)
            {
                var bed=target.GetComponent<Bed>();if(!bed||!bed.m_spawnPoint||bed.GetOwner()!=0&&bed.GetOwner()!=id)throw new InvalidOperationException("Bed belongs to another character");
                Cover.GetCoverForPoint(bed.GetSpawnPoint(),out float cover,out bool roof,.5f);
                if(!roof||cover<.8f)throw new InvalidOperationException("Bed requires shelter");
                var point=bed.GetSpawnPoint();var saved=state.Rows.FirstOrDefault(row=>row.Table=="spawn"&&(string)row.Values[0]=="bed");
                bool current=bed.GetOwner()==id&&saved!=null&&Vector3.Distance(point,new Vector3(Convert.ToSingle(saved.Values[1]),Convert.ToSingle(saved.Values[2]),Convert.ToSingle(saved.Values[3])))<1;
                if(current)
                {
                    if(!EnvMan.CanSleep()||player.IsSensed()||!EffectArea.IsPointInsideArea(bed.transform.position,EffectArea.Type.Heat)||
                        GameCombatContext.Effects(state,player).Any(effect=>effect.NameHash()==SEMan.s_statusEffectWet)||GameAttachmentRuntime.Occupied(bed.m_spawnPoint))
                        throw new InvalidOperationException("Bed sleep conditions are not satisfied");
                    using(var world=new PlayerActionObjectGame(bed.m_nview.GetZDO()))
                        return world.Finish(new PlayerBatch(request.Action.Operation,state.Revision,Array.Empty<PlayerChange>()),()=>GameAttachmentRuntime.Attach(player,bed.m_nview,1,0));
                }
                var name=state.Rows.First(row=>row.Table=="state"&&(string)row.Values[0]=="player_name");
                using(var world=new PlayerActionObjectGame(bed.m_nview.GetZDO()))
                {
                    world.Set(ZDOVars.s_owner,id);world.Set(ZDOVars.s_ownerName,(string)name.Values[3]);
                    return world.Finish(new PlayerBatch(request.Action.Operation,state.Revision,new[]{new PlayerChange("spawn",false,"bed",(double)point.x,(double)point.y,(double)point.z)}));
                }
            }
            var chairs=target.GetComponentsInChildren<Chair>(true);int index=request.Gameplay.Variant;
            if(index<0||index>=chairs.Length)throw new InvalidOperationException("Unknown chair");var chair=chairs[index];
            if(!chair.m_attachPoint||!chair.gameObject.activeInHierarchy||Vector3.Distance(actor.GetPosition(),chair.m_attachPoint.position)>=chair.m_useDistance||
                GameAttachmentRuntime.Occupied(chair.m_attachPoint)||GameAttachmentRuntime.Cooling(actor.m_uid))throw new InvalidOperationException("Chair is unavailable");
            var previous=GameCombatContext.Current;
            try
            {
                GameCombatContext.Current=new GameCombatContext.Frame{Player=player,State=state,Equipment=GameCombatEquipment.Equipped(state),Effects=GameCombatContext.Effects(state,player)};
                if(player.IsEncumbered())throw new InvalidOperationException("Encumbered character cannot sit");
            }
            finally{GameCombatContext.Current=previous;}
            var view=target.GetComponent<ZNetView>();
            using(var world=new PlayerActionObjectGame(view.GetZDO()))
                return world.Finish(new PlayerBatch(request.Action.Operation,state.Revision,Array.Empty<PlayerChange>()),()=>GameAttachmentRuntime.Attach(player,view,2,index));
        }
        internal static PlayerActionPlan Detach(ZDO actor,InventoryMoveRequest request,PlayerSnapshot state)
        {
            var player=ZNetScene.instance.FindInstance(actor.m_uid)?.GetComponent<Player>();
            if(!player||player.IsSleeping()||request.Action.Amount!=1||request.Gameplay.TargetId!=0)throw new InvalidOperationException("Character cannot leave furniture now");
            return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(request.Action.Operation,state.Revision,Array.Empty<PlayerChange>()),new System.Collections.Generic.Dictionary<long,ObjectRecord>()),()=>GameAttachmentRuntime.Detach(player));
        }
        private static bool Managed(Humanoid player)=>PlayerSessionGame.Managed&&player==Player.m_localPlayer;
        private static void Send(Component furniture,string definition)
        {
            var view=furniture.GetComponentInParent<ZNetView>();if(!view||!view.IsValid())return;
            var id=view.GetZDO().m_uid;int index=definition==ChairUse?Array.IndexOf(view.GetComponentsInChildren<Chair>(true),furniture as Chair):0;
            InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand{Kind=PlayerActionKind.UseOn,Definition=definition,TargetUser=id.UserID,TargetId=id.ID,Variant=index});
        }
        [HarmonyPatch(typeof(Bed),nameof(Bed.Interact))]
        private static class BedIntent
        {private static bool Prefix(Bed __instance,Humanoid human,bool repeat,ref bool __result){if(!Managed(human))return true;__result=false;if(!repeat)Send(__instance,BedUse);return false;}}
        [HarmonyPatch(typeof(Chair),nameof(Chair.Interact))]
        private static class ChairIntent
        {private static bool Prefix(Chair __instance,Humanoid human,bool hold,ref bool __result){if(!Managed(human))return true;__result=false;if(!hold)Send(__instance,ChairUse);return false;}}
        [HarmonyPatch(typeof(Bed),"RPC_SetOwner")]
        private static class BedOwner
        {private static bool Prefix()=>!GameCreatureAuthority.Enabled;}
    }
}
