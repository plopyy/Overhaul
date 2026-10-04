using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using HarmonyLib;

namespace Overhaul.Persistence
{
    internal static class GameStaffGuardRuntime
    {
        private sealed class Control
        {internal bool Held,Pending,Loaded;internal double Seen,Next,NextView;internal SE_StaffGuard Guard;}
        private static readonly Dictionary<ZDOID,Control> controls=new Dictionary<ZDOID,Control>();
        private static bool clientHeld,sent,last;private static double next;
        private static Control Get(ZDOID actor){if(!controls.TryGetValue(actor,out var value))controls.Add(actor,value=new Control());return value;}
        internal static void ControlInput(ZDO actor,bool held)
        {if(actor==null||InventoryMoveGame.State(actor.m_uid)==null)return;var input=Get(actor.m_uid);input.Held=held;input.Seen=Time.timeAsDouble;}
        internal static bool Held(Player player)=>player&&controls.TryGetValue(player.GetZDOID(),out var input)&&input.Held&&Time.timeAsDouble-input.Seen<1.5;
        internal static bool CanHold(PlayerSnapshot state,Player player)
            =>Held(player)&&PlayerResources.Read(state,"health")>0&&!GameDeathProgress.IsDead(state)&&!player.IsTeleporting()&&!player.InIntro()&&
              !player.IsAttached()&&!player.InDodge()&&!DynamicCombat.IsDashing(player)&&!player.IsStaggering()&&GameCombatEquipment.Equipped(state).Any(IsStaff);
        internal static SE_StaffGuard Restore(PlayerSnapshot state,Player player)
        {
            var rows=state.Rows.Where(row=>(row.Table=="status"||row.Table=="status_data")&&Convert.ToInt32(row.Values[0])==GameStaffGuardRules.Id).ToArray();
            return rows.Length==0?null:(SE_StaffGuard)GameStatusCodec.Restore(rows,player);
        }
        internal static void Committed(ZDOID actor,PlayerSnapshot state)
        {if(actor.IsNone())return;var input=Get(actor);input.Guard=Restore(state,null);input.Loaded=true;var instance=ZNetScene.instance?ZNetScene.instance.FindInstance(actor):null;var player=instance?instance.GetComponent<Player>():null;GameStaffGuardView.Publish(player,input.Guard);input.NextView=Time.timeAsDouble+1;}
        internal static SE_StaffGuard Read(Player player)
        {
            if(!player)return null;
            if(GameMovementRuntime.Managed(player))
            {var input=Get(player.GetZDOID());if(!input.Loaded)Committed(player.GetZDOID(),InventoryMoveGame.State(player.GetZDOID()));return input.Guard;}
            return player.GetSEMan()?.GetStatusEffect(GameStaffGuardRules.Id) as SE_StaffGuard;
        }
        internal static bool Blocked(PlayerSnapshot state)=>state.Rows.Any(row=>row.Table=="status_data"&&Convert.ToInt32(row.Values[0])==GameStaffGuardRules.Id&&((string)row.Values[1]=="SE_StaffGuard.m_guardCast"||(string)row.Values[1]=="SE_StaffGuard.m_guardStun")&&Convert.ToDouble(row.Values[2])>0);
        internal static bool Casting(Player player)=>Read(player)?.m_guardCast>0;
        internal static bool Stunned(Player player)=>Read(player)?.m_guardStun>0;
        internal static void Input(bool held){clientHeld=held;ClientTick();}
        internal static void ClientTick()
        {
            if(!PlayerSessionGame.Managed||!Player.m_localPlayer)return;
            bool held=clientHeld&&!Player.m_localPlayer.IsDead();if(sent&&last==held&&(!held||Time.timeAsDouble<next))return;
            InventoryMoveGame.Client?.StaffGuardControl(held);last=held;sent=true;next=Time.timeAsDouble+.1;
        }
        internal static void ClearClient(){clientHeld=sent=last=false;next=0;}
        internal static void Forget(ZDOID actor)=>controls.Remove(actor);
        internal static void Tick(ZDO actor)
        {
            if(actor==null)return;var instance=ZNetScene.instance.FindInstance(actor.m_uid);var player=instance?instance.GetComponent<Player>():null;
            if(!GameMovementRuntime.Managed(player))return;var input=Get(actor.m_uid);
            if(input.Pending||Time.timeAsDouble<input.Next)return;input.Next=Time.timeAsDouble+.1;
            var guard=Read(player);if(guard?.m_guardActive==true&&Time.timeAsDouble>=input.NextView){GameStaffGuardView.Publish(player,guard);input.NextView=Time.timeAsDouble+1;}if(!Held(player)&&guard==null)return;
            input.Pending=true;
            if(!InventoryMoveGame.TimedAction(player,state=>
            {
                var saved=Restore(state,player);var staff=GameCombatEquipment.Equipped(state).FirstOrDefault(IsStaff);
                bool held=CanHold(state,player);
                SE_StaffGuard changed=null;bool casting=false;
                if(held&&(saved==null||!saved.m_guardActive&&saved.m_guardCast<=0&&saved.m_guardStun<=0&&!saved.m_guardBroken))
                {
                    var template=ObjectDB.instance.GetStatusEffect(GameStaffGuardRules.Id) as SE_StaffGuard;
                    if(template){changed=GameStaffGuardRules.Begin(saved,template,staff);casting=true;}
                }
                else if(saved!=null&&!held&&(saved.m_guardActive||saved.m_guardCast>0||saved.m_guardBroken))
                {changed=(SE_StaffGuard)saved.Clone();GameStaffGuardRules.Advance(changed,false,0,0);}
                if(changed==null){input.Pending=false;return null;}
                var rows=changed.IsDone()?new[]{new PlayerChange("status",true,GameStaffGuardRules.Id)}:GameStatusCodec.Delta(state,changed,ZDOID.None);
                return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(Guid.NewGuid().ToString("N"),state.Revision,rows),new Dictionary<long,ObjectRecord>()),()=>
                {
                    input.Pending=false;
                    if(casting&&player)
                    {
                        GameAttackRuntime.Forget(player.GetZDOID());GameAttackRuntime.ForgetControls(player.GetZDOID());
                        if(player.m_zanim)player.m_zanim.SetTrigger("staff_shield");
                    }
                });
            }))input.Pending=false;
        }
        [HarmonyPatch(typeof(Player),nameof(Player.CanMove))]
        private static class Movement
        {private static bool Prefix(Player __instance,ref bool __result){if(!GameMovementRuntime.Managed(__instance)&&!(PlayerSessionGame.Managed&&__instance==Player.m_localPlayer)||!Casting(__instance)&&!Stunned(__instance))return true;__result=false;return false;}}
        internal static bool IsStaff(ItemDrop.ItemData item)=>item!=null&&item.IsWeapon()&&item.m_dropPrefab&&item.m_dropPrefab.name.StartsWith("Staff",StringComparison.OrdinalIgnoreCase);
    }
}

