using System;using System.IO;using System.Linq;using System.Reflection;using System.Collections.Generic;using UnityEngine;
public static class OverhaulTreeSeedChecks {
 const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
 public static void Run(){
 AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var p=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
 var plugin=Assembly.LoadFrom(Path.GetFullPath("../../../Tools/AugaWork/ConstructionStage/Overhaul.dll"));var scope=plugin.GetType("Overhaul.TreeSeedDrops");var field=scope.GetField("Current",F);var patch=scope.GetNestedType("GuaranteedSeeds",BindingFlags.NonPublic|BindingFlags.Public);var harmonyType=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll")).GetType("HarmonyLib.Harmony");var harmony=Activator.CreateInstance(harmonyType,new object[]{"overhaul.tests.treeSeeds"});var processor=harmonyType.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(harmony,new object[]{patch});processor.GetType().GetMethod("Patch",Type.EmptyTypes).Invoke(processor,null);var objects=new List<GameObject>();float rate=Game.m_resourceRate;var rng=UnityEngine.Random.state;int checks=0;
 try{var wood=new GameObject("Wood");objects.Add(wood);foreach(var name in new[]{"BeechSeeds","BirchSeeds","Acorn","FirCone","PineCone","FirConeFrost"}){var seed=new GameObject(name);objects.Add(seed);var table=new DropTable{m_dropChance=1,m_dropMin=2,m_dropMax=5};table.m_drops.Add(new DropTable.DropData{m_item=wood,m_weight=1,m_stackMin=1,m_stackMax=1});table.m_drops.Add(new DropTable.DropData{m_item=seed,m_weight=1,m_stackMin=1,m_stackMax=2});
 foreach(float multiplier in new[]{0f,.5f,1f,3f})for(int i=0;i<128;i++){
 Game.m_resourceRate=multiplier;field.SetValue(null,null);UnityEngine.Random.InitState(i);var baseline=table.GetDropList();field.SetValue(null,table);UnityEngine.Random.InitState(i);var actual=table.GetDropList();int count=actual.Count(g=>g==seed);if(count<1||count>3||actual.Count(g=>g==wood)!=baseline.Count(g=>g==wood))throw new Exception("Seed guarantee changed non-seed loot or exceeded bounds");checks++;
 field.SetValue(null,new DropTable());UnityEngine.Random.InitState(i);if(!table.GetDropList().SequenceEqual(baseline))throw new Exception("Unrelated loot modified");checks++;
 }
 table.m_dropChance=0;field.SetValue(null,table);if(table.GetDropList().Count<1)throw new Exception("No seed on failed native roll");checks++;
 }
 var barren=new DropTable();field.SetValue(null,barren);if(barren.GetDropList().Count!=0)throw new Exception("Invented seed for seedless tree");
 var previous=new DropTable();var error=new Exception("scope test");if(!ReferenceEquals(scope.GetMethod("Finalizer",F).Invoke(null,new object[]{previous,error}),error)||!ReferenceEquals(field.GetValue(null),previous))throw new Exception("Exception scope not restored");
 File.WriteAllText("../../../Tools/AugaWork/tree-seed-checks.txt","PASS "+checks+" loot checks: six tree seed types, rates 0/0.5/1/3, exact 1-3 seeds, other drops preserved, unrelated tables unchanged, failed native rolls, seedless trees and scope restoration.");
 }finally{field.SetValue(null,null);harmonyType.GetMethod("UnpatchSelf",Type.EmptyTypes).Invoke(harmony,null);Game.m_resourceRate=rate;UnityEngine.Random.state=rng;foreach(var go in objects)UnityEngine.Object.DestroyImmediate(go);}
 }
}


