using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public static class IntegratedXPortalCheck
{
    static BindingFlags F=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int okay,cancel,ping;
    static bool Okay(){okay++;return false;}
    static bool Cancel(){cancel++;return false;}
    static bool Ping(){ping++;return false;}
    static bool Bound(ref string __result){__result="";return false;}
    static bool Icon(ref Sprite __result){__result=null;return false;}
    public static void Run()
    {
        var loc=(Localization)typeof(AugaRightPanelCheck).GetMethod("Localize",F).Invoke(null,null);
        var translations=Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText("../../../Packages/Overhaul/Localisation/translationsFR.json"));
        foreach(var f in typeof(Localization).GetFields(F).Where(f=>!f.IsStatic&&f.FieldType==typeof(Dictionary<string,string>)))
        {var words=(Dictionary<string,string>)f.GetValue(loc);foreach(var p in translations.Properties())words[p.Name]=(string)p.Value;words["menu_ok"]="Valider";words["hud_ping"]="Repérer";}
        AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{foreach(var dir in new[]{"../../Libs","../Auga/bin/Release"}){var p=Path.GetFullPath(dir+"/"+new AssemblyName(a.Name).Name+".dll");if(File.Exists(p))return Assembly.LoadFrom(p);}return null;};
        var xp=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));
        var plugin=xp;
        plugin.GetType("Overhaul.IntegratedUi").GetMethod("LoadDependencies",F).Invoke(null,null);
        var modules=plugin.GetType("Auga.UiModules");((IDictionary)modules.GetField("EnabledModules",F).GetValue(null))[Enum.Parse(plugin.GetType("Auga.UiModule"),"TextInput")]=true;
        var ha=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/0Harmony.dll"));var ht=ha.GetType("HarmonyLib.Harmony");var hm=ha.GetType("HarmonyLib.HarmonyMethod");var harmony=Activator.CreateInstance(ht,new object[]{"auga.xportal.tests"});
        var bridge=plugin.GetType("Auga.XPortalCompatibility");
        var stream=plugin.GetManifestResourceStream("Overhaul.augaassets");var bytes=new byte[stream.Length];stream.Read(bytes,0,bytes.Length);stream.Dispose();var bundle=AssetBundle.LoadFromMemory(bytes);var prefab=bundle.LoadAsset<GameObject>("AugaXPortal");if(!prefab)throw new Exception("Missing bundled prefab");bridge.GetField("Prefab",F).SetValue(null,prefab);
        bridge.GetMethod("Install",F).Invoke(null,new[]{harmony});
        var panelType=xp.GetType("XPortal.UI.PortalConfigurationPanel");var owner=panelType.GetProperty("Instance",F).GetValue(null);
        var game=new GameObject("_GameMain");var loading=new GameObject("LoadingGUI");loading.transform.SetParent(game.transform);var front=new GameObject("CustomGUIFront");front.transform.SetParent(loading.transform);
        var patch=ht.GetMethods().First(m=>m.Name=="Patch"&&m.GetParameters()[0].ParameterType==typeof(MethodBase)&&m.GetParameters()[1].ParameterType==hm);
        var boundArgs=new object[patch.GetParameters().Length];boundArgs[0]=typeof(Localization).GetMethod("GetBoundKeyString",F);boundArgs[1]=Activator.CreateInstance(hm,new object[]{typeof(IntegratedXPortalCheck).GetMethod("Bound",F)});patch.Invoke(harmony,boundArgs);
        var managerType=Assembly.LoadFrom(Path.GetFullPath("../../../Libs/Jotunn.dll")).GetType("Jotunn.Managers.GUIManager");
        if(managerType.GetProperty("Instance",F)==null||managerType.GetMethod("GetSprite",F,null,new[]{typeof(string)},null)==null)throw new Exception("Jotunn icon API changed");
        var iconArgs=new object[patch.GetParameters().Length];iconArgs[0]=bridge.GetMethod("InputIcon",F);iconArgs[1]=Activator.CreateInstance(hm,new object[]{typeof(IntegratedXPortalCheck).GetMethod("Icon",F)});patch.Invoke(harmony,iconArgs);
        foreach(var pair in new[]{new[]{"OnOkayButtonClicked","Okay"},new[]{"OnCancelButtonClicked","Cancel"},new[]{"OnPingMapButtonClicked","Ping"}})
        {var args=new object[patch.GetParameters().Length];args[0]=panelType.GetMethod(pair[0],F);args[1]=Activator.CreateInstance(hm,new object[]{typeof(IntegratedXPortalCheck).GetMethod(pair[1],F)});patch.Invoke(harmony,args);}
        panelType.GetMethod("InitialiseUI",F).Invoke(owner,null);
        var view=(GameObject)panelType.GetField("mainPanel",F).GetValue(owner);if(!view||view.activeSelf)throw new Exception("Replacement missing / initially active");
        panelType.GetMethod("InitialiseUI",F).Invoke(owner,null);if(front.transform.childCount!=1)throw new Exception("Duplicate view");
        foreach(var pair in new[]{new[]{"XPortal_OkayButton","Okay"},new[]{"XPortal_CancelButton","Cancel"},new[]{"XPortal_PingMapButton","Ping"}})view.GetComponentsInChildren<Button>(true).Single(b=>b.name==pair[0]).onClick.Invoke();
        if(okay!=1||cancel!=1||ping!=1)throw new Exception("Native button handlers not wired exactly once");
        XPortalLogicChecks.Run(plugin,owner);
        panelType.GetMethod("Dispose",F).Invoke(owner,null);UnityEngine.Object.DestroyImmediate(view);
        panelType.GetMethod("InitialiseUI",F).Invoke(owner,null);view=(GameObject)panelType.GetField("mainPanel",F).GetValue(owner);
        if(!view || view.activeSelf || front.transform.childCount!=1 || (bool)panelType.GetField("DropdownExpanded",F).GetValue(owner))throw new Exception("UI session cleanup/recreation failed");
        var input=(InputField)panelType.GetField("portalNameInputField",F).GetValue(owner);input.text="Base d'Aedis";
        var toggle=(Toggle)panelType.GetField("defaultPortalToggle",F).GetValue(owner);if(toggle.name!="XPortal_DefaultPortalCheckbox")throw new Exception("Wrong toggle");toggle.isOn=true;
        var dropdown=(Dropdown)panelType.GetField("targetPortalDropdown",F).GetValue(owner);
        if(!dropdown.template||!dropdown.captionText||!dropdown.itemText||!dropdown.template.GetComponentInChildren<Toggle>(true))throw new Exception("Invalid native dropdown bindings");
        if(view.GetComponentsInChildren<UIGamePad>(true).Length!=5)throw new Exception("Gamepad shortcuts missing");
        foreach(var b in view.GetComponentsInChildren<Button>(true))if(!b.targetGraphic||!b.spriteState.highlightedSprite||b.GetComponents<AugaUnity.ColorButtonTextValues>().Any(v=>!v.Text))throw new Exception("Button hover binding missing: "+b.name);
        var confirm=view.GetComponentsInChildren<Button>(true).Single(b=>b.name=="XPortal_OkayButton");var back=view.GetComponentsInChildren<Button>(true).Single(b=>b.name=="XPortal_CancelButton");if(((RectTransform)confirm.transform).anchoredPosition.x<((RectTransform)back.transform).anchoredPosition.x)throw new Exception("Wrong button order");
        dropdown.onValueChanged.RemoveAllListeners();
        dropdown.AddOptions(new List<string>{"(Aucun)","Base principale (120 m)","Forêt noire (1,2 km)","Montagne (2,8 km)","Plaines (4,1 km)"});dropdown.value=2;
        loc.Localize(view.transform);
        foreach(var t in view.GetComponentsInChildren<Transform>(true).Where(t=>t.name=="gamepad_hint"))t.gameObject.SetActive(false);
        typeof(AugaPauseCheck).GetMethod("Strip",F).Invoke(null,new object[]{view});
        var wrapper=new GameObject("Preview",typeof(RectTransform));view.transform.SetParent(wrapper.transform,false);view.SetActive(true);
        var openWrapper=new GameObject("Open preview",typeof(RectTransform));openWrapper.SetActive(false);
        var openView=UnityEngine.Object.Instantiate(view,openWrapper.transform,false);
        typeof(AugaPauseCheck).GetMethod("Render",F).Invoke(null,new object[]{wrapper,"integrated-xportal",870,405});
        RenderOpen(openWrapper,openView.GetComponentInChildren<Dropdown>(true));UnityEngine.Object.DestroyImmediate(openWrapper);
        Debug.Log("XPORTAL CHECK PASSED: integrated Overhaul DLL (no external XPortal), Harmony init, single view, native callbacks, field bindings, 5 keyboard/gamepad hints, button hover and opened dropdown template.");
        UnityEngine.Object.DestroyImmediate(wrapper);UnityEngine.Object.DestroyImmediate(game);bundle.Unload(true);ht.GetMethod("UnpatchSelf").Invoke(harmony,null);
    }
    static void RenderOpen(GameObject go,Dropdown dropdown)
    {
        var cg=new GameObject("Canvas",typeof(Canvas),typeof(GraphicRaycaster));var cameraGo=new GameObject("Camera",typeof(Camera));var camera=cameraGo.GetComponent<Camera>();camera.orthographic=true;camera.orthographicSize=250;camera.transform.position=new Vector3(0,0,-10);camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.08f,.085f,.09f);camera.cullingMask=1<<30;
        var canvas=cg.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=camera;canvas.planeDistance=1;canvas.referencePixelsPerUnit=50;cg.layer=30;var rt=new RenderTexture(870,500,24);camera.targetTexture=rt;
        var events=new GameObject("Events",typeof(UnityEngine.EventSystems.EventSystem));
        go.transform.SetParent(cg.transform,false);var rect=(RectTransform)go.transform;rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;rect.offsetMin=rect.offsetMax=Vector2.zero;go.SetActive(true);
        Canvas.ForceUpdateCanvases();typeof(Dropdown).GetMethod("Start",F).Invoke(dropdown,null);dropdown.alphaFadeSpeed=0;dropdown.Show();Canvas.ForceUpdateCanvases();
        if(!dropdown.transform.Find("Dropdown List"))throw new Exception("Native dropdown failed to expand");
        dropdown.transform.Find("Dropdown List").GetComponent<CanvasGroup>().alpha=1;
        foreach(var t in cg.GetComponentsInChildren<Transform>(true))t.gameObject.layer=30;
        Canvas.ForceUpdateCanvases();
        var labels=dropdown.transform.Find("Dropdown List").GetComponentsInChildren<Text>().Where(t=>!string.IsNullOrEmpty(t.text)).ToArray();
        if(labels.Length!=dropdown.options.Count||labels.Any(t=>t.rectTransform.rect.height<22))throw new Exception("Dropdown option text missing or clipped");
        camera.Render();RenderTexture.active=rt;var texture=new Texture2D(870,500,TextureFormat.RGB24,false);texture.ReadPixels(new Rect(0,0,870,500),0,0);texture.Apply();File.WriteAllBytes("../../../Tools/AugaWork/integrated-xportal-destinations.png",texture.EncodeToPNG());RenderTexture.active=null;
        foreach(var name in new[]{"m_Dropdown","m_Blocker"}){var field=typeof(Dropdown).GetField(name,F);var obj=field.GetValue(dropdown) as GameObject;field.SetValue(dropdown,null);if(obj)UnityEngine.Object.DestroyImmediate(obj);}
        go.SetActive(false);go.transform.SetParent(null);UnityEngine.Object.DestroyImmediate(cg);UnityEngine.Object.DestroyImmediate(cameraGo);UnityEngine.Object.DestroyImmediate(events);UnityEngine.Object.DestroyImmediate(texture);UnityEngine.Object.DestroyImmediate(rt);
    }
}
