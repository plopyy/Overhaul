using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Overhaul.Dungeons
{
    internal static class BossNativeMaterials
    {
        private const string Prefix="OverhaulNative:";
        private static readonly List<DungeonDB.RoomData> retained=new List<DungeonDB.RoomData>();
        internal static void Bind(params GameObject[] prefabs)
        {
            var renderers=prefabs.SelectMany(p=>p.GetComponentsInChildren<Renderer>(true)).ToArray();
            var needed=new HashSet<string>(renderers.SelectMany(r=>r.sharedMaterials).Where(m=>m&&m.name.StartsWith(Prefix)).Select(m=>m.name.Substring(Prefix.Length)));
            var found=new Dictionary<string,Material>();
            foreach(var room in DungeonDB.instance.m_rooms.Where(r=>(r.m_theme&(Room.Theme.ForestCrypt|Room.Theme.SunkenCrypt|Room.Theme.Cave))!=0))
            {
                if(needed.Count==0)break;
                room.m_prefab.Load();bool keep=false;
                try
                {
                    foreach(var renderer in room.m_prefab.Asset.GetComponentsInChildren<Renderer>(true))
                        foreach(var material in renderer.sharedMaterials)
                            if(material&&needed.Remove(material.name)){found[material.name]=material;keep=true;}
                }
                finally{if(keep)retained.Add(room);else room.m_prefab.Release();}
            }
            if(needed.Count>0)throw new InvalidOperationException("Missing native crypt materials: "+string.Join(", ",needed));
            foreach(var renderer in renderers)
                renderer.sharedMaterials=renderer.sharedMaterials.Select(m=>m&&m.name.StartsWith(Prefix)?found[m.name.Substring(Prefix.Length)]:m).ToArray();
        }
        internal static void Release()
        {foreach(var room in retained)room.m_prefab.Release();retained.Clear();}
    }
}
