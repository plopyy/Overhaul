using System.Reflection;using System;using System.IO;using System.Linq;using System.Collections.Generic;using UnityEngine;public static class MobWeaponInventory {const string Root="D:/Valheim/valheim_Data/StreamingAssets/SoftRef/";
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
var mobs=new SortedSet<string>();var weapons=new List<string>();
foreach(var entry in paths.Where(p=>p.Key.EndsWith(".prefab",StringComparison.OrdinalIgnoreCase)&&(p.Key.StartsWith("Assets/Characters/",StringComparison.OrdinalIgnoreCase)||p.Key.StartsWith("Assets/GameElements/Items/",StringComparison.OrdinalIgnoreCase)))) {
 Load(entry.Value);var prefab=bundles[entry.Value].LoadAsset<GameObject>(entry.Key);if(!prefab)continue;
 if(prefab.name=="Player"){
 var clips=prefab.GetComponentsInChildren<Animator>(true).Where(a=>a.runtimeAnimatorController).SelectMany(a=>a.runtimeAnimatorController.animationClips).Distinct();
 File.WriteAllLines("../../../Tools/AugaWork/player-attack-clips.tsv",clips.Where(c=>c.events.Any(e=>e.functionName=="Hit"||e.functionName=="Speed")).Select(c=>c.name+"\t"+string.Join(";",c.events.Select(e=>e.functionName+"@"+e.time+":"+e.floatParameter))));
}
var character=prefab.GetComponent<Character>();var ai=prefab.GetComponent<BaseAI>();
 if(character && !(character is Player)){mobs.Add(prefab.name);report.Add("MOB\t"+prefab.name+"\t"+(ai?ai.GetType().Name:"none")+"\t"+character.m_faction+"\t"+character.m_name);}
 else if(prefab.GetComponent<RandomFlyingBird>()){mobs.Add(prefab.name);report.Add("MOB\t"+prefab.name+"\tRandomFlyingBird");}
 var drop=prefab.GetComponent<ItemDrop>();if(drop && drop.m_itemData.IsWeapon() && entry.Key.StartsWith("Assets/GameElements/Items/",StringComparison.OrdinalIgnoreCase)){var s=drop.m_itemData.m_shared;weapons.Add(prefab.name+"\t"+s.m_itemType+"\t"+s.m_skillType+"\t"+s.m_attack.m_attackAnimation+"\t"+s.m_secondaryAttack.m_attackAnimation+"\t"+s.m_backstabBonus.ToString(System.Globalization.CultureInfo.InvariantCulture)+"\t"+s.m_name);}
}
File.WriteAllLines("../../../Tools/AugaWork/native-mob-names.txt",mobs);File.WriteAllLines("../../../Tools/AugaWork/native-weapons.tsv",weapons.OrderBy(w=>w));File.WriteAllLines("../../../Tools/AugaWork/native-mobs.tsv",report);
}}
