using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using AugaUnity;
using UnityEngine;
using TMPro;
public static class RelocationSettingsCheck
{
 const BindingFlags F=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
 public static void Run()
 {
  AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var p=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
  var mod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));mod.GetType("Overhaul.IntegratedUi").GetMethod("LoadDependencies",F).Invoke(null,null);
  var bep=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/BepInEx.dll"));var dir=Path.GetFullPath("../../../Tools/AugaWork/relocation-settings-fixture");Directory.CreateDirectory(dir);
  bep.GetType("BepInEx.Paths").GetMethod("SetExecutablePath",F).Invoke(null,new object[]{Path.Combine(dir,"Test.exe"),null,null,new string[0]});
  var file=Path.Combine(dir,"settings-"+Guid.NewGuid().ToString("N")+".cfg");var config=Activator.CreateInstance(bep.GetType("BepInEx.Configuration.ConfigFile"),new object[]{file,false});
  var val=mod.GetType("EquipmentAndQuickSlots.ValConfig");Activator.CreateInstance(val,F,null,new[]{config},null);
  mod.GetType("Auga.EquipmentQuickSlotsCompatibility").GetMethod("InstallSettings",F).Invoke(null,new object[]{mod});AugaModsSettings.IsEqsActive=()=>true;
  var loc=(Localization)typeof(AugaRightPanelCheck).GetMethod("Localize",F).Invoke(null,null);
  var words=Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText("../../../Packages/Overhaul/Localisation/translationsFR.json"));
  var native=JsonUtility.FromJson<AugaGamepadCheck.Words>(File.ReadAllText("../../../Tools/AugaWork/valheim-french-settings.json"));foreach(var e in native.entries)if(words[e.key]==null)words[e.key]=e.value;
  words["menu_settings"]="Paramètres";
  foreach(var f in typeof(Localization).GetFields(F))if(!f.IsStatic&&f.FieldType==typeof(Dictionary<string,string>)){var d=(Dictionary<string,string>)f.GetValue(loc);if(d!=null)foreach(var p in words.Properties())d[p.Name]=(string)p.Value;}
  var bundle=AssetBundle.LoadFromFile("AssetBundles/augaassets");var host=new GameObject("Inactive settings");host.SetActive(false);
  var root=UnityEngine.Object.Instantiate(bundle.LoadAsset<GameObject>("AugaSettings"),host.transform,false);var page=root.GetComponentInChildren<AugaModsSettings>(true);page.Initialize();
  if(page.BindButtons.Length!=6||page.Displays.Length!=6)throw new Exception("Missing sixth binding");
  string initial=AugaModsSettings.ReadShortcut(5);page.SetPending(5,"J");page.OnBack();page.OnOkAsync(null);if(AugaModsSettings.ReadShortcut(5)!=initial)throw new Exception("Back changed binding");
  page.SetPending(5,"J");page.OnOkAsync(null);if(AugaModsSettings.ReadShortcut(5)!="J"||!File.ReadAllText(file).Contains("Move object = J"))throw new Exception("Apply did not save J");
  var label=mod.GetType("Overhaul.Storage.RelocationShortcut").GetProperty("Label",F).GetValue(null).ToString();if(label!=AugaModsSettings.KeyLabel(KeyCode.J))throw new Exception("Runtime hint ignores configured key");
  var entry=val.GetField("MoveObjectKey",F).GetValue(null);entry.GetType().GetMethod("SetSerializedValue").Invoke(entry,new object[]{"None"});page.Initialize();
  foreach(Transform p in root.transform.Find("panel/Tabs"))p.gameObject.SetActive(p.name=="Mods");
  foreach(Transform t in root.transform.Find("panel/TabButtons/Tabs"))if(t.Find("Selected"))t.Find("Selected").gameObject.SetActive(t.name=="Mods");
  foreach(var t in root.GetComponentsInChildren<TMP_Text>(true))t.text=System.Text.RegularExpressions.Regex.Replace(t.text,@"\$[a-zA-Z0-9_]+",m=>(string)words[m.Value.Substring(1)]??m.Value);
  foreach(var t in root.GetComponentsInChildren<UnityEngine.UI.Text>(true))t.text=System.Text.RegularExpressions.Regex.Replace(t.text,@"\$[a-zA-Z0-9_]+",m=>(string)words[m.Value.Substring(1).ToLowerInvariant()]??m.Value);
  foreach(var b in root.GetComponentsInChildren<MonoBehaviour>(true))if(b&&!(b is UnityEngine.EventSystems.UIBehaviour)&&!(b is HorizontalDividerFitter))UnityEngine.Object.DestroyImmediate(b);
  typeof(AugaPauseCheck).GetMethod("Render",F).Invoke(null,new object[]{root,"mods-always-active-trash",1920,1080});
  File.WriteAllText("../../../Tools/AugaWork/relocation-settings-checks.txt","PASS six bindings\nPASS Back cancels\nPASS Apply persists custom key\nPASS runtime hint follows custom key\nPASS native Mods prefab rendered\n");
 }
}
