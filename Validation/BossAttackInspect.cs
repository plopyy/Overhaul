using System.Reflection;using System;using System.IO;using System.Linq;using System.Collections.Generic;using UnityEngine;public static class BossAttackInspect {const string Root="D:/Valheim/valheim_Data/StreamingAssets/SoftRef/";
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
var names=new[]{"Greydwarf_Shaman","Greydwarf_Elite","Troll","Ghost","Draugr_Elite","BlobElite","Abomination","Wraith","Fenring_Cultist","StoneGolem","Ulv"};
foreach(var name in names){var path=paths.First(p=>p.Key.EndsWith("/"+name+".prefab",StringComparison.OrdinalIgnoreCase));Load(path.Value);var prefab=bundles[path.Value].LoadAsset<GameObject>(path.Key);var ai=prefab.GetComponent<MonsterAI>();report.Add(name+" minAttackInterval="+ai.m_minAttackInterval);foreach(var animator in prefab.GetComponentsInChildren<Animator>(true)){if(!animator.runtimeAnimatorController)continue;foreach(var clip in animator.runtimeAnimatorController.animationClips.Distinct()){var events=clip.events; if(events.Any(e=>e.functionName=="Hit"||e.functionName=="Speed")||clip.name.ToLower().Contains("attack"))report.Add("  "+clip.name+" duration="+clip.length+" events="+string.Join(";",events.Select(e=>e.functionName+"@"+e.time+":"+e.floatParameter)));}}}
File.WriteAllLines("../../../Tools/AugaWork/boss-attack-inspect.txt",report);
}}
