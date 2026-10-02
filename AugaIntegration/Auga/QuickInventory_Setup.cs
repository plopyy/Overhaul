using System;
using HarmonyLib;
using UnityEngine;
using TMPro;
namespace Auga
{
 [UiModule(UiModule.QuickInventory)]
 internal static class QuickInventory_Setup
 {
  internal static void EnsureHotbar(Hud hud) {
    if(UiModules.Enabled(UiModule.Hud))return;
    var old=hud.transform.Find("hudroot/HotKeyBar");
    if(old.Find("EquipmentQuickSlots"))return;
    var obj=UnityEngine.Object.Instantiate(Auga.Assets.Hud.transform.Find("hudroot/HotKeyBar").gameObject,old.parent,false);obj.name="HotKeyBar";
    old.name="ReplacedHotKeyBar";old.gameObject.SetActive(false);UnityEngine.Object.Destroy(old.gameObject);
  }
  [HarmonyPatch(typeof(Hud), nameof(Hud.Awake))]
  private static class HotbarInstall {
   private static void Postfix(Hud __instance) {
    EnsureHotbar(__instance);
   }
  }
  [HarmonyPatch(typeof(HotkeyBar), "UpdateIcons")]
  private static class HotbarItems {
   private static void Postfix(HotkeyBar __instance) {
    if(__instance.name!="HotKeyBar")return;
    for(int i=0;i<__instance.m_elements.Count;i++) {
     var tooltip=__instance.m_elements[i].m_go.GetComponent<AugaUnity.ItemTooltip>();
     if(tooltip)tooltip.Item=__instance.m_items.Find(item=>item.m_gridPos.x==i);
    }
   }
  }
  [HarmonyPatch(typeof(Hud), "Update")]
  private static class Visibility {
   private static void Postfix(Hud __instance) {
    if(UiModules.Enabled(UiModule.Hud))return;
    var bar=__instance.transform.Find("hudroot/HotKeyBar");if(bar)bar.gameObject.SetActive(!InventoryGui.IsVisible());
   }
  }
  [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Awake))]
  private static class InventoryInstall {
   private static void Prefix(InventoryGui __instance) {
    if(UiModules.Enabled(UiModule.Inventory))return;
    AugaUnity.AddItemIconMaterial.IconMaterial=__instance.m_dragItemPrefab.transform.Find("icon").GetComponent<UnityEngine.UI.Image>().material;
    var old=__instance.m_player;
    var panel=UnityEngine.Object.Instantiate(Auga.Assets.InventoryScreen.transform.Find("root/Player").gameObject,old.parent,false);panel.name="Player";panel.transform.SetSiblingIndex(old.GetSiblingIndex());
    InstallContainer(__instance, Auga.Assets.InventoryScreen.transform.Find("root/Container").gameObject, old.parent);
    var nativeSplit=__instance.m_splitDialog;
    var split=UnityEngine.Object.Instantiate(Auga.Assets.InventoryScreen.transform.Find("root/SplitDialog").gameObject,nativeSplit.transform.parent,false);
    split.name="SplitDialog";split.transform.SetSiblingIndex(nativeSplit.transform.GetSiblingIndex());
    __instance.m_splitDialog=split.GetComponent<SplitDialog>();
    Localization.instance.Localize(split.transform);
    nativeSplit.gameObject.SetActive(false);UnityEngine.Object.Destroy(nativeSplit.gameObject);
    var oldGroup=old.GetComponent<UIGroupHandler>();var group=panel.GetComponent<UIGroupHandler>();
    for(int i=0;i<__instance.m_uiGroups.Length;i++)if(__instance.m_uiGroups[i]==oldGroup)__instance.m_uiGroups[i]=group;
    // Other native windows keep their references; the native Awake binds every grid callback.
    if(__instance.m_dropButton.transform.IsChildOf(old))__instance.m_dropButton.transform.SetParent(old.parent,true);
    __instance.m_player=(RectTransform)panel.transform;
    panel.transform.Find("StandardDivider").gameObject.SetActive(true);
    panel.transform.Find("TrashDivider").gameObject.SetActive(true);
    __instance.m_playerGrid=panel.transform.Find("PlayerGrid").GetComponent<InventoryGrid>();
    __instance.m_weight=panel.transform.Find("Weight/Text").GetComponent<TMP_Text>();__instance.m_armor=panel.transform.Find("Armor/Text").GetComponent<TMP_Text>();
    old.gameObject.SetActive(false);UnityEngine.Object.Destroy(old.gameObject);
   }
  }
  internal static void InstallContainer(InventoryGui gui, GameObject prefab, Transform parent) {
   var old=gui.m_container;
   var container=UnityEngine.Object.Instantiate(prefab,parent,false);container.name="Container";
   // Preserve the native drawing order even when the original chest was nested in Player.
   var nativeLayer=old.transform;
   while(nativeLayer.parent!=parent&&nativeLayer.parent)nativeLayer=nativeLayer.parent;
   if(nativeLayer.parent==parent)container.transform.SetSiblingIndex(nativeLayer.GetSiblingIndex()+1);
   var oldGroup=old.GetComponent<UIGroupHandler>();var group=container.GetComponent<UIGroupHandler>();
   for(int i=0;i<gui.m_uiGroups.Length;i++)if(gui.m_uiGroups[i]==oldGroup)gui.m_uiGroups[i]=group;
   gui.m_container=(RectTransform)container.transform;
   container.AddComponent<ContainerInventoryFollower>();
   gui.m_containerGrid=container.transform.Find("ContainerGrid").GetComponent<InventoryGrid>();
   gui.m_containerName=container.transform.Find("ContainerHeader/Name").GetComponent<TMP_Text>();
   gui.m_containerWeight=container.transform.Find("Weight/Text").GetComponent<TMP_Text>();
   gui.m_takeAllButton=container.transform.Find("TakeAll").GetComponent<UnityEngine.UI.Button>();
   gui.m_stackAllButton=container.transform.Find("StackAll").GetComponent<UnityEngine.UI.Button>();
   old.gameObject.SetActive(false);UnityEngine.Object.Destroy(old.gameObject);
  }
  [HarmonyPatch(typeof(InventoryGui), "Update")]
  private static class PanelAnimation {
   private static void Postfix(InventoryGui __instance) {
    if(UiModules.Enabled(UiModule.Inventory))return;
    var animator=__instance.m_player.GetComponent<Animator>();
    if(animator)animator.SetBool("visible",__instance.GetComponent<Animator>().GetBool("visible"));
   }
  }
  [HarmonyPatch(typeof(InventoryGrid), "UpdateGui")]
  private static class Rows {
   private static void Postfix(InventoryGrid __instance) {
    if(!InventoryGui.instance||UiModules.Enabled(UiModule.Inventory))return;
    if(__instance!=InventoryGui.instance.m_playerGrid&&__instance!=InventoryGui.instance.m_containerGrid)return;
    var top=__instance.transform.Find("Top");var main=__instance.transform.Find("Main/Grid");
    foreach(var element in __instance.m_elements){if(EquipmentPanelBridge.OwnsRow(__instance,element.Position.y))continue;var tooltip=element.GetComponent<AugaUnity.ItemTooltip>();if(tooltip)tooltip.Item=element.m_used?__instance.m_inventory.GetItemAt(element.Position.x,element.Position.y):null;if(top&&main){var parent=element.Position.y==0?top:main;if(element.transform.parent!=parent)element.transform.SetParent(parent,false);}}
   }
  }
 }
 internal sealed class ContainerInventoryFollower : MonoBehaviour {
  private void LateUpdate() {
   var gui=InventoryGui.instance;
   if(!gui || !gui.m_player || !InventoryGui.IsVisible())return;
   global::EquipmentAndQuickSlots.EquipmentPanel.FollowInventoryBackground(
       gui.m_player.Find("Bkg") as RectTransform, (RectTransform)transform);
  }
 }
}
