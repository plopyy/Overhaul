using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameBlockControl
    {
        private sealed class Hold{internal bool Held;internal double Since,Seen;internal Vector3 Forward;}
        internal sealed class Pose{internal int Slot;internal bool Timed;internal Vector3 Forward;}
        private static readonly Dictionary<ZDOID,Hold> controls=new Dictionary<ZDOID,Hold>();
        private static bool sent,lastHeld;
        private static double next;
        internal static void Control(ZDO actor,bool held,Vector3 forward)
        {
            if(actor==null||InventoryMoveGame.State(actor.m_uid)==null||float.IsNaN(forward.sqrMagnitude)||Mathf.Abs(forward.sqrMagnitude-1)>.01f)return;
            double now=Time.timeAsDouble;
            if(!controls.TryGetValue(actor.m_uid,out var input)){input=new Hold();controls.Add(actor.m_uid,input);}
            if(held&&(!input.Held||now-input.Seen>1.5))input.Since=now;
            input.Held=held;input.Seen=now;input.Forward=forward.normalized;
        }
        internal static Pose Blocking(ZDOID actor,Player player,PlayerSnapshot state,Vector3 incoming)
        {
            if(!player||!controls.TryGetValue(actor,out var input)||!input.Held||Time.timeAsDouble-input.Seen>1.5||PlayerResources.Read(state,"health")<=0||
                GameDeathProgress.IsDead(state)||player.IsTeleporting()||player.InDodge()||player.IsStaggering()||player.InMinorAction()||GameAttackRuntime.Active(actor)||
                float.IsNaN(incoming.sqrMagnitude)||float.IsInfinity(incoming.sqrMagnitude)||Vector3.Dot(incoming,input.Forward)>0)return null;
            var equipment=GameCombatEquipment.Equipped(state);
            var blocker=equipment.FirstOrDefault(i=>i.m_shared.m_itemType==ItemDrop.ItemData.ItemType.Shield||i.m_shared.m_itemType==ItemDrop.ItemData.ItemType.Bow||i.m_shared.m_itemType==ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft);
            if(blocker==null&&equipment.Any(i=>i.m_shared.m_itemType==ItemDrop.ItemData.ItemType.OneHandedWeapon))blocker=equipment.FirstOrDefault(i=>i.m_shared.m_itemType==ItemDrop.ItemData.ItemType.Torch);
            blocker=blocker??equipment.FirstOrDefault(i=>i.IsWeapon()&&i.m_shared.m_itemType!=ItemDrop.ItemData.ItemType.Torch)??equipment.FirstOrDefault(i=>i.IsWeapon());
            return new Pose{Slot=blocker==null?-1:blocker.m_gridPos.y*256+blocker.m_gridPos.x,Timed=Time.timeAsDouble-input.Since<.25,Forward=input.Forward};
        }
        internal static void Forget(ZDOID actor)=>controls.Remove(actor);
        internal static void ClearClient(){sent=false;lastHeld=false;next=0;}
        internal static void Clear(){controls.Clear();ClearClient();}
        internal static void ClientTick()
        {
            var player=Player.m_localPlayer;if(!player||!PlayerSessionGame.Managed)return;
            bool held=player.m_blocking&&!player.IsDead();
            if(sent&&held==lastHeld&&(!held||Time.timeAsDouble<next))return;
            InventoryMoveGame.Client?.BlockControl(held,player.transform.forward);sent=true;lastHeld=held;next=Time.timeAsDouble+.1;
        }
    }
}
