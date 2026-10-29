using System;
using System.Collections.Generic;
using GUIFramework;
using JetBrains.Annotations;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AugaUnity
{
    public class AugaCraftingPanel : MonoBehaviour
    {
        public TMP_Text WorkbenchName;
        public Image WorkbenchIcon;
        public TMP_Text WorkbenchLevel;
        public RectTransform WorkbenchLevelRoot;
        public Button RepairButton;
        public Image RepairGlow;
        public Button DefaultRepairButton;
        public Image DefaultRepairGlow;
        public AugaTabController TabController;
        public CraftingRequirementsPanel CraftingRequirementsPanel;
        public CraftingRequirementsPanel UpgradeRequirementsPanel;
        public CraftingRequirementsPanel MaxQualityUpgradeRequirementsPanel;
        public CraftingRequirementsPanel GenericCraftingRequirementsPanel;
        public RectTransform RecipeList;
        public Scrollbar RecipeListScrollbar;
        public ScrollRectEnsureVisible RecipeListEnsureVisible;
        public GameObject RecipeItemPrefab;
        public ComplexTooltip ItemInfo;
        public GameObject RequirementsContainer;
        public Button CraftButton;
        public Button CraftCancelButton;
        public Button VariantButton;
        public GameObject VariantButtonContainer;
        public GameObject NonVariantButtonContainer;
        public VariantDialog VariantDialog;
        public Button CustomVariantButton;
        public GameObject CustomVariantDialog;
        public TMP_Text CustomVariantText;
        public Transform CraftProgressPanel;
        public Image CraftProgressBar;
        public GameObject ResultsPanelPrefab;
        public CraftQuantity Quantity;
        public RecipeFilterBar Filters;

        [Header("MultiCraft Objects")]
        public GameObject Multicraft;
        public Button PlusButton;
        public Button MinusButton;
        public TMP_Text CraftAmountText;
        public GameObject CraftAmountBG;
        public GameObject AAA;
        public GuiInputField InputAmount;
        public TMP_Text InputText;

        [Header("Dummy Objects")]
        public Image DummyIcon;
        public TMP_Text DummyName;
        public TMP_Text DummyDescription;
        public Transform DummyRepairPanelSelection;
        public Button DummyCraftTabButton;
        public Button DummyUpgradeTabButton;
        public GuiBar DummyCraftProgressBar;
        public RectTransform DummyQualityPanel;
        public Image DummyMinStationLevelIcon;

        public static AugaCraftingPanel _instance = null;
        private CraftingRequirementsPanel _currentPanel;
        private Action<bool> _onShowCustomVariantDialog;
        private bool _multiCraftEnabled; private bool _initialized;
        private Recipe _quantityRecipe;
        private float _nextMaxRefresh;
        private const int MaxCraftAmount = 999;

        private void Awake()
        {
            if (_instance != null)
            {
                Debug.LogError($"Too many instances of AugaCraftingPanel exist! other={_instance} parent={_instance.transform.parent}");
            }
            
            _instance = this;
        }

        public virtual void Initialize(InventoryGui inventoryGui)
        {
            if (_initialized) return; _initialized = true;
            TabController.OnTabChanged += OnTabChanged;
            ActivatePanel(CraftingRequirementsPanel);
            CustomVariantButton.onClick.AddListener(OnCustomVariantButtonClicked);
            if (Filters) Filters.Changed += OnFilterChanged;
        }

        private void OnFilterChanged()
        {
            var inventoryGui = InventoryGui.instance;
            if (!inventoryGui || !Player.m_localPlayer) return;
            inventoryGui.UpdateCraftingPanel();
            ScrollRecipesToTop();
        }

        public void ScrollRecipesToTop()
        {
            RecipeList.anchoredPosition = new Vector2(RecipeList.anchoredPosition.x, 0);
            RecipeListScrollbar.value = 1;
        }

        // Filters the station's recipe list in place (before the native list is built) and hides
        // the upgrade tab where the station has nothing to upgrade.
        public void FilterRecipes(List<Recipe> recipes)
        {
            var station = Player.m_localPlayer ? Player.m_localPlayer.GetCurrentCraftingStation() : null;
            UpdateUpgradeTab(station);
            if (!Filters) return;
            if (Filters.Prepare(station, recipes)) ScrollRecipesToTop();
            recipes.RemoveAll(recipe => !Filters.Accepts(recipe));
        }

        private void UpdateUpgradeTab(CraftingStation station)
        {
            if (TabController.TabButtons.Count < 2) return;
            var show = StationRecipes.OffersUpgrades(station);
            var tab = TabController.TabButtons[1];
            if (tab.gameObject.activeSelf != show) tab.gameObject.SetActive(show);
            if (!show && TabController.SelectedIndex == 1) TabController.SelectTab(0);
        }

        // Feeds the chosen quantity to the native multi-craft fields before the game reads them.
        public void ApplyQuantity(InventoryGui inventoryGui)
        {
            if (!Quantity) return;
            // The amount of a running craft is read again when it completes.
            if (InventorySelection.Timer(inventoryGui) >= 0)
            {
                Quantity.gameObject.SetActive(false);
                return;
            }

            var player = Player.m_localPlayer;
            var recipe = inventoryGui.m_selectedRecipe.Recipe;
            var station = player ? player.GetCurrentCraftingStation() : null;
            var usable = player && recipe && inventoryGui.m_selectedRecipe.ItemData == null && !(station && station.m_upgrader);
            Quantity.gameObject.SetActive(usable);
            if (!usable) return;

            if (recipe != _quantityRecipe)
            {
                _quantityRecipe = recipe;
                _nextMaxRefresh = 0;
                Quantity.ResetAmount();
            }
            if (Time.unscaledTime >= _nextMaxRefresh)
            {
                _nextMaxRefresh = Time.unscaledTime + 0.25f;
                Quantity.SetMax(MaxCrafts(player, recipe));
            }
            inventoryGui.m_multiCraftAmount = Quantity.Amount;
            inventoryGui.m_touchMultiCrafting = Quantity.Amount > 1;
        }

        public bool QuantityActive => Quantity && Quantity.gameObject.activeInHierarchy;

        // Largest amount the resources allow (bag plus nearby chests through the patched counts),
        // also limited by the room left in the bag for the crafted items.
        public static int MaxCrafts(Player player, Recipe recipe)
        {
            int max;
            if (player.NoCostCheat() || ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoCraftCost))
            {
                max = MaxCraftAmount;
            }
            else
            {
                if (!player.HaveRequirements(recipe, false, 1, 1)) return 1;
                int low = 1, high = 2;
                while (high <= MaxCraftAmount && player.HaveRequirements(recipe, false, 1, high))
                {
                    low = high;
                    high *= 2;
                }
                high = Mathf.Min(high, MaxCraftAmount + 1);
                while (high - low > 1)
                {
                    var middle = (low + high) / 2;
                    if (player.HaveRequirements(recipe, false, 1, middle)) low = middle;
                    else high = middle;
                }
                max = low;
            }

            var inventory = player.GetInventory();
            var prefab = recipe.m_item.gameObject;
            if (inventory.CanAddItem(prefab, recipe.m_amount * max)) return max;
            int fits = 1, fails = max;
            while (fails - fits > 1)
            {
                var middle = (fits + fails) / 2;
                if (inventory.CanAddItem(prefab, recipe.m_amount * middle)) fits = middle;
                else fails = middle;
            }
            return fits;
        }

        private void OnTabChanged(int previousTabIndex, int currentTabIndex)
        {
            VariantDialog.OnClose();

            var inventoryGui = InventoryGui.instance;
            if (inventoryGui != null)
            {
                if (currentTabIndex == 0)
                {
                    ActivatePanel(CraftingRequirementsPanel);
                    inventoryGui.OnTabCraftPressed();
                }
                else if (currentTabIndex == 1)
                {
                    ActivatePanel(UpgradeRequirementsPanel);
                    inventoryGui.OnTabUpgradePressed();
                }
                else
                {
                    ActivatePanel(GenericCraftingRequirementsPanel);
                    inventoryGui.m_tabCraft.interactable = true;
                    inventoryGui.m_tabUpgrade.interactable = true;
                    InventorySelection.RefreshCrafting(inventoryGui);
                }
            }
        }

        public virtual void ActivatePanel(CraftingRequirementsPanel panel)
        {
            CraftingRequirementsPanel.gameObject.SetActive(false);
            UpgradeRequirementsPanel.gameObject.SetActive(false);
            MaxQualityUpgradeRequirementsPanel.gameObject.SetActive(false);
            GenericCraftingRequirementsPanel.gameObject.SetActive(false);

            var inventoryGui = InventoryGui.instance;
            _currentPanel = panel;
            if (InventorySelection.Item(inventoryGui)?.GetIcon() != null)
            {
                _currentPanel.Icon.sprite = InventorySelection.Item(inventoryGui).GetIcon();
            }
            SetRecipe(InventorySelection.Recipe(inventoryGui), InventorySelection.Item(inventoryGui), InventorySelection.Variant(inventoryGui));
            panel.gameObject.SetActive(true);
            if (inventoryGui) panel.Activate(inventoryGui, ItemInfo);
            
        }

        [UsedImplicitly]
        public virtual void OnEnable()
        {
            if (!_initialized || !InventoryGui.instance) return;
            var player = Player.m_localPlayer;
            if (player != null && player.GetCurrentCraftingStation() == null)
            {
                OnTabChanged(-1, 0);
            }

            var inventoryGui = InventoryGui.instance;
            SetRecipe(InventorySelection.Recipe(inventoryGui), InventorySelection.Item(inventoryGui), InventorySelection.Variant(inventoryGui));
        }

        [UsedImplicitly]
        public virtual void OnDisable()
        {
            VariantDialog.OnClose();
        }

        public void SetRecipe(Recipe recipe, ItemDrop.ItemData item, int variant)
        {
            if (recipe == null)
            {
                ItemInfo.gameObject.SetActive(false);
            }
            else
            {
                if (item != null)
                {
                    var quality = Mathf.Min(item.m_quality + 1, item.m_shared.m_maxQuality);
                    ItemInfo.SetItem(item, quality, variant);
                }
                else
                {
                    ItemInfo.SetItem(recipe.m_item.m_itemData, 1, variant);
                }
                ItemInfo.gameObject.SetActive(true);
            }

            VariantDialog.OnClose();
            UpdateRequirementsContainerVisibility();
        }

        public void OnUpdateRecipe(InventoryGui instance)
        {
            if (InventorySelection.Timer(instance) >= 0)
            {
                CraftProgressBar.gameObject.SetActive(true);
                var percent = InventorySelection.CraftProgress(instance.m_craftProgressBar);
                CraftProgressBar.fillAmount = percent;
            }

            UpdateVariantButtonVisibility();
            UpdateRequirementsContainerVisibility();
        }

        public virtual void UpdateVariantButtonVisibility()
        {
            var showingAnyVariantButton = VariantButton.gameObject.activeSelf || CustomVariantButton.gameObject.activeSelf;
            VariantButtonContainer.SetActive(showingAnyVariantButton);
            NonVariantButtonContainer.SetActive(!showingAnyVariantButton);
        }

        public virtual void UpdateRequirementsContainerVisibility()
        {
            if (!InventoryGui.instance) return;
            var hasRecipe = TabController.SelectedIndex > 1 || InventorySelection.Recipe(InventoryGui.instance) != null;
            var showingVariants = InventoryGui.instance.m_variantDialog.gameObject.activeInHierarchy || CustomVariantDialog.gameObject.activeInHierarchy;

            RequirementsContainer.SetActive(hasRecipe && !showingVariants);
        }

        public virtual bool HaveRequirementsHelper(Player player, Piece.Requirement[] requirements, int qualityLevel)
        {
            foreach (var resource in requirements)
            {
                if (resource.m_resItem)
                {
                    var amount = resource.GetAmount(qualityLevel);
                    var num = player.GetInventory().CountItems(resource.m_resItem.m_itemData.m_shared.m_name);
                    if (num < amount)
                        return false;
                }
            }
            return true;
        }

        public virtual void PostSetupRequirementList(Recipe recipe, ItemDrop.ItemData item, int quality, Player player, bool allowedWorkbenchQuality)
        {
            if (TabController.SelectedIndex == 1 && item != null)
            {
                var maxQuality = item.m_shared.m_maxQuality == item.m_quality;
                if (maxQuality)
                {
                    ActivatePanel(MaxQualityUpgradeRequirementsPanel);
                    return;
                }
                else
                {
                    ActivatePanel(UpgradeRequirementsPanel);
                    SetRecipe(recipe, item, item.m_variant);
                }
            }

            if (_currentPanel == null || _currentPanel.WireFrame == null)
            {
                return;
            }

            var canCraft = allowedWorkbenchQuality;
            var states = new List<WireState>();
            var index = 0;
            if (allowedWorkbenchQuality)
            {
                foreach (var resource in recipe.m_resources)
                {
                    var amountRequired = resource.GetAmount(quality);
                    if (resource.m_resItem != null && amountRequired > 0)
                    {
                        var have = HaveRequirementsHelper(player, new[] { resource }, quality);
                        states.Add(have ? WireState.Have : WireState.DontHave);
                        canCraft = canCraft && have;
                        ++index;
                    }
                }
            }

            for (; index < _currentPanel.WireFrame.Wires.Length; ++index)
            {
                states.Add(WireState.Absent);
            }

            var currentCraftingStation = player.GetCurrentCraftingStation();
            var requiredCraftingStation = recipe.GetRequiredStation(quality);
            var requiredStationLevel = recipe.GetRequiredStationLevel(quality);
            var hasStation = requiredCraftingStation == null || currentCraftingStation == null || currentCraftingStation.m_name == requiredCraftingStation.m_name;
            var stationLevel = requiredCraftingStation == null || currentCraftingStation == null || currentCraftingStation.GetLevel() >= requiredStationLevel;
            canCraft = canCraft && hasStation && stationLevel;

            _currentPanel.WireFrame.Set(states, canCraft);
        }

        public void OnCustomVariantButtonClicked()
        {
            CustomVariantDialog.gameObject.SetActive(!CustomVariantDialog.gameObject.activeSelf);
            UpdateRequirementsContainerVisibility();
            _onShowCustomVariantDialog?.Invoke(CustomVariantDialog.gameObject.activeSelf);
        }

        public virtual TMP_Text EnableCustomVariantDialog(string buttonLabel, Action<bool> onShow)
        {
            _onShowCustomVariantDialog = onShow;

            VariantButton.gameObject.SetActive(false);
            CustomVariantButton.gameObject.SetActive(true);
            SetCustomVariantButtonLabel(buttonLabel);

            UpdateVariantButtonVisibility();

            return CustomVariantText;
        }

        public void SetCustomVariantButtonLabel(string buttonLabel)
        {
            CustomVariantButton.GetComponentInChildren<TMP_Text>().text = Localization.instance.Localize(buttonLabel);
        }

        public virtual void DisableCustomVariantDialog()
        {
            if (CustomVariantDialog != null)
                CustomVariantDialog.gameObject.SetActive(false);
            _onShowCustomVariantDialog = null;
            CustomVariantButton.gameObject.SetActive(false);
            UpdateVariantButtonVisibility();
        }

        public virtual GameObject CreateResultsPanel()
        {
            return Instantiate(ResultsPanelPrefab, transform, false);
        }

        public void SetMultiCraftEnabled(bool multiCraftEnabled)
        {
            _multiCraftEnabled = multiCraftEnabled;
        }

        [UsedImplicitly]
        public void Update()
        {
            if (InventorySelection.Timer(InventoryGui.instance) >= 0)
            {
                Multicraft.SetActive(false);
            }
            else
            {
                Multicraft.SetActive(_multiCraftEnabled);
            }
        }
    }
}
