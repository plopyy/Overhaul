using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using System.Reflection;
public static class KilnWoodChecks {
const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
static Assembly jot;static object metadata,harmony;static Type ht,hm;
static bool Skip()=>false;
static bool Source(object __instance){jot.GetType("Jotunn.Entities.CustomEntity").GetField("<SourceMod>k__BackingField",F).SetValue(__instance,metadata);return false;}
static void Patch(MethodBase original,string prefix){ht.GetMethods().First(x=>x.Name=="Patch"&&x.GetParameters().Length==5).Invoke(harmony,new object[]{original,Activator.CreateInstance(hm,new object[]{typeof(KilnWoodChecks).GetMethod(prefix,F)}),null,null,null});}
static void Bootstrap(){
var ha=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll"));ht=ha.GetType("HarmonyLib.Harmony");hm=ha.GetType("HarmonyLib.HarmonyMethod");harmony=Activator.CreateInstance(ht,new object[]{"overhaul.kiln.checks"});
foreach(var t in new[]{typeof(ObjectDB),typeof(ZNetView),typeof(ZNetScene),typeof(ItemDrop),typeof(Smelter)})Patch(t.GetMethod("Awake",F),"Skip");
jot=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/Jotunn.dll"));Patch(jot.GetType("Jotunn.Main").GetMethod("LogInit",F),"Skip");jot.GetType("Jotunn.Main").GetField("rootObject",F).SetValue(null,new GameObject("Jotunn kiln fixture root"));
var bep=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/BepInEx.dll"));bep.GetType("BepInEx.Paths").GetMethod("SetExecutablePath",F).Invoke(null,new object[]{Path.GetFullPath("../../../Tools/JotunnAudit/Test.exe"),null,null,new string[0]});metadata=Activator.CreateInstance(bep.GetType("BepInEx.BepInPlugin"),new object[]{"audit.overhaul","Overhaul kiln audit","2.2.0"});
// Supply plugin metadata in the isolated editor; execute actual Jotunn conversion registration below.
Patch(jot.GetType("Jotunn.Entities.CustomEntity").GetConstructor(F,null,new[]{typeof(Assembly)},null),"Source");
}
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
var mod=System.Reflection.Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));Bootstrap();var flags=System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic;var type=mod.GetType("Overhaul.Storage.CharcoalKilnWoods");var woods=(string[])type.GetField("Woods",flags).GetValue(null);var host=new GameObject("Inactive kiln checks");host.SetActive(false);var db=host.AddComponent<ObjectDB>();typeof(ObjectDB).GetField("m_instance",F).SetValue(null,db);var scene=host.AddComponent<ZNetScene>();typeof(ZNetScene).GetField("s_instance",F).SetValue(null,scene);db.m_items=new List<GameObject>();
foreach(var name in woods.Concat(new[]{"Coal"})){var p=paths.Single(x=>x.Key.EndsWith("/"+name+".prefab"));Load(p.Value);db.m_items.Add(bundles[p.Value].LoadAsset<GameObject>(p.Key));}db.UpdateRegisters();var path=paths.Single(x=>x.Key.EndsWith("/charcoal_kiln.prefab"));Load(path.Value);var kilnObject=UnityEngine.Object.Instantiate(bundles[path.Value].LoadAsset<GameObject>(path.Key),host.transform);var kiln=kilnObject.GetComponent<Smelter>();float duration=kiln.m_secPerProduct;kilnObject.name="charcoal_kiln";scene.m_namedPrefabs[kilnObject.name.GetStableHashCode()]=kilnObject;type.GetMethod("Initialize",flags).Invoke(null,null);var im=jot.GetType("Jotunn.Managers.ItemManager");var manager=im.GetProperty("Instance",F).GetValue(null);type.GetMethod("Register",flags).Invoke(null,null);im.GetMethod("RegisterCustomItemConversions",F).Invoke(manager,null);type.GetMethod("PrioritizeNormalWood",flags).Invoke(null,new object[]{kiln});int count=kiln.m_conversion.Count;type.GetMethod("Register",flags).Invoke(null,null);im.GetMethod("RegisterCustomItemConversions",F).Invoke(manager,null);type.GetMethod("PrioritizeNormalWood",flags).Invoke(null,new object[]{kiln});if(kiln.m_conversion.Count!=count||kiln.m_secPerProduct!=duration)throw new Exception("Duplicate or duration changed");
var report=new List<string>();foreach(var name in woods){if(!kiln.m_conversion.Any(c=>c.m_from.name==name&&c.m_to.name=="Coal"))throw new Exception("Missing wood "+name);report.Add("PASS "+name+" to Coal");}if(kiln.m_conversion[0].m_from.name!="Wood")throw new Exception("Priority");
var inventory=new Inventory("Woods",null,8,4);foreach(var name in new[]{"FineWood","Wood","RoundLog"}){var item=db.GetItemPrefab(name).GetComponent<ItemDrop>().m_itemData.Clone();item.m_dropPrefab=db.GetItemPrefab(name);item.m_stack=2;inventory.AddItem(item);}var selected=kiln.FindCookableItem(inventory);if(selected.m_dropPrefab.name!="Wood")throw new Exception("Inventory priority");inventory.RemoveItem(selected,2);if(selected.m_dropPrefab.name!="Wood"||inventory.GetAllItems().Any(i=>i.m_dropPrefab==selected.m_dropPrefab))throw new Exception("Selected type not exhausted");report.Add("PASS normal wood selected first regardless of inventory order; captured selection remains Wood after exhaustion");report.Add("PASS idempotent conversions and native processing duration");File.WriteAllLines("../../../Tools/AugaWork/kiln-wood-checks.txt",report);type.GetMethod("Shutdown",flags).Invoke(null,null);
}}