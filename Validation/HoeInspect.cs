using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
public static class HoeInspect {
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
var report=new List<string>();foreach(var p in paths.Where(p=>p.Key.EndsWith("/Hoe.prefab",StringComparison.OrdinalIgnoreCase))){Load(p.Value);var hoe=bundles[p.Value].LoadAsset<GameObject>(p.Key);var table=hoe.GetComponent<ItemDrop>().m_itemData.m_shared.m_buildPieces;report.Add("TABLE="+table.name);foreach(var piece in table.m_pieces){report.Add(piece.name+" "+string.Join(",",piece.GetComponents<Component>().Where(c=>c).Select(c=>c.GetType().Name)));report.Add(JsonUtility.ToJson(piece.GetComponent<Piece>(),true));var op=piece.GetComponent<TerrainOp>();if(op)report.Add(JsonUtility.ToJson(op,true));}}
File.WriteAllLines("../../../Tools/AugaWork/hoe-inspect.txt",report);}}
