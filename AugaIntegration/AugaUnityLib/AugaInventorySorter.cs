using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace AugaUnity
{
    public class AugaInventorySorter : MonoBehaviour
    {
        public bool Container;
        public Button Button;
        const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        static readonly FieldInfo Drag=typeof(InventoryGui).GetField("m_dragItem",Flags);
        static readonly FieldInfo CurrentContainer=typeof(InventoryGui).GetField("m_currentContainer",Flags);
        static readonly MethodInfo Changed=typeof(Inventory).GetMethod("Changed",Flags);
        public void Awake(){if(!Button)Button=GetComponent<Button>();Button.onClick.AddListener(SortClicked);}
        public void OnDestroy(){if(Button)Button.onClick.RemoveListener(SortClicked);}
        void Update(){if(Button)Button.interactable=InventoryGui.instance&&Drag.GetValue(InventoryGui.instance)==null;}
        public void SortClicked()
        {
            var gui=InventoryGui.instance;var player=Player.m_localPlayer;
            if(!gui||!player||!InventoryGui.IsVisible()||Drag.GetValue(gui)!=null)return;
            Inventory inventory;int firstRow=Container?0:1;int rows;
            if(Container)
            {
                var chest=CurrentContainer.GetValue(gui) as global::Container;
                if(!chest||!chest.IsOwner())return;
                inventory=chest.GetInventory();rows=inventory.GetHeight();
            }
            else
            {
                inventory=player.GetInventory();rows=inventory.GetHeight();
                // EQS appends reserved logical rows. Never pack ordinary items into them.
                var slots=AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name=="Overhaul" || a.GetName().Name=="EquipmentAndQuickSlots")
                    .Select(a=>a.GetType("EquipmentAndQuickSlots.Slots")).FirstOrDefault(t=>t!=null);
                if(slots!=null)
                {
                    var property=slots.GetProperty("VisibleRows",Flags);
                    if(property==null){Debug.LogWarning("[Auga] Sorting skipped: Equipment & QuickSlots visible row count unavailable.");return;}
                    rows=Math.Min(rows,(int)property.GetValue(null,null));
                }
            }
            Sort(inventory,firstRow,rows,!Container);
        }
        static bool Compatible(ItemDrop.ItemData a,ItemDrop.ItemData b)
        {
            return a.m_shared.m_maxStackSize>1 && a.m_shared.m_maxStackSize==b.m_shared.m_maxStackSize
                && a.m_shared.m_name==b.m_shared.m_name && a.m_quality==b.m_quality
                && a.m_worldLevel==b.m_worldLevel && a.m_variant==b.m_variant
                && a.m_durability==b.m_durability && a.m_crafterID==b.m_crafterID
                && a.m_crafterName==b.m_crafterName
                && a.m_customData.Count==b.m_customData.Count
                && a.m_customData.All(p=>b.m_customData.TryGetValue(p.Key,out var value)&&value==p.Value);
        }
        public static bool Sort(Inventory inventory,int firstRow,int rows,bool preserveEquipped)
        {
            if(inventory==null||firstRow<0||rows>inventory.GetHeight()||rows<=firstRow)return false;
            int width=inventory.GetWidth();if(width<=0)return false;
            var all=inventory.GetAllItems();
            if(all.Select(i=>i.m_gridPos).Distinct().Count()!=all.Count)return false;
            var moving=all.Where(i=>i.m_gridPos.y>=firstRow&&i.m_gridPos.y<rows&&i.m_gridPos.x>=0&&i.m_gridPos.x<width&&(!preserveEquipped||!i.m_equipped)).ToList();
            var movingSet=new HashSet<ItemDrop.ItemData>(moving);
            var occupied=new HashSet<Vector2i>(all.Where(i=>!movingSet.Contains(i)).Select(i=>i.m_gridPos));
            var cells=new List<Vector2i>();for(int y=firstRow;y<rows;y++)for(int x=0;x<width;x++){var p=new Vector2i(x,y);if(!occupied.Contains(p))cells.Add(p);}
            if(cells.Count<moving.Count)return false;
            bool changed=false;
            for(int i=0;i<moving.Count;i++)
            {
                var target=moving[i];
                if(target.m_stack<=0)continue;
                for(int j=i+1;j<moving.Count&&target.m_stack<target.m_shared.m_maxStackSize;j++)
                {
                    var source=moving[j];
                    if(source.m_stack<=0||!Compatible(target,source))continue;
                    int amount=Math.Min(source.m_stack,target.m_shared.m_maxStackSize-target.m_stack);
                    target.m_stack+=amount;source.m_stack-=amount;changed=true;
                    if(source.m_stack==0)all.Remove(source);
                }
            }
            moving.RemoveAll(i=>i.m_stack==0);
            var ordered=moving.OrderBy(i=>i.m_shared.m_itemType)
                .ThenBy(i=>Localization.instance!=null?Localization.instance.Localize(i.m_shared.m_name):i.m_shared.m_name,StringComparer.CurrentCultureIgnoreCase)
                .ThenByDescending(i=>i.m_quality).ThenBy(i=>i.m_variant).ThenByDescending(i=>i.m_stack)
                .ThenBy(i=>i.m_gridPos.y).ThenBy(i=>i.m_gridPos.x).ToArray();
            for(int i=0;i<ordered.Length;i++)if(ordered[i].m_gridPos!=cells[i]){changed=true;break;}
            if(!changed)return false;
            for(int i=0;i<ordered.Length;i++)ordered[i].m_gridPos=cells[i];
            Changed.Invoke(inventory,new object[]{false,false}); // Native save/synchronization notification.
            return true;
        }
    }
}
