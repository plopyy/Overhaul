using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using HarmonyLib;

namespace Overhaul.Persistence
{
    internal static class GameEquipmentWear
    {
        internal const string Identity="Overhaul.ItemIdentity";
        internal static PlayerActionPlan Identify(PlayerSnapshot snapshot)
        {
            var changes=new List<PlayerChange>();var seen=new HashSet<string>();
            foreach(var row in snapshot.Rows.Where(r=>r.Table=="inventory"))
            {
                var values=row.Values;var item=PlayerInventoryView.ReadItem(values,null,true);
                if(!item.m_shared.m_useDurability || item.m_shared.m_maxStackSize!=1)continue;
                string token=Token(snapshot,values);
                if(Guid.TryParseExact(token,"N",out _) && seen.Add(token))continue;
                token=Guid.NewGuid().ToString("N");seen.Add(token);
                changes.Add(new PlayerChange("item_data",false,"main",values[1],values[2],Identity,token));
            }
            return Plan(snapshot,changes);
        }
        private static string Token(PlayerSnapshot snapshot,object[] item)
            =>snapshot.Rows.Where(r=>r.Table=="item_data").Select(r=>r.Values).Where(v=>Convert.ToInt32(v[1])==Convert.ToInt32(item[1]) && Convert.ToInt32(v[2])==Convert.ToInt32(item[2]) && (string)v[3]==Identity).Select(v=>(string)v[4]).FirstOrDefault();
        internal static Dictionary<string,double> Rates(PlayerSnapshot snapshot)
        {
            var rates=new Dictionary<string,double>();
            if(PlayerResources.Read(snapshot,"health")<=0)return rates;
            float protection=PlayerCraftProgressGame.Passive(snapshot,"artisan")?1:Mathf.Clamp01(PlayerCraftProgressGame.Bonus(snapshot,"durability"));
            if(protection>=1)return rates;
            var layout=InventoryMoveGame.PlayerLayout(snapshot.Rows);
            foreach(var row in snapshot.Rows.Where(r=>r.Table=="inventory"))
            {
                var values=row.Values;if(!Convert.ToBoolean(values[7]) || layout.Cosmetic(Convert.ToInt32(values[2])*256+Convert.ToInt32(values[1])))continue;
                var item=PlayerInventoryView.ReadItem(values,null,true);string token=Token(snapshot,values);
                if(token==null || !item.m_shared.m_useDurability || item.m_shared.m_durabilityDrain<=0)continue;
                double rate=item.m_shared.m_durabilityDrain*Game.m_durabilityRate*(1-protection);
                if(double.IsNaN(rate) || double.IsInfinity(rate) || rate<0)throw new InvalidOperationException("Invalid equipment wear rate");
                if(rate>0 && !rates.ContainsKey(token))rates.Add(token,rate);
            }
            return rates;
        }
        internal static PlayerActionPlan Debit(PlayerSnapshot snapshot,IDictionary<string,double> debits)
        {
            var inventory=new PlayerActionInventory(snapshot.Rows,InventoryMoveGame.PlayerLayout(snapshot.Rows));
            foreach(int key in inventory.Keys)
            {
                var values=inventory.Item(key);string token=Token(snapshot,values);
                if(token==null || !debits.TryGetValue(token,out double drain))continue;
                Apply(inventory,key,values,drain);
            }
            return Plan(snapshot,inventory.Delta(Guid.NewGuid().ToString("N"),snapshot.Revision).Changes);
        }
        private static PlayerActionPlan Plan(PlayerSnapshot snapshot,IEnumerable<PlayerChange> changes)
        {
            var batch=new PlayerBatch(Guid.NewGuid().ToString("N"),snapshot.Revision,changes);
            return batch.Changes.Any()?new PlayerActionPlan(new PlayerWorldAction(batch,new Dictionary<long,ObjectRecord>()),()=>{ }):null;
        }
        internal static void Apply(PlayerActionInventory inventory,int key,object[] values,double drain)
        {
            if(double.IsNaN(drain) || double.IsInfinity(drain) || drain<0)throw new InvalidOperationException("Invalid equipment durability drain");
            var item=PlayerInventoryView.ReadItem(values,null,true);
            float remaining=(float)Math.Max(0,item.m_durability-drain);
            if(remaining==item.m_durability)return;
            values[6]=remaining;inventory.Set(key,values);
            if(remaining>0)return;
            inventory.Equip(key,false);
            if(item.m_shared.m_destroyBroken)inventory.Remove(key,item.m_stack,true);
        }
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
                Apply(inventory,key,values,drain);
            }
            var batch=inventory.Delta(Guid.NewGuid().ToString("N"),snapshot.Revision);
            return batch.Changes.Any()?new PlayerActionPlan(new PlayerWorldAction(batch,new Dictionary<long,ObjectRecord>()),()=>{ }):null;
        }
        [HarmonyPatch(typeof(Humanoid),"DrainEquipedItemDurability")]
        private static class NativeWear
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Humanoid __instance)
            {
                if(!(__instance is Player player))return true;
                if(player==Player.m_localPlayer && PlayerSessionGame.Managed)return false;
                return !player.m_nview || !player.m_nview.IsValid() || InventoryMoveGame.State(player.m_nview.GetZDO().m_uid)==null;
            }
        }
    }
}
