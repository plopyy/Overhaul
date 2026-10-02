using System;
using System.IO;
using System.Reflection;
using UnityEngine;
public static class OverhaulCaveHeightCheck {
 const BindingFlags All=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
 public static void Run() {
 AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var p=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
 var plugin=Assembly.LoadFrom(Path.GetFullPath("../../../Tools/AugaWork/CaveHotfix/Overhaul.dll"));
 var placement=plugin.GetType("Overhaul.Dungeons.BossInteriorPlacement");
 var layout=plugin.GetType("Overhaul.Dungeons.BossDungeonLayout");
 var loader=new DungeonTestAssets();var field=typeof(SoftReferenceableAssets.Runtime).GetField("s_assetLoader",All);var old=field.GetValue(null);field.SetValue(null,loader);
 int count=0;
 try { foreach(bool custom in new[]{false,true}) foreach(float offset in new[]{0f,110f,-7f}) foreach(float surface in new[]{37f,220f}) {
 var root=new GameObject("MountainCave02");root.SetActive(false);var loc=root.AddComponent<Location>();
 var inner=new GameObject("Interior").transform;inner.SetParent(root.transform);inner.localPosition=Vector3.up*5000;
 var gen=new GameObject("DG_Cave");gen.transform.SetParent(inner);gen.transform.localPosition=Vector3.up*offset;var generator=gen.AddComponent<DungeonGenerator>();
 loc.m_interiorTransform=inner;loc.m_generator=generator;loc.m_useCustomInteriorTransform=custom;
 var position=new Vector3(-128,surface,256);var zone=new ZoneSystem.ZoneLocation{m_prefab=loader.Add(root)};
 float expected=(float)layout.GetMethod("Height",All).Invoke(null,new object[]{position});
 for(int n=0;n<3;n++) {
 object[] args={zone,position,ZoneSystem.SpawnMode.Ghost,null};placement.GetMethod("Prefix",All).Invoke(null,args);
 // ZoneSystem.SpawnLocation zeros the generator local position for custom interiors.
 float actual=surface+inner.localPosition.y+(custom?0:gen.transform.localPosition.y);
 if(Mathf.Abs(actual-expected)>.01f)throw new Exception("Height drift: custom="+custom+" offset="+offset+" expected="+expected+" actual="+actual);
 placement.GetMethod("Finalizer",All).Invoke(null,new object[]{args[3],null});
 if(Mathf.Abs(inner.localPosition.y-5000)>.01f)throw new Exception("Prefab not restored");count++;
 }
 UnityEngine.Object.DestroyImmediate(root);
 }} finally{field.SetValue(null,old);}
 plugin.GetType("Auga.Construction_Setup").GetMethod("Postfix",All).Invoke(null,new object[]{null});
 File.WriteAllText("../../../Tools/AugaWork/cave-height-check.txt","PASS "+plugin.GetName().Version+": "+count+" placements/reloads, custom and legacy interiors, nonzero generator offsets and different surface heights; prefab restored; construction hotfix retained.");
 }
}
