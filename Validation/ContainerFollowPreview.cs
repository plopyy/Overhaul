using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using TMPro;
public static class ContainerFollowPreview
{
 static void Strip(GameObject root) {
  foreach(var a in root.GetComponentsInChildren<Animator>(true))UnityEngine.Object.DestroyImmediate(a);
  foreach(var s in root.GetComponentsInChildren<MonoBehaviour>(true).OrderBy(s=>s is AugaUnity.ItemTooltip?1:0))if(s&&!(s is UnityEngine.EventSystems.UIBehaviour)&&!(s is GuiBar)&&!(s is AugaUnity.HorizontalDividerFitter)&&!(s is AugaUnity.ColorButtonTextValues))UnityEngine.Object.DestroyImmediate(s);
 }
  static int extraRows;
 public static void Run(){AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var p=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};for(extraRows=0;extraRows<=2;extraRows++)Render(true,-1,32);File.WriteAllText("../../../Tools/AugaWork/container-follow-check.txt","PASS 0/1/2 extra rows: frame gap, lower screen edge, unchanged scale and repeated placement at 1080 high.");}
 public static void Containers(){Render(true,-1,10);Render(true,-1,32);}
 static int privacyState;
 public static void Privacy(){try{for(privacyState=1;privacyState<=3;privacyState++)Render(true,-1,32);}finally{privacyState=0;}}
 static bool quickSlotsPreview;
 public static void QuickSlots(){quickSlotsPreview=true;try{Render(false);}finally{quickSlotsPreview=false;}}
 static bool hiddenActionsPreview;
 public static void HiddenActions(){hiddenActionsPreview=true;try{Render(true,-1,32);}finally{hiddenActionsPreview=false;}}
 static bool equipmentPreview; public static void Equipment(){equipmentPreview=true;Render(true);equipmentPreview=false;}
 static bool trashPreview; public static void Trash(){trashPreview=true;Render(true);trashPreview=false;}
 static void Render(bool opened,float sample=-1,int chestSlots=0) {
  var bundle=AssetBundle.LoadFromFile("AssetBundles/augaassets");
  var inventory=bundle.LoadAsset<GameObject>("Inventory_screen");var hud=bundle.LoadAsset<GameObject>("HUD");
  var host=new GameObject("Preview inactive");host.SetActive(false);
  var source=opened?inventory.transform.Find("root/Player"):hud.transform.Find("hudroot/HotKeyBar");
  var obj=UnityEngine.Object.Instantiate(source.gameObject,host.transform,false);
  var itemPrefab=opened?obj.GetComponentInChildren<InventoryGrid>(true).m_elementPrefab:obj.GetComponent<HotkeyBar>().m_elementPrefab;
  var top=opened?obj.transform.Find("PlayerGrid/Top"):obj.transform;
  var main=opened?obj.transform.Find("PlayerGrid/Main/Grid"):null;
  for(int i=0;i<(opened?(4+extraRows)*8:8);i++) {
   var element=UnityEngine.Object.Instantiate(itemPrefab,i<8?top:main,false);
   if(!opened)element.transform.localPosition=new Vector3(i*obj.GetComponent<HotkeyBar>().m_elementSpace,0,0);
   element.transform.Find("binding").GetComponent<TMP_Text>().text=i<8?(i+1).ToString():"";
   foreach(var name in new[]{"amount","quality"})if(element.transform.Find(name))element.transform.Find(name).GetComponent<TMP_Text>().text="";
   foreach(var name in new[]{"equiped","queued","selected","noteleport","foodicon","dropFocus"})if(element.transform.Find(name))element.transform.Find(name).gameObject.SetActive(false);
   element.transform.Find("icon").GetComponent<Image>().enabled=false;
   element.transform.Find("durability").gameObject.SetActive(false);
   foreach(var q in element.GetComponentsInChildren<AugaUnity.QualityIndicator>(true))q.SetEnabled(false);
   foreach(var f in element.GetComponentsInChildren<AugaUnity.FoodIndicator>(true))f.Image.enabled=false;
  }
  if(opened){foreach(var label in obj.transform.Find("TrashDivider").GetComponentsInChildren<TMP_Text>(true))if(label.text=="$auga_trash")label.text="CORBEILLE";obj.transform.Find("Weight/Text").GetComponent<TMP_Text>().text="120 / 300";obj.transform.Find("Armor/Text").GetComponent<TMP_Text>().text="43";}
    if(equipmentPreview){
   var panel=obj.transform.Find("AugaEquipment");panel.gameObject.SetActive(true);
   for(int i=0;i<9;i++){
    var anchor=i<6?panel.Find("Equipment"+i):panel.Find("QuickSlots");var cell=UnityEngine.Object.Instantiate(itemPrefab,anchor,false);var r=(RectTransform)cell.transform;r.anchorMin=r.anchorMax=r.pivot=new Vector2(.5f,.5f);r.anchoredPosition=i<6?Vector2.zero:new Vector2((i-7)*70,0);
    foreach(var n in new[]{"amount","quality"})cell.transform.Find(n).GetComponent<TMP_Text>().text="";cell.transform.Find("binding").GetComponent<TMP_Text>().text=i<6?"":new[]{"Z","X","C"}[i-6];
    foreach(var n in new[]{"equiped","queued","selected","noteleport","foodicon","dropFocus","durability"})cell.transform.Find(n).gameObject.SetActive(false);cell.transform.Find("icon").GetComponent<Image>().enabled=false;
    foreach(var q in cell.GetComponentsInChildren<AugaUnity.QualityIndicator>(true))q.SetEnabled(false);foreach(var f in cell.GetComponentsInChildren<AugaUnity.FoodIndicator>(true))f.Image.enabled=false;
   }
  }
  if(quickSlotsPreview){
   var anchor=obj.transform.Find("EquipmentQuickSlots");
   var template=top.Cast<Transform>().First(t=>t.Find("binding"));
   for(int i=0;i<3;i++){var cell=UnityEngine.Object.Instantiate(template.gameObject,anchor,false);cell.name="QuickSlot"+i;cell.transform.localPosition=new Vector3(i*obj.GetComponent<HotkeyBar>().m_elementSpace,0,0);cell.transform.Find("binding").GetComponent<TMP_Text>().text=new[]{"Z","X","C"}[i];}
  }
  RuntimeAnimatorController motion=null;
  if(opened&&obj.GetComponent<Animator>())motion=obj.GetComponent<Animator>().runtimeAnimatorController;
  if(hiddenActionsPreview)obj.transform.Find("Sort").gameObject.SetActive(false);
  Strip(obj);
  GameObject chest=null;
  if(chestSlots>0){chest=UnityEngine.Object.Instantiate(inventory.transform.Find("root/Container").gameObject,host.transform,false);var grid=chest.GetComponentInChildren<InventoryGrid>(true);var content=grid.m_gridRoot;int width=chestSlots==10?5:8;
   for(int i=0;i<chestSlots;i++){var e=UnityEngine.Object.Instantiate(grid.m_elementPrefab,content,false);((RectTransform)e.transform).anchoredPosition=new Vector2(i%width*grid.m_elementSpace,-(i/width)*grid.m_elementSpace);foreach(var name in new[]{"binding","amount","quality"})e.transform.Find(name).GetComponent<TMP_Text>().text="";foreach(var name in new[]{"equiped","queued","selected","noteleport","foodicon","dropFocus","durability"})e.transform.Find(name).gameObject.SetActive(false);e.transform.Find("icon").GetComponent<Image>().enabled=false;foreach(var q in e.GetComponentsInChildren<AugaUnity.QualityIndicator>(true))q.SetEnabled(false);foreach(var f in e.GetComponentsInChildren<AugaUnity.FoodIndicator>(true))f.Image.enabled=false;}
   content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,(chestSlots/width)*grid.m_elementSpace);
   chest.transform.Find("ContainerHeader/Name").GetComponent<TMP_Text>().text="COFFRE";chest.transform.Find("Weight/Text").GetComponent<TMP_Text>().text="204";
   if(hiddenActionsPreview)foreach(var name in new[]{"Sort","StackAll","TakeAll"})chest.transform.Find(name).gameObject.SetActive(false);
   if(privacyState>0){
    var privacy=chest.transform.Find("Privacy");privacy.gameObject.SetActive(privacyState!=3);
    privacy.Find("Locked").gameObject.SetActive(privacyState==2);privacy.Find("Public").gameObject.SetActive(privacyState!=2);
    foreach(var name in new[]{"Bkg","selected_frame","Darken"}){
     var layer=chest.transform.Find(name);var r=(RectTransform)layer;
     layer.GetComponent<AugaUnity.AugaPanelNotches>().CentersFromTopLeft=new[]{"Sort","StackAll","TakeAll","Privacy"}.Select(n=>chest.transform.Find(n)).Where(t=>t.gameObject.activeSelf).Select(t=>{var p=layer.InverseTransformPoint(t.position);return new Vector2(p.x-r.rect.xMin,r.rect.yMax-p.y);}).ToArray();
    }
   }
   Strip(chest);
  }
  int imageWidth=equipmentPreview?960:trashPreview||chestSlots>0?670:1440,height=equipmentPreview||trashPreview?420:1080; var root=new GameObject("Canvas",typeof(Canvas));var cameraObj=new GameObject("Camera",typeof(Camera));var camera=cameraObj.GetComponent<Camera>();camera.orthographic=true;camera.orthographicSize=height/2f;camera.transform.position=new Vector3(0,0,-10);camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.08f,.085f,.09f);camera.cullingMask=1<<30;
  var canvas=root.GetComponent<Canvas>();canvas.renderMode=RenderMode.WorldSpace;((RectTransform)root.transform).sizeDelta=new Vector2(imageWidth,height);canvas.worldCamera=camera;canvas.planeDistance=1;canvas.referencePixelsPerUnit=50;root.layer=30;
  // Preview-only backdrop, behind the unmodified bundle instances, to verify real holes.
  if(hiddenActionsPreview)for(int y=0;y<height;y+=16)for(int x=0;x<imageWidth;x+=16){
   var tile=new GameObject("Backdrop",typeof(RectTransform),typeof(Image));tile.layer=30;tile.transform.SetParent(root.transform,false);
   var r=(RectTransform)tile.transform;r.anchorMin=r.anchorMax=r.pivot=new Vector2(0,1);r.anchoredPosition=new Vector2(x,-y);r.sizeDelta=new Vector2(16,16);
   var im=tile.GetComponent<Image>();im.color=((x/16+y/16)%2==0)?new Color(.22f,.42f,.50f):new Color(.65f,.72f,.70f);im.raycastTarget=false;
  }
  var rt=new RenderTexture(imageWidth,height,24);camera.targetTexture=rt;obj.transform.SetParent(root.transform,false);foreach(var t in obj.GetComponentsInChildren<Transform>(true))t.gameObject.layer=30;obj.SetActive(true);
  if(chest){chest.transform.SetParent(root.transform,false);foreach(var t in chest.GetComponentsInChildren<Transform>(true))t.gameObject.layer=30;chest.SetActive(true);}
  Canvas.ForceUpdateCanvases();LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)obj.transform);Canvas.ForceUpdateCanvases();foreach(var fitter in obj.GetComponentsInChildren<AugaUnity.HorizontalDividerFitter>(true))fitter.LateUpdate();
  if(sample>=0&&motion){var animator=obj.AddComponent<Animator>();animator.runtimeAnimatorController=motion;animator.Rebind();animator.Update(0);animator.Update(sample/3);Canvas.ForceUpdateCanvases();}
  if(chest){Canvas.ForceUpdateCanvases();foreach(var fitter in chest.GetComponentsInChildren<AugaUnity.HorizontalDividerFitter>(true))fitter.LateUpdate();}
    var bg=(RectTransform)obj.transform.Find("Bkg");bg.anchorMin=new Vector2(0,-extraRows/4f+.01f*Math.Max(extraRows-1,0));
  var plugin=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));var method=plugin.GetType("EquipmentAndQuickSlots.EquipmentPanel").GetMethod("FollowInventoryBackground",BindingFlags.Static|BindingFlags.NonPublic);
  var cr=(RectTransform)chest.transform;method.Invoke(null,new object[]{bg,cr});var position=cr.anchoredPosition;var scale=cr.localScale;method.Invoke(null,new object[]{bg,cr});if(Vector2.Distance(position,cr.anchoredPosition)>.01f||Vector3.Distance(scale,cr.localScale)>.001f)throw new Exception("Layout drift");
  var pc=new Vector3[4];var cc=new Vector3[4];var vc=new Vector3[4];bg.GetWorldCorners(pc);((RectTransform)chest.transform.Find("Bkg")).GetWorldCorners(cc);((RectTransform)root.transform).GetWorldCorners(vc);
  Debug.Log("GEOMETRY rows="+extraRows+" bagbottom="+pc[0]+" chesttop="+cc[1]+" chestbottom="+cc[0]+" viewport="+vc[0]+" scale="+scale+" rootrect="+((RectTransform)root.transform).rect); if(cc[1].y>pc[0].y-.01f||cc[0].y<vc[0].y)throw new Exception("Overlap or offscreen chest");if(Vector3.Distance(scale,Vector3.one)>.001)throw new Exception("Distorted scale");
  camera.Render();RenderTexture.active=rt;var texture=new Texture2D(imageWidth,height,TextureFormat.RGB24,false);texture.ReadPixels(new Rect(0,0,imageWidth,height),0,0);texture.Apply();File.WriteAllBytes("../../../Tools/AugaWork/container-follow-"+extraRows+"-"+(privacyState>0?"privacy-"+new[]{"","public","private","visitor"}[privacyState]:hiddenActionsPreview?"notches-without-buttons":equipmentPreview?"equipment":trashPreview?"trash":chestSlots>0?"chest-"+chestSlots:sample>=0?"opening":opened?"open":"closed")+".png",texture.EncodeToPNG());RenderTexture.active=null;
  UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(cameraObj);UnityEngine.Object.DestroyImmediate(texture);UnityEngine.Object.DestroyImmediate(rt);bundle.Unload(true);
  Debug.Log("QUICK INVENTORY PREVIEW: opened="+opened+", static empty slots");
 }
}




