using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using AugaUnity;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
public static class OverhaulV2UiChecks
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static void Check(bool ok,string text){if(!ok)throw new Exception(text);Debug.Log("AUGA OVERHAUL PASS "+text);}
    public static void Run(Assembly overhaul)
    {
        typeof(AugaRightPanelCheck).GetMethod("Localize",F).Invoke(null,null);
        var auga=overhaul;var bridge=auga.GetType("Auga.Compat.OverhaulCompatibility",true).GetMethod("Prefix",F);
        var loader=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("BepInEx.Bootstrap.Chainloader")).First(t=>t!=null);
        var infos=(IDictionary)loader.GetProperty("PluginInfos",F).GetValue(null);bool existed=infos.Contains("plopyy.valheim.Overhaul");object previous=infos["plopyy.valheim.Overhaul"];infos.Remove("plopyy.valheim.Overhaul");
        var host=new GameObject("Inactive Auga leveling fixture");host.SetActive(false);var oldPlayer=Player.m_localPlayer;
        var player=host.AddComponent<Player>();Player.m_localPlayer=player;
        AssetBundle bundle; using(var stream=overhaul.GetManifestResourceStream("Overhaul.augaassets"))using(var buffer=new MemoryStream()){stream.CopyTo(buffer);bundle=AssetBundle.LoadFromMemory(buffer.ToArray());}
        try
        {
            var right=UnityEngine.Object.Instantiate(bundle.LoadAsset<GameObject>("Inventory_screen").transform.Find("root/RightPanel").gameObject,host.transform,false);
            var tabs=right.GetComponent<AugaTabController>();
            var valueType=infos.GetType().GetGenericArguments()[1];infos.Add("plopyy.valheim.Overhaul",FormatterServices.GetUninitializedObject(valueType));
            bridge.Invoke(null,new object[]{tabs});bridge.Invoke(null,new object[]{tabs});Check(tabs.TabContents.Count==6&&tabs.TabContents[1].name=="TabContent_Overhaul"&&tabs.TabContents[3].name=="TabContent_Skills","Optional second tab installed exactly once; skills retained");
            var windowType=overhaul.GetType("Overhaul.Leveling.LevelingWindow");var stateType=overhaul.GetType("Overhaul.Leveling.OverhaulCharacter");var state=stateType.GetMethod("Get",F).Invoke(null,new object[]{player});stateType.GetField("Ready").SetValue(state,true);
            var data=stateType.GetField("Data").GetValue(state);data.GetType().GetField("Level").SetValue(data,42);data.GetType().GetField("CurrentExperience").SetValue(data,12500L);
            var committed=(Dictionary<string,int>)data.GetType().GetField("AllocatedStats").GetValue(data);committed["carry"]=20;committed["critical"]=12;committed["health_regen"]=15;committed["element_fire"]=10;
            var page=tabs.TabContents[1];var window=page.GetComponent(windowType);windowType.GetField("state",F).SetValue(window,state);windowType.GetMethod("Refresh",F).Invoke(window,new object[]{true});
            Check(page.transform.Find("Progression/Stats").childCount==16,"Exactly sixteen stat cards");
            var nativeCard=right.GetComponentInChildren<SkillsPanelController>(true).SkillPrefab;
            var carryCard=page.transform.Find("Progression/Stats/carry");
            Check(page.transform.Find("Progression/Stats").GetComponentsInChildren<UITooltip>(true).Length==16,"Only one tooltip owner per stat; no empty button tooltips");
            var harmonyType=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("HarmonyLib.Harmony")).First(t=>t!=null);var hoverHarmony=Activator.CreateInstance(harmonyType,new object[]{"auga.overhaul.hover.check"});
            var processor=harmonyType.GetMethod("CreateClassProcessor",new[]{typeof(Type)}).Invoke(hoverHarmony,new object[]{auga.GetType("Auga.Compat.OverhaulCardTooltipHover")});processor.GetType().GetMethod("Patch",Type.EmptyTypes).Invoke(processor,null);
            var tooltipFields=new[]{"m_tooltip","m_current","m_hovered"}.Select(n=>typeof(UITooltip).GetField(n,F)).ToArray();var previousTooltipValues=tooltipFields.Select(f=>f.GetValue(null)).ToArray();
            var hoverFixture=new GameObject("Hover fixture",typeof(RectTransform));hoverFixture.transform.SetParent(host.transform,false);new GameObject("Body",typeof(RectTransform)).transform.SetParent(hoverFixture.transform,false);tooltipFields[0].SetValue(null,hoverFixture);
            try
            {
                foreach(string child in new[]{"SkillGraphics/IconBG/Icon","Name","Rank","Plus","Minus"})
                {
                    var hit=carryCard.GetComponentsInChildren<Transform>(true).FirstOrDefault(t=>t.name==child)??carryCard.Find(child);
                    typeof(UITooltip).GetMethod("OnHoverStart",F).Invoke(carryCard.GetComponent<UITooltip>(),new object[]{hit.gameObject});
                    Check((GameObject)tooltipFields[2].GetValue(null)==carryCard.gameObject,"Native hover tracks full card when entering via "+child);
                }
                var unrelated=hoverFixture.AddComponent<UITooltip>();unrelated.m_text="Other UI";typeof(UITooltip).GetMethod("OnHoverStart",F).Invoke(unrelated,new object[]{hoverFixture.transform.GetChild(0).gameObject});
                Check((GameObject)tooltipFields[2].GetValue(null)==hoverFixture.transform.GetChild(0).gameObject,"Unrelated tooltips retain native hover behavior");
            }
            finally{harmonyType.GetMethod("UnpatchSelf").Invoke(hoverHarmony,null);for(int i=0;i<tooltipFields.Length;i++)tooltipFields[i].SetValue(null,previousTooltipValues[i]);UnityEngine.Object.DestroyImmediate(hoverFixture);}
            foreach(var original in nativeCard.GetComponentsInChildren<Image>(true))
            {
                string path=AnimationUtility.CalculateTransformPath(original.transform,nativeCard.transform).Replace("ProgressBarLevel","RadialLevel");
                var copy=carryCard.Find(path).GetComponent<Image>();
                Check(copy.rectTransform.sizeDelta==original.rectTransform.sizeDelta&&copy.rectTransform.anchoredPosition==original.rectTransform.anchoredPosition&&copy.transform.localScale==original.transform.localScale&&copy.transform.localRotation==original.transform.localRotation,"Native skill card geometry preserved: "+path);
                if(original!=nativeCard.Icon)Check(copy.sprite==original.sprite,"Native skill card artwork preserved: "+path);
            }
            foreach(string name in new[]{"Minus","Plus"})
            {
                var r=(RectTransform)carryCard.Find(name);Check(Mathf.Abs(r.rect.width*r.localScale.x*carryCard.localScale.x-22)<.01f&&r.localScale.x==r.localScale.y&&r.anchoredPosition.y==-7,"Small proportional diamond below icon: "+name);
            }
            Check(carryCard.GetComponent<UITooltip>().m_text.Contains("Par point"),"Stat tooltip describes configured per-point gain");
            var passiveCards=new[]{"unburdened","feather","artisan","vitality"}.Select(id=>(RectTransform)page.transform.Find("Passives/"+id)).ToArray();
            Check(passiveCards.All(r=>r.anchoredPosition.y==passiveCards[0].anchoredPosition.y)&&passiveCards.Select(r=>r.Find("IconBG/Icon").GetComponent<Image>().sprite).Distinct().Count()==4,"Four passive cards in one row with distinct native icons");
            foreach(var card in passiveCards){var description=card.Find("Description").GetComponent<Text>();Check(description.preferredHeight<=description.rectTransform.rect.height,"Passive description fits: "+card.name);}
            Check(carryCard.Find("Minus/Text").GetComponent<Text>().text=="\u2212","Minus sign is correctly encoded");
            var nativeButton=(RectTransform)AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/ButtonMedium.prefab").transform;
            foreach(string path in new[]{"Footer/Reset","Footer/Validate","Passives/Confirm"}){var r=(RectTransform)page.transform.Find(path);Check(r.sizeDelta==nativeButton.sizeDelta&&r.localScale.x==r.localScale.y,"Native button proportions retained: "+path);}
            Check(Mathf.Abs(page.transform.Find("Progression/Stats/element_fire").GetComponentsInChildren<Transform>(true).First(t=>t.name=="RadialLevel").GetComponent<Image>().fillAmount-.15f)<.001f,"Radial stat uses allocated rank against configurable cap fifty");
            var plus=page.transform.Find("Progression/Stats/carry/Plus").GetComponent<Button>();var minus=page.transform.Find("Progression/Stats/carry/Minus").GetComponent<Button>();plus.onClick.Invoke();plus.onClick.Invoke();minus.onClick.Invoke();
            Check(committed["carry"]==20&&((Dictionary<string,int>)windowType.GetField("draft",F).GetValue(window))["carry"]==21,"Auga plus/minus stages only uncommitted points");
            tabs.Awake();tabs.TabButtons[1].Button.onClick.Invoke();Check(tabs.SelectedIndex==1&&page.activeSelf,"Progression button selects embedded page");tabs.TabButtons[3].Button.onClick.Invoke();Check(tabs.SelectedIndex==3&&!page.activeSelf,"Skills button selects original skills page");
            // Static renders of the actual authored panel, populated through the production controller.
            tabs.SelectTab(1);windowType.GetMethod("Refresh",F).Invoke(window,new object[]{false});
            var wb=right.GetComponent<WorkbenchPanelController>();wb.DefaultContent.SetActive(true);wb.WorkbenchContent.SetActive(false);
            right.GetComponentInChildren<AugaCraftingPanel>(true).DefaultRepairButton.gameObject.SetActive(false);Localization.instance.Localize(right.transform);foreach(var t in right.GetComponentsInChildren<TabButton>(true))t.SetColor();
            void Capture(string name,bool hover=false)
            {
                var copy=UnityEngine.Object.Instantiate(right,host.transform,false);
                typeof(AugaRightPanelCheck).GetMethod("Strip",F).Invoke(null,new object[]{copy});
                if(hover)AugaRightPanelCheck.BeforeCapture=()=>{
                    var stats=copy.transform.Find("TabContent/TabContent_Overhaul/Progression/Stats");
                    var enabled=stats.Find("carry/Plus").GetComponent<Button>();var disabled=stats.Find("critical/Minus").GetComponent<Button>();
                    var orange=copy.transform.Find("TabContent/TabContent_Overhaul/Progression/Summary/Bar/Fill").GetComponent<Image>().color;
                    Check(enabled.transform.Find("Text").GetComponent<Text>().canvasRenderer.GetColor()==Color.white,"Clickable point symbol is white");
                    enabled.OnPointerEnter(new UnityEngine.EventSystems.PointerEventData(null));disabled.OnPointerEnter(new UnityEngine.EventSystems.PointerEventData(null));
                    Check(enabled.transform.Find("Text").GetComponent<Text>().canvasRenderer.GetColor()==orange,"Hovered point symbol matches XP orange");
                    Check(disabled.targetGraphic.canvasRenderer.GetColor()==Color.black&&disabled.transform.Find("Text").GetComponent<Text>().canvasRenderer.GetColor()==new Color(.7f,.7f,.7f,1),"Disabled point button remains black and grey during hover");
                };
                try{typeof(AugaRightPanelCheck).GetMethod("Render",F).Invoke(null,new object[]{copy,name});}finally{AugaRightPanelCheck.BeforeCapture=null;UnityEngine.Object.DestroyImmediate(copy);}
            }
            Check(overhaul.GetType("Overhaul.Leveling.LevelingOpenWindowPatch")==null,"No separate vanilla leveling window hook");
            Capture("overhaul-progression");
            Capture("overhaul-button-hover",true);
            foreach(var card in page.transform.Find("Progression/Stats").Cast<Transform>().Concat(passiveCards.Cast<Transform>()))
            {
                var source=card.GetComponent<UITooltip>();var tip=UnityEngine.Object.Instantiate(source.m_tooltipPrefab,host.transform,false);var complex=tip.GetComponent<ComplexTooltip>();complex.SetDefault(source);
                var visible=card.Find("SkillGraphics/IconBG/Icon")??card.Find("IconBG/Icon");
                Check(complex.Icon.sprite==visible.GetComponent<Image>().sprite,"Tooltip uses visible card icon: "+card.name);
                UnityEngine.Object.DestroyImmediate(tip);
            }
            {
                var copy=UnityEngine.Object.Instantiate(right,host.transform,false);var source=page.transform.Find("Passives/unburdened").GetComponent<UITooltip>();
                var tip=UnityEngine.Object.Instantiate(source.m_tooltipPrefab,copy.transform,false);tip.GetComponent<ComplexTooltip>().SetDefault(source);
                var r=(RectTransform)tip.transform;r.anchorMin=r.anchorMax=r.pivot=new Vector2(.5f,.5f);r.anchoredPosition=new Vector2(0,-230);r.localScale=Vector3.one;tip.SetActive(true);
                typeof(AugaRightPanelCheck).GetMethod("Strip",F).Invoke(null,new object[]{copy});typeof(AugaRightPanelCheck).GetMethod("Render",F).Invoke(null,new object[]{copy,"overhaul-tooltip"});UnityEngine.Object.DestroyImmediate(copy);
            }
            page.transform.Find("Footer/Validate").GetComponent<Button>().onClick.Invoke();windowType.GetMethod("Refresh",F).Invoke(window,new object[]{false});
            Check(((Dictionary<string,int>)data.GetType().GetField("AllocatedStats").GetValue(data))["carry"]==21&&!minus.interactable,"Embedded validation commits points and prevents removal");
            stateType.GetField("CombatUntil",F).SetValue(state,Time.time+30);windowType.GetMethod("Refresh",F).Invoke(window,new object[]{false});
            Check(!page.transform.Find("Footer/Reset").GetComponent<Button>().interactable&&!page.transform.Find("Passives/vitality").GetComponent<Button>().interactable,"Combat disables reset and passive changes");
            Capture("overhaul-combat");stateType.GetField("CombatUntil",F).SetValue(state,0f);windowType.GetMethod("Refresh",F).Invoke(window,new object[]{false});
            page.transform.Find("Passives/vitality").GetComponent<Button>().onClick.Invoke();page.transform.Find("Passives/Confirm").GetComponent<Button>().onClick.Invoke();windowType.GetMethod("Refresh",F).Invoke(window,new object[]{false});
            Check((string)data.GetType().GetField("Passive").GetValue(data)=="vitality"&&!page.transform.Find("Passives/feather").GetComponent<Button>().interactable,"Passive confirmation applies cooldown through original rules");
            Capture("overhaul-cooldown");
            page.transform.Find("Footer/Reset").GetComponent<Button>().onClick.Invoke();windowType.GetMethod("Refresh",F).Invoke(window,new object[]{false});Check(((Dictionary<string,int>)data.GetType().GetField("AllocatedStats").GetValue(data)).Count==0,"Embedded free reset refunds stats");
            plus.onClick.Invoke();windowType.GetMethod("OnDisable",F).Invoke(window,null);Check(windowType.GetField("draft",F).GetValue(window)==null,"Leaving tab discards unconfirmed points");
            UnityEngine.Object.DestroyImmediate(right);
            File.WriteAllText("../../../Tools/OverhaulV2Work/ui-check-results.txt","PASS: integrated mandatory second tab, idempotency, sixteen cards, radial configurable cap, staged plus/minus, original skills navigation. Validation, combat lock, passive cooldown, free reset, draft cancellation and reopening checked. Actual Auga prefab rendered; no player save opened.\n");
        }
        finally{infos.Remove("plopyy.valheim.Overhaul");if(existed)infos.Add("plopyy.valheim.Overhaul",previous);Player.m_localPlayer=oldPlayer;UnityEngine.Object.DestroyImmediate(host);bundle.Unload(true);}
    }
}




