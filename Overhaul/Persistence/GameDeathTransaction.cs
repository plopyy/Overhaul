using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameDeathTransaction
    {
        // Incoming damage and its armor wear are included in the same durable
        // action as the tombstone. No inventory is removed before world commit.
        internal static PlayerActionPlan Prepare(PlayerSnapshot state,IEnumerable<PlayerChange> damage,Player player,HitData hit)
        {
            if(GameDeathProgress.IsDead(state))return null;
            var changes=damage.ToList();var injured=PlayerProgressService.Overlay(state,changes);
            if(PlayerResources.Read(injured,"health")>0||!player||!ZoneSystem.instance)
                throw new InvalidOperationException("Death requires a lethal server damage result");
            bool Key(GlobalKeys key)=>ZoneSystem.instance.GetGlobalKey(key);
            var contents=GameDeathInventory.Partition(injured,Key(GlobalKeys.DeathKeepInventory),Key(GlobalKeys.DeathKeepEquip),Key(GlobalKeys.DeathDeleteItems),Key(GlobalKeys.DeathDeleteUnequipped));
            var elapsed=injured.Rows.Single(r=>r.Table=="state"&&(string)r.Values[0]=="time_since_death");
            bool hard=Convert.ToDouble(elapsed.Values[2])>player.m_hardDeathCooldown;
            var progression=GameDeathProgress.Prepare(injured,player.transform.position,hit,hard,Key(GlobalKeys.DeathSkillsReset),player.GetSkills().m_DeathLowerFactor*Game.m_skillReductionRate);
            foreach(var change in contents.Changes.Concat(progression))
            {
                changes.RemoveAll(r=>r.SameKey(change)||change.Delete&&change.Table=="inventory"&&r.Table=="item_data"&&r.SameInventorySlot(change)||
                    change.Delete&&change.Table=="status"&&r.Table=="status_data"&&r.SameStatus(change));
                changes.Add(change);
            }
            var position=player.GetCenterPoint();
            if(Character.InInterior(position)&&global::Overhaul.Dungeons.DungeonDeathGrave.TryExterior(position,out var exterior))position=exterior;
            object State(string key,int index)=>injured.Rows.Single(r=>r.Table=="state"&&(string)r.Values[0]==key).Values[index];
            int width=EquipmentAndQuickSlots.Slots.VanillaInventoryWidth;
            int height=Math.Max(InventoryMoveGame.PlayerRows(injured.Rows)+EquipmentAndQuickSlots.Slots.ExtraRows+EquipmentAndQuickSlots.Slots.HiddenRows,
                contents.Grave.Length==0?1:contents.Grave.Max(i=>i.m_gridPos.y)+1);
            var grave=GameDeathInventory.Tombstone(contents,player.m_tombstone,position,player.transform.rotation,(string)State("player_name",3),Convert.ToInt64(State("player_id",1)),width,height);
            var records=new Dictionary<long,ObjectRecord>();if(grave!=null)records.Add(grave.Id,grave);
            return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(Guid.NewGuid().ToString("N"),state.Revision,changes),records),
                ()=>{if(grave!=null)GamePersistence.PublishActionObject(grave);GameDeathRuntime.Publish(player,hit);});
        }
    }
}
