using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
public static class TrinketAudit {
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
var rows=new List<string>();var items=new List<ItemDrop.ItemData>();
foreach(var p in paths.Where(p=>p.Key.EndsWith(".prefab")&&p.Key.IndexOf("/items/",StringComparison.OrdinalIgnoreCase)>=0)){
Load(p.Value);var go=bundles[p.Value].LoadAsset<GameObject>(p.Key);if(!go)continue;var item=go.GetComponent<ItemDrop>();if(item)items.Add(item.m_itemData);if(item)rows.Add(item.m_itemData.m_shared.m_maxStackSize+"\t"+go.name+"\t"+item.m_itemData.m_shared.m_name);
}
File.WriteAllLines("../../../Tools/AugaWork/item-stack-survey.tsv",rows.OrderByDescending(r=>int.Parse(r.Split('\t')[0])));
var report=new List<string>();foreach(var item in items.Where(i=>i.m_shared.m_itemType==ItemDrop.ItemData.ItemType.Trinket)){var se=item.m_shared.m_fullAdrenalineSE;report.Add(UnityEngine.JsonUtility.ToJson(item.m_shared,true));if(item.m_dropPrefab)report.Add("COMPONENTS "+string.Join(",",item.m_dropPrefab.GetComponentsInChildren<Component>(true).Where(c=>c).Select(c=>c.GetType().Name)));report.Add("ITEM "+item.m_shared.m_name+" description="+item.m_shared.m_description+" equip="+se+" attack="+item.m_shared.m_attackStatusEffect);if(se)report.Add(UnityEngine.JsonUtility.ToJson(se,true));}File.WriteAllLines("../../../Tools/AugaWork/trinket-audit.txt",report);
}}