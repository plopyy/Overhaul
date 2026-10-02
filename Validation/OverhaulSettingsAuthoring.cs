using System.IO;
using AugaUnity;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
public static class OverhaulSettingsAuthoring
{
 public static void Run()
 {
  const string settingsPath="Assets/Prefabs/AugaSettings.prefab";
  var root=PrefabUtility.LoadPrefabContents(settingsPath);
  try
  {
   var page=root.GetComponentInChildren<AugaModsSettings>(true);
   var content=page.Notice.transform.parent;content.name="Overhaul";
   if(page.TrashToggle)Object.DestroyImmediate(page.TrashToggle.gameObject);page.TrashToggle=null;
   for(int i=0;i<page.Displays.Length;i++){
    var row=(RectTransform)page.Displays[i].transform;
    row.anchoredPosition=new Vector2(row.anchoredPosition.x,-48*i);
   }
   var notice=(RectTransform)page.Notice.transform;notice.anchoredPosition=new Vector2(notice.anchoredPosition.x,-48*page.Displays.Length-12);
   if(page.AugaTab){Object.DestroyImmediate(page.AugaTab.gameObject);page.AugaTab=null;}
   if(page.AugaPage){Object.DestroyImmediate(page.AugaPage);page.AugaPage=null;}
   page.EqsTab.name="Overhaul";
   var tab=(RectTransform)page.EqsTab.transform;tab.anchoredPosition=new Vector2(tab.anchoredPosition.x,0);
   foreach(var text in page.EqsTab.GetComponentsInChildren<TMP_Text>(true))text.text="$overhaul_mods_settings";
   page.EqsTab.onClick=new Button.ButtonClickedEvent();UnityEventTools.AddPersistentListener(page.EqsTab.onClick,page.SelectEqs);
   page.EqsTab.transform.Find("Selected").gameObject.SetActive(true);content.gameObject.SetActive(true);
   PrefabUtility.SaveAsPrefabAsset(root,settingsPath);
  }
  finally{PrefabUtility.UnloadPrefabContents(root);}
  const string inventoryPath="Assets/Prefabs/Inventory_screen.prefab";
  root=PrefabUtility.LoadPrefabContents(inventoryPath);
  try
  {
   var panel=root.transform.Find("root/Player/AugaEquipment");
   // Last equipment row is at -235; successive rows are exactly 70 px lower.
   ((RectTransform)panel.Find("AmmoSlots")).anchoredPosition=new Vector2(0,-305);
   ((RectTransform)panel.Find("QuickSlots")).anchoredPosition=new Vector2(0,-375);
   ((RectTransform)panel).SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,432);
   PrefabUtility.SaveAsPrefabAsset(root,inventoryPath);
  }
  finally{PrefabUtility.UnloadPrefabContents(root);}
  AugaCompatibilityBundleBuild.Run();
  File.Copy("AssetBundles/augaassets","../../../Overhaul/AugaIntegration/Assets/augaassets",true);
 }
}
