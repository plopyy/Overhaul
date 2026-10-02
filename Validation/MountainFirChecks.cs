using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using AugaUnity;
public static class MountainFirChecks
{
 const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
 static List<string> report=new List<string>();
 static void Check(bool ok,string text){report.Add((ok?"PASS ":"FAIL ")+text);if(!ok)throw new Exception(text);}
 static T New<T>(string name) where T:Component {var go=new GameObject(name);go.SetActive(false);return go.AddComponent<T>();}
 public static void Run(){try{Test();}finally{File.WriteAllLines("../../../Tools/AugaWork/mountain-fir-checks.txt",report);}}
 static void Test(){
  MountainTreeInspect.Run();var prefabs=MountainTreeInspect.Prefabs;
  var zone=prefabs["_ZoneSystem"].GetComponent<ZoneSystem>();
  Check(zone.m_vegetation.Any(v=>v.m_prefab.name=="FirTree"&&v.m_enable&&(v.m_biome&Heightmap.Biome.Mountain)!=0),"mountain vegetation uses FirTree");
  Check(zone.m_vegetation.Any(v=>v.m_prefab.name=="FirTree"&&v.m_enable&&(v.m_biome&Heightmap.Biome.BlackForest)!=0),"Black Forest uses the same FirTree prefab");
  var mod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));var type=mod.GetType("Overhaul.MountainFirDrops");
  var scope=type.GetField("SpawningMountainFir",F);int key=(int)type.GetField("OriginKey",F).GetValue(null);
  var ha=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll"));var ht=ha.GetType("HarmonyLib.Harmony");var harmony=Activator.CreateInstance(ht,new object[]{"mountain.fir.check"});
  foreach(var nested in type.GetNestedTypes(F)){var cp=ht.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(harmony,new object[]{nested});cp.GetType().GetMethod("Patch",Type.EmptyTypes).Invoke(cp,null);Check(true,"patch installs "+nested.Name);}
  var net=New<ZNet>("network");typeof(ZNet).GetField("m_instance",F).SetValue(null,net);ZNet.m_isServer=true;
  var scene=New<ZNetScene>("scene");typeof(ZNetScene).GetField("s_instance",F).SetValue(null,scene);foreach(var p in prefabs)scene.m_namedPrefabs[p.Key.GetStableHashCode()]=p.Value;
  var game=New<Game>("game");typeof(Game).GetProperty("instance",F).SetValue(null,game);
  var rpc=new ZRoutedRpc(true);var manager=new ZDOMan(512);rpc.SetUID(ZNet.GetUID());
  var pine=prefabs["PineTree_log_half"].GetComponent<TreeLog>().m_dropWhenDestroyed;
  var fir=prefabs["FirTree_log_half"].GetComponent<TreeLog>().m_dropWhenDestroyed;
  Check(pine.m_drops.Count==2&&pine.m_drops.All(e=>e.m_item.name=="Wood"||e.m_item.name=="RoundLog")&&pine.m_dropMin==15&&pine.m_dropMax==15,"pine half-log: 15 rolls, only normal and core wood");
  Check(pine.m_drops[0].m_weight==pine.m_drops[1].m_weight,"native wood ratio is 50/50");
  foreach(var name in new[]{"FirTree_log","FirTree_log_half","PineTree_log_half","FirTree_Snow_log_half"})foreach(bool mountain in new[]{false,true}){
   var log=New<TreeLog>(name);var view=log.gameObject.AddComponent<ZNetView>();var data=manager.CreateNewZDO(Vector3.zero,name.GetStableHashCode());data.SetOwner(ZNet.GetUID());view.m_zdo=data;
   var original=prefabs[name].GetComponent<TreeLog>().m_dropWhenDestroyed;log.m_dropWhenDestroyed=original;scope.SetValue(null,mountain);
   type.GetMethod("Configure",F).Invoke(null,new object[]{log});
   bool changed=name=="FirTree_log_half"&&mountain;Check(ReferenceEquals(log.m_dropWhenDestroyed,changed?pine:original),name+" origin="+mountain+" table correct");
   if(name.StartsWith("FirTree_log")){
    Check(data.GetInt(key,-1)==(mountain?1:0),"origin persisted in ZDO");scope.SetValue(null,!mountain);log.m_dropWhenDestroyed=original;
    type.GetMethod("Configure",F).Invoke(null,new object[]{log});Check(ReferenceEquals(log.m_dropWhenDestroyed,changed?pine:original),"saved origin survives reload with opposite spawn context");
   }
   if(name=="FirTree_log_half"&&mountain){data.Set(key,0);type.GetMethod("Configure",F).Invoke(null,new object[]{log});Check(ReferenceEquals(log.m_dropWhenDestroyed,fir),"late authoritative forest marker restores native fir table");}
   UnityEngine.Object.DestroyImmediate(log.gameObject);
  }
  var resolve=type.GetMethod("ResolveOrigin",F);Check((bool)resolve.Invoke(null,new object[]{-1,null,Heightmap.Biome.Mountain})&&!(bool)resolve.Invoke(null,new object[]{-1,null,Heightmap.Biome.BlackForest}),"unmarked old logs use their current biome");
  foreach(var nested in type.GetNestedTypes(F).Where(n=>n.GetMethod("Finalizer",F)!=null)){var error=new Exception("fixture");scope.SetValue(null,true);Check(ReferenceEquals(nested.GetMethod("Finalizer",F).Invoke(null,new object[]{null,error}),error)&&scope.GetValue(null)==null,"scope restored after exception "+nested.Name);}
  float rate=Game.m_resourceRate;Game.m_resourceRate=1;for(int i=0;i<128;i++){var drops=pine.GetDropList();if(drops.Count!=15||drops.Any(p=>p.name!="Wood"&&p.name!="RoundLog"))throw new Exception("Native roll mismatch");}Game.m_resourceRate=rate;
  Check(fir.m_dropMin==10&&fir.m_drops.Count==1&&fir.m_drops[0].m_item.name=="Wood","original fir prefab remains unchanged");
  Check(prefabs["FirTree"].GetComponent<TreeBase>().m_dropWhenDestroyed.m_drops.Any(d=>d.m_item.name=="FirCone")&&!prefabs["FirTree"].GetComponent<TreeBase>().m_dropWhenDestroyed.m_drops.Any(d=>d.m_item.name=="PineCone"),"standing fir retains its own cones and native non-wood loot");
  scope.SetValue(null,null);ht.GetMethod("UnpatchSelf").Invoke(harmony,null);
  Translation(mod);
 }
 static void Translation(Assembly mod){
  mod.GetType("Overhaul.IntegratedUi").GetMethod("LoadDependencies",F).Invoke(null,null);
  var loc=(Localization)typeof(AugaRightPanelCheck).GetMethod("Localize",F).Invoke(null,null);
  loc.AddWord("inventory_slash","Coupant");loc.Localize("$inventory_slash");
  mod.GetType("Auga.Auga").GetMethod("LoadTranslations",F).Invoke(null,new object[]{loc,"French"});
  Check(loc.Localize("$inventory_slash")=="Tranchant","French native damage key overwritten and cached old value invalidated");
  var bundle=AssetBundle.LoadFromFile("AssetBundles/augaassets");var host=new GameObject("inactive preview");host.SetActive(false);
  var wrapper=new GameObject("Tranchant",typeof(RectTransform));wrapper.transform.SetParent(host.transform,false);
  var obj=UnityEngine.Object.Instantiate(bundle.LoadAsset<GameObject>("InventoryTooltip"),wrapper.transform,false);var tip=obj.GetComponent<ComplexTooltip>();tip.ClearTextBoxes();var box=tip.AddTextBox(tip.TwoColumnTextBoxPrefab);
  var item=new ItemDrop.ItemData{m_shared=new ItemDrop.ItemData.SharedData()};
  typeof(ComplexTooltip).GetMethod("AddDamageLine",F).Invoke(tip,new object[]{box,item,"$inventory_slash",40f,30f,.4f,1f,false});
  Check(box.Text.text.Contains("Tranchant")&&!box.Text.text.Contains("Coupant"),"actual item tooltip uses Tranchant");
  RenderBox(box.gameObject);
  bundle.Unload(true);
 }
static void RenderBox(GameObject box)
    {
        var root=new GameObject("Canvas",typeof(Canvas));var cameraGo=new GameObject("Camera",typeof(Camera));
        var camera=cameraGo.GetComponent<Camera>();camera.orthographic=true;camera.orthographicSize=240;camera.transform.position=new Vector3(0,0,-10);
        camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.12f,.11f,.095f);camera.cullingMask=1<<30;
        var canvas=root.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=camera;canvas.planeDistance=1;canvas.referencePixelsPerUnit=50;root.layer=30;
        var target=new RenderTexture(560,480,24);camera.targetTexture=target;
        box.transform.SetParent(root.transform,false);var rect=(RectTransform)box.transform;rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(.5f,1);
        rect.anchoredPosition=new Vector2(0,-25);rect.sizeDelta=new Vector2(500,420);foreach(var t in box.GetComponentsInChildren<Transform>(true))t.gameObject.layer=30;
        box.SetActive(true);Canvas.ForceUpdateCanvases();UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(rect);Canvas.ForceUpdateCanvases();
        camera.Render();RenderTexture.active=target;var image=new Texture2D(560,480,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,560,480),0,0);image.Apply();
        File.WriteAllBytes("../../../Tools/AugaWork/tranchant-tooltip.png",image.EncodeToPNG());RenderTexture.active=null;
        UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate(cameraGo);UnityEngine.Object.DestroyImmediate(target);UnityEngine.Object.DestroyImmediate(image);
    }
}
