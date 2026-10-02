using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;

public static class MistlandsLootChecks
{
    const BindingFlags F=BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Static|BindingFlags.Instance;
    static readonly List<string> report=new List<string>();
    static void Check(bool ok,string text){if(!ok)throw new Exception(text);report.Add("PASS "+text);}
    public static void Run()
    {
        var author=typeof(MistlandsBossRoomAuthoring);author.GetMethod("Sources",F).Invoke(null,null);
        AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var p=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        var mod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));
        var paths=(Dictionary<string,string>)author.GetField("paths",F).GetValue(null);
        var sources=paths.Keys.Where(p=>p.Contains("/Rooms/mistlands/")&&p.EndsWith(".prefab")).Select(p=>(GameObject)author.GetMethod("Get",F).Invoke(null,new object[]{p})).Where(g=>g.GetComponent<Room>()&&(g.GetComponent<Room>().m_theme&Room.Theme.DvergerTown)!=0).ToArray();
        var host=new GameObject("Inactive loot fixtures");host.SetActive(false);
        var room=(GameObject)mod.GetType("Overhaul.Dungeons.MistlandsBossRoom").GetMethod("Build",F).Invoke(null,new object[]{host.transform,sources});
        ZNet.m_instance=host.AddComponent<ZNet>();typeof(Game).GetProperty("instance",F).SetValue(null,host.AddComponent<Game>());new ZRoutedRpc(true);var manager=new ZDOMan(512);ZNetScene.s_instance=host.AddComponent<ZNetScene>();ObjectDB.m_instance=host.AddComponent<ObjectDB>();
        var picks=room.GetComponentsInChildren<Pickable>(true).Where(p=>p.transform.IsChildOf(room.transform.Find("SecretRoomLeft"))||p.transform.IsChildOf(room.transform.Find("SecretRoomRight"))||p.transform.parent.name.StartsWith("SecretJelly_")).ToArray();
        foreach(string id in new[]{"Pickable_DvergrMineTreasure","Pickable_BlackCoreStand","Pickable_RoyalJelly"})
            Check(picks.Any(p=>Utils.GetPrefabName(p.gameObject)==id),"native collectible present: "+id);
        foreach(var pick in picks)
        {
            Check(pick.m_itemPrefab&&pick.m_itemPrefab.GetComponent<ItemDrop>()&&pick.m_amount>0,"real item drop configured: "+pick.name);
            Check(pick.GetComponentsInChildren<Collider>(true).Length>0,"interactable collider: "+pick.name);
            var nv=pick.GetComponent<ZNetView>();Check(nv,"collectible network state: "+pick.name);nv.m_zdo=manager.CreateNewZDO(pick.transform.position,Utils.GetPrefabName(pick.gameObject).GetStableHashCode());nv.m_zdo.SetOwner(ZNet.GetUID());pick.m_nview=nv;
            pick.RPC_SetPicked(0,true);
            if(!pick.m_hideWhenPicked&&pick.m_respawnTimeMinutes<=0){Check(pick.GetPicked()&&nv.GetZDO()==null,"native one-shot collectible removed from network scene: "+pick.name);continue;}
            Check(pick.GetPicked()&&nv.m_zdo.GetBool(ZDOVars.s_picked)&&!pick.m_hideWhenPicked.activeSelf,"native collection persists and hides only collected content: "+pick.name);
            var reload=UnityEngine.Object.Instantiate(pick,host.transform);reload.m_nview=reload.GetComponent<ZNetView>();reload.m_nview.m_zdo=nv.m_zdo;
            reload.Awake();Check(reload.GetPicked()&&!reload.m_hideWhenPicked.activeSelf,"collected state restored by native Awake: "+pick.name);
            UnityEngine.Object.DestroyImmediate(reload.gameObject);
        }
        foreach(string name in new[]{"SecretRoomLeft","SecretRoomRight"})
        {
            var chests=room.transform.Find(name).GetComponentsInChildren<Container>(true);Check(chests.Length>0,"real containers present in "+name);
            foreach(var chest in chests)
            {
                var nv=chest.GetComponent<ZNetView>();Check(nv&&chest.m_defaultItems.m_drops.Count>0,"native networked chest with loot table: "+name);
                foreach(var drop in chest.m_defaultItems.m_drops)ObjectDB.instance.m_itemByHash[drop.m_item.name.GetStableHashCode()]=drop.m_item;
                nv.m_zdo=manager.CreateNewZDO(chest.transform.position,Utils.GetPrefabName(chest.gameObject).GetStableHashCode());nv.m_zdo.SetOwner(ZNet.GetUID());chest.m_nview=nv;chest.m_inventory=new Inventory("secret",null,chest.m_width,chest.m_height);
                chest.AddDefaultItems();Check(chest.m_inventory.NrOfItems()>0,"native chest loot generated: "+name);chest.Save();
                var restored=new Inventory("restored",null,chest.m_width,chest.m_height);restored.Load(new ZPackage(nv.m_zdo.GetByteArray(ZDOVars.s_items)));
                Check(restored.NrOfItems()==chest.m_inventory.NrOfItems(),"chest inventory survives save/load: "+name);
                var player=new Inventory("player",null,8,8);player.MoveAll(restored);Check(restored.NrOfItems()==0&&player.NrOfItems()>0,"chest contents transfer into player inventory: "+name);
            }
        }
        report.Add("PASS total="+report.Count+" package="+mod.GetName().Version);File.WriteAllLines("../../../Tools/BossDungeonWork/Mistlands/loot-checks.txt",report);
    }
}
