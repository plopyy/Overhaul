using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameWeaponReload
    {
        private sealed class Reload
        {
            internal ItemDrop.ItemData Weapon;
            internal int Slot;
            internal double Last,Seen,Elapsed,Paid,Prepared,Duration,Next;
            internal bool Pending,Cancelled,Loaded,Initial;
            internal Action<int,int,bool> Reply;
        }
        private static readonly Dictionary<ZDOID,Reload> reloads=new Dictionary<ZDOID,Reload>();
        [ThreadStatic] private static bool feedback;
        [ThreadStatic] private static int clientScope;
        private static double nextControl;
        private static ItemDrop.ItemData clientWeapon;
        private static bool Same(ItemDrop.ItemData a,ItemDrop.ItemData b)=>a.m_dropPrefab==b.m_dropPrefab && a.m_quality==b.m_quality &&
            a.m_customData.Count==b.m_customData.Count && a.m_customData.All(p=>b.m_customData.TryGetValue(p.Key,out var value)&&value==p.Value);
        private static ItemDrop.ItemData Weapon(PlayerSnapshot state,int slot)
        {
            var row=state.Rows.FirstOrDefault(r=>r.Table=="inventory" && Convert.ToInt32(r.Values[2])*256+Convert.ToInt32(r.Values[1])==slot)?.Values;
            if(row==null)return null;var item=PlayerInventoryView.ReadItem(row,null,true);
            item.m_customData=state.Rows.Where(r=>r.Table=="item_data" && Convert.ToInt32(r.Values[2])*256+Convert.ToInt32(r.Values[1])==slot).ToDictionary(r=>(string)r.Values[3],r=>(string)r.Values[4]);
            return item.m_equipped && item.m_shared.m_attack?.m_requiresReload==true && (!item.m_shared.m_useDurability || item.m_durability>0)?item:null;
        }
        internal static void Control(ZDO actor,bool cancel,int x,int y,Action<int,int,bool> reply)
        {
            if(actor==null || x<0 || x>=256 || y<0 || y>=256)return;
            var state=InventoryMoveGame.State(actor.m_uid);if(state==null)return;
            int slot=y*256+x;double now=Time.timeAsDouble;
            if(reloads.TryGetValue(actor.m_uid,out var current))
            {
                if(current.Slot!=slot)return;
                if(cancel){Capture(actor,current);current.Cancelled=true;Queue(actor.m_uid,current);return;}
                current.Seen=now;return;
            }
            if(cancel || PlayerResources.Read(state,"health")<=0 || GameAttackRuntime.Active(actor.m_uid))return;
            var weapon=Weapon(state,slot);if(weapon==null)return;
            var go=ZNetScene.instance.FindInstance(actor.m_uid);var player=go?go.GetComponent<Player>():null;
            if(!player || player.IsDead() || player.IsTeleporting() || player.m_grappling>0 || player.m_blockReload>0)return;
            if(weapon.m_shared.m_attack.m_reloadEitrDrain>0 && PlayerResources.Read(state,"eitr")<=weapon.m_shared.m_attack.m_reloadEitrDrain)return;
            var reload=new Reload{Weapon=weapon,Slot=slot,Last=now,Seen=now,Duration=GameWeaponPreparation.Duration(state,weapon,true),Reply=reply};
            reloads[actor.m_uid]=reload;Queue(actor.m_uid,reload);
        }
        private static void Capture(ZDO actor,Reload reload)
        {
            double now=Time.timeAsDouble,elapsed=Math.Max(0,Math.Min(now,reload.Seen+1.5)-reload.Last);reload.Last=now;
            if(reload.Loaded || reload.Cancelled)return;
            var state=InventoryMoveGame.State(actor.m_uid);var weapon=state==null?null:Weapon(state,reload.Slot);
            var go=ZNetScene.instance.FindInstance(actor.m_uid);var player=go?go.GetComponent<Player>():null;
            if(weapon==null || !Same(weapon,reload.Weapon) || !player || player.IsDead() || player.IsTeleporting() || player.InDodge() || player.IsStaggering() || now-reload.Seen>1.5)
            {reload.Cancelled=true;return;}
            if(!GameAttackRuntime.Active(actor.m_uid) && !player.InAttack() && player.m_grappling<=0 && player.m_blockReload<=0)
                reload.Elapsed=Math.Min(reload.Duration,reload.Elapsed+elapsed);
        }
        private static void Queue(ZDOID actor,Reload reload,bool final=false)
        {
            if(reload.Pending&&!final)return;reload.Pending=true;
            double paid=reload.Paid;bool exhausted=false;
            if(!InventoryMoveGame.Progress(actor,state=>
            {
                paid=reload.Elapsed;var rows=new List<PlayerChange>();
                if(!reload.Initial)
                {
                    // Native UpdateWeaponLoading pays this entry cost before
                    // the per-second minor-action drain starts.
                    double entry=Math.Max(0,reload.Weapon.m_shared.m_attack.m_reloadEitrDrain)*Game.m_eitrRate*(1-Mathf.Clamp01(PlayerCraftProgressGame.Bonus(state,"eitr_cost")));
                    if(entry>0)
                    {
                        double eitr=PlayerResources.Read(state,"eitr");exhausted=eitr<=entry;
                        rows.Add(PlayerResources.Row("eitr",Math.Max(0,eitr-entry)));
                        rows.Add(PlayerResources.Row(PlayerResources.EitrDelay,Game.instance.m_playerPrefab.GetComponent<Player>().m_eitrRegenDelay));
                        state=PlayerProgressService.Overlay(state,rows);
                    }
                    reload.Initial=true;
                }
                var step=GameWeaponPreparation.Advance(state,reload.Weapon,true,Math.Max(reload.Paid,reload.Prepared),paid);
                reload.Prepared=paid;exhausted|=step.Exhausted;
                foreach(var row in step.Changes){rows.RemoveAll(r=>PlayerProgressService.SameKey(r,row));rows.Add(row);}
                return rows;
            },()=>
            {
                reload.Pending=false;reload.Paid=Math.Max(paid,reload.Paid);reload.Cancelled|=exhausted;
                if(reload.Cancelled)
                {if(reloads.TryGetValue(actor,out var active)&&active==reload)reloads.Remove(actor);reload.Reply?.Invoke(reload.Slot%256,reload.Slot/256,false);}
                else if(!reload.Loaded && reload.Paid>=reload.Duration)
                {reload.Loaded=true;reload.Reply?.Invoke(reload.Slot%256,reload.Slot/256,true);}
            }))reload.Pending=false;
        }
        internal static void Tick(ZDO actor)
        {
            if(!reloads.TryGetValue(actor.m_uid,out var reload))return;
            if(Time.timeAsDouble<reload.Next)return;reload.Next=Time.timeAsDouble+.2;
            if(reload.Loaded)
            {
                var state=InventoryMoveGame.State(actor.m_uid);var weapon=state==null?null:Weapon(state,reload.Slot);
                if(weapon!=null&&Same(weapon,reload.Weapon))return;
                reload.Cancelled=true;
            }
            Capture(actor,reload);Queue(actor.m_uid,reload);
        }
        internal static Action Prepare(ZDOID actor,ItemDrop.ItemData weapon,int slot)
        {
            if(!reloads.TryGetValue(actor,out var reload) || !reload.Loaded || reload.Cancelled || reload.Slot!=slot || !Same(weapon,reload.Weapon))
                throw new InvalidOperationException("Weapon has no confirmed server reload");
            return ()=>{reloads.Remove(actor);reload.Reply?.Invoke(slot%256,slot/256,false);};
        }
        internal static void Close(ZDO actor)
        {if(reloads.TryGetValue(actor.m_uid,out var reload)){Capture(actor,reload);reload.Cancelled=true;Queue(actor.m_uid,reload,true);reloads.Remove(actor.m_uid);}}
        internal static void Clear(){reloads.Clear();clientWeapon=null;clientScope=0;}
        internal static void Receive(int x,int y,bool loaded)
        {
            var player=Player.m_localPlayer;if(!player || !PlayerSessionGame.Managed)return;
            var item=player.GetInventory().GetItemAt(x,y);
            if(loaded && (item==null || !item.m_equipped || item.m_shared.m_attack?.m_requiresReload!=true))return;
            feedback=true;
            try{player.CancelReloadAction();player.SetWeaponLoaded(loaded?item:null);}
            finally{feedback=false;}
        }
        [HarmonyPatch(typeof(Player),"UpdateWeaponLoading")]
        private static class Input
        {
            private static bool Prefix(Player __instance,ItemDrop.ItemData weapon)
            {
                if(__instance!=Player.m_localPlayer || !PlayerSessionGame.Managed)return true;
                if(clientWeapon!=null && clientWeapon!=weapon)
                {InventoryMoveGame.Client?.ReloadControl(true,clientWeapon.m_gridPos.x,clientWeapon.m_gridPos.y);clientWeapon=null;Receive(0,0,false);}
                if(weapon?.m_shared.m_attack?.m_requiresReload!=true)return false;
                if(__instance.m_weaponLoaded==weapon)return false;
                if(clientWeapon!=weapon || Time.timeAsDouble>=nextControl)
                {InventoryMoveGame.Client?.ReloadControl(false,weapon.m_gridPos.x,weapon.m_gridPos.y);clientWeapon=weapon;nextControl=Time.timeAsDouble+.4;}
                if(!__instance.IsReloadActionQueued())__instance.QueueReloadAction();
                return false;
            }
        }
        [HarmonyPatch(typeof(Player),"UpdateActionQueue")]
        private static class DrainScope
        {
            private static void Prefix(Player __instance,out bool __state)
            {__state=__instance==Player.m_localPlayer && PlayerSessionGame.Managed && __instance.m_actionQueue.Count>0 && __instance.m_actionQueue[0].m_type==Player.MinorActionData.ActionType.Reload;if(__state)clientScope++;}
            private static Exception Finalizer(Exception __exception,bool __state){if(__state)clientScope--;return __exception;}
        }
        [HarmonyPatch(typeof(Player),nameof(Player.UseStamina))]
        private static class Stamina{private static bool Prefix(Player __instance)=>clientScope==0 || __instance!=Player.m_localPlayer;}
        [HarmonyPatch(typeof(Player),nameof(Player.UseEitr))]
        private static class Eitr{private static bool Prefix(Player __instance)=>clientScope==0 || __instance!=Player.m_localPlayer;}
        [HarmonyPatch(typeof(Player),"SetWeaponLoaded")]
        private static class Loaded{private static bool Prefix(Player __instance)=>feedback || __instance!=Player.m_localPlayer || !PlayerSessionGame.Managed;}
        [HarmonyPatch(typeof(Player),"CancelReloadAction")]
        private static class Cancel
        {
            private static void Prefix(Player __instance)
            {if(!feedback && __instance==Player.m_localPlayer && PlayerSessionGame.Managed && clientWeapon!=null)
                {InventoryMoveGame.Client?.ReloadControl(true,clientWeapon.m_gridPos.x,clientWeapon.m_gridPos.y);clientWeapon=null;}}
        }
    }
}
