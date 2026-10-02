using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using static EquipmentAndQuickSlots.Slots;

namespace EquipmentAndQuickSlots
{
    internal static class AmmoSlots
    {
        const string SelectionKey="overhaul_ammo_slot";
        internal static bool IsAmmo(ItemDrop.ItemData item)=>item!=null&&(item.m_shared.m_itemType==ItemDrop.ItemData.ItemType.Ammo||item.m_shared.m_itemType==ItemDrop.ItemData.ItemType.AmmoNonEquipable);
        internal static bool IsFood(ItemDrop.ItemData item)=>item!=null&&item.m_shared.m_itemType==ItemDrop.ItemData.ItemType.Consumable&&
            (item.m_shared.m_food>0||item.m_shared.m_foodStamina>0||item.m_shared.m_foodEitr>0);
        internal static bool Ready(Player player)=>player&&player==Player.m_localPlayer&&!player.m_isLoading&&slots[AmmoSlotStartIndex]!=null;
        internal static int Selected(Player player)=>player&&player.m_customData.TryGetValue(SelectionKey,out var value)&&int.TryParse(value,out int index)?Mathf.Clamp(index,0,2):0;
        internal static void SetSelected(Player player,int index)=>player.m_customData[SelectionKey]=Mathf.Clamp(index,0,2).ToString();
        static ItemDrop.ItemData Item(int index)
        {
            var pos=slots[AmmoSlotStartIndex+index].GridPosition;
            return PlayerInventory?.GetItemAt(pos.x,pos.y);
        }
        static bool Matches(ItemDrop.ItemData item,string ammo,string prefab)=>IsAmmo(item)&&item.m_stack>0&&
            (string.IsNullOrEmpty(ammo)||item.m_shared.m_ammoType==ammo)&&(prefab==null||(item.m_dropPrefab&&item.m_dropPrefab.name==prefab));
        internal static ItemDrop.ItemData Select(Player player,string ammo,string prefab=null,bool cycle=false)
        {
            int start=Selected(player);
            for(int step=0;step<3;step++)
            {
                int index=(start+step+(cycle?1:0))%3;var item=Item(index);
                if(!Matches(item,ammo,prefab))continue;
                SetSelected(player,index);return item;
            }
            return null;
        }
        internal static bool Shortcut(bool held=false)
        {
            var player=Player.m_localPlayer;
            return Ready(player)&&player.TakeInput()&&ValConfig.AmmoCycleKey!=null&&
                (held?PreventSimilarHotkeys.IsShortcutPressed(ValConfig.AmmoCycleKey.Value):PreventSimilarHotkeys.IsShortcutDown(ValConfig.AmmoCycleKey.Value));
        }
        internal static void Update(Player player)
        {
            if(!Ready(player))return;
            string ammo=player.GetCurrentWeapon()?.m_shared.m_ammoType;
            bool cycle=Shortcut();var selected=Select(player,ammo,null,cycle);
            if(cycle)player.Message(MessageHud.MessageType.Center,selected!=null?selected.m_shared.m_name:"$overhaul_no_ammo");
        }
        // An ammo request with consumable ammunition (some modded weapons) keeps vanilla rules.
        static bool Managed(string type)=>!string.IsNullOrEmpty(type)&&
            (type=="$ammo_arrows"||type=="$ammo_bolts"||slots.Skip(AmmoSlotStartIndex).Take(3).Any(s=>s!=null&&IsAmmo(s.Item)&&s.Item.m_shared.m_ammoType==type)||
             (ObjectDB.instance&&ObjectDB.instance.m_items.Any(go=>go&&IsAmmo(go.GetComponent<ItemDrop>()?.m_itemData)&&go.GetComponent<ItemDrop>().m_itemData.m_shared.m_ammoType==type)));
        internal static bool Insert(Player player,Inventory source,ItemDrop.ItemData item)
        {
            if(!Ready(player)||!IsAmmo(item)||source==null||!source.ContainsItem(item))return false;
            var inventory=player.GetInventory();var slot=source==inventory?GetItemSlot(item):null;
            if(slot?.IsAmmoSlot==true){SetSelected(player,slot.Index-AmmoSlotStartIndex);return true;}
            int before=item.m_stack,selected=-1;
            for(int i=0;i<3&&item.m_stack>0;i++)
            {
                var existing=Item(i);
                if(existing==null||existing==item||!existing.IsSameType(item)||existing.m_stack>=existing.m_shared.m_maxStackSize)continue;
                var pos=slots[AmmoSlotStartIndex+i].GridPosition;int amount=item.m_stack;
                inventory.MoveItemToThis(source,item,Mathf.Min(amount,existing.m_shared.m_maxStackSize-existing.m_stack),pos.x,pos.y);
                ClearCachedItems();if(item.m_stack<amount&&selected<0)selected=i;
            }
            for(int i=0;i<3&&item.m_stack>0;i++)
            {
                if(Item(i)!=null)continue;var pos=slots[AmmoSlotStartIndex+i].GridPosition;
                if(source==inventory){item.m_gridPos=pos;ClearCachedItems();inventory.Changed();if(selected<0)selected=i;break;}
                int amount=item.m_stack;inventory.MoveItemToThis(source,item,Mathf.Min(amount,item.m_shared.m_maxStackSize),pos.x,pos.y);
                ClearCachedItems();if(item.m_stack<amount&&selected<0)selected=i;
            }
            if(selected>=0){SetSelected(player,selected);return true;}
            return item.m_stack<before;
        }
        internal static void MigrateInvalidSlots()
        {
            var player=Player.m_localPlayer;if(!Ready(player))return;
            var inventory=player.GetInventory();bool moved=false;
            foreach(var slot in slots.Where(s=>s.IsQuickSlot||s.IsAmmoSlot))
            {
                var item=slot.Item;if(item==null||slot.ItemFits(item))continue;
                if(slot.IsQuickSlot&&IsAmmo(item))
                {
                    int old=Selected(player);Insert(player,inventory,item);SetSelected(player,old);
                    if(!inventory.ContainsItem(item)||GetItemSlot(item)?.IsAmmoSlot==true){moved=true;ClearCachedItems();continue;}
                }
                Vector2i empty=emptyPosition;
                for(int y=0;y<VisibleRows&&empty==emptyPosition;y++)for(int x=0;x<InventoryWidth;x++)
                    if(inventory.GetItemAt(x,y)==null){empty=new Vector2i(x,y);break;}
                if(empty!=emptyPosition){item.m_gridPos=empty;moved=true;}
                else if(!player.DropItem(inventory,item,item.m_stack))
                    EquipmentAndQuickSlots.LogWarning("Could not move legacy food/ammo slot item yet; retaining it for retry.");
                ClearCachedItems();
            }
            if(moved)inventory.Changed();
        }
        [HarmonyPatch(typeof(Inventory),nameof(Inventory.GetAmmoItem))]
        private static class InventoryAmmo
        {
            private static bool Prefix(Inventory __instance,string ammoName,string matchPrefabName,ref ItemDrop.ItemData __result)
            {
                var player=Player.m_localPlayer;if(!Ready(player)||__instance!=player.GetInventory()||!Managed(ammoName))return true;
                __result=Select(player,ammoName,matchPrefabName);return false;
            }
        }
        [HarmonyPatch(typeof(Humanoid),nameof(Humanoid.GetAmmoItem))]
        private static class EquippedAmmo
        {
            private static bool Prefix(Humanoid __instance,ref ItemDrop.ItemData __result)
            {
                var player=__instance as Player;if(!Ready(player))return true;
                string ammo=player.GetCurrentWeapon()?.m_shared.m_ammoType;if(!Managed(ammo))return true;
                __result=Select(player,ammo);return false;
            }
        }
        [HarmonyPatch(typeof(InventoryGui),"OnRightClickItem")]
        private static class RightClick
        {
            private static bool Prefix(InventoryGrid grid,ItemDrop.ItemData item)
            {
                var player=Player.m_localPlayer;if(!Ready(player)||!IsAmmo(item))return true;
                if(!Insert(player,grid.GetInventory(),item))player.Message(MessageHud.MessageType.Center,"$overhaul_ammo_slots_full");
                return false;
            }
        }
    }
}
