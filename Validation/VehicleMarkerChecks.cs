using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.Serialization;
using UnityEngine;
using UnityEngine.UI;

public static class VehicleMarkerChecks
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static Type feature;static readonly List<string> report=new List<string>();
    static object Call(string method,params object[] args)=>feature.GetMethod(method,F).Invoke(null,args);
    static void Check(bool ok,string text){report.Add((ok?"PASS ":"FAIL ")+text);if(!ok)throw new Exception(text);}
    static T New<T>(string name) where T:Component {var go=new GameObject("vehicle-test-"+name);go.SetActive(false);return go.AddComponent<T>();}
    static bool Awake(Component __instance)=>!__instance.name.StartsWith("vehicle-test-");
    static bool DestroyName(Minimap.PinNameData __instance)=>__instance.PinNameGameObject;
    const string Root="D:/Valheim/valheim_Data/StreamingAssets/SoftRef/";
    static readonly Dictionary<string,List<string>> deps=new Dictionary<string,List<string>>();
    static readonly Dictionary<string,AssetBundle> bundles=new Dictionary<string,AssetBundle>();
    static void Load(string id){if(bundles.ContainsKey(id))return;bundles[id]=null;if(deps.ContainsKey(id))foreach(var d in deps[id])Load(d);bundles[id]=AssetBundle.LoadFromFile(Root+"Bundles/"+id);}
    static Dictionary<string,GameObject> Assets()
    {
        var paths=new Dictionary<string,string>();string current=null,bundle=null;bool assets=false;
        foreach(var line in File.ReadAllLines(Root+"manifest_extended").Concat(File.ReadAllLines(Root+"manifest")))
        {
            if(line=="bundle dependencies:")assets=false;else if(line.StartsWith("asset locations:"))assets=true;
            else if(!assets){if(line.StartsWith("- bundle: ")){current=line.Substring(10).Trim();deps[current]=new List<string>();}else if(current!=null&&line.StartsWith("  - "))deps[current].Add(line.Substring(4).Trim());}
            else if(line.StartsWith("  bundle: "))bundle=line.Substring(10).Trim();else if(line.StartsWith("  path in bundle: "))paths[line.Substring(18).Trim()]=bundle;
        }
        var result=new Dictionary<string,GameObject>();
        foreach(var name in new[]{"Raft","Karve","VikingShip","VikingShip_Ashlands","Cart","BatteringRam","Catapult","IngameGui_HUD"})
        {
            var path=paths.FirstOrDefault(p=>p.Key.EndsWith("/"+name+".prefab",StringComparison.OrdinalIgnoreCase));
            if(path.Key==null)continue;Load(path.Value);result[name]=bundles[path.Value].LoadAsset<GameObject>(path.Key);
        }
        return result;
    }
    static void Language()
    {
        var loc=(Localization)FormatterServices.GetUninitializedObject(typeof(Localization));
        foreach(var f in typeof(Localization).GetFields(F).Where(f=>!f.IsStatic))
        {
            if(f.FieldType==typeof(char[]))f.SetValue(loc," (){}[]+-!?/\\&%,.:-=<>\n".ToCharArray());
            else if(f.FieldType==typeof(System.Text.StringBuilder))f.SetValue(loc,new System.Text.StringBuilder());
            else if(f.FieldType.IsGenericType&&(f.FieldType.GetGenericTypeDefinition()==typeof(Dictionary<,>)||f.FieldType.GetGenericTypeDefinition()==typeof(List<>)))f.SetValue(loc,Activator.CreateInstance(f.FieldType));
            else if(f.Name=="m_cache")f.SetValue(loc,Activator.CreateInstance(f.FieldType,new object[]{100}));
        }
        typeof(Localization).GetField("m_instance",F).SetValue(null,loc);
        var words=(Dictionary<string,string>)typeof(Localization).GetField("m_translations",F).GetValue(loc);words["overhaul_map_ship"]="Bateau";words["overhaul_map_cart"]="Chariot";words["overhaul_map_ram"]="Bélier";
    }
    public static void Run()
    {
        AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var path=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(path)?Assembly.LoadFrom(path):null;};
        try
        {
            var mod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));feature=mod.GetType("Overhaul.Storage.VehicleMarkers",true);
            var ha=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll"));var ht=ha.GetType("HarmonyLib.Harmony");var h=Activator.CreateInstance(ht,new object[]{"overhaul.vehicle.check"});
            var patch=ht.GetMethods().First(m=>m.Name=="Patch"&&m.GetParameters().Length==5);var hm=ha.GetType("HarmonyLib.HarmonyMethod");
            patch.Invoke(h,new object[]{typeof(Minimap).GetMethod("Reset",F),Activator.CreateInstance(hm,new object[]{typeof(VehicleMarkerChecks).GetMethod("Awake",F)}),null,null,null});
            patch.Invoke(h,new object[]{typeof(Minimap.PinNameData).GetMethod("DestroyMapMarker",F),Activator.CreateInstance(hm,new object[]{typeof(VehicleMarkerChecks).GetMethod("DestroyName",F)}),null,null,null});
            foreach(var t in feature.GetNestedTypes(F).Where(t=>t.IsClass&&!t.Name.StartsWith("<")))
            {var cp=ht.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(h,new object[]{t});cp.GetType().GetMethod("Patch").Invoke(cp,null);Check(true,"Harmony installs "+t.Name);}
            Language();var prefabs=Assets();
            foreach(var name in new[]{"Raft","Karve","VikingShip","VikingShip_Ashlands","Cart","BatteringRam"})
            {Check(prefabs.ContainsKey(name),"native prefab exists "+name);Check(Call("Classify",prefabs[name]).ToString()==(name=="Cart"?"Cart":name=="BatteringRam"?"Ram":"Ship"),"native category "+name);}
            Check(Call("Classify",prefabs["Catapult"])==null,"catapult not confused with cart");
            var net=New<ZNet>("network");typeof(ZNet).GetField("m_instance",F).SetValue(null,net);ZNet.m_isServer=true;
            var game=New<Game>("game");typeof(Game).GetProperty("instance",F).SetValue(null,game);
            var scene=New<ZNetScene>("scene");typeof(ZNetScene).GetField("s_instance",F).SetValue(null,scene);
            foreach(var p in prefabs)scene.m_namedPrefabs[p.Key.GetStableHashCode()]=p.Value;
            var rpc=new ZRoutedRpc(true);var manager=new ZDOMan(512);rpc.SetUID(ZNet.GetUID());
            feature.GetField("session",F).SetValue(null,manager);
            var ids=new List<ZDO>();foreach(var name in new[]{"Karve","Cart","BatteringRam"}){var data=manager.CreateNewZDO(new Vector3(ids.Count*1000,0,4000),name.GetStableHashCode());data.SetPrefab(name.GetStableHashCode());ids.Add(data);}
            var snapshot=Call("Snapshot");Check(((IList)snapshot).Count==3,"server finds unloaded vehicles across distant sectors");
            var packet=(ZPackage)Call("Encode",snapshot);packet.SetPos(0);Check((bool)Call("Decode",packet),"snapshot packet round trip");
            var positions=(IDictionary)feature.GetField("positions",F).GetValue(null);Check(positions.Count==3,"snapshot restores all three vehicles");
            feature.GetField("scene",F).SetValue(null,scene);Call("Tick");var before=positions[ids[0].m_uid];ids[0].SetPosition(new Vector3(50,0,50));Call("Tick");
            Check(positions[ids[0].m_uid].Equals(before),"position updates throttled between one-second ticks");
            feature.GetField("nextUpdate",F).SetValue(null,0f);Call("Tick");Check(!positions[ids[0].m_uid].Equals(before),"next one-second tick samples current position");
            var bad=new ZPackage();bad.Write(2);bad.Write(ids[0].m_uid);bad.SetPos(0);Check(!(bool)Call("Decode",bad)&&positions.Count==3,"truncated packet leaves last valid snapshot intact");
            var forbidden=new ZPackage();forbidden.Write(0);forbidden.SetPos(0);Call("Receive",null,forbidden);Check(positions.Count==3,"server ignores client marker packets");
            ZNet.m_isServer=false;forbidden.SetPos(0);Call("Receive",new ZRpc(null),forbidden);Check(positions.Count==3,"client ignores unauthenticated sender");ZNet.m_isServer=true;
            var map=New<Minimap>("map");map.m_visibleIconTypes=Enumerable.Repeat(true,32).ToArray();Call("SyncPins",map);
            var pins=(IDictionary)feature.GetField("pins",F).GetValue(null);Check(pins.Count==3&&map.m_pins.Count==3,"three automatic map pins created");
            var shipPin=(Minimap.PinData)pins[ids[0].m_uid];Check(!shipPin.m_save&&!shipPin.m_checked,"automatic pins excluded from saved player pins");
            foreach(Minimap.PinData pin in pins.Values){pin.m_iconElement=new GameObject("pin-test-image",typeof(RectTransform),typeof(CanvasRenderer),typeof(Image)).GetComponent<Image>();pin.m_iconElement.color=Color.white;}
            Call("Tint");Check(shipPin.m_iconElement.color.b>shipPin.m_iconElement.color.g&&((Minimap.PinData)pins[ids[1].m_uid]).m_iconElement.color.g>.8f&&((Minimap.PinData)pins[ids[2].m_uid]).m_iconElement.color.r>.9f,"native white tint overridden by blue green red");
            Check(map.GetClosestPin(shipPin.m_pos,100,false)==null,"mouse and gamepad edit/delete selection excludes vehicle markers");
            map.RemovePin(shipPin);Check(map.m_pins.Contains(shipPin),"direct pin removal blocked");
            ids[0].SetPosition(new Vector3(7000,0,7000));snapshot=Call("Snapshot");packet=(ZPackage)Call("Encode",snapshot);packet.SetPos(0);Call("Decode",packet);Call("SyncPins",map);
            Check((Minimap.PinData)pins[ids[0].m_uid]==shipPin&&shipPin.m_pos==ids[0].GetPosition(),"moving vehicle updates same pin without duplicates");
            ids[1].Set(ZDOVars.s_health,0f);snapshot=Call("Snapshot");packet=(ZPackage)Call("Encode",snapshot);packet.SetPos(0);Call("Decode",packet);Call("SyncPins",map);Check(pins.Count==2&&!pins.Contains(ids[1].m_uid),"destroyed vehicle marker removed");
            var fresh=manager.CreateNewZDO(Vector3.one,"Cart".GetStableHashCode());fresh.SetPrefab("Cart".GetStableHashCode());Check(((IList)Call("Snapshot")).Count==3,"new vehicle indexed through native prefab hook");
            snapshot=Call("Snapshot");packet=(ZPackage)Call("Encode",snapshot);packet.SetPos(0);Call("Decode",packet);Call("SyncPins",map);
            var native=prefabs["IngameGui_HUD"].GetComponentInChildren<Minimap>(true);Check(native&&native.m_pinPrefab,"native map marker prefab found");
            Preview(mod,native.m_pinPrefab,map);
            Call("Clear");Check(positions.Count==0&&pins.Count==0&&map.m_pins.Count==0,"world exit removes all automatic marker state");
        }
        catch(Exception e){report.Add("FAIL "+e);throw;}
        finally{File.WriteAllLines("../../../Tools/AugaWork/vehicle-marker-checks.txt",report);}
    }
    static void Preview(Assembly mod,GameObject pinPrefab,Minimap map)
    {
        var root=new GameObject("Canvas",typeof(Canvas));var camera=new GameObject("Camera",typeof(Camera)).GetComponent<Camera>();camera.orthographic=true;camera.orthographicSize=180;camera.transform.position=new Vector3(0,0,-10);camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.11f,.14f,.13f);camera.cullingMask=1<<30;
        var canvas=root.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=camera;canvas.planeDistance=1;canvas.referencePixelsPerUnit=50;root.layer=30;
        var target=new RenderTexture(840,360,24);camera.targetTexture=target;
        var colors=mod.GetType("Overhaul.Storage.VehicleMarkerIcons");var kind=feature.GetNestedType("Kind",F);
        for(int i=0;i<3;i++)
        {
            var type=Enum.ToObject(kind,i);var sprite=(Sprite)colors.GetMethod("Get",F).Invoke(null,new object[]{type});var color=(Color)colors.GetMethod("ColorFor",F).Invoke(null,new object[]{type});
            var pin=UnityEngine.Object.Instantiate(pinPrefab,root.transform);pin.GetComponent<Image>().sprite=sprite;pin.GetComponent<Image>().color=color;pin.transform.Find("Checked").gameObject.SetActive(false);
            var rect=(RectTransform)pin.transform;rect.anchorMin=rect.anchorMax=new Vector2(.5f,.5f);rect.sizeDelta=new Vector2(90,90);rect.anchoredPosition=new Vector2((i-1)*250,25);
            var label=new GameObject("label",typeof(RectTransform),typeof(CanvasRenderer),typeof(TMPro.TextMeshProUGUI));label.transform.SetParent(root.transform,false);var text=label.GetComponent<TMPro.TextMeshProUGUI>();text.font=UnityEditor.AssetDatabase.LoadAssetAtPath<TMPro.TMP_FontAsset>("Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");text.text=new[]{"Bateau","Chariot","Bélier"}[i];text.fontSize=26;text.color=color;text.alignment=TMPro.TextAlignmentOptions.Center;var tr=(RectTransform)label.transform;tr.sizeDelta=new Vector2(230,45);tr.anchoredPosition=new Vector2((i-1)*250,-55);
            // Also show actual minimap-size readability below the enlarged legend.
            var small=UnityEngine.Object.Instantiate(pin,root.transform);var sr=(RectTransform)small.transform;sr.sizeDelta=new Vector2(28,28);sr.anchoredPosition=new Vector2((i-1)*250,-108);
            Check(sprite.texture.GetPixels().Any(c=>c.a>0)&&sprite.texture.GetPixels().Any(c=>c.a==0),"outlined transparent icon "+type);
        }
        foreach(var t in root.GetComponentsInChildren<Transform>(true))t.gameObject.layer=30;Canvas.ForceUpdateCanvases();camera.Render();RenderTexture.active=target;var image=new Texture2D(840,360,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,840,360),0,0);image.Apply();File.WriteAllBytes("../../../Tools/AugaWork/vehicle-markers-preview-v2.png",image.EncodeToPNG());RenderTexture.active=null;
        UnityEngine.Object.DestroyImmediate(root);Check(true,"static Unity preview uses actual map pin prefab and production icons/colors");
    }
}

