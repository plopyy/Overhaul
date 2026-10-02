using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEngine;
using UnityEditor;
using AugaUnity;

public static class OverhaulSkillTooltipCheck
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
            plugin.GetType("Auga.Auga").GetMethod("LoadTranslations",F).Invoke(null,new object[]{loc,"French"});
            loc.AddWord("skill_swords","Épées");loc.AddWord("skill","Compétence");loc.AddWord("level","Niveau");loc.AddWord("experience","Expérience");loc.AddWord("to_next_level","Prochain niveau");
            var host=new GameObject("Skill fixture");host.SetActive(false);
            var player=host.AddComponent<Player>();var previous=Player.m_localPlayer;Player.m_localPlayer=player;
            var skills=host.AddComponent<Skills>();skills.m_player=player;player.m_skills=skills;
            var seman=(SEMan)FormatterServices.GetUninitializedObject(typeof(SEMan));
            typeof(SEMan).GetField("m_statusEffects",F).SetValue(seman,new List<StatusEffect>());player.m_seman=seman;
            var effect=ScriptableObject.CreateInstance<SE_Stats>();effect.m_name="Bonus de set (test)";effect.m_skillLevel=Skills.SkillType.Swords;effect.m_skillLevelModifier=15;
            seman.GetStatusEffects().Add(effect);
            var skill=new Skills.Skill(new Skills.SkillDef{m_skill=Skills.SkillType.Swords,m_icon=AssetDatabase.LoadAssetAtPath<Sprite>("Assets/OverhaulAugaIcons/SwordSilver.png"),m_description="Améliore l’utilisation des épées."});skill.m_level=40;skills.m_skillData[Skills.SkillType.Swords]=skill;
            var go=UnityEngine.Object.Instantiate(bundle.LoadAsset<GameObject>("InventoryTooltip"),host.transform,false);
            var tip=go.GetComponent<ComplexTooltip>();tip.SetSkill(skill);
            string texts=string.Join("\n",go.GetComponentsInChildren<TMPro.TMP_Text>(true).Select(t=>t.text));
            if(!texts.Contains("+33")||!texts.Contains("+15")||!texts.Contains("(+15)")||texts.Contains("Niveau effectif")||texts.Contains("Bonus de set (test)"))throw new Exception("Skill bonus/range missing: "+texts);
            if(texts.Contains("Prochain niveau")||texts.Contains("Expérience")||texts.Contains("Coût des attaques"))throw new Exception("Removed details still visible");
            skills.GetRandomSkillRange(out float min,out float max,Skills.SkillType.Swords);
            if(Mathf.Abs(min-.58f)>.0001f||Mathf.Abs(max-.88f)>.0001f)throw new Exception("Native range mismatch");
            skill.m_level=95;ClearPreviewBoxes(tip);typeof(ComplexTooltip).GetField("_skillRefreshAt",F).SetValue(tip,0f);tip.SetSkill(skill);
            skills.GetRandomSkillRange(out min,out max,Skills.SkillType.Swords);
            if(Mathf.Abs(min-.85f)>.0001f||max!=1f)throw new Exception("Cap mismatch");
            texts=string.Join("\n",go.GetComponentsInChildren<TMPro.TMP_Text>(true).Select(t=>t.text));
            if(!texts.Contains("52.5")&&!texts.Contains("52,5"))throw new Exception("Capped raw damage gain mismatch");
            seman.GetStatusEffects().Clear();ClearPreviewBoxes(tip);typeof(ComplexTooltip).GetField("_skillRefreshAt",F).SetValue(tip,0f);tip.SetSkill(skill);
            texts=string.Join("\n",go.GetComponentsInChildren<TMPro.TMP_Text>(true).Select(t=>t.text));
            if(texts.Contains("(+15)"))throw new Exception("Stale effect after removal");
            seman.GetStatusEffects().Add(effect);skill.m_level=40;ClearPreviewBoxes(tip);typeof(ComplexTooltip).GetField("_skillRefreshAt",F).SetValue(tip,0f);tip.SetSkill(skill);
            RenderBox(go);
            var inventory=bundle.LoadAsset<GameObject>("Inventory_screen");
            var panel=inventory.GetComponentInChildren<SkillsPanelController>(true);
            var card=UnityEngine.Object.Instantiate(panel.SkillPrefab,host.transform,false);card.SkillType=Skills.SkillType.Swords;card.UpdateSkill();
            if(!card.LevelText.text.Contains("(+15)"))throw new Exception("Card bonus missing");
            var compareGo=UnityEngine.Object.Instantiate(bundle.LoadAsset<GameObject>("InventoryTooltip"),host.transform,false);
            var compareTip=compareGo.GetComponent<ComplexTooltip>();
            foreach(float level in new[]{40.49f,40.5f,40.99f,41f,99.9f,100f}) {
                skill.m_level=level;card.UpdateSkill();ClearPreviewBoxes(compareTip);typeof(ComplexTooltip).GetField("_skillRefreshAt",F).SetValue(compareTip,0f);compareTip.SetSkill(skill);
                string expected=Mathf.FloorToInt(level).ToString();
                if(card.LevelText.text!="Niveau "+expected&&!card.LevelText.text.StartsWith("Niveau "+expected+" "))throw new Exception("Card rounds fractional level upward: "+card.LevelText.text);
                var tooltipLines=compareGo.GetComponentsInChildren<TMPro.TMP_Text>(true).Select(t=>t.text).ToArray();
                if(!tooltipLines.Any(t=>t==expected||t.StartsWith(expected+" <color=orange>")))throw new Exception("Tooltip/card level mismatch at "+level);
            }
            UnityEngine.Object.DestroyImmediate(compareGo);skill.m_level=40.75f;card.UpdateSkill();
            RenderBox(card.gameObject,true);
            Player.m_localPlayer=previous;
            UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(effect);
            File.WriteAllText("../../../Tools/OverhaulV2Work/skill-tooltip-results.txt","PASS native skill range, active SE_Stats bonus, cap 100, removal refresh, actual French prefab. "+plugin.GetName().Version);
        }
        finally{bundle.Unload(true);typeof(Localization).GetField("m_instance",F).SetValue(null,old);}
    }
    static void ClearPreviewBoxes(ComplexTooltip tip) { var boxes=(List<GameObject>)typeof(ComplexTooltip).GetField("_textBoxes",F).GetValue(tip);foreach(var box in boxes)UnityEngine.Object.DestroyImmediate(box);boxes.Clear(); }
    static void RenderBox(GameObject box, bool card=false)
    {
        var root=new GameObject("Canvas",typeof(Canvas));var cameraGo=new GameObject("Camera",typeof(Camera));
        var camera=cameraGo.GetComponent<Camera>();camera.orthographic=true;camera.orthographicSize=card?140:340;camera.transform.position=new Vector3(0,0,-10);
        camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.12f,.11f,.095f);camera.cullingMask=1<<30;
        var canvas=root.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=camera;canvas.planeDistance=1;canvas.referencePixelsPerUnit=50;root.layer=30;
        var target=new RenderTexture(card?280:600,card?280:680,24);camera.targetTexture=target;
        box.transform.SetParent(root.transform,false);var rect=(RectTransform)box.transform;rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(.5f,1);
        rect.anchoredPosition=new Vector2(0,-25);if(!card)rect.sizeDelta=new Vector2(500,1000);foreach(var t in box.GetComponentsInChildren<Transform>(true))t.gameObject.layer=30;
        box.SetActive(true);Canvas.ForceUpdateCanvases();UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(rect);Canvas.ForceUpdateCanvases();
        camera.Render();RenderTexture.active=target;var image=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,target.width,target.height),0,0);image.Apply();
        File.WriteAllBytes(card?"../../../Tools/AugaWork/skill-card-bonus.png":"../../../Tools/AugaWork/skill-tooltip-details.png",image.EncodeToPNG());RenderTexture.active=null;
        UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate(cameraGo);UnityEngine.Object.DestroyImmediate(target);UnityEngine.Object.DestroyImmediate(image);
    }
}


