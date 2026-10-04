using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Overhaul.Persistence
{
    // Detached inventory partition. The death transaction must commit both the
    // removals and the grave record before publishing any native object.
    internal static class GameDeathInventory
    {
        internal sealed class Result
        {
            internal PlayerChange[] Changes;
            internal ItemDrop.ItemData[] Grave;
            internal bool CreateGrave;
        }
        internal static Result Partition(PlayerSnapshot state,bool keepInventory,bool keepEquipment,bool deleteItems,bool deleteUnequipped)
        {
            var rows=state.Rows.Where(r=>r.Table=="inventory"&&!r.Delete).ToArray();
            var changes=new List<PlayerChange>();var grave=new List<ItemDrop.ItemData>();
            var result=new Result{CreateGrave=rows.Length>0&&!keepInventory};
            if(!keepInventory)foreach(var row in rows)
            {
                var values=row.Values;var item=PlayerInventoryView.ReadItem(values,null,true);
                item.m_customData=state.Rows.Where(r=>r.Table=="item_data"&&!r.Delete&&Equals(r.Values[0],values[0])&&Convert.ToInt32(r.Values[1])==Convert.ToInt32(values[1])&&Convert.ToInt32(r.Values[2])==Convert.ToInt32(values[2]))
                    .ToDictionary(r=>(string)r.Values[3],r=>(string)r.Values[4]);
                bool equipped=item.m_equipped;
                if(!keepEquipment&&!deleteUnequipped)equipped=false;
                bool deleted=(deleteItems||deleteUnequipped)&&!item.m_shared.m_questItem&&!equipped;
                if(deleteUnequipped&&!keepEquipment)equipped=false;
                bool dropped=!deleted&&!item.m_shared.m_questItem&&!equipped;
                if(deleted||dropped)
                {
                    changes.Add(new PlayerChange("inventory",true,values[0],values[1],values[2]));
                    if(dropped){item.m_equipped=false;item.m_customData.Remove("eaqs_parked");grave.Add(item);}
                }
                else if(equipped!=item.m_equipped)
                {
                    var copy=(object[])values.Clone();copy[7]=equipped;changes.Add(new PlayerChange("inventory",false,copy));
                }
            }
            result.Changes=changes.ToArray();result.Grave=grave.ToArray();return result;
        }
        internal static ObjectRecord Tombstone(Result contents,GameObject prefab,Vector3 position,Quaternion rotation,string ownerName,long ownerId,int width,int height)
        {
            if(!contents.CreateGrave)return null;
            if(!prefab||!prefab.GetComponent<Container>()||!prefab.GetComponent<TombStone>()||width<1||width>256||height<1||height>256)
                throw new InvalidOperationException("Invalid server tombstone definition");
            var bag=new Inventory("Grave",null,width,height);
            foreach(var item in contents.Grave)
            {
                if(item.m_gridPos.x<0||item.m_gridPos.x>=width||item.m_gridPos.y<0||item.m_gridPos.y>=height)
                    throw new InvalidOperationException("Grave item is outside its saved inventory");
                bag.m_inventory.Add(item.Clone());
            }
            var package=new ZPackage();bag.Save(package);
            var record=GamePersistence.AllocateActionObject(prefab,position,rotation);
            void Add(int key,string type,object value)=>record.Properties.Add(new PropertyRecord{Key=key,Type=type,Name=NameCatalog.Key(key),Value=value});
            Add(ZDOVars.s_items,"bytes",package.GetArray());Add(ZDOVars.s_addedDefaultItems,"int",1);
            Add(ZDOVars.s_ownerName,"string",ownerName);Add(ZDOVars.s_owner,"long",ownerId);
            return record;
        }
    }
}
