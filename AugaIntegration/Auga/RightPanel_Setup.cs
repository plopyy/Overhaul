using AugaUnity;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Auga
{
    [UiModule(UiModule.RightPanel)]
    internal static class RightPanel_Setup
    {
        static AugaCraftingPanel Panel(InventoryGui gui) => gui && gui.m_crafting ? gui.m_crafting.GetComponentInChildren<AugaCraftingPanel>(true) : null;

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Awake))]
        internal static class Install
        {
            static void Prefix(InventoryGui __instance, out GameObject __state)
            {
                __state = null;
                if (UiModules.Enabled(UiModule.Inventory)) return;
                var gui = __instance;
                var old = gui.m_crafting;
                var staging = new GameObject("AugaRightPanelLoading", typeof(RectTransform));
                staging.SetActive(false);
                staging.transform.SetParent(old.parent, false);
                __state = staging;
                var right = Object.Instantiate(Auga.Assets.InventoryScreen.transform.Find("root/RightPanel"), staging.transform, false);
                right.name = "RightPanel";
                var panel = right.GetComponentInChildren<AugaCraftingPanel>(true);
                var group = right.GetComponent<UIGroupHandler>();
                for (int i = 0; i < gui.m_uiGroups.Length; i++)
                    if (gui.m_uiGroups[i] && gui.m_uiGroups[i].transform == old) gui.m_uiGroups[i] = group;
                // Native fields not represented in Auga (upgrade preview, touch hints) retain
                // their inactive backing objects; no native component may reference destroyed UI.
                old.gameObject.SetActive(false);
                gui.m_crafting = (RectTransform)right;
                gui.m_playerName = right.Find("DefaultContent/TitleContainer/PlayerPanelTitle").GetComponent<TMP_Text>();
                gui.m_pvp = right.Find("TabContent/TabContent_PVP/Dummy/PVPToggle").GetComponent<Toggle>();
                gui.m_recipeElementPrefab = panel.RecipeItemPrefab;
                gui.m_recipeListRoot = panel.RecipeList;
                gui.m_recipeListScroll = panel.RecipeListScrollbar;
                gui.m_recipeEnsureVisible = panel.RecipeListEnsureVisible;
                gui.m_recipeListSpace = 34;
                gui.m_craftingStationName = panel.WorkbenchName;
                gui.m_craftingStationIcon = panel.WorkbenchIcon;
                gui.m_craftingStationLevelRoot = panel.WorkbenchLevelRoot;
                gui.m_craftingStationLevel = panel.WorkbenchLevel;
                gui.m_craftButton = panel.CraftButton;
                gui.m_craftCancelButton = panel.CraftCancelButton;
                gui.m_craftProgressPanel = panel.CraftProgressPanel;
                gui.m_variantButton = panel.VariantButton;
                gui.m_variantDialog = panel.VariantDialog;
                gui.m_repairButton = panel.DefaultRepairButton;
                gui.m_repairButtonGlow = panel.DefaultRepairGlow;
                gui.m_repairPanel = panel.DefaultRepairButton.transform;
                gui.m_recipeIcon = panel.DummyIcon;
                gui.m_recipeName = panel.DummyName;
                gui.m_recipeDecription = panel.DummyDescription;
                gui.m_repairPanelSelection = panel.DummyRepairPanelSelection;
                gui.m_tabCraft = panel.DummyCraftTabButton;
                gui.m_tabUpgrade = panel.DummyUpgradeTabButton;
                gui.m_craftProgressBar = panel.DummyCraftProgressBar;
                gui.m_qualityPanel = panel.DummyQualityPanel;
                gui.m_minStationLevelIcon = panel.DummyMinStationLevelIcon;
                gui.m_minStationLevelText = panel.CraftingRequirementsPanel.WorkbenchLevel;
                gui.m_itemCraftType = panel.CraftingRequirementsPanel.ItemCraftType;
                gui.m_recipeRequirementList = panel.CraftingRequirementsPanel.RequirementList;
            }
            static void Postfix(InventoryGui __instance, GameObject __state)
            {
                if (!__state) return;
                var panel = Panel(__instance);
                InventoryPanel_Patches.CraftingPanel = panel;
                panel.SetMultiCraftEnabled(Auga.HasMultiCraft);
                panel.Initialize(__instance);
                panel.VariantButton.onClick.AddListener(__instance.OnShowVariantSelection);
                __instance.m_info.gameObject.SetActive(false);
                Localization.instance.Localize(__instance.m_crafting);
                __instance.m_crafting.gameObject.SetActive(false);
                __instance.m_crafting.SetParent(__state.transform.parent, false);
                Object.Destroy(__state);
            }
        }
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show))]
        internal static class Open
        {
            static void Prefix(InventoryGui __instance) { if (Panel(__instance)) __instance.m_crafting.gameObject.SetActive(true); }
        }
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
        internal static class Close
        {
            static void Postfix(InventoryGui __instance) { if (Panel(__instance)) __instance.m_crafting.gameObject.SetActive(false); }
        }

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.SetRecipe))]
        internal static class Selection
        {
            static void Postfix(InventoryGui __instance)
            {
                var panel = Panel(__instance);
                if (panel) panel.SetRecipe(InventorySelection.Recipe(__instance), InventorySelection.Item(__instance), InventorySelection.Variant(__instance));
            }
        }
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnVariantSelected))]
        internal static class VariantSelection
        {
            static void Postfix(InventoryGui __instance)
            {
                var panel = Panel(__instance);
                if (panel) panel.SetRecipe(InventorySelection.Recipe(__instance), InventorySelection.Item(__instance), InventorySelection.Variant(__instance));
            }
        }
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.UpdateRecipe))]
        internal static class RecipeUpdate
        {
            static void Postfix(InventoryGui __instance) { var p = Panel(__instance); if (p) p.OnUpdateRecipe(__instance); }
        }
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.SetupRequirementList))]
        internal static class Requirements
        {
            static void Postfix(InventoryGui __instance, int quality, Player player, bool allowedQuality)
            {
                var p = Panel(__instance); var recipe = InventorySelection.Recipe(__instance);
                if (p && recipe) p.PostSetupRequirementList(recipe, InventorySelection.Item(__instance), quality, player, allowedQuality);
            }
        }
        [HarmonyPatch(typeof(MessageHud), "Awake")]
        internal static class LogSetup
        {
            static void Postfix(MessageHud __instance) { if (!__instance.GetComponent<AugaMessageLog>()) __instance.gameObject.AddComponent<AugaMessageLog>(); }
        }
        [HarmonyPatch(typeof(MessageHud), "AddLog")]
        internal static class NativeLog
        {
            static void Postfix(string logText) { if (AugaMessageLog.instance) AugaMessageLog.instance.AddNativeMessage(logText); }
        }
    }
}
