using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using TMPro;
public static class AmmoEquipmentPreview
{
 static void Strip(GameObject root) {
  foreach(var a in root.GetComponentsInChildren<Animator>(true))UnityEngine.Object.DestroyImmediate(a);
  foreach(var s in root.GetComponentsInChildren<MonoBehaviour>(true).OrderBy(s=>s is AugaUnity.ItemTooltip?1:0))if(s&&!(s is UnityEngine.EventSystems.UIBehaviour)&&!(s is GuiBar)&&!(s is AugaUnity.HorizontalDividerFitter)&&!(s is AugaUnity.ColorButtonTextValues))UnityEngine.Object.DestroyImmediate(s);
 }
 public static void Run(){Render(false);Render(true);Render(true,0.25f);}
 public static void Containers(){Render(true,-1,10);Render(true,-1,32);}
 static int privacyState;
 public static void Privacy(){try{for(privacyState=1;privacyState<=3;privacyState++)Render(true,-1,32);}finally{privacyState=0;}}
 static bool quickSlotsPreview;
 public static void QuickSlots(){quickSlotsPreview=true;try{Render(false);}finally{quickSlotsPreview=false;}}
 static bool hiddenActionsPreview;
 public static void HiddenActions(){hiddenActionsPreview=true;try{Render(true,-1,32);}finally{hiddenActionsPreview=false;}}
 static bool equipmentPreview; public static void Equipment(){equipmentPreview=true;Render(true);equipmentPreview=false;}
 static bool cosmeticPreview; public static void Cosmetics(){equipmentPreview=true;cosmeticPreview=true;try{Render(true);}finally{equipmentPreview=false;cosmeticPreview=false;}}
 static bool trashPreview; public static void Trash(){trashPreview=true;Render(true);trashPreview=false;}
 static void Render(bool opened,float sample=-1,int chestSlots=0) {
  AppDomain.CurrentDomain.AssemblyResolve+=(s,a)=>{var p=Path.GetFullPath("../../../Libs/"+new AssemblyName(a.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
  var bundle=AssetBundle.LoadFromFile("AssetBundles/augaassets");
  var inventory=bundle.LoadAsset<GameObject>("Inventory_screen");var hud=bundle.LoadAsset<GameObject>("HUD");
  var host=new GameObject("Preview inactive");host.SetActive(false);
  var source=opened?inventory.transform.Find("root/Player"):hud.transform.Find("hudroot/HotKeyBar");
  var obj=UnityEngine.Object.Instantiate(source.gameObject,host.transform,false);
  var itemPrefab=opened?obj.GetComponentInChildren<InventoryGrid>(true).m_elementPrefab:obj.GetComponent<HotkeyBar>().m_elementPrefab;
  var top=opened?obj.transform.Find("PlayerGrid/Top"):obj.transform;
  var main=opened?obj.transform.Find("PlayerGrid/Main/Grid"):null;
  for(int i=0;i<(opened?32:8);i++) {
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
   var art=AssetBundle.GetAllLoadedAssetBundles().FirstOrDefault(b=>b.name=="eaqs")??AssetBundle.LoadFromFile("../../../Overhaul/EquipmentIntegration/Assets/eaqs");
   foreach(Transform child in panel)if(child.name!="Background"&&child.name!="Tabs")child.gameObject.SetActive(child.name=="CosmeticPage"?cosmeticPreview:!cosmeticPreview);
   foreach(var text in panel.GetComponentsInChildren<TMP_Text>(true)){if(text.text=="$overhaul_equipment_tab")text.text="Équipement";if(text.text=="$overhaul_cosmetic_tab")text.text="Cosmétique";if(text.text=="$overhaul_cosmetic_hint")text.text="Apparence uniquement\nAucun bonus d’équipement";}
   panel.Find("Tabs/Equipment").GetComponent<Button>().interactable=cosmeticPreview;panel.Find("Tabs/Cosmetic").GetComponent<Button>().interactable=!cosmeticPreview;
   panel.Find("Tabs/Equipment/Selected").gameObject.SetActive(!cosmeticPreview);panel.Find("Tabs/Cosmetic/Selected").gameObject.SetActive(cosmeticPreview);
   var body=panel.Find(cosmeticPreview?"CosmeticPage/Paperdoll":"Paperdoll").GetComponent<Image>();body.sprite=art.LoadAsset<Sprite>("PaperdollMale");body.enabled=true;
   Debug.Log("PAPERDOLL "+body.sprite+" rect="+body.sprite.rect+" texture="+body.sprite.texture+" active="+body.gameObject.activeSelf+" color="+body.color+" rectTransform="+body.rectTransform.rect);
   foreach(var img in art.LoadAsset<GameObject>("Paperdolls").GetComponentsInChildren<Image>(true))Debug.Log("EQS SOURCE "+img.name+" sprite="+img.sprite+" color="+img.color+" rect="+img.rectTransform.rect);

   for(int i=0;i<(cosmeticPreview?4:13);i++){
    var slot=cosmeticPreview?i:i<7?new[]{0,1,2,3,4,6,5}[i]:i<10?8:9;var anchor=cosmeticPreview?panel.Find("CosmeticPage/Slot"+i):i<7?panel.Find("Equipment"+slot):panel.Find(i<10?"QuickSlots":"AmmoSlots");var cell=UnityEngine.Object.Instantiate(itemPrefab,anchor,false);var r=(RectTransform)cell.transform;r.anchorMin=r.anchorMax=r.pivot=new Vector2(.5f,.5f);r.anchoredPosition=i<7?Vector2.zero:new Vector2(((i-7)%3-1)*70,0);
    foreach(var n in new[]{"amount","quality"})cell.transform.Find(n).GetComponent<TMP_Text>().text="";cell.transform.Find("binding").GetComponent<TMP_Text>().text=i<7||i>=10?"":new[]{"C","V","B"}[i-7];
    foreach(var n in new[]{"equiped","queued","selected","noteleport","foodicon","dropFocus","durability"})cell.transform.Find(n).gameObject.SetActive(false);cell.transform.Find("icon").GetComponent<Image>().enabled=false;
    if(slot>=0){
     var plugin=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));
     plugin.GetType("Auga.EquipmentPanelBridge").GetMethod("UpdateSlotHint",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{cell.GetComponent<InventoryElement>(),slot,false});
    }
    if(i==10){var plugin=Assembly.LoadFrom(Path.GetFullPath("../../../Packages/Overhaul/Overhaul.dll"));plugin.GetType("Auga.EquipmentPanelBridge").GetMethod("UpdateAmmoSelection",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{cell.GetComponent<InventoryElement>(),true});}
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
  int imageWidth=equipmentPreview?960:trashPreview||chestSlots>0?670:1440,height=equipmentPreview?540:trashPreview?420:900; var root=new GameObject("Canvas",typeof(Canvas));var cameraObj=new GameObject("Camera",typeof(Camera));var camera=cameraObj.GetComponent<Camera>();camera.orthographic=true;camera.orthographicSize=height/2f;camera.transform.position=new Vector3(0,0,-10);camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.08f,.085f,.09f);camera.cullingMask=1<<30;
  var canvas=root.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=camera;canvas.planeDistance=1;canvas.referencePixelsPerUnit=50;root.layer=30;
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
  camera.Render();RenderTexture.active=rt;var texture=new Texture2D(imageWidth,height,TextureFormat.RGB24,false);texture.ReadPixels(new Rect(0,0,imageWidth,height),0,0);texture.Apply();File.WriteAllBytes("../../../Tools/AugaWork/overhaul-spacing-"+(privacyState>0?"privacy-"+new[]{"","public","private","visitor"}[privacyState]:hiddenActionsPreview?"notches-without-buttons":cosmeticPreview?"cosmetics":equipmentPreview?"equipment":trashPreview?"trash":chestSlots>0?"chest-"+chestSlots:sample>=0?"opening":opened?"open":"closed")+".png",texture.EncodeToPNG());RenderTexture.active=null;
  UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(cameraObj);UnityEngine.Object.DestroyImmediate(texture);UnityEngine.Object.DestroyImmediate(rt);bundle.Unload(true);
  Debug.Log("QUICK INVENTORY PREVIEW: opened="+opened+", static empty slots");
 }
}
