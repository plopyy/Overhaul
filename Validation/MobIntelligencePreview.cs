using System;using System.IO;using System.Linq;using System.Reflection;using UnityEngine;using UnityEngine.UI;using UnityEditor;using TMPro;using Object=UnityEngine.Object;
public static class MobIntelligencePreview
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    public static void Run()
    {
        AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var p=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        var mod=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));var update=mod.GetType("Overhaul.AI.MobIntelligenceHud").GetMethod("Update",F);
        var bundle=OverhaulBuildSource.Load();var paths=OverhaulBuildSource.Bundles.SelectMany(b=>b.GetAllAssetNames().Where(n=>n.EndsWith(".prefab")&&n.ToLowerInvariant().Contains("enemyhud")).Select(n=>new{b,n})).ToArray();
        File.WriteAllLines("../../../Tools/AugaWork/intelligence-hud-prefabs.txt",paths.Select(p=>p.n));
        var original=paths.Select(p=>p.b.LoadAsset<GameObject>(p.n)).First(p=>p&&p.GetComponent<EnemyHud>());var enemy=original.GetComponent<EnemyHud>();
        var host=new GameObject("inactive fixtures");host.SetActive(false);ZNet.m_instance=host.AddComponent<ZNet>();typeof(Game).GetProperty("instance",F).SetValue(null,host.AddComponent<Game>());new ZRoutedRpc(true);var manager=new ZDOMan(512);
        var cameraGo=new GameObject("Preview camera",typeof(Camera));var camera=cameraGo.GetComponent<Camera>();camera.transform.position=new Vector3(0,0,-10);camera.orthographic=true;camera.orthographicSize=270;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.06f,.075f,.09f);camera.cullingMask=1<<30;
        var canvasGo=new GameObject("Canvas",typeof(Canvas));var canvas=canvasGo.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=camera;canvas.planeDistance=1;canvas.referencePixelsPerUnit=50;canvasGo.layer=30;
        var rt=new RenderTexture(720,540,24);camera.targetTexture=rt;
        for(int i=0;i<5;i++)
        {
            var go=new GameObject("mob");go.transform.SetParent(host.transform);var character=go.AddComponent<Humanoid>();character.m_nview=go.AddComponent<ZNetView>();var data=manager.CreateNewZDO(Vector3.zero,0);data.SetOwner(123456);character.m_nview.m_zdo=data;data.Set("overhaul_ai_intelligence_v1",i==0?1:i==2?0:2);
            var gui=Object.Instantiate(enemy.m_baseHud,host.transform);foreach(var script in gui.GetComponentsInChildren<MonoBehaviour>(true))if(!(script is Graphic)&&!(script is LayoutGroup)&&!(script is ContentSizeFitter)&&!(script is Mask)&&!(script is RectMask2D))Object.DestroyImmediate(script);
            var name=gui.transform.Find("Name");if(name){var text=name.GetComponent<TMP_Text>();if(text)text.text=i==3?"Gardien de donjon":i==4?"Eikthyr":"Chaman gobelin";}
            foreach(var t in gui.GetComponentsInChildren<TMP_Text>(true)){if(t.text.Contains("$"))t.text="Chaman gobelin";t.color=Color.white;}
            foreach(var n in new[]{"level_2","level_3","Alerted","Aware","Health/health_fast_friendly"}){var t=gui.transform.Find(n);if(t)t.gameObject.SetActive(false);}
            var hud=new EnemyHud.HudData{m_gui=gui,m_character=character};update.Invoke(null,new object[]{hud});if(i==3)data.Set("overhaul_boss_character_v1".GetStableHashCode(),true);if(i==4)character.m_boss=true;if(i>=3)update.Invoke(null,new object[]{hud});var icon=gui.transform.Find("Health/OverhaulIntelligence");if((i<2||i==3)!=(icon&&icon.gameObject.activeSelf))throw new Exception("Wrong icon visibility");if(icon&&icon.GetComponent<Graphic>().raycastTarget)throw new Exception("Icon intercepts input");update.Invoke(null,new object[]{hud});if(gui.GetComponentsInChildren<Transform>(true).Count(t=>t.name=="OverhaulIntelligence")!=(i==2?0:1))throw new Exception("Duplicate icon");
            gui.transform.SetParent(canvasGo.transform,false);var rect=(RectTransform)gui.transform;rect.anchorMin=rect.anchorMax=new Vector2(.5f,.5f);rect.anchoredPosition=new Vector2(0,180-i*90);gui.SetActive(true);foreach(var t in gui.GetComponentsInChildren<Transform>(true))t.gameObject.layer=30;
        }
        Canvas.ForceUpdateCanvases();File.WriteAllLines("../../../Tools/AugaWork/intelligence-hud-geometry.txt",canvasGo.GetComponentsInChildren<RectTransform>(true).Select(t=>t.name+" active="+t.gameObject.activeInHierarchy+" rect="+t.rect+" position="+t.position+" scale="+t.lossyScale+" graphic="+(t.GetComponent<Graphic>()?t.GetComponent<Graphic>().color.ToString():"none")));camera.Render();RenderTexture.active=rt;var image=new Texture2D(720,540,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,720,540),0,0);image.Apply();File.WriteAllBytes("../../../Tools/AugaWork/mob-intelligence-preview.png",image.EncodeToPNG());RenderTexture.active=null;
        File.WriteAllText("../../../Tools/AugaWork/mob-intelligence-hud-checks.txt","PASS native prefab, replicated intelligence icons, guardian smart visible, normal and biome boss hidden including previously created icons, no duplicate, no raycast. Static preview; package "+mod.GetName().Version);
    }
}
