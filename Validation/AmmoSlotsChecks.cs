using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.Serialization;
using UnityEngine;
using UnityEditor;
using AugaUnity;

public static class AmmoSlotsChecks
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static Assembly mod;
    static Type T(string name)=>mod.GetType("EquipmentAndQuickSlots."+name,true);
    static object Call(string type,string method,params object[] args)=>T(type).GetMethods(F).Single(m=>m.Name==method&&m.GetParameters().Length==args.Length).Invoke(null,args);
    static List<string> results=new List<string>();
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);results.Add("PASS "+message);}
    static Player player;static ObjectDB db;static GameObject host;static Array slots;
    static Inventory Inv=>player.GetInventory();
    static void Clear(){Inv.m_inventory.Clear();Call("Slots","ClearCachedItems");Call("AmmoSlots","SetSelected",player,0);}
    static Vector2i Pos(int index)=>(Vector2i)slots.GetValue(index).GetType().GetProperty("GridPosition").GetValue(slots.GetValue(index));
    static ItemDrop.ItemData Make(string name,ItemDrop.ItemData.ItemType type,int stack,Vector2i pos,string ammo="")
    {
        var item=(ItemDrop.ItemData)typeof(OverhaulEquipmentChecks).GetMethod("MakeItem",F).Invoke(null,new object[]{host,db,name,type,pos});
        item.m_shared.m_maxStackSize=100;item.m_shared.m_ammoType=ammo;item.m_stack=stack;return item;
    }
    static ItemDrop.ItemData Arrow(string name,int stack,int slot)=>Make(name,ItemDrop.ItemData.ItemType.Ammo,stack,Pos(slot),"$ammo_arrows");
    static void Put(params ItemDrop.ItemData[] items){Inv.m_inventory.AddRange(items);Call("Slots","ClearCachedItems");}
    static bool Fits(int slot,ItemDrop.ItemData item)=>(bool)slots.GetValue(slot).GetType().GetMethod("ItemFits").Invoke(slots.GetValue(slot),new object[]{item});
    static ItemDrop.ItemData Select(string type="$ammo_arrows",bool cycle=false)=>(ItemDrop.ItemData)Call("AmmoSlots","Select",player,type,null,cycle);
    static bool Insert(Inventory source,ItemDrop.ItemData item)=>(bool)Call("AmmoSlots","Insert",player,source,item);
    static List<ItemDrop.ItemData> dropped=new List<ItemDrop.ItemData>();
    public static bool Drop(Inventory inventory,ItemDrop.ItemData item,int amount,ref bool __result){dropped.Add(item.Clone());inventory.RemoveItem(item);__result=true;return false;}
    public static void Run()
    {
        try {Test();File.WriteAllLines("../../../Tools/AugaWork/ammo-slots-checks.txt",results);}
        catch(Exception e){results.Add("FAIL "+e);File.WriteAllLines("../../../Tools/AugaWork/ammo-slots-checks.txt",results);throw;}
    }
    static void Test()
    {
        AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var p=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        mod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));
        mod.GetType("Overhaul.IntegratedUi").GetMethod("LoadDependencies",F).Invoke(null,null);
        var loc=(Localization)FormatterServices.GetUninitializedObject(typeof(Localization));
        foreach(var field in typeof(Localization).GetFields(F).Where(f=>!f.IsStatic)){
            if(field.FieldType==typeof(char[]))field.SetValue(loc," (){}[]+-!?/\\&%,.:-=<>\n".ToCharArray());
            else if(field.FieldType==typeof(System.Text.StringBuilder))field.SetValue(loc,new System.Text.StringBuilder());
            else if(field.FieldType.IsGenericType&&(field.FieldType.GetGenericTypeDefinition()==typeof(Dictionary<,>)||field.FieldType.GetGenericTypeDefinition()==typeof(List<>)))field.SetValue(loc,Activator.CreateInstance(field.FieldType));
            else if(field.Name=="m_cache")field.SetValue(loc,Activator.CreateInstance(field.FieldType,new object[]{100}));
        }
        typeof(Localization).GetField("m_instance",F).SetValue(null,loc);
        var temp=Path.GetFullPath("../../../Tools/AugaWork/ammo-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        var bep=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/BepInEx.dll"));
        bep.GetType("BepInEx.Paths").GetMethod("SetExecutablePath",F).Invoke(null,new object[]{Path.Combine(temp,"Test.exe"),null,null,new string[0]});
        var cfg=Call("EquipmentAndQuickSlots","OpenConfig",temp,temp);
        var logger=Activator.CreateInstance(bep.GetType("BepInEx.Logging.ManualLogSource"),new object[]{"Ammo validation"});
        Call("EquipmentAndQuickSlots","Initialize",logger,cfg);
        Check((bool)T("EquipmentAndQuickSlots").GetProperty("IsInitialized").GetValue(null),"all Harmony patches install on current native game methods");
        var bindings=(Array)T("ValConfig").GetField("QuickSlotKeys",F).GetValue(null);
        string Key(object entry)=>entry.GetType().GetProperty("Value").GetValue(entry).ToString();
        Check(Key(bindings.GetValue(0))=="C"&&Key(bindings.GetValue(1))=="V"&&Key(bindings.GetValue(2))=="B","new food default keys C V B");
        Check(Key(T("ValConfig").GetField("AmmoCycleKey",F).GetValue(null))=="Z","ammo physical Z key (W AZERTY)");
        host=new GameObject("Inactive ammo fixture");host.SetActive(false);player=host.AddComponent<Player>();typeof(Humanoid).GetField("m_inventory",F).SetValue(player,new Inventory("Player",null,8,8));Player.m_localPlayer=player;
        db=host.AddComponent<ObjectDB>();ObjectDB.m_instance=db;
        slots=(Array)T("Slots").GetField("slots",F).GetValue(null);
        Check(Pos(0)==new Vector2i(0,4)&&Pos(8)==new Vector2i(0,5)&&Pos(24)==new Vector2i(0,7)&&Pos(28)==new Vector2i(4,7),"saved equipment/food/cosmetic cells unchanged; ammo occupies unused cells");
        var fire=Arrow("FireTest",12,28);var ice=Arrow("IceTest",8,29);var poison=Arrow("PoisonTest",1,30);
        var food=Make("FoodTest",ItemDrop.ItemData.ItemType.Consumable,10,Pos(0));food.m_shared.m_food=20;
        var potion=Make("MeadTest",ItemDrop.ItemData.ItemType.Consumable,5,new Vector2i(0,0));
        Check(Fits(0,food)&&!Fits(0,fire)&&!Fits(0,potion)&&Fits(28,fire)&&!Fits(28,food),"food and ammo enforce separate types, mead excluded");
        Put(fire,ice,poison);
        Check(Select()==fire&&Select(cycle:true)==ice&&Select(cycle:true)==poison&&Select(cycle:true)==fire,"selection starts first and cycles 1 2 3 1");
        var bolt=Make("BoltTest",ItemDrop.ItemData.ItemType.Ammo,10,Pos(29),"$ammo_bolts");Inv.RemoveItem(ice);Put(bolt);
        Check(Select(cycle:true)==poison,"arrow cycle skips bolts");Check(Select("$ammo_bolts")==bolt,"crossbow selects compatible bolt slot");
        var bow=Make("BowTest",ItemDrop.ItemData.ItemType.Bow,1,new Vector2i(7,0),"$ammo_arrows");player.m_rightItem=bow;
        Call("AmmoSlots","SetSelected",player,2);
        var attack=new Attack();typeof(Attack).GetField("m_character",F).SetValue(attack,player);typeof(Attack).GetField("m_weapon",F).SetValue(attack,bow);
        var useArgs=new object[]{null};Check((bool)typeof(Attack).GetMethod("UseAmmo",F).Invoke(attack,useArgs)&&useArgs[0]==poison&&!Inv.ContainsItem(poison),"native attack consumes exactly one selected arrow");
        Check(player.GetAmmoItem()==fire&&Inv.GetAmmoItem("$ammo_arrows")==fire&&fire.m_stack==12,"depleted slot wraps to next arrow without consuming it");
        Clear();var bag=Arrow("BagOnlyTest",20,0);bag.m_gridPos=new Vector2i(0,0);Put(bag);
        Check(Inv.GetAmmoItem("$ammo_arrows")==null&&player.GetAmmoItem()==null,"empty ammo slots never fall back to backpack arrows");
        var other=new Inventory("NPC",null,8,4);other.m_inventory.Add(bag.Clone());Check(other.GetAmmoItem("$ammo_arrows")!=null,"other inventories keep native ammo lookup");
        var gui=host.AddComponent<InventoryGui>();var grid=host.AddComponent<InventoryGrid>();grid.m_inventory=Inv;
        typeof(InventoryGui).GetMethod("OnRightClickItem",F).Invoke(gui,new object[]{grid,bag,bag.m_gridPos});
        Check(bag.m_gridPos==Pos(28)&&Inv.NrOfItems()==1,"native right click moves same object from bag into free ammo slot");
        var more=bag.Clone();more.m_gridPos=new Vector2i(1,0);more.m_stack=90;Put(more);
        Check(Insert(Inv,more)&&bag.m_stack==100&&more.m_stack==10&&more.m_gridPos==Pos(29)&&Inv.NrOfItems()==2,"right click fills stack then moves remainder into free slot");
        Clear();fire.m_stack=95;Put(fire,bolt,poison);var chest=new Inventory("Chest",null,8,4);more=fire.Clone();more.m_gridPos=new Vector2i(0,0);more.m_stack=12;chest.m_inventory.Add(more);
        Check(Insert(chest,more)&&fire.m_stack==100&&more.m_stack==7&&chest.ContainsItem(more),"full ammo row retains chest surplus after partial stacking");
        Check(!Insert(chest,more)&&more.m_stack==7,"full ammo row refuses transfer without loss");
        Inv.RemoveItem(poison);Check(Insert(chest,more)&&chest.NrOfItems()==0&&Inv.GetItemAt(Pos(30).x,Pos(30).y).m_customData["test"]=="kept","chest transfer preserves item metadata");
        Clear();var legacy=Arrow("LegacyTest",30,0);var material=Make("MaterialTest",ItemDrop.ItemData.ItemType.Material,4,Pos(1));Put(legacy,material,food);food.m_gridPos=Pos(2);Call("Slots","ClearCachedItems");
        Call("AmmoSlots","MigrateInvalidSlots");Check(legacy.m_gridPos==Pos(28)&&material.m_gridPos.y<4&&food.m_gridPos==Pos(2)&&Inv.NrOfItems()==3,"legacy migration moves ammo, returns material to bag, retains food");
        var pack=new ZPackage();Inv.Save(pack);Inv.m_inventory.Clear();player.m_isLoading=true;Inv.Load(new ZPackage(pack.GetArray()));player.m_isLoading=false;Call("Slots","ClearCachedItems");
        Check(Inv.NrOfItems()==3&&Inv.GetItemAt(Pos(28).x,Pos(28).y)?.m_stack==30,"ammo and food survive native inventory save/load");
        foreach(var item in Inv.GetAllItems())item.m_dropPrefab=db.GetItemPrefab(item.m_shared.m_name);
        var grave=new Inventory("Grave",null,8,8);grave.MoveAll(Inv);Check(grave.NrOfItems()==3&&Inv.NrOfItems()==0,"grave receives food and ammo");
        Inv.MoveAll(grave);Call("Slots","ClearCachedItems");Check(Inv.NrOfItems()==3&&grave.NrOfItems()==0&&Inv.GetItemAt(Pos(28).x,Pos(28).y)?.m_stack==30,"grave recovery returns ammo to its cell without duplication");
        var passDrop=T("InventoryPatches").GetMethod("PassDropItem",F);
        Check(!(bool)passDrop.Invoke(null,new object[]{"Test",grid,Inv,material,Pos(28)}),"dragging material into ammo slot is rejected");
        Check(!(bool)passDrop.Invoke(null,new object[]{"Test",grid,Inv,legacy,Pos(0)}),"dragging ammo into food slot is rejected");
        Clear();material.m_gridPos=Pos(0);Put(material);
        for(int y=0;y<4;y++)for(int x=0;x<8;x++){var f=material.Clone();f.m_stack=100;f.m_gridPos=new Vector2i(x,y);Put(f);}
        var harmonyAssembly=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll"));var harmonyType=harmonyAssembly.GetType("HarmonyLib.Harmony");
        var harmony=Activator.CreateInstance(harmonyType,new object[]{"overhaul.ammo.fixture.drop"});
        var prefix=Activator.CreateInstance(harmonyAssembly.GetType("HarmonyLib.HarmonyMethod"),new object[]{typeof(AmmoSlotsChecks).GetMethod("Drop",F)});
        harmonyType.GetMethods().First(m=>m.Name=="Patch"&&m.GetParameters().Length==5).Invoke(harmony,new object[]{typeof(Humanoid).GetMethod("DropItem",F),prefix,null,null,null});
        Call("AmmoSlots","MigrateInvalidSlots");Call("AmmoSlots","MigrateInvalidSlots");
        Check(dropped.Count==1&&dropped[0].m_stack==4&&Inv.NrOfItems()==32,"full inventory migration requests one native ground drop without duplication");
        var invalid=material.Clone();invalid.m_stack=1;
        Check(!Inv.AddItem(invalid,1,Pos(28).x,Pos(28).y,false)&&invalid.m_stack==1&&Inv.NrOfItems()==32,"direct targeted addition refuses invalid item when backpack is full");
        Check(!Inv.AddItem(invalid,Pos(28))&&Inv.NrOfItems()==32,"position overload also refuses invalid item when backpack is full");
        harmonyType.GetMethod("UnpatchSelf").Invoke(harmony,null);
        var bundle=AssetBundle.LoadFromFile("AssetBundles/augaassets");
        var settings=UnityEngine.Object.Instantiate(bundle.LoadAsset<GameObject>("AugaSettings"),host.transform,false).GetComponentInChildren<AugaModsSettings>(true);
        Check(settings.BindButtons.Length==6&&settings.Displays.Length==6,"real Mods prefab includes ammo and class bindings");
        var writes=new List<string>();AugaModsSettings.IsEqsActive=()=>true;AugaModsSettings.ReadShortcut=i=>new[]{"C","V","B","Z","P","H"}[i];AugaModsSettings.WriteShortcut=(i,v)=>writes.Add(i+":"+v);AugaModsSettings.DisplayShortcut=s=>s;
        settings.Initialize();settings.SetPending(3,"G");settings.OnBack();settings.OnOkAsync(null);Check(writes.Count==0,"Back discards ammo binding edit");
        settings.SetPending(3,"H");settings.OnOkAsync(null);Check(writes.SequenceEqual(new[]{"3:H"}),"Apply saves ammo binding through fourth callback");
        var panel=bundle.LoadAsset<GameObject>("Inventory_screen").transform.Find("root/Player/AugaEquipment");
        Check(panel.Find("AmmoSlots")&&((RectTransform)panel.Find("AmmoSlots")).anchoredPosition.y>((RectTransform)panel.Find("QuickSlots")).anchoredPosition.y,"actual equipment prefab places ammo above food");
        results.Add("Native drop creation / live input / multiplayer: requires in-game verification; drop test captures the native call.");
    }
}
