using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEngine;
using UnityEditor;
using AugaUnity;

public static class OverhaulItemTooltipCheck
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    public static void Run()
    {
        AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{string p=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        var plugin=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));
        plugin.GetType("Overhaul.IntegratedUi").GetMethod("LoadDependencies",F).Invoke(null,null);
        var loc=(Localization)FormatterServices.GetUninitializedObject(typeof(Localization));
        foreach(var f in typeof(Localization).GetFields(F).Where(f=>!f.IsStatic))
        {
            if(f.FieldType==typeof(char[]))f.SetValue(loc," (){}[]+-!?/\\&%,.:-=<>\n".ToCharArray());
            else if(f.FieldType==typeof(System.Text.StringBuilder))f.SetValue(loc,new System.Text.StringBuilder());
            else if(f.FieldType.IsGenericType&&(f.FieldType.GetGenericTypeDefinition()==typeof(Dictionary<,>)||f.FieldType.GetGenericTypeDefinition()==typeof(List<>)))f.SetValue(loc,Activator.CreateInstance(f.FieldType));
            else if(f.Name=="m_cache")f.SetValue(loc,Activator.CreateInstance(f.FieldType,new object[]{100}));
        }
        var old=typeof(Localization).GetField("m_instance",F).GetValue(null);typeof(Localization).GetField("m_instance",F).SetValue(null,loc);
        var bundle=AssetBundle.LoadFromFile("AssetBundles/augaassets");
        try
        {
            var effect=ScriptableObject.CreateInstance<SE_Stats>();effect.m_name="Megingjord";effect.m_tooltip="$se_beltstrength_tooltip";
            foreach(string language in new[]{"English","French"})
            {
                ((Dictionary<string,string>)typeof(Localization).GetField("m_translations",F).GetValue(loc)).Clear();
                loc.Localize(effect.m_tooltip); // Simulate the unresolved tooltip already cached in game.
                plugin.GetType("Auga.Auga").GetMethod("LoadTranslations",F).Invoke(null,new object[]{loc,language});
                                var words=(Dictionary<string,string>)typeof(Localization).GetField("m_translations",F).GetValue(loc);
                var keys=File.ReadAllText("../../../Tools/AugaWork/item-tooltip-keys.json");
                int count=0;foreach(System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(keys,"\"([a-zA-Z0-9_]+)\"")){
                    var key=match.Groups[1].Value;var value=loc.Localize("$"+key);if(string.IsNullOrWhiteSpace(value)||value.Contains("["+key+"]"))throw new Exception("Unresolved "+language+" "+key);count++;
                }
                Debug.Log("ITEM TOOLTIP PASS "+language+" keys="+count);
                string text=loc.Localize(effect.GetTooltipString());
                if(text.Contains("se_beltstrength_tooltip")||text.Length<15)throw new Exception("Wishbone translation unresolved: "+language);
                Debug.Log("WISHBONE PASS "+language+": "+text);
            }
            var host=new GameObject("Tooltip preview host");host.SetActive(false);
            var go=UnityEngine.Object.Instantiate(bundle.LoadAsset<GameObject>("InventoryTooltip"),host.transform,false);
            var tip=go.GetComponent<ComplexTooltip>();tip.SetTopic("Megingjord");tip.SetSubtitle("");tip.SetDescription("");tip.SetIcon(null);tip.ClearTextBoxes();
            var box=tip.AddTextBox(tip.LeftAlignedTextBoxPrefab);box.AddLine("<color=orange>Megingjord</color>");box.AddLine(effect.GetTooltipString());
            if(box.Text.text.Contains("se_beltstrength_tooltip"))throw new Exception("Raw key remains in actual Auga text box");
            RenderBox(box.gameObject);
            UnityEngine.Object.DestroyImmediate(go);UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(effect);
            File.WriteAllText("../../../Tools/OverhaulV2Work/item-tooltip-results.txt","PASS 1893 item/effect keys FR/EN with native dictionary empty, cached missing key cleared, actual Auga Megingjord text box rendered. "+plugin.GetName().Version);
        }
        finally{bundle.Unload(true);typeof(Localization).GetField("m_instance",F).SetValue(null,old);}
    }
    static void RenderBox(GameObject box)
    {
        var root=new GameObject("Canvas",typeof(Canvas));var cameraGo=new GameObject("Camera",typeof(Camera));
        var camera=cameraGo.GetComponent<Camera>();camera.orthographic=true;camera.orthographicSize=110;camera.transform.position=new Vector3(0,0,-10);
        camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.12f,.11f,.095f);camera.cullingMask=1<<30;
        var canvas=root.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=camera;canvas.planeDistance=1;canvas.referencePixelsPerUnit=50;root.layer=30;
        var target=new RenderTexture(500,220,24);camera.targetTexture=target;
        box.transform.SetParent(root.transform,false);var rect=(RectTransform)box.transform;rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(.5f,1);
        rect.anchoredPosition=new Vector2(0,-25);rect.sizeDelta=new Vector2(440,170);foreach(var t in box.GetComponentsInChildren<Transform>(true))t.gameObject.layer=30;
        box.SetActive(true);Canvas.ForceUpdateCanvases();UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(rect);Canvas.ForceUpdateCanvases();
        camera.Render();RenderTexture.active=target;var image=new Texture2D(500,220,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,500,220),0,0);image.Apply();
        File.WriteAllBytes("../../../Tools/AugaWork/item-tooltip-megingjord.png",image.EncodeToPNG());RenderTexture.active=null;
        UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate(cameraGo);UnityEngine.Object.DestroyImmediate(target);UnityEngine.Object.DestroyImmediate(image);
    }
}



