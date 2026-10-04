using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameBowDraw
    {
        private sealed class Draw
        {
            internal ItemDrop.ItemData Weapon;
            internal int Slot;
            internal double Began,Seen,Paid,Prepared,End=-1,Next;
            internal bool Released,Pending,Consumed,Exhausted;
        }
        internal sealed class Shot
        {
            internal PlayerChange[] Changes;
            internal float Fraction;
            internal Action Confirm;
        }
        private static readonly Dictionary<ZDOID,Draw> draws=new Dictionary<ZDOID,Draw>();
        [ThreadStatic] private static int clientScope;
        private static ItemDrop.ItemData clientWeapon;
        private static double nextControl;
        private static bool IsBow(ItemDrop.ItemData item)=>item?.m_shared.m_attack?.m_bowDraw==true && !PlayerFishingCastGame.FloatPrefab(item.m_shared.m_attack.m_attackProjectile);
        private static bool Same(ItemDrop.ItemData a,ItemDrop.ItemData b)=>a.m_dropPrefab==b.m_dropPrefab && a.m_quality==b.m_quality &&
            a.m_customData.Count==b.m_customData.Count && a.m_customData.All(p=>b.m_customData.TryGetValue(p.Key,out var value)&&value==p.Value);
        private static ItemDrop.ItemData Weapon(PlayerSnapshot state,int slot)
        {
            var row=state.Rows.FirstOrDefault(r=>r.Table=="inventory" && Convert.ToInt32(r.Values[2])*256+Convert.ToInt32(r.Values[1])==slot)?.Values;
            if(row==null)return null;
            var item=PlayerInventoryView.ReadItem(row,null,true);item.m_customData=state.Rows.Where(r=>r.Table=="item_data" && Convert.ToInt32(r.Values[2])*256+Convert.ToInt32(r.Values[1])==slot)
                .ToDictionary(r=>(string)r.Values[3],r=>(string)r.Values[4]);
            return item.m_equipped && (!item.m_shared.m_useDurability || item.m_durability>0) && IsBow(item)?item:null;
        }
        internal static void Control(ZDO actor,int mode,int x,int y)
        {
            if(actor==null || mode<0 || mode>2 || x<0 || x>=256 || y<0 || y>=256)return;
            var state=InventoryMoveGame.State(actor.m_uid);if(state==null)return;
            double now=Time.timeAsDouble;int slot=y*256+x;
            if(draws.TryGetValue(actor.m_uid,out var draw))
            {
                if(draw.Slot!=slot)return;
                if(draw.End>=0 || draw.Consumed)return;
                draw.Seen=now;
                if(mode!=1){draw.End=now;draw.Released=mode==2;Queue(actor.m_uid,draw);}
                return;
            }
            if(mode!=1 || PlayerResources.Read(state,"health")<=0 || GameAttackRuntime.Active(actor.m_uid))return;
            var weapon=Weapon(state,slot);if(weapon==null)return;
            var instance=ZNetScene.instance.FindInstance(actor.m_uid);var player=instance?instance.GetComponent<Player>():null;
            if(!player || player.IsDead() || player.IsTeleporting() || player.IsStaggering() || player.InMinorAction() || player.InDodge())return;
            draws[actor.m_uid]=new Draw{Weapon=weapon,Slot=slot,Began=now,Seen=now,Next=now+.2};
        }
        private static double Until(Draw draw)=>Math.Max(0,(draw.End>=0?draw.End:Math.Min(Time.timeAsDouble,draw.Seen+1.5))-draw.Began);
        private static void Queue(ZDOID actor,Draw draw,bool final=false)
        {
            if(draw.Pending&&!final || draw.Consumed)return;
            draw.Pending=true;double paid=draw.Paid;bool exhausted=false;
            if(!InventoryMoveGame.Progress(actor,state=>
            {
                if(draw.Consumed)return Array.Empty<PlayerChange>();
                paid=Until(draw);var step=GameWeaponPreparation.Advance(state,draw.Weapon,false,Math.Max(draw.Paid,draw.Prepared),paid);
                // A close flush may share the same progress transaction with an
                // already queued tick. Reserve that interval during preparation
                // so the second callback only accounts for the remaining tail.
                // A failed transaction closes the endpoint; it is never reused.
                draw.Prepared=paid;
                exhausted=step.Exhausted;return step.Changes;
            },()=>
            {
                draw.Pending=false;draw.Paid=Math.Max(draw.Paid,paid);draw.Exhausted|=exhausted;
                if(draw.Exhausted && draw.End<0){draw.End=Time.timeAsDouble;draw.Released=false;}
                if((draw.Consumed || draw.End>=0&&!draw.Released) && draw.Paid>=Until(draw) && draws.TryGetValue(actor,out var active)&&active==draw)draws.Remove(actor);
            }))draw.Pending=false;
        }
        internal static void Tick(ZDO actor)
        {
            if(!draws.TryGetValue(actor.m_uid,out var draw))return;
            double now=Time.timeAsDouble;
            if(now<draw.Next)return;draw.Next=now+.2;
            var state=InventoryMoveGame.State(actor.m_uid);var current=state==null?null:Weapon(state,draw.Slot);
            if(draw.End<0 && (now-draw.Seen>1.5 || current==null || !Same(draw.Weapon,current) || PlayerResources.Read(state,"health")<=0))
            {draw.End=Math.Min(now,draw.Seen+1.5);draw.Released=false;}
            if(draw.End>=0 && draw.Released && now-draw.End>2)draw.Released=false;
            Queue(actor.m_uid,draw);
        }
        internal static Shot Prepare(ZDOID actor,PlayerSnapshot state,ItemDrop.ItemData weapon,int slot)
        {
            if(!draws.TryGetValue(actor,out var draw) || draw.Consumed || draw.Exhausted || draw.Slot!=slot || !Same(draw.Weapon,weapon) ||
                !draw.Released || draw.End<0 || Time.timeAsDouble-draw.End>2)throw new InvalidOperationException("Bow release has no live server draw");
            double until=Until(draw);var step=GameWeaponPreparation.Advance(state,weapon,false,draw.Paid,until);
            if(step.Exhausted)throw new InvalidOperationException("Bow draw exhausted its resources");
            return new Shot{Changes=step.Changes,Fraction=step.Fraction,Confirm=()=>
            {draw.Paid=until;draw.Consumed=true;if(draws.TryGetValue(actor,out var active)&&active==draw)draws.Remove(actor);}};
        }
        internal static void Close(ZDO actor)
        {
            if(!draws.TryGetValue(actor.m_uid,out var draw))return;
            if(draw.End<0)draw.End=Math.Min(Time.timeAsDouble,draw.Seen+1.5);
            draw.Released=false;Queue(actor.m_uid,draw,true);draws.Remove(actor.m_uid);
        }
        internal static void Forget(ZDOID actor)=>draws.Remove(actor);
        internal static void Clear(){draws.Clear();clientWeapon=null;clientScope=0;}
        [HarmonyPatch(typeof(Player),"UpdateAttackBowDraw")]
        private static class Input
        {
            private static void Prefix(Player __instance,ItemDrop.ItemData weapon,out bool __state)
            {
                __state=__instance==Player.m_localPlayer && PlayerSessionGame.Managed && IsBow(weapon);
                if(!__state)return;clientScope++;
                bool blocked=__instance.m_blocking || __instance.InMinorAction() || __instance.IsAttached();
                bool hold=__instance.m_attackHold&&!blocked;
                if(clientWeapon!=null && (clientWeapon!=weapon || !hold))
                {InventoryMoveGame.Client?.BowControl(!blocked&&clientWeapon==weapon?2:0,clientWeapon.m_gridPos.x,clientWeapon.m_gridPos.y);clientWeapon=null;}
                if(hold && (clientWeapon!=weapon || Time.timeAsDouble>=nextControl))
                {InventoryMoveGame.Client?.BowControl(1,weapon.m_gridPos.x,weapon.m_gridPos.y);clientWeapon=weapon;nextControl=Time.timeAsDouble+.4;}
            }
            private static Exception Finalizer(Exception __exception,bool __state){if(__state)clientScope--;return __exception;}
        }
        [HarmonyPatch(typeof(Player),nameof(Player.UseStamina))]
        private static class Stamina{private static bool Prefix(Player __instance)=>clientScope==0 || __instance!=Player.m_localPlayer;}
        [HarmonyPatch(typeof(Player),nameof(Player.UseEitr))]
        private static class Eitr{private static bool Prefix(Player __instance)=>clientScope==0 || __instance!=Player.m_localPlayer;}
        [HarmonyPatch(typeof(Attack),nameof(Attack.StartDraw))]
        private static class Ammo
        {
            private static bool Prefix(Attack __instance,Humanoid character,ItemDrop.ItemData weapon,ref bool __result)
            {if(clientScope==0 || character!=Player.m_localPlayer)return true;__result=Attack.HaveAmmo(character,weapon);return false;}
        }
    }
}

