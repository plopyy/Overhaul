using HarmonyLib;
using UnityEngine;
namespace Auga {
 [UiModule(UiModule.Construction)]
 [HarmonyPatch(typeof(Hud),nameof(Hud.Awake))]
 internal static class Construction_Setup {
  internal static void PreservePieceAuthorWindow(Hud hud) {
   // Valheim 1.0 keeps this crosshair dependency under BuildHud. It has no
   // replacement in AugaBuildHudBindings and must outlive the old panel.
   var author = hud.m_hoveredPieceAuthorWindow;
   if (author && hud.m_buildHud && author.transform.IsChildOf(hud.m_buildHud.transform))
    author.transform.SetParent(hud.m_buildHud.transform.parent, true);
  }
  [HarmonyPriority(Priority.Last)]
  private static void Postfix(Hud __instance) {
   if(!Auga.Assets.Construction || !__instance.m_buildUi)return;
   var old=__instance.m_buildUi;
   var root=Object.Instantiate(Auga.Assets.Construction);
   root.name="AugaConstruction";
   var ui=root.GetComponent<BuildUi>();

   if(!ui || !ui.m_favoritesDropdown || !ui.m_tagListScrollRect || !ui.m_pieceScrollRect) {
    Object.Destroy(root);Debug.LogWarning("Overhaul: invalid construction prefab, keeping native BuildUi.");return;
   }
   ui.m_majorMaterials=new System.Collections.Generic.List<ItemDrop>(old.m_majorMaterials);
   // Native UpdateRequirements encodes desaturation in the vertex color. The
   // matching game material must interpret it instead of tinting the icon yellow.
   ui.m_pieceButtonPrefab.GetComponent<BuildUiPieceButton>().m_icon.material =
    old.m_pieceButtonPrefab.GetComponent<BuildUiPieceButton>().m_icon.material;
   // Initialize outside hudroot: that parent can still be inactive while a character loads.
   root.SetActive(true);
   if(ui.m_recentPieceList==null || ui.m_favoritePieceList==null) {
    Object.Destroy(root);Debug.LogWarning("Overhaul: construction initialization incomplete, keeping native BuildUi.");return;
   }
   ui.m_favoritesDropdown.gameObject.SetActive(true);ui.m_favoritesDropdown.Close();
   ui.SubscribeEvents();
   ui.Close();root.transform.SetParent(__instance.transform.Find("hudroot"),false);
   __instance.m_buildUi=ui;
   var close=root.transform.Find("Controls/CloseButton").GetComponent<UIInputHandler>();
   close.m_onLeftClick+=__instance.OnClosePieceSelection;
   close.m_onRightClick+=__instance.OnClosePieceSelection;
   old.gameObject.SetActive(false);Object.Destroy(old.gameObject);
   // Preserve the authored Auga selected-piece panel and resource requirements.
   var panel=Object.Instantiate(Auga.Assets.Hud.transform.Find("hudroot/BuildHud").gameObject,__instance.m_buildHud.transform.parent,false);
   PreservePieceAuthorWindow(__instance);
   __instance.m_buildHud.SetActive(false);Object.Destroy(__instance.m_buildHud);
   panel.GetComponent<AugaUnity.AugaBuildHudBindings>().Bind(__instance);
   Localization.instance.Localize(root.transform);Localization.instance.Localize(panel.transform);
   // Awake/Start initialize the native controller before character favourites are loaded.
   ui.Close();
  }
  [HarmonyPatch(typeof(BuildUi), "Start")]
  private static class InitializedConstructionStart {
   private static bool Prefix(BuildUi __instance) { return __instance.gameObject.name != "AugaConstruction"; }
  } }
}
