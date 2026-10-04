using System;
using System.Linq;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameBlockCharges
    {
        internal static PlayerChange[] Advance(PlayerSnapshot state,Player player,double seconds)
        {
            if(GameAdrenaline.Read(state,PlayerResources.BlockCharges)==0)return Array.Empty<PlayerChange>();
            var item=GameCombatEquipment.Equipped(state).FirstOrDefault(i=>i.m_shared.m_buildBlockCharges);
            if(item==null)return Reset();
            bool blocking=GameBlockControl.Blocking(player.GetZDOID(),player,state,Vector3.zero)!=null;
            // Native UpdateBlock adds dt twice outside the blocking stance.
            double rate=blocking?item.m_shared.m_blockChargeBlockingDecayMult:2;
            double age=GameAdrenaline.Read(state,PlayerResources.BlockChargeAge)+seconds*Math.Max(0,rate);
            if(age>=item.m_shared.m_blockChargeDecayTime)return Reset();
            return new[]{PlayerResources.Row(PlayerResources.BlockChargeAge,age)};
        }
        private static PlayerChange[] Reset()=>new[]{PlayerResources.Row(PlayerResources.BlockCharges,0),PlayerResources.Row(PlayerResources.BlockChargeAge,0)};
        internal static void Publish(Player player,PlayerSnapshot state,GameBlockMath.Result block)
        {
            var hand=player.m_visEquipment?player.m_visEquipment.m_leftHand:null;
            block.Item.m_shared.m_blockChargeEffects.Create(hand?hand.position:player.transform.position,player.transform.rotation,null,1,block.ChargeEffect,player.GetZDOID());
            if(!block.Discharge||PlayerResources.Read(state,"health")<=0)return;
            // Clone the attack: shared item definitions must not retain another
            // player's actor/body between simultaneous shield discharges.
            var attack=block.Item.m_shared.m_attack?.Clone();
            if(attack!=null)GameCombatContext.Run(player,state,block.Item,null,()=>attack.StartWithoutAnimation(player,player.m_body,player.m_visEquipment,block.Item));
        }
    }
}
