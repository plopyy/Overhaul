using System;
using System.Collections.Generic;
using System.Linq;

namespace Overhaul.Persistence
{
    internal static class GameAttackInventory
    {
        internal sealed class Result
        {
            internal ItemDrop.ItemData Weapon,Ammo;
            internal Attack Definition;
            internal PlayerWorldAction Change;
        }
        private static ItemDrop.ItemData Read(PlayerActionInventory inventory,int slot)
        {
            var item=PlayerInventoryView.ReadItem(inventory.Item(slot),null,true);
            item.m_customData=inventory.Data(slot);return item;
        }
        // Resolves native trigger consumption against detached server rows. The
        // attack scheduler must commit Change before producing a hit/projectile.
        internal static Result Trigger(PlayerSnapshot snapshot,int weaponSlot,bool secondary,string operation)
        {
            if(PlayerResources.Read(snapshot,"health")<=0)throw new InvalidOperationException("Dead character cannot attack");
            var inventory=new PlayerActionInventory(snapshot.Rows,InventoryMoveGame.PlayerLayout(snapshot.Rows));
            ItemDrop.ItemData weapon;
            if(weaponSlot==-1)
            {
                if(inventory.Keys.Select(k=>Read(inventory,k)).Any(i=>i.m_equipped && i.IsWeapon()))throw new InvalidOperationException("Character already has an equipped weapon");
                var definition=Game.instance?Game.instance.m_playerPrefab?.GetComponent<Player>()?.m_unarmedWeapon:null;
                if(!definition)throw new InvalidOperationException("Unarmed attack definition is unavailable");
                weapon=definition.m_itemData.Clone();weapon.m_dropPrefab=definition.gameObject;weapon.m_equipped=true;
            }
            else weapon=Read(inventory,weaponSlot);
            if(weaponSlot!=-1 && (!inventory.Available(weaponSlot) || inventory.Layout.Cosmetic(weaponSlot)) || !weapon.m_equipped || !weapon.IsWeapon() ||
                weapon.m_shared.m_buildPieces || weapon.m_shared.m_useDurability && weapon.m_durability<=0)
                throw new InvalidOperationException("Attack weapon is unavailable");
            var definition=secondary?weapon.m_shared.m_secondaryAttack:weapon.m_shared.m_attack;
            if(definition==null || string.IsNullOrEmpty(definition.m_attackAnimation))throw new InvalidOperationException("Attack definition is unavailable");
            ItemDrop.ItemData ammo=null;var additional=new List<PlayerChange>();
            if(!string.IsNullOrWhiteSpace(weapon.m_shared.m_ammoType))
            {
                var candidates=inventory.Keys.Where(k=>inventory.Available(k) && !inventory.Layout.Cosmetic(k)).Select(k=>Read(inventory,k)).Where(i=>
                    i.m_shared.m_ammoType==weapon.m_shared.m_ammoType && (i.m_shared.m_itemType==ItemDrop.ItemData.ItemType.Ammo ||
                    i.m_shared.m_itemType==ItemDrop.ItemData.ItemType.AmmoNonEquipable || i.m_shared.m_itemType==ItemDrop.ItemData.ItemType.Consumable));
                ammo=candidates.OrderBy(i=>i.m_equipped?0:1).ThenBy(i=>i.m_gridPos.y*256+i.m_gridPos.x).FirstOrDefault();
                if(ammo==null)throw new InvalidOperationException("No compatible ammunition in server inventory");
                int ammoSlot=ammo.m_gridPos.y*256+ammo.m_gridPos.x;
                if(ammo.m_shared.m_itemType==ItemDrop.ItemData.ItemType.Consumable)
                {
                    var request=new InventoryMoveRequest{Gameplay=new PlayerActionCommand{Kind=PlayerActionKind.Consume},
                        Action=new InventoryMoveAction(operation,snapshot.Revision,0,InventoryMoveKind.Gameplay,0,0,ammo.m_gridPos.x,ammo.m_gridPos.y,0,0,1)};
                    var consumed=PlayerFoodGame.Prepare(request,snapshot,inventory).Change.Player;
                    additional.AddRange(consumed.Changes.Where(r=>r.Table!="inventory" && r.Table!="item_data"));
                }
                else inventory.Remove(ammoSlot,1);
            }
            if(definition.m_consumeItem)
            {if(weaponSlot==-1)throw new InvalidOperationException("Unarmed definition cannot consume an inventory weapon");inventory.Remove(weaponSlot,1,true);}
            var batch=inventory.Delta(operation,snapshot.Revision);
            return new Result{Weapon=weapon,Ammo=ammo,Definition=definition.Clone(),Change=new PlayerWorldAction(
                new PlayerBatch(operation,snapshot.Revision,batch.Changes.Concat(additional)),new Dictionary<long,ObjectRecord>())};
        }
    }
}
