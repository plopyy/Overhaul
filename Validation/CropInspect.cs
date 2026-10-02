using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
public static class CropInspect {
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
foreach(var p in paths.Where(p=>p.Key.EndsWith("/Cultivator.prefab",StringComparison.OrdinalIgnoreCase))){Load(p.Value);var go=bundles[p.Value].LoadAsset<GameObject>(p.Key);
foreach(var piece in go.GetComponent<ItemDrop>().m_itemData.m_shared.m_buildPieces.m_pieces){var plant=piece.GetComponent<Plant>();if(!plant)continue;report.Add("PLANT "+piece.name+" components="+string.Join(",",piece.GetComponents<Component>().Where(c=>c).Select(c=>c.GetType().Name))); report.Add("COST "+string.Join(",",piece.GetComponent<Piece>().m_resources.Select(r=>r.m_resItem.name+":"+r.m_amount+":"+r.m_recover)));
foreach(var grown in plant.m_grownPrefabs){var pick=grown.GetComponent<Pickable>();report.Add("GROWN "+grown.name+" components="+string.Join(",",grown.GetComponents<Component>().Where(c=>c).Select(c=>c.GetType().Name))+" ITEM="+(pick?pick.m_itemPrefab.name:"-")+" AMOUNT="+(pick?pick.m_amount:0));}
}}
File.WriteAllLines("../../../Tools/AugaWork/crop-inspect.txt",report);}}
