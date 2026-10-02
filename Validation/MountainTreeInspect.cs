using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
public static class MountainTreeInspect {
public static Dictionary<string,GameObject> Prefabs=new Dictionary<string,GameObject>();
const string Root="D:/Valheim/valheim_Data/StreamingAssets/SoftRef/";
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
foreach(var p in paths.Where(p=>p.Key.EndsWith(".prefab",StringComparison.OrdinalIgnoreCase)&&(p.Key.IndexOf("Fir",StringComparison.OrdinalIgnoreCase)>=0||p.Key.IndexOf("Pinetree",StringComparison.OrdinalIgnoreCase)>=0||p.Key.Contains("_LocationList")||p.Key.EndsWith("/_ZoneSystem.prefab")))){
Load(p.Value);var go=bundles[p.Value].LoadAsset<GameObject>(p.Key);if(!go)continue;Prefabs[go.name]=go;
var locations=go.GetComponent<LocationList>();if(locations)foreach(var v in locations.m_vegetation.Where(v=>v.m_prefab&&(v.m_prefab.name.Contains("Fir")||v.m_prefab.name.Contains("Pine"))))report.Add("VEG "+v.m_prefab.name+" biome="+v.m_biome+" enabled="+v.m_enable);
var zone=go.GetComponent<ZoneSystem>();if(zone)foreach(var v in zone.m_vegetation.Where(v=>v.m_prefab&&(v.m_prefab.name.Contains("Fir")||v.m_prefab.name.Contains("Pine"))))report.Add("VEG "+v.m_prefab.name+" biome="+v.m_biome+" enabled="+v.m_enable);
var tree=go.GetComponent<TreeBase>();var log=go.GetComponent<TreeLog>();if(!tree&&!log)continue;
report.Add("PREFAB "+go.name+" path="+p.Key+(tree?" log="+tree.m_logPrefab.name:" sub="+(log.m_subLogPrefab?log.m_subLogPrefab.name:"none")+" points="+log.m_subLogPoints.Length));
var d=tree?tree.m_dropWhenDestroyed:log.m_dropWhenDestroyed;report.Add("TABLE rolls="+d.m_dropMin+".."+d.m_dropMax+" chance="+d.m_dropChance+" each="+d.m_oneOfEach);foreach(var e in d.m_drops)report.Add("DROP "+e.m_item.name+" stack="+e.m_stackMin+".."+e.m_stackMax+" weight="+e.m_weight);
}
File.WriteAllLines("../../../Tools/AugaWork/mountain-tree-inspect.txt",report);}}