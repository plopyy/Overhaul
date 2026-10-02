using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
public static class ProductionCapacityCheck {
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
var plugin=System.Reflection.Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));
var flags=System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic;
var smelterPatch=plugin.GetType("Overhaul.Storage.SmelterCapacityPatch").GetMethod("Prefix",flags);
var cookingPatch=plugin.GetType("Overhaul.Storage.CookingFuelCapacityPatch").GetMethod("Prefix",flags);
var rows=new List<string>{plugin.GetName().Version.ToString()};int checks=0;
foreach(var p in paths.Where(p=>p.Key.EndsWith(".prefab")&&p.Key.Contains("/Pieces/"))){Load(p.Value);var go=bundles[p.Value].LoadAsset<GameObject>(p.Key);if(!go)continue;
foreach(var machine in go.GetComponentsInChildren<Smelter>(true)){int ore=machine.m_maxOre,fuel=machine.m_maxFuel;smelterPatch.Invoke(null,new object[]{machine});if(machine.m_maxOre!=(ore>0?Math.Max(100,ore):ore)||machine.m_maxFuel!=(fuel>0?Math.Max(100,fuel):fuel))throw new Exception(go.name+" capacity mismatch");checks++;rows.Add("PASS "+go.name+" input/fuel "+ore+"/"+fuel+" -> "+machine.m_maxOre+"/"+machine.m_maxFuel);}
foreach(var machine in go.GetComponentsInChildren<CookingStation>(true)){int slots=machine.m_slots.Length,fuel=machine.m_maxFuel;var anchors=machine.m_slots.ToArray();cookingPatch.Invoke(null,new object[]{machine});if(!anchors.SequenceEqual(machine.m_slots)||machine.m_maxFuel!=(machine.m_useFuel&&fuel>0?Math.Max(100,fuel):fuel))throw new Exception(go.name+" cooking mismatch");checks++;rows.Add("PASS "+go.name+" physical slots "+slots+" preserved; fuel "+fuel+" -> "+machine.m_maxFuel+"; uses fuel="+machine.m_useFuel);}}
rows.Add("PASS "+checks+" native production devices");File.WriteAllLines("../../../Tools/AugaWork/production-capacity-checks.txt",rows);}}
