using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
public static class CraftingStationChecks {
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
var mod=System.Reflection.Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));var flags=System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic;var report=new List<string>();
var path=paths.Single(p=>p.Key.EndsWith("/Recipe_ArrowWood.asset"));Load(path.Value);var recipe=UnityEngine.Object.Instantiate(bundles[path.Value].LoadAsset<Recipe>(path.Key));int amount=recipe.m_amount;var resources=recipe.m_resources;
var host=new GameObject("Inactive recipe check");host.SetActive(false);var db=host.AddComponent<ObjectDB>();db.m_recipes=new List<Recipe>{recipe};typeof(ObjectDB).GetField("m_instance",flags|System.Reflection.BindingFlags.Public).SetValue(null,db);mod.GetType("Overhaul.Patches.WoodenArrowRecipe").GetMethod("Apply",flags).Invoke(null,null);if(recipe.GetRequiredStation(1)!=null||recipe.m_amount!=amount||recipe.m_resources!=resources)throw new Exception("Arrow recipe mismatch");report.Add("PASS native wooden arrow recipe requires no station; amount and ingredients unchanged");
var config=mod.GetType("Overhaul.DvergerCirclet").GetMethod("RecipeConfig",flags).Invoke(null,null);var ct=config.GetType();if((string)ct.GetProperty("CraftingStation").GetValue(config)!="forge"||(int)ct.GetProperty("MinStationLevel").GetValue(config)!=1)throw new Exception("Circlet station mismatch");report.Add("PASS spirit circlet requires forge level 1");var req=(Array)ct.GetProperty("Requirements").GetValue(config);if(req.Length!=2)throw new Exception("Ingredients count");for(int i=0;i<2;i++){var r=req.GetValue(i);if((string)r.GetType().GetProperty("Item").GetValue(r)!=(i==0?"HelmetDverger":"Demister")||(int)r.GetType().GetProperty("Amount").GetValue(r)!=1)throw new Exception("Ingredient mismatch");}report.Add("PASS circlet ingredients unchanged: one HelmetDverger and one Demister");File.WriteAllLines("../../../Tools/AugaWork/crafting-station-checks.txt",report);UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(recipe);
}}