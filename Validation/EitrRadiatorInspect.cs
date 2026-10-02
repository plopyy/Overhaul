using System.Reflection;using System;using System.IO;using System.Linq;using System.Collections.Generic;using UnityEngine;public static class EitrRadiatorInspect {const string Root="D:/Valheim/valheim_Data/StreamingAssets/SoftRef/";
public static Action<Animator,List<string>> AfterPose;
static Dictionary<string,List<string>> deps=new Dictionary<string,List<string>>();
static Dictionary<string,AssetBundle> bundles=new Dictionary<string,AssetBundle>();
static void Load(string id){if(bundles.ContainsKey(id))return;bundles[id]=null;if(deps.ContainsKey(id))foreach(var d in deps[id])Load(d);bundles[id]=AssetBundle.LoadFromFile(Root+"Bundles/"+id);}
public static void Run(){ AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var p=Path.GetFullPath("../../../Libs/"+new System.Reflection.AssemblyName(a.Name).Name+".dll");return File.Exists(p)?System.Reflection.Assembly.LoadFrom(p):null;};
var paths=new Dictionary<string,string>();string current=null,bundle=null;bool assets=false;
foreach(var line in File.ReadAllLines(Root+"manifest_extended").Concat(File.ReadAllLines(Root+"manifest"))){
if(line=="bundle dependencies:")assets=false;else if(line.StartsWith("asset locations:"))assets=true;
else if(!assets){if(line.StartsWith("- bundle: ")){current=line.Substring(10).Trim();deps[current]=new List<string>();}else if(current!=null&&line.StartsWith("  - "))deps[current].Add(line.Substring(4).Trim());}
else if(line.StartsWith("  bundle: "))bundle=line.Substring(10).Trim();else if(line.StartsWith("  path in bundle: "))paths[line.Substring(18).Trim()]=bundle;
}
var report=new List<string>();
var mod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));var prefix=mod.GetType("Overhaul.Patches.EitrRefineryRadiation").GetMethod("Prefix",BindingFlags.Static|BindingFlags.NonPublic);
foreach(var prefabName in new[]{"eitrrefinery","Eitr"}){var path=paths.First(p=>p.Key.EndsWith("/"+prefabName+".prefab",StringComparison.OrdinalIgnoreCase));Load(path.Value);var prefab=bundles[path.Value].LoadAsset<GameObject>(path.Key);
report.Add("PREFAB "+prefab.name+" smelter="+(prefab.GetComponent<Smelter>()!=null));
foreach(var r in prefab.GetComponentsInChildren<Radiator>(true)){if((bool)prefix.Invoke(null,new object[]{r}))throw new Exception("Emitter not suppressed: "+prefabName);report.Add("RADIATOR "+r.name+" parent smelter="+(r.GetComponentInParent<Smelter>(true)?r.GetComponentInParent<Smelter>(true).name:"item:"+r.GetComponentInParent<ItemDrop>(true).name)+" projectile="+r.m_projectile.name+" suppressed=True");}
}
File.WriteAllLines("../../../Tools/AugaWork/eitr-radiator-inspect.txt",report);
}}
