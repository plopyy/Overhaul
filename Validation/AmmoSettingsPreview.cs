using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AugaUnity;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
public static class AmmoSettingsPreview
{
 public static void Run()
 {
  const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
  AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var p=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
  var loc=(Localization)typeof(AugaRightPanelCheck).GetMethod("Localize",F).Invoke(null,null);
  var words=Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText("../../../Overhaul/Overhaul/Distribution/Localisation/translationsFR.json"));
  var native=JsonUtility.FromJson<AugaGamepadCheck.Words>(File.ReadAllText("../../../Tools/AugaWork/valheim-french-settings.json"));
  foreach(var e in native.entries)if(words[e.key]==null)words[e.key]=e.value;
  foreach(var field in typeof(Localization).GetFields(F))if(!field.IsStatic&&field.FieldType==typeof(System.Collections.Generic.Dictionary<string,string>)){
   var dictionary=(System.Collections.Generic.Dictionary<string,string>)field.GetValue(loc);
   if(dictionary!=null)foreach(var word in words.Properties())dictionary[word.Name]=(string)word.Value;
  }
  var bundle=AssetBundle.LoadFromFile("AssetBundles/augaassets");var host=new GameObject("Inactive ammo settings preview");host.SetActive(false);
  var root=UnityEngine.Object.Instantiate(bundle.LoadAsset<GameObject>("AugaSettings"),host.transform,false);
  var page=root.GetComponentInChildren<AugaModsSettings>(true);
  AugaModsSettings.IsEqsActive=()=>true;AugaModsSettings.ReadShortcut=i=>new[]{"C","V","B","W","P"}[i];AugaModsSettings.WriteShortcut=(i,v)=>{};AugaModsSettings.DisplayShortcut=s=>s;
  page.Initialize();page.SelectEqs();
  foreach(var t in root.GetComponentsInChildren<TMP_Text>(true))t.text=System.Text.RegularExpressions.Regex.Replace(t.text,@"\$[a-zA-Z0-9_]+",m=>(string)words[m.Value.Substring(1)]??m.Value);
  foreach(var t in root.GetComponentsInChildren<Text>(true))t.text=System.Text.RegularExpressions.Regex.Replace(t.text,@"\$[a-zA-Z0-9_]+",m=>(string)words[m.Value.Substring(1)]??m.Value);
  foreach(Transform p in root.transform.Find("panel/Tabs"))p.gameObject.SetActive(p.name=="Mods");
  foreach(Transform t in root.transform.Find("panel/TabButtons/Tabs"))if(t.Find("Selected"))t.Find("Selected").gameObject.SetActive(t.name=="Mods");
  foreach(var b in root.GetComponentsInChildren<MonoBehaviour>(true))if(b&&!(b is UnityEngine.EventSystems.UIBehaviour)&&!(b is HorizontalDividerFitter))UnityEngine.Object.DestroyImmediate(b);
  typeof(AugaPauseCheck).GetMethod("Render",F).Invoke(null,new object[]{root,"class-panel-settings",1920,1080});
  UnityEngine.Object.DestroyImmediate(host);bundle.Unload(true);
 }
 public static void All(){AmmoEquipmentPreview.Equipment();Run();}
}
