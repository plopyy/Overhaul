using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
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
            static void Prefix(InventoryGui __instance) { var p = Panel(__instance); if (p) p.ApplyQuantity(__instance); }
            static void Postfix(InventoryGui __instance) { var p = Panel(__instance); if (p) p.OnUpdateRecipe(__instance); }
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code) => MultiCraftKey.Replace(code);
        }
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnCraftPressed))]
        internal static class CraftPressed
        {
            static void Prefix(InventoryGui __instance) { var p = Panel(__instance); if (p) p.ApplyQuantity(__instance); }
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code) => MultiCraftKey.Replace(code);
        }
        // The quantity selector replaces Shift (AltPlace) as the way to craft several at once:
        // without this, Shift would still force multi-crafting with whatever amount is chosen.
        internal static class MultiCraftKey
        {
            static readonly MethodInfo GetButton = AccessTools.Method(typeof(ZInput), nameof(ZInput.GetButton), new[] { typeof(string) });
            internal static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> code)
            {
                CodeInstruction previous = null;
                foreach (var instruction in code)
                {
                    if (previous != null && previous.opcode == OpCodes.Ldstr && (string)previous.operand == "AltPlace" && instruction.Calls(GetButton))
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MultiCraftKey), nameof(Pressed))).MoveLabelsFrom(instruction);
                    else
                        yield return instruction;
                    previous = instruction;
                }
            }
            static bool Pressed(string button)
            {
                var p = Panel(InventoryGui.instance);
                return !(p && p.QuantityActive) && ZInput.GetButton(button);
            }
        }
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.UpdateRecipeList))]
        internal static class RecipeFilter
        {
            static void Prefix(InventoryGui __instance, List<Recipe> recipes) { var p = Panel(__instance); if (p) p.FilterRecipes(recipes); }
        }
        static bool SearchFocused()
        {
            var p = Panel(InventoryGui.instance);
            return p && p.Filters && p.Filters.SearchFocused;
        }
        // The game blocks player controls, hotkeys and the map while the build search is typed in;
        // the crafting search reuses that same flag.
        [HarmonyPatch(typeof(BuildUi), nameof(BuildUi.SearchFieldFocused), MethodType.Getter)]
        internal static class SearchBlocksInput
        {
            static void Postfix(ref bool __result) { __result = __result || SearchFocused(); }
        }
        // Typing Tab, E or Escape in the search must not close the inventory.
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Update))]
        internal static class SearchKeepsInventoryOpen
        {
            static void Prefix(InventoryGui __instance) { if (SearchFocused()) __instance.m_shownFrames = 0; }
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
