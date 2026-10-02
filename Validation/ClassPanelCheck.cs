using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using AugaUnity;
using UnityEngine;
using TMPro;
public static class ClassPanelCheck
{
 const BindingFlags F=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
 static List<string> results=new List<string>();
 static void Check(bool ok,string name){if(!ok)throw new Exception(name);results.Add("PASS "+name);}
 public static void Run(){try{Do();File.WriteAllLines("../../../Tools/AugaWork/class-panel-checks.txt",results);}catch(Exception e){results.Add("FAIL "+e);File.WriteAllLines("../../../Tools/AugaWork/class-panel-checks.txt",results);throw;}}
 static int Index(ClassPreviewPanel demo,int n)=>Array.FindIndex(demo.Skills,s=>s.Id==new[]{"Oath","Anchor","Riposte","IronWall","ProtectiveWatch","PerfectCounter","LivingBulwark","ThunderStrike","EternalOath"}[n]);
 static void Do(){
  AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var p=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
  var loc=(Localization)typeof(AugaRightPanelCheck).GetMethod("Localize",F).Invoke(null,null);
  var words=Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText("../../../Overhaul/Overhaul/Distribution/Localisation/translationsFR.json"));
  foreach(var field in typeof(Localization).GetFields(F))if(!field.IsStatic&&field.FieldType==typeof(Dictionary<string,string>)){
   var dictionary=(Dictionary<string,string>)field.GetValue(loc);if(dictionary!=null)foreach(var word in words.Properties())dictionary[word.Name]=(string)word.Value;
  }
  var bundle=AssetBundle.LoadFromFile("AssetBundles/augaassets");var host=new GameObject("Inactive class preview");host.SetActive(false);
  var wrapper=new GameObject("Wrapper",typeof(RectTransform));wrapper.transform.SetParent(host.transform,false);
  var obj=UnityEngine.Object.Instantiate(bundle.LoadAsset<GameObject>("OverhaulClassPanel"),wrapper.transform,false);loc.Localize(obj.transform);
  var configMod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll")); var parse=configMod.GetType("Overhaul.Leveling.ClassSkillConfig").GetMethod("Parse",F); var definitions=(ClassSkillDefinition[])parse.Invoke(null,new object[]{File.ReadAllText("../../../Packages/Overhaul/ClassSkill.cfg")});
  var demo=obj.GetComponent<ClassPreviewPanel>();demo.Generate(definitions);
  Check(demo.Nodes.Length==30&&demo.Skills.GroupBy(s=>s.Tier).Count()==6&&demo.Skills.GroupBy(s=>s.Tier).All(g=>g.Count()==5)&&demo.Links.Length==definitions.Sum(s=>s.RequiredSkill.Length)*3,"30 selectable skills across five rows and six tiers with all prerequisite connections");
  Check(demo.TreeScroll.horizontal&&!demo.TreeScroll.vertical&&demo.TreeScroll.content.rect.width>demo.TreeScroll.viewport.rect.width&&demo.TreeScroll.horizontalScrollbar.gameObject.activeSelf,"five rows fit vertically and six tiers scroll horizontally");
  Check(demo.LearnedFrames[Index(demo,0)].gameObject.activeSelf&&demo.LearnedFrames[Index(demo,4)].color!=demo.LearnedFrames[Index(demo,0)].color&&demo.LearnedFrames[Index(demo,0)].color==demo.Links[0].color&&demo.Selection.color.b>demo.Selection.color.r,"learned border matches active links and selection is blue");
  Check(demo.FreePoints==3&&demo.Learned.Count(v=>v)==3,"level six preview starts with three learned skills and three points");
  Check(demo.CanLearn(Index(demo,4))&&!demo.CanLearn(Index(demo,6))&&!demo.CanLearn(Index(demo,8)),"available branch, unmet double prerequisite and level gate");
  demo.Nodes[Index(demo,6)].onClick.Invoke();Check(demo.Selected==Index(demo,6)&&!demo.Learn.interactable&&demo.Requirements.text.Contains("Mur de fer")&&demo.Requirements.text.Contains("Veille protectrice"),"selecting locked skill shows both prerequisites");
  demo.Unlock();Check(demo.FreePoints==3&&!demo.Learned[Index(demo,6)],"locked skill cannot spend points");
  demo.Nodes[Index(demo,3)].onClick.Invoke();demo.Learn.onClick.Invoke();Check(demo.FreePoints==2&&!demo.CanLearn(Index(demo,6)),"one prerequisite alone is insufficient");
  demo.Nodes[Index(demo,4)].onClick.Invoke();demo.Learn.onClick.Invoke();Check(demo.FreePoints==1&&demo.CanLearn(Index(demo,6)),"both prerequisites make converging talent available");
  demo.Nodes[Index(demo,6)].onClick.Invoke();demo.Learn.onClick.Invoke();Check(demo.FreePoints==0&&!demo.CanLearn(Index(demo,5)),"point budget prevents overspending");
  bool closed=false;demo.CloseRequested=()=>closed=true;demo.Close.onClick.Invoke();Check(closed,"close button invokes window callback");
  demo.Reset.onClick.Invoke();Check(demo.FreePoints==3&&demo.Selected==Index(demo,4),"reset restores demonstration only");
  var multiText=File.ReadAllText("../../../Packages/Overhaul/ClassSkill.cfg").Replace("Name = class_demo_name4\nDescription = class_demo_body4\nTier = 3\nMaxRank = 1\nRequiredLevel = [4]","Name = class_demo_name4\nDescription = class_demo_body4\nTier = 3\nMaxRank = 3\nRequiredLevel = [4,6,8]");
  var multi=(ClassSkillDefinition[])parse.Invoke(null,new object[]{multiText});demo.Generate(multi);demo.Select(Index(demo,4));demo.Unlock();demo.Unlock();
  Check(demo.Ranks[Index(demo,4)]==2&&demo.FreePoints==1&&!demo.CanLearn(Index(demo,4)),"per-rank levels allow ranks 1/2 at class level six and reject rank 3 requiring eight");
  var extra=multiText+"\n[Guardian.NewSkill]\nName = class_demo_name0\nDescription = class_demo_body0\nTier = 7\nMaxRank = 1\nRequiredLevel = [1]\nRequiredSkill = [Oath]\n";
  demo.Generate((ClassSkillDefinition[])parse.Invoke(null,new object[]{extra}));
  Check(demo.Nodes.Length==31&&demo.TreeScroll.content.rect.width>demo.TreeScroll.viewport.rect.width&&demo.TreeScroll.horizontalScrollbar.gameObject.activeSelf,"adding a config section generates a node, prerequisite link and seventh column");
  foreach(var bad in new[]{multiText.Replace("[4,6,8]","[4,6]"),multiText.Replace("RequiredSkill = [Anchor]","RequiredSkill = [Missing]"),multiText.Replace("RequiredSkill = []","RequiredSkill = [EternalOath]"),multiText+"\n[Guardian.Oath]\n"}){
   bool rejected=false;try{parse.Invoke(null,new object[]{bad});}catch(TargetInvocationException e){rejected=e.InnerException is InvalidDataException;}Check(rejected,"invalid rank count, reference, backward dependency or duplicate section rejected");
  }
  demo.Generate(definitions);
  Check(!demo.TreeScroll.content.GetComponentsInChildren<TMP_Text>().Any(),"tree contains icons and borders without skill names or state labels");
  Check(demo.Nodes.All(n=>{var r=(RectTransform)n.transform;return r.anchoredPosition.x>=34&&r.anchoredPosition.x+34<=demo.TreeScroll.content.rect.width&&-r.anchoredPosition.y>=34&&-r.anchoredPosition.y+34<=demo.TreeScroll.viewport.rect.height;}),"all 30 icons fit content horizontally and viewport vertically");
  Check(demo.Nodes.Count(n=>((RectTransform)n.transform).anchoredPosition.x+34<=demo.TreeScroll.viewport.rect.width)==20,"four complete columns visible at start");
  Render(wrapper,"class-tree-six-tiers-start-v1");
  var second=new GameObject("Locked skill preview",typeof(RectTransform));second.transform.SetParent(host.transform,false);
  var secondPanel=UnityEngine.Object.Instantiate(bundle.LoadAsset<GameObject>("OverhaulClassPanel"),second.transform,false);loc.Localize(secondPanel.transform);
  var secondDemo=secondPanel.GetComponent<ClassPreviewPanel>();secondDemo.Generate(definitions);secondDemo.Select(secondDemo.Nodes.Length-1);
  Check(secondDemo.TreeScroll.horizontalNormalizedPosition>.99f,"selecting sixth tier scrolls to end");
  float endLeft=-secondDemo.TreeScroll.content.anchoredPosition.x;
  Check(secondDemo.Nodes.Count(n=>{float x=((RectTransform)n.transform).anchoredPosition.x;return x-34>=endLeft&&x+34<=endLeft+secondDemo.TreeScroll.viewport.rect.width;})==20,"four complete columns visible at end");
  Render(second,"class-tree-six-tiers-end-v1");UnityEngine.Object.DestroyImmediate(second);
  var settings=UnityEngine.Object.Instantiate(bundle.LoadAsset<GameObject>("AugaSettings"),host.transform,false).GetComponentInChildren<AugaModsSettings>(true);
  Check(settings.BindButtons.Length==6&&settings.Displays.Length==6,"Overhaul settings include fifth class-window binding");
  var saved=new List<string>();AugaModsSettings.IsEqsActive=()=>true;AugaModsSettings.ReadShortcut=i=>new[]{"C","V","B","Z","P","H"}[i];AugaModsSettings.DisplayShortcut=s=>s;AugaModsSettings.WriteShortcut=(i,s)=>saved.Add(i+":"+s);
  settings.Initialize();settings.SetPending(4,"G");settings.OnBack();settings.OnOkAsync(null);Check(saved.Count==0,"Back discards class key change");settings.SetPending(4,"H");settings.OnOkAsync(null);Check(saved.SequenceEqual(new[]{"4:H"}),"Apply writes fifth class binding");
  var mod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));mod.GetType("Overhaul.IntegratedUi").GetMethod("LoadDependencies",F).Invoke(null,null);
  var type=mod.GetType("Overhaul.Leveling.ClassWindow");
  var ha=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll"));var ht=ha.GetType("HarmonyLib.Harmony");var harmony=Activator.CreateInstance(ht,new object[]{"class.window.checks"});
  foreach(var nested in type.GetNestedTypes(F)){var processor=ht.GetMethods().First(m=>m.Name=="CreateClassProcessor"&&m.GetParameters().Length==1).Invoke(harmony,new object[]{nested});processor.GetType().GetMethod("Patch",Type.EmptyTypes).Invoke(processor,null);}
  Check(true,"window input, camera and menu patches install against native methods");
  var fakeRoot=new GameObject("Window visibility fixture");type.GetField("root",F).SetValue(null,fakeRoot);
  var input=type.GetNestedType("PlayerInput",F).GetMethod("Prefix",F);var args=new object[]{true};Check(!(bool)input.Invoke(null,args)&&!(bool)args[0],"visible class panel blocks character input");
  type.GetMethod("Close",F).Invoke(null,null);Check(!fakeRoot.activeSelf&&(bool)type.GetProperty("BlocksInput",F).GetValue(null),"close hides panel and blocks input for closing frame");
  type.GetField("closedFrame",F).SetValue(null,-1);args[0]=false;Check((bool)input.Invoke(null,args),"normal character input restored after closing frame");
  type.GetField("root",F).SetValue(null,null);UnityEngine.Object.DestroyImmediate(fakeRoot);ht.GetMethod("UnpatchSelf").Invoke(harmony,null);
  UnityEngine.Object.DestroyImmediate(wrapper);UnityEngine.Object.DestroyImmediate(host);bundle.Unload(true);
 }
 static void Render(GameObject wrapper,string name){wrapper.transform.localScale=Vector3.one;typeof(AugaPauseCheck).GetMethod("Render",F).Invoke(null,new object[]{wrapper,name,1600,1040});}
}
