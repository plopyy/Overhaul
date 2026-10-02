using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Object = UnityEngine.Object;

public static class TrainingMeterAuthoring
{
    const string Path = "Assets/Prefabs/AugaTrainingMeter.prefab";
    static readonly Color Gold = new Color(.86f,.76f,.55f);
    static RectTransform Rect(Transform parent,string name,Vector2 position,Vector2 size)
    {
        var go=new GameObject(name,typeof(RectTransform));go.layer=5;
        var r=(RectTransform)go.transform;r.SetParent(parent,false);
        r.anchorMin=r.anchorMax=r.pivot=new Vector2(0,1);r.anchoredPosition=position;r.sizeDelta=size;return r;
    }
    static TMP_Text Label(Transform parent,TMP_Text donor,string name,string value,Vector2 pos,Vector2 size,float fontSize,TextAlignmentOptions align,Color color)
    {
        var t=Rect(parent,name,pos,size).gameObject.AddComponent<TextMeshProUGUI>();
        t.font=donor.font;t.fontSharedMaterial=donor.fontSharedMaterial;t.fontSize=fontSize;
        t.text=value;t.alignment=align;t.color=color;t.raycastTarget=false;t.enableWordWrapping=false;
        return t;
    }
    public static void Run()
    {
        var root=Rect(null,"AugaTrainingMeter",new Vector2(22,0),new Vector2(248,196));
        root.anchorMin=root.anchorMax=root.pivot=new Vector2(0,.5f);
        try {
            var background=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/AugaPanelBase.prefab"),root,false);
            background.name="Frame";
            var bg=(RectTransform)background.transform;bg.anchorMin=bg.anchorMax=bg.pivot=new Vector2(0,1);bg.anchoredPosition=Vector2.zero;bg.sizeDelta=new Vector2(248,142);
            background.AddComponent<CanvasGroup>().alpha=.62f;
            foreach(var graphic in background.GetComponentsInChildren<Graphic>(true))graphic.raycastTarget=false;
            var button=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/ButtonFancy.prefab"),root,false);button.name="Reset";
            var br=(RectTransform)button.transform;br.anchorMin=br.anchorMax=br.pivot=new Vector2(.5f,1);br.anchoredPosition=new Vector2(0,-154);
            br.localScale=Vector3.one*.72f;
            button.GetComponent<Button>().onClick=new Button.ButtonClickedEvent();
            button.GetComponent<Button>().navigation=new Navigation{mode=Navigation.Mode.None};
            foreach(var tooltip in button.GetComponentsInChildren<UITooltip>(true))Object.DestroyImmediate(tooltip);
            var donor=button.GetComponentInChildren<TMP_Text>(true);donor.text="$overhaul_training_reset";donor.fontSize=26;donor.enableAutoSizing=false;donor.enableWordWrapping=false;donor.raycastTarget=false;
            var typography=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/ButtonMedium.prefab").GetComponentInChildren<TMP_Text>(true);
            donor.font=typography.font;donor.fontSharedMaterial=typography.fontSharedMaterial;donor.fontStyle=FontStyles.UpperCase;
            Label(root,donor,"Title","$overhaul_training_title",new Vector2(16,-10),new Vector2(216,27),18,TextAlignmentOptions.Center,Gold);
            Label(root,donor,"DpsLabel","DPS",new Vector2(16,-45),new Vector2(75,40),21,TextAlignmentOptions.Left,Color.white);
            Label(root,donor,"DpsValue","—",new Vector2(94,-43),new Vector2(137,44),32,TextAlignmentOptions.Right,Color.white);
            Label(root,donor,"PeakLabel","$overhaul_training_peak",new Vector2(16,-95),new Vector2(136,30),21,TextAlignmentOptions.Left,Color.white);
            Label(root,donor,"PeakValue","—",new Vector2(150,-94),new Vector2(81,30),22,TextAlignmentOptions.Right,Color.white);
            PrefabUtility.SaveAsPrefabAsset(root.gameObject,Path);
            AssetImporter.GetAtPath(Path).assetBundleName="augaassets";
            AssetDatabase.SaveAssets();
        } finally { Object.DestroyImmediate(root.gameObject); }
        AugaCompatibilityBundleBuild.Run();
        File.Copy("AssetBundles/augaassets","../../../Overhaul/AugaIntegration/Assets/augaassets",true);
        Preview();
    }
    public static void Preview()
    {
        var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(Path);
        Render(prefab,1920,1080,false,"training-meter-screen.png");
        Render(prefab,380,290,true,"training-meter-detail.png");
    }
    static void Render(GameObject prefab,int width,int height,bool close,string file)
    {
        var cameraGo=new GameObject("PreviewCamera",typeof(Camera));var camera=cameraGo.GetComponent<Camera>();
        camera.orthographic=true;camera.transform.position=new Vector3(0,0,-10);camera.clearFlags=CameraClearFlags.SolidColor;
        camera.backgroundColor=new Color(.16f,.185f,.175f);camera.cullingMask=1<<30;
        var canvasGo=new GameObject("PreviewCanvas",typeof(Canvas));var canvas=canvasGo.GetComponent<Canvas>();
        canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=camera;canvas.planeDistance=1;canvas.referencePixelsPerUnit=50;
        var target=new RenderTexture(width,height,24);camera.targetTexture=target;
        var panel=Object.Instantiate(prefab,canvasGo.transform,false);
        if(close) { var r=(RectTransform)panel.transform;r.anchorMin=r.anchorMax=r.pivot=new Vector2(.5f,.5f);r.anchoredPosition=Vector2.zero; }
        foreach(var t in panel.GetComponentsInChildren<TMP_Text>(true)) {
            if(t.text=="$overhaul_training_title")t.text="ENTRAÎNEMENT";
            if(t.text=="$overhaul_training_peak")t.text="Dégât Max";
            if(t.text=="$overhaul_training_reset")t.text="Réinitialiser";
        }
        panel.transform.Find("DpsValue").GetComponent<TMP_Text>().text="245,8";
        panel.transform.Find("PeakValue").GetComponent<TMP_Text>().text="487,2";
        foreach(var t in canvasGo.GetComponentsInChildren<Transform>(true))t.gameObject.layer=30;
        Canvas.ForceUpdateCanvases();camera.Render();RenderTexture.active=target;
        var texture=new Texture2D(width,height,TextureFormat.RGB24,false);texture.ReadPixels(new Rect(0,0,width,height),0,0);texture.Apply();
        File.WriteAllBytes("../../../Tools/AugaWork/"+file,texture.EncodeToPNG());RenderTexture.active=null;
        Object.DestroyImmediate(texture);Object.DestroyImmediate(canvasGo);Object.DestroyImmediate(cameraGo);Object.DestroyImmediate(target);
    }
}
