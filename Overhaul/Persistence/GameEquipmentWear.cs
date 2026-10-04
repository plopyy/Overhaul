using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameEquipmentWear
    {
        internal static PlayerActionPlan Prepare(PlayerSnapshot snapshot,double seconds)
        {
            if(double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds<0)throw new ArgumentOutOfRangeException(nameof(seconds));
            if(seconds==0 || PlayerResources.Read(snapshot,"health")<=0)return null;
            float protection=PlayerCraftProgressGame.Passive(snapshot,"artisan")?1:Mathf.Clamp01(PlayerCraftProgressGame.Bonus(snapshot,"durability"));
            if(protection>=1)return null;
            var inventory=new PlayerActionInventory(snapshot.Rows,InventoryMoveGame.PlayerLayout(snapshot.Rows));
            foreach(int key in inventory.Keys)
            {
                var values=inventory.Item(key);
                if(!Convert.ToBoolean(values[7]) || inventory.Layout.Cosmetic(key))continue;
                var item=PlayerInventoryView.ReadItem(values,null,true);
                if(!item.m_shared.m_useDurability || item.m_shared.m_durabilityDrain<=0)continue;
                double drain=item.m_shared.m_durabilityDrain*seconds*Game.m_durabilityRate*(1-protection);
                if(double.IsNaN(drain) || double.IsInfinity(drain) || drain<0)throw new InvalidOperationException("Invalid equipment durability drain");
                float remaining=(float)Math.Max(0,item.m_durability-drain);
                if(remaining==item.m_durability)continue;
                values[6]=remaining;inventory.Set(key,values);
                if(remaining>0)continue;
                inventory.Equip(key,false);
                if(item.m_shared.m_destroyBroken)inventory.Remove(key,item.m_stack,true);
            }
            var batch=inventory.Delta(Guid.NewGuid().ToString("N"),snapshot.Revision);
            return batch.Changes.Any()?new PlayerActionPlan(new PlayerWorldAction(batch,new Dictionary<long,ObjectRecord>()),()=>{ }):null;
        }
    }
}
