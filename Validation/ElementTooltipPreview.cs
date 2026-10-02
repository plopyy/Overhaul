using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEngine;
using UnityEditor;
using AugaUnity;

public static class ElementTooltipPreview
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
        try {
var words=(Dictionary<string,string>)typeof(Localization).GetField("m_translations",F).GetValue(loc);
var labels=new[]{"fire","frost","poison","lightning","spirit","blood"};var names=new[]{"Feu","Glace","Poison","Foudre","Esprit","Sang"};
for(int i=0;i<labels.Length;i++)words["inventory_"+labels[i]]=names[i];words["item_knockback"]="Repoussement";words["overhaul_projectile_extra"]="Projectile sup.";
var host=new GameObject("inactive tooltip");host.SetActive(false);
var go=UnityEngine.Object.Instantiate(bundle.LoadAsset<GameObject>("InventoryTooltip"),host.transform,false);
var tip=go.GetComponent<ComplexTooltip>();tip.ClearTextBoxes();var box=tip.AddTextBox(tip.TwoColumnTextBoxPrefab);
var item=new ItemDrop.ItemData();item.m_shared=new ItemDrop.ItemData.SharedData();item.m_shared.m_attack=new Attack();item.m_shared.m_secondaryAttack=new Attack();item.m_shared.m_itemType=ItemDrop.ItemData.ItemType.Bow;item.m_shared.m_attack.m_attackType=Attack.AttackType.Projectile;item.m_shared.m_attack.m_attackProjectile=host;item.m_shared.m_attack.m_attackEitr=40;var add=typeof(ComplexTooltip).GetMethod("AddDamageLine",F);
for(int i=0;i<labels.Length;i++)add.Invoke(tip,new object[]{box,item,"$inventory_"+labels[i],15f,10f,.4f,1f,false});
typeof(ComplexTooltip).GetField("HasProjectileStat",F).SetValue(null,new Func<Player,bool>(p=>true));
var callback=typeof(ComplexTooltip).GetField("ElementStatBonus",F);callback.SetValue(null,new Func<Player,string,float>((p,id)=>id=="element_fire"?15f:id=="projectile"?.13f:0f));
typeof(ComplexTooltip).GetMethod("AddElementStatLines",F).Invoke(tip,new object[]{box,null});box.AddLine("$item_knockback",20);typeof(ComplexTooltip).GetMethod("AddProjectileStatLine",F).Invoke(tip,new object[]{box,null,item});
if(!box.Text.text.Contains("Feu(stat)")||box.Text.text.IndexOf("Feu(stat)")>box.Text.text.IndexOf("Repoussement"))throw new Exception("Stat position invalid");
if(!box.Text.text.Contains("#FFF0AD")||!box.RightColumnText.text.Contains("#FF703D"))throw new Exception("Colors missing");
if(!box.Text.text.Contains("Projectile sup.")||box.Text.text.IndexOf("Projectile sup.")<box.Text.text.IndexOf("Repoussement")||!box.RightColumnText.text.Contains("13%"))throw new Exception("Projectile row invalid");
var melee=tip.AddTextBox(tip.TwoColumnTextBoxPrefab);item.m_shared.m_attack.m_attackType=Attack.AttackType.Horizontal;
 tip.AddProjectileStatLine(melee,null,item);if(melee.Text.text.Contains("Projectile"))throw new Exception("Melee projectile stat visible");
 item.m_shared.m_secondaryAttack.m_attackType=Attack.AttackType.Projectile;item.m_shared.m_secondaryAttack.m_attackProjectile=host;
 if(!ComplexTooltip.UsesProjectiles(item))throw new Exception("Secondary projectile ignored");
 item.m_shared.m_attack.m_attackType=Attack.AttackType.Projectile;
 if(ComplexTooltip.DisplayedEitrCost(item,null)!=40)throw new Exception("Base Eitr cost changed");
 callback.SetValue(null,new Func<Player,string,float>((p,id)=>id=="eitr_cost"?.25f:0));
 if(ComplexTooltip.DisplayedEitrCost(item,null)!=30)throw new Exception("Eitr reduction ignored");
 words["item_eitruse"]="Coût en Eitr";box.AddLine("$item_eitruse",ComplexTooltip.DisplayedEitrCost(item,null));
 callback.SetValue(null,new Func<Player,string,float>((p,id)=>2));if(ComplexTooltip.DisplayedEitrCost(item,null)!=0)throw new Exception("Eitr reduction not clamped");
 RenderBox(box.gameObject);
var upgradeTip=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/CraftingResultPanel.prefab"),host.transform,false).GetComponentInChildren<ComplexTooltip>(true);var upgrade=upgradeTip.AddTextBox(upgradeTip.UpgradeTwoColumnTextBoxPrefab);add.Invoke(tip,new object[]{upgrade,item,"$inventory_fire",20f,15f,.4f,1f,true});
if(!upgrade.RightColumnText.text.Contains("#FF703D")||!upgrade.ThirdColumnText.text.Contains("#FF703D"))throw new Exception("Upgrade colors missing");
callback.SetValue(null,new Func<Player,string,float>((p,id)=>0));var empty=tip.AddTextBox(tip.TwoColumnTextBoxPrefab);typeof(ComplexTooltip).GetMethod("AddElementStatLines",F).Invoke(tip,new object[]{empty,null});if(empty.Text.text.Contains("(stat)"))throw new Exception("Zero bonus must not show");
File.WriteAllText("../../../Tools/AugaWork/element-tooltip-checks.txt","PASS melee projectile hidden, secondary projectile recognized, Eitr base/reduction/clamp, native and upgrade colors, pale stat label before knockback, zero bonus hidden. Preview uses sample damage values and real tooltip prefab. Package "+plugin.GetName().Version);
typeof(ComplexTooltip).GetField("HasProjectileStat",F).SetValue(null,new Func<Player,bool>(p=>false));typeof(ComplexTooltip).GetMethod("AddProjectileStatLine",F).Invoke(tip,new object[]{empty,null,item});if(empty.Text.text.Contains("Projectile"))throw new Exception("Unallocated projectile row shown");
callback.SetValue(null,null);UnityEngine.Object.DestroyImmediate(host);
}finally{bundle.Unload(true);typeof(Localization).GetField("m_instance",F).SetValue(null,old);}
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
        File.WriteAllBytes("../../../Tools/AugaWork/weapon-projectile-eitr-tooltip.png",image.EncodeToPNG());RenderTexture.active=null;
        UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate(cameraGo);UnityEngine.Object.DestroyImmediate(target);UnityEngine.Object.DestroyImmediate(image);
    }
}



