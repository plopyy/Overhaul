using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
public static class ItemStackSurvey {
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
var mod=System.Reflection.Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));
var apply=mod.GetType("Overhaul.AlterItemStat").GetMethod("Apply",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
int changed=0;
foreach(var item in items){int before=item.m_shared.m_maxStackSize;int stack=item.m_stack;apply.Invoke(null,new object[]{item});int expected=before>1&&before<100?100:before;if(item.m_shared.m_maxStackSize!=expected||item.m_stack!=stack)throw new Exception("Stack rule failed: "+item.m_shared.m_name);if(before!=expected)changed++;apply.Invoke(null,new object[]{item});if(item.m_shared.m_maxStackSize!=expected)throw new Exception("Not idempotent");}
var material=items.First(i=>i.m_shared.m_name=="$item_wood");var inv=new Inventory("stack-test",null,2,1);var a=material.Clone();a.m_stack=60;inv.AddItem(a);var b=material.Clone();b.m_stack=40;inv.AddItem(b);if(inv.NrOfItems()!=1||inv.GetAllItems()[0].m_stack!=100)throw new Exception("Native inventory merge to 100 failed");
File.WriteAllText("../../../Tools/AugaWork/stack100-checks.txt","PASS "+items.Count+" real item prefabs: "+changed+" limits raised; exceptions and quantities preserved; idempotent; native inventory merges 60+40 to 100.");
}
}
