using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameCombatEquipment
    {
        internal static ItemDrop.ItemData[] Equipped(PlayerSnapshot state)
        {
            var layout=InventoryMoveGame.PlayerLayout(state.Rows);
            var items=new Dictionary<int,ItemDrop.ItemData>();
            foreach(var row in state.Rows.Where(r=>r.Table=="inventory"))
            {
                var v=row.Values;int slot=Convert.ToInt32(v[2])*256+Convert.ToInt32(v[1]);
                if(!Convert.ToBoolean(v[7]) || layout.Cosmetic(slot))continue;
                var item=PlayerInventoryView.ReadItem(v,null,true);
                if(item.m_shared.m_useDurability && item.m_durability<=0)continue;
                items.Add(slot,item);
            }
            foreach(var row in state.Rows.Where(r=>r.Table=="item_data"))
            {var v=row.Values;if(items.TryGetValue(Convert.ToInt32(v[2])*256+Convert.ToInt32(v[1]),out var item))item.m_customData[(string)v[3]]=(string)v[4];}
            return items.OrderBy(p=>p.Key).Select(p=>p.Value).ToArray();
        }
        internal static bool Armor(ItemDrop.ItemData item)
        {
            var type=item.m_shared.m_itemType;
            return type==ItemDrop.ItemData.ItemType.Chest || type==ItemDrop.ItemData.ItemType.Legs || type==ItemDrop.ItemData.ItemType.Helmet || type==ItemDrop.ItemData.ItemType.Shoulder;
        }
        internal static float BodyArmor(IEnumerable<ItemDrop.ItemData> equipment,IEnumerable<StatusEffect> effects)
        {
            float armor=equipment.Where(Armor).Sum(i=>i.GetArmor());
            foreach(var effect in effects)effect.ModifyArmorMods(ref armor);
            return armor;
        }
        internal static PlayerChange[] ArmorWear(PlayerSnapshot state,HitData hit,int selection)
        {
            var items=Equipped(state).Where(Armor).ToArray();if(items.Length==0)return Array.Empty<PlayerChange>();
            if(selection<0 || selection>=items.Length)throw new ArgumentOutOfRangeException(nameof(selection));
            float damage=hit.GetTotalPhysicalDamage()+hit.GetTotalElementalDamage();
            if(float.IsNaN(damage) || float.IsInfinity(damage) || damage<0)throw new InvalidOperationException("Invalid armor damage");
            var item=items[selection];if(damage==0 || !item.m_shared.m_useDurability)return Array.Empty<PlayerChange>();
            float protection=PlayerCraftProgressGame.Passive(state,"artisan")?1:Mathf.Clamp01(PlayerCraftProgressGame.Bonus(state,"durability"));
            // Match native DamageArmorDurability followed by Overhaul's refund:
            // protection applies to the actual clamped loss, including overkill.
            float loss=Mathf.Min(item.m_durability,damage)*(1-protection);
            if(loss<=0)return Array.Empty<PlayerChange>();
            var inventory=new PlayerActionInventory(state.Rows,InventoryMoveGame.PlayerLayout(state.Rows));
            int slot=item.m_gridPos.y*256+item.m_gridPos.x;var values=inventory.Item(slot);
            GameEquipmentWear.Apply(inventory,slot,values,loss);
            return inventory.Delta(Guid.NewGuid().ToString("N"),state.Revision).Changes.ToArray();
        }
    }
}
