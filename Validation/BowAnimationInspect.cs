using System.Reflection;using System;using System.IO;using System.Linq;using System.Collections.Generic;using UnityEngine;public static class BowAnimationInspect {const string Root="D:/Valheim/valheim_Data/StreamingAssets/SoftRef/";
public static Action<Animator,List<string>> AfterPose;
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
var path=paths.First(p=>p.Key.EndsWith("/Player.prefab",StringComparison.OrdinalIgnoreCase));Load(path.Value);var prefab=bundles[path.Value].LoadAsset<GameObject>(path.Key);
foreach(var animator in prefab.GetComponentsInChildren<Animator>(true)){
 if(!animator.runtimeAnimatorController)continue;report.Add("CONTROLLER "+animator.runtimeAnimatorController.name+" type="+animator.runtimeAnimatorController.GetType()+" human="+animator.isHuman);
 foreach(var clip in animator.runtimeAnimatorController.animationClips.Distinct().Where(c=>c.name.ToLower().Contains("bow")||c.name.ToLower().Contains("crouch")))report.Add("CLIP "+clip.name+" length="+clip.length);
 foreach(var clip in animator.runtimeAnimatorController.animationClips.Distinct().Where(c=>c.name.ToLower().Contains("sneak")))report.Add("SNEAK "+clip.name);
 var parent=new GameObject("inactive bow inspect");parent.SetActive(false);var copy=UnityEngine.Object.Instantiate(animator.gameObject,parent.transform);foreach(var c in copy.GetComponentsInChildren<MonoBehaviour>(true))UnityEngine.Object.DestroyImmediate(c);parent.SetActive(true);var probe=copy.GetComponent<Animator>();probe.Rebind();probe.cullingMode=AnimatorCullingMode.AlwaysAnimate;probe.fireEvents=false;
 foreach(var par in probe.parameters)report.Add("PARAM "+par.name+" "+par.type);
 for(int j=0;j<probe.layerCount;j++)report.Add("RUNTIME LAYER "+j+" "+probe.GetLayerName(j));
 foreach(var name in new[]{"Movement","Crouch","Sneak","Sneaking","Bow","Bow draw"})report.Add("HAS "+name+"="+probe.HasState(0,Animator.StringToHash("Base Layer."+name)));
 foreach(var mode in new[]{"crouch","bow","both"}){
 probe.Rebind();probe.Play("Base Layer.Movement",0,0);probe.SetFloat("statef",3);probe.SetInteger("statei",3);probe.SetBool("onGround",true);probe.SetBool("crouching",mode!="bow");probe.SetBool("bow_aim",mode!="crouch");probe.SetFloat("drawpercent",1);for(int frame=0;frame<120;frame++)probe.Update(1f/60);
 report.Add("POSE "+mode+" hips="+probe.GetBoneTransform(HumanBodyBones.Hips).localPosition+" leftHand="+probe.GetBoneTransform(HumanBodyBones.LeftHand).position+" arm="+probe.GetBoneTransform(HumanBodyBones.LeftUpperArm).localRotation);
 for(int layer=0;layer<probe.layerCount;layer++)report.Add("POSE LAYER "+layer+" weight="+probe.GetLayerWeight(layer)+" hash="+probe.GetCurrentAnimatorStateInfo(layer).fullPathHash+" clips="+string.Join(",",probe.GetCurrentAnimatorClipInfo(layer).Select(c=>c.clip.name+":"+c.weight)));
 }
 AfterPose?.Invoke(probe,report);
 UnityEngine.Object.DestroyImmediate(parent);
 var controller=animator.runtimeAnimatorController as UnityEditor.Animations.AnimatorController;
 if(controller!=null)foreach(var layer in controller.layers){report.Add("LAYER "+layer.name+" weight="+layer.defaultWeight+" mask="+(layer.avatarMask?layer.avatarMask.name:"none"));Dump(layer.stateMachine,report,"  ");}
}
File.WriteAllLines("../../../Tools/AugaWork/bow-animation-inspect.txt",report);
}
static void Dump(UnityEditor.Animations.AnimatorStateMachine machine,List<string> report,string prefix){foreach(var child in machine.states){var st=child.state;report.Add(prefix+"STATE "+st.name+" tag="+st.tag+" motion="+(st.motion?st.motion.name:"none"));foreach(var tr in st.transitions)report.Add(prefix+" -> "+(tr.destinationState?tr.destinationState.name:"exit")+" "+string.Join(",",tr.conditions.Select(c=>c.parameter+" "+c.mode+" "+c.threshold)));}foreach(var child in machine.stateMachines)Dump(child.stateMachine,report,prefix+child.stateMachine.name+"/");}
}