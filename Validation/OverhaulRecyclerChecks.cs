using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public static class OverhaulRecyclerChecks
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static readonly List<string> report=new List<string>();
    static void Check(bool ok,string text){if(!ok)throw new Exception(text);report.Add("PASS "+text);}
    public static void Run()
    {
        try { Test(); File.WriteAllLines("../../../Tools/AugaWork/recycler-checks.txt",report); }
        catch(Exception e){report.Add("FAIL "+e);File.WriteAllLines("../../../Tools/AugaWork/recycler-checks.txt",report);throw;}
    }
    static void Test()
    {
        AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var p=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        var mod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));
        var type=mod.GetType("Overhaul.Storage.EquipmentRecycler");
        var root=new GameObject("Recycler tests");root.SetActive(false);
        var db=root.AddComponent<ObjectDB>();typeof(ObjectDB).GetField("m_instance",F).SetValue(null,db);
        ItemDrop Prefab(string name,ItemDrop.ItemData.ItemType kind,int stack=50)
        {
            var go=new GameObject(name);go.transform.SetParent(root.transform);var d=go.AddComponent<ItemDrop>();
            d.m_itemData=new ItemDrop.ItemData{m_shared=new ItemDrop.ItemData.SharedData()};d.m_itemData.m_shared.m_name=name;d.m_itemData.m_shared.m_itemType=kind;d.m_itemData.m_shared.m_maxStackSize=stack;d.m_itemData.m_dropPrefab=go;
            db.m_items.Add(go);db.m_itemByHash[name.GetStableHashCode()]=go;return d;
        }
        var wood=Prefab("Wood",ItemDrop.ItemData.ItemType.Material);var coal=Prefab("Coal",ItemDrop.ItemData.ItemType.Material);
        var iron=Prefab("Iron",ItemDrop.ItemData.ItemType.Material);var food=Prefab("Food",ItemDrop.ItemData.ItemType.Consumable);
        var sword=Prefab("Sword",ItemDrop.ItemData.ItemType.OneHandedWeapon,1);
        var recipe=ScriptableObject.CreateInstance<Recipe>();recipe.m_item=sword;recipe.m_resources=new[]{new Piece.Requirement{m_resItem=wood,m_amount=5,m_amountPerLevel=2},new Piece.Requirement{m_resItem=iron,m_amount=10,m_amountPerLevel=3}};db.m_recipes.Add(recipe);
        var item=sword.m_itemData.Clone();item.m_quality=3;
        var refund=(List<ItemDrop.ItemData>)type.GetMethod("Refund",F).Invoke(null,new object[]{item,recipe});
        Check(refund.Single(i=>i.m_shared.m_name=="Wood").m_stack==5,"wood refund floor((5+2+4)/2)=5");
        Check(refund.Single(i=>i.m_shared.m_name=="Iron").m_stack==9,"metal refund floor((10+3+6)/2)=9");
        int Recycle(Inventory inv,List<ItemDrop.ItemData> overflow)=>(int)type.GetMethod("Recycle",F).Invoke(null,new object[]{inv,new Action<ItemDrop.ItemData>(overflow.Add)});
        void Add(Inventory inv,ItemDrop d,int n){var clone=d.m_itemData.Clone();clone.m_stack=n;inv.AddItem(clone);}
        var inv=new Inventory("test",null,8,4);inv.AddItem(item);Add(inv,wood,4);Add(inv,food,2);Add(inv,iron,7);var extra=new List<ItemDrop.ItemData>();
        Check(Recycle(inv,extra)==2,"only equipment and input wood consumed");
        Check(inv.CountItems("Coal")==3&&inv.CountItems("Wood")==5,"75 percent coal, refunded wood not recycled in same operation");
        Check(inv.CountItems("Food")==2&&inv.CountItems("Iron")==16&&inv.CountItems("Sword")==0,"food and existing metal preserved, equipment replaced by refund");
        Check(extra.Count==0,"refund fits in inventory");
        foreach(var name in new[]{"RoundLog","FineWood","ElderBark","YggdrasilWood","Blackwood"})
        {var d=Prefab(name,ItemDrop.ItemData.ItemType.Material);Check((bool)type.GetMethod("IsWood",F).Invoke(null,new[]{d.m_itemData}),"wood type "+name);}
        var tiny=new Inventory("tiny",null,1,1);var s=sword.m_itemData.Clone();s.m_quality=3;tiny.AddItem(s);extra.Clear();Recycle(tiny,extra);
        Check(tiny.CountItems("Wood")+extra.Where(i=>i.m_shared.m_name=="Wood").Sum(i=>i.m_stack)==5&&tiny.CountItems("Iron")+extra.Where(i=>i.m_shared.m_name=="Iron").Sum(i=>i.m_stack)==9,"full container preserves all refund through overflow");
        var safe=new Inventory("safe",null,4,2);Add(safe,food,3);Add(safe,iron,4);extra.Clear();Check(Recycle(safe,extra)==0&&safe.CountItems("Food")==3&&safe.CountItems("Iron")==4,"unsupported items never destroyed or converted to coal");
        recipe.m_requireOnlyOneIngredient=true;Check(((IList)type.GetMethod("Refund",F).Invoke(null,new object[]{item,recipe})).Count==0,"ambiguous alternative recipe cannot multiply refunds");
        var harmonyAssembly=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll"));var ht=harmonyAssembly.GetType("HarmonyLib.Harmony");var harmony=Activator.CreateInstance(ht,new object[]{"overhaul.recycler.checks"});
        var processor=ht.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(harmony,new object[]{type});processor.GetType().GetMethod("Patch").Invoke(processor,null);
        var machine=root.AddComponent<Incinerator>();var routine=machine.Incinerate(1);Check(routine.GetType().DeclaringType==type,"native incineration replaced with owner-checked recycler coroutine");ht.GetMethod("UnpatchSelf").Invoke(harmony,null);
    }
}
