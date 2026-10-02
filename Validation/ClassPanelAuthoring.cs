using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using AugaUnity;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Object=UnityEngine.Object;
public static class ClassPanelAuthoring
{
 static TMP_FontAsset font,titleFont;
 static readonly Color Gold=new Color(.91f,.71f,.32f),Pale=new Color(.84f,.8f,.72f);
 static RectTransform Rect(Transform p,string n,float x,float y,float w,float h){var r=new GameObject(n,typeof(RectTransform)).GetComponent<RectTransform>();r.SetParent(p,false);r.anchorMin=r.anchorMax=r.pivot=new Vector2(0,1);r.anchoredPosition=new Vector2(x,-y);r.sizeDelta=new Vector2(w,h);return r;}
 static TMP_Text Text(Transform p,string n,string value,float x,float y,float w,float h,int size=20,bool title=false){var r=Rect(p,n,x,y,w,h);var t=r.gameObject.AddComponent<TextMeshProUGUI>();t.font=title?titleFont:font;t.fontSize=size;t.text=value;t.color=Pale;t.raycastTarget=false;t.alignment=TextAlignmentOptions.TopLeft;return t;}
 static Image Image(Transform p,string n,float x,float y,float w,float h,Sprite sprite=null){var i=Rect(p,n,x,y,w,h).gameObject.AddComponent<Image>();i.sprite=sprite;i.raycastTarget=false;return i;}
 static GameObject Clone(string asset,Transform p,string n,float x,float y,float w,float h){var obj=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/"+asset+".prefab"),p,false);obj.name=n;var r=(RectTransform)obj.transform;r.anchorMin=r.anchorMax=r.pivot=new Vector2(0,1);r.anchoredPosition=new Vector2(x,-y);r.sizeDelta=new Vector2(w,h);return obj;}
 static Button Button(Transform p,string n,string text,float x,float y,float w,float h){var obj=Clone("ButtonFancy",p,n,x,y,w,h);var b=obj.GetComponent<Button>();b.onClick=new Button.ButtonClickedEvent();foreach(var t in obj.GetComponentsInChildren<TMP_Text>(true)){t.text=text;t.fontSize=21;}return b;}
 static void Divider(Transform p,string n,float x,float y,float w){var obj=Clone("DividerMedium",p,n,x,y,w,12);foreach(var g in obj.GetComponentsInChildren<Graphic>())g.raycastTarget=false;}
 static Sprite Icon(string name)=>AssetDatabase.LoadAssetAtPath<Sprite>("Assets/OverhaulAugaIcons/"+name+".png");
 public static void Run(){
  font=AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Fonts/SourceSansPro-Regular SDF.asset");titleFont=AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Fonts/Norsebold SDF.asset");
  var root=new GameObject("OverhaulClassPanel",typeof(RectTransform));var rt=(RectTransform)root.transform;rt.anchorMin=rt.anchorMax=rt.pivot=new Vector2(.5f,.5f);rt.sizeDelta=new Vector2(1440,900);
  var panel=Clone("AugaPanelBase",rt,"Frame",0,0,1440,900);
  var demo=root.AddComponent<ClassPreviewPanel>();
  // Original Auga panel illustrations, below all controls and labels.
  var levelArt=Image(rt,"LevelBackgroundArt",42,166,238,238,AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Sprites/PlayerPanelBGArt.png"));levelArt.preserveAspect=true;
  var treeArt=Image(rt,"TreeBackgroundArt",427,225,536,536,AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Sprites/SkillPanelBGArt.png"));treeArt.preserveAspect=true;treeArt.color=new Color(1,1,1,.7f);
  var detailArt=Image(rt,"DetailBackgroundArt",1112,160,280,254,AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Sprites/Crafting/CraftingBG.png"));detailArt.preserveAspect=true;detailArt.color=new Color(.22f,.19f,.15f,.8f);
  Image(rt,"HeaderIcon",39,29,80,80,Icon("shield_banded2")).preserveAspect=true;
  Text(rt,"Title","$class_demo_class",140,25,1100,54,42,true);
  Text(rt,"Subtitle","$class_demo_role",142,82,1100,25,18).color=Gold;
  Divider(rt,"HeaderDivider",34,130,1370);
  var header=rt.Find("HeaderDivider").GetComponent<HorizontalDividerFitter>();header.Content.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal,210);
  var template=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/BuildHud.prefab").transform.Find("DividerLarge/TabContainer/Tabs/Misc");
  var tab=Object.Instantiate(template.gameObject,header.Content,false);tab.name="SkillsTab";var tabRect=(RectTransform)tab.transform;tabRect.anchorMin=tabRect.anchorMax=tabRect.pivot=new Vector2(.5f,.5f);tabRect.anchoredPosition=Vector2.zero;tabRect.sizeDelta=new Vector2(210,34);
  foreach(var c in tab.GetComponentsInChildren<MonoBehaviour>(true).ToArray())if(c&&!(c is UnityEngine.EventSystems.UIBehaviour))Object.DestroyImmediate(c);
  foreach(var text in tab.GetComponentsInChildren<TMP_Text>(true)){text.text="$class_demo_tab";text.fontSize=22;text.alignment=TextAlignmentOptions.Center;}
  tab.GetComponent<Button>().onClick=new Button.ButtonClickedEvent();tab.transform.Find("Selected").gameObject.SetActive(true);
  Image(rt,"LeftDivider",307,180,1,625).color=new Color(.67f,.61f,.47f,.25f);
  Image(rt,"RightDivider",1082,180,1,625).color=new Color(.67f,.61f,.47f,.25f);
  Text(rt,"Level","6",40,193,240,177,156).alignment=TextAlignmentOptions.Center;
  Text(rt,"Experience","$class_demo_xp",42,381,238,24,18);
  Image(rt,"XpBackground",42,417,238,8).color=new Color(.08f,.075f,.06f);
  Image(rt,"XpFill",42,417,143,8).color=Gold;
  Text(rt,"ExperienceHint","$class_demo_xp_hint",42,446,238,100,19);
  Text(rt,"ClassChoice","$class_demo_single",42,568,238,96,18).color=Gold;
  Text(rt,"DemoNotice","$class_demo_notice",42,697,238,109,16);
  demo.Points=Text(rt,"Points","",737,177,315,26,20);demo.Points.alignment=TextAlignmentOptions.Right;demo.Points.color=Gold;
  var scrollHost=Rect(rt,"TreeScroll",330,216,730,571);demo.TreeScroll=scrollHost.gameObject.AddComponent<ScrollRect>();demo.TreeScroll.horizontal=true;demo.TreeScroll.vertical=false;demo.TreeScroll.movementType=ScrollRect.MovementType.Clamped;demo.TreeScroll.scrollSensitivity=40;
  var viewport=Rect(scrollHost,"Viewport",0,0,730,538);viewport.gameObject.AddComponent<RectMask2D>();var capture=viewport.gameObject.AddComponent<Image>();capture.color=Color.clear;capture.raycastTarget=true;
  var tree=Rect(viewport,"Tree",0,0,1180,538);demo.TreeScroll.viewport=viewport;demo.TreeScroll.content=tree;
  var bar=Clone("ScrollBar",scrollHost,"HorizontalScrollbar",0,555,730,12).GetComponent<Scrollbar>();bar.SetDirection(Scrollbar.Direction.LeftToRight,true);var br=(RectTransform)bar.transform;br.anchorMin=br.anchorMax=br.pivot=new Vector2(0,1);br.anchoredPosition=new Vector2(0,-555);br.sizeDelta=new Vector2(730,12);demo.TreeScroll.horizontalScrollbar=bar;demo.TreeScroll.horizontalScrollbarVisibility=ScrollRect.ScrollbarVisibility.AutoHide;demo.TreeScroll.horizontalNormalizedPosition=0;
  var positions=new[]{new Vector2(105,250),new Vector2(335,120),new Vector2(335,410),new Vector2(575,60),new Vector2(575,245),new Vector2(575,440),new Vector2(815,160),new Vector2(815,410),new Vector2(1055,285)};
  var icons=new[]{"shield_banded2","BeltStrength","SwordSilver","shield_banded2","MeadBaseHealthMinor","shield_ironbuckler","shield_banded2","Lightning","TrophyGhost"};
  var links=new List<Image>();var targets=new List<int>();
  var templateParents=new[]{new int[0],new[]{0},new[]{0},new[]{1},new[]{1},new[]{2},new[]{3,4},new[]{5},new[]{6,7}};
  for(int i=0;i<9;i++)foreach(var p in templateParents[i]){
   Vector2 from=positions[p],to=positions[i];from.x+=38;to.x-=38;
   float middle=(from.x+to.x)*.5f;var points=new[]{from,new Vector2(middle,from.y),new Vector2(middle,to.y),to};
   for(int segment=0;segment<3;segment++){
    var a=points[segment];var b=points[segment+1];var image=Image(tree,"Link"+p+"_"+i+"_"+segment,a.x,a.y,(b-a).magnitude,2);
    image.rectTransform.pivot=new Vector2(0,.5f);image.rectTransform.localRotation=Quaternion.Euler(0,0,-Mathf.Atan2(b.y-a.y,b.x-a.x)*Mathf.Rad2Deg);image.color=Gold;links.Add(image);targets.Add(i);
   }
  }
  demo.Links=links.ToArray();demo.LinkTargets=targets.ToArray();demo.Nodes=new Button[9];demo.States=new TMP_Text[9];demo.Icons=new Image[9];demo.LearnedFrames=new Image[9];
  var inventory=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Inventory_screen.prefab");var donor=inventory.GetComponentInChildren<InventoryGrid>(true).m_elementPrefab;
  for(int i=0;i<9;i++){
   var pos=positions[i];var cell=Object.Instantiate(donor,tree,false);cell.name="Skill"+i;var cr=(RectTransform)cell.transform;cr.anchorMin=cr.anchorMax=new Vector2(0,1);cr.pivot=new Vector2(.5f,.5f);cr.anchoredPosition=new Vector2(pos.x,-pos.y);cr.sizeDelta=new Vector2(66,66);
   foreach(var component in cell.GetComponentsInChildren<MonoBehaviour>(true).OrderBy(c=>c is ItemTooltip?1:0).ToArray())if(component&&!(component is UnityEngine.EventSystems.UIBehaviour))Object.DestroyImmediate(component);
   foreach(Transform child in cell.transform)child.gameObject.SetActive(child.name=="icon");
   var icon=cell.transform.Find("icon").GetComponent<Image>();icon.sprite=Icon(icons[i]);icon.enabled=true;icon.preserveAspect=true;
   demo.Nodes[i]=cell.GetComponent<Button>();demo.Nodes[i].onClick=new Button.ButtonClickedEvent();demo.Icons[i]=icon;
   var learned=Object.Instantiate(donor.transform.Find("selected").gameObject,cell.transform,false);learned.name="LearnedFrame";demo.LearnedFrames[i]=learned.GetComponent<Image>();demo.LearnedFrames[i].raycastTarget=false;
   Text(tree,"Name"+i,"$class_demo_name"+i,pos.x-96,pos.y+38,192,25,18).alignment=TextAlignmentOptions.Center;
   demo.States[i]=Text(tree,"State"+i,"",pos.x-85,pos.y+63,170,22,15);demo.States[i].alignment=TextAlignmentOptions.Center;
  }
  var selected=Object.Instantiate(donor.transform.Find("selected").gameObject,demo.Nodes[4].transform,false);selected.name="ClassSelection";selected.SetActive(true);demo.Selection=selected.GetComponent<Image>();demo.Selection.color=Gold;demo.Selection.raycastTarget=false;
  demo.DetailIcon=Image(rt,"DetailIcon",1202,207,94,94,Icon("MeadBaseHealthMinor"));demo.DetailIcon.preserveAspect=true;
  demo.DetailTitle=Text(rt,"DetailTitle","",1111,329,282,55,29,true);
  demo.Status=Text(rt,"DetailStatus","",1112,387,280,27,18);demo.Status.color=Gold;
  Clone("DividerSmall",rt,"DetailDivider",1112,426,282,12);
  demo.DetailBody=Text(rt,"DetailBody","",1112,462,280,107,20);
  demo.Requirements=Text(rt,"Requirements","",1112,586,280,147,18);
  demo.Learn=Button(rt,"Learn","",1113,750,280,44);demo.LearnLabel=demo.Learn.GetComponentInChildren<TMP_Text>(true);
  Clone("DividerSmall",rt,"FooterDivider",34,817,1370,12);
  demo.Close=Button(rt,"Close","$menu_close",620,843,200,39);
  demo.Reset=Button(rt,"Reset","$class_demo_reset",1103,843,300,39);
  demo.PrepareTemplates();
  PrefabUtility.SaveAsPrefabAsset(root,"Assets/Prefabs/OverhaulClassPanel.prefab");Object.DestroyImmediate(root);AssetImporter.GetAtPath("Assets/Prefabs/OverhaulClassPanel.prefab").assetBundleName="augaassets";
  Settings();AssetDatabase.SaveAssets();AugaCompatibilityBundleBuild.Run();File.Copy("AssetBundles/augaassets","../../../Overhaul/AugaIntegration/Assets/augaassets",true);
 }
 static void Settings(){const string path="Assets/Prefabs/AugaSettings.prefab";var root=PrefabUtility.LoadPrefabContents(path);try{
  var page=root.GetComponentInChildren<AugaModsSettings>(true);var parent=page.Notice.transform.parent;var old=parent.Find("ClassWindow");
  var row=old?old.GetComponent<AugaBindingDisplay>():Object.Instantiate(page.Displays[3],parent,false);row.name="ClassWindow";
  var r=(RectTransform)row.transform;r.anchoredPosition=new Vector2(r.anchoredPosition.x,-240);var button=row.GetComponentInChildren<Button>(true);button.onClick=new Button.ButtonClickedEvent();UnityEventTools.AddIntPersistentListener(button.onClick,page.BeginBinding,4);
  foreach(var t in row.GetComponentsInChildren<TMP_Text>(true))if(!t.transform.IsChildOf(button.transform))t.text="$class_demo_binding";
  var tip=row.GetComponent<UITooltip>();if(tip){tip.m_topic="$class_demo_binding";tip.m_text="$class_demo_binding_tip";}
  page.BindButtons=page.BindButtons.Take(4).Concat(new[]{button}).ToArray();page.Displays=page.Displays.Take(4).Concat(new[]{row}).ToArray();
  var oldMove=parent.Find("MoveObject");var move=oldMove?oldMove.GetComponent<AugaBindingDisplay>():Object.Instantiate(row,parent,false);move.name="MoveObject";
  var mr=(RectTransform)move.transform;mr.anchoredPosition=new Vector2(mr.anchoredPosition.x,-300);
  var moveButton=move.GetComponentInChildren<Button>(true);moveButton.onClick=new Button.ButtonClickedEvent();UnityEventTools.AddIntPersistentListener(moveButton.onClick,page.BeginBinding,5);
  foreach(var t in move.GetComponentsInChildren<TMP_Text>(true))if(!t.transform.IsChildOf(moveButton.transform))t.text="$overhaul_move_binding";
  var moveTip=move.GetComponent<UITooltip>();if(moveTip){moveTip.m_topic="$overhaul_move_binding";moveTip.m_text="$overhaul_move_binding_tip";}
  page.BindButtons=page.BindButtons.Concat(new[]{moveButton}).ToArray();page.Displays=page.Displays.Concat(new[]{move}).ToArray();
  if(page.TrashToggle)Object.DestroyImmediate(page.TrashToggle.gameObject);page.TrashToggle=null;
  for(int i=0;i<page.Displays.Length;i++){var binding=(RectTransform)page.Displays[i].transform;binding.anchoredPosition=new Vector2(binding.anchoredPosition.x,-48*i);}
  var notice=(RectTransform)page.Notice.transform;notice.anchoredPosition=new Vector2(notice.anchoredPosition.x,-300);
  PrefabUtility.SaveAsPrefabAsset(root,path);
 }finally{PrefabUtility.UnloadPrefabContents(root);}}
}
