using System.Collections.Generic;
using System.Linq;
using AugaUnity;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

// Crafting panel rework (TabContent_Crafting prefab):
// - recipe list content no taller than its viewport, so a short list has nothing to scroll;
// - min / minus / amount / plus / max row above the craft button (item info shortened to make room);
// - name search field above the recipe list (copy of the construction window's search);
// - column of diamond filter buttons straddling the panel's left border.
// Idempotent: rerunning rebuilds the generated objects.
public static class CraftPanelAuthoring
{
    const string CraftingPath = "Assets/Prefabs/TabContent_Crafting.prefab";
    const string RightPanelPath = "Assets/Prefabs/RightPanel.prefab";
    const float RowShift = 44f;

    static readonly (RecipeCategory category, string sprite, bool itemIcon)[] Filters =
    {
        (RecipeCategory.All, "Assets/Sprites/TabIcons/CraftingPanel.png", false),
        (RecipeCategory.Swords, null, true), (RecipeCategory.Axes, null, true), (RecipeCategory.Clubs, null, true),
        (RecipeCategory.Spears, null, true), (RecipeCategory.Polearms, null, true), (RecipeCategory.Knives, null, true),
        (RecipeCategory.Fists, null, true), (RecipeCategory.Bows, null, true), (RecipeCategory.Crossbows, null, true),
        (RecipeCategory.Staffs, null, true), (RecipeCategory.Shields, null, true),
        (RecipeCategory.Armor, null, true), (RecipeCategory.Accessories, null, true),
        (RecipeCategory.Tools, null, true), (RecipeCategory.Ammo, null, true), (RecipeCategory.Bait, null, true),
        (RecipeCategory.Health, "Assets/Sprites/PlayerHealth.png", false),
        (RecipeCategory.Stamina, "Assets/Sprites/PlayerStamina.png", false),
        (RecipeCategory.Eitr, "Assets/Sprites/PlayerEitr.png", false),
        (RecipeCategory.Balanced, "Assets/Sprites/PlayerStamina.png", false),
        (RecipeCategory.Misc, null, true),
    };

    public static void Run()
    {
        var right = PrefabUtility.LoadPrefabContents(RightPanelPath);
        var crafting = PrefabUtility.LoadPrefabContents(CraftingPath);
        try
        {
            var template = right.transform.Find("DefaultContent/TabButtonContainer/Tabs/TabButton_PlayerPanel").gameObject;
            var tabColors = right.transform.Find("WorkbenchContent/TabButtonContainer/Content/TabButton_WorkbenchCraft").GetComponent<TabButton>();
            var panel = crafting.GetComponent<AugaCraftingPanel>();

            var recipes = (RectTransform)crafting.transform.Find("LeftColumn/Recipes");
            recipes.sizeDelta = new Vector2(recipes.sizeDelta.x, ((RectTransform)recipes.parent).sizeDelta.y);

            var column = crafting.transform.Find("RightColumn");
            // Leftover amount field of the old MultiCraft mod compatibility, replaced by the selector.
            column.Find("BottomBar/AAA").gameObject.SetActive(false);
            ShortenItemInfo(column, crafting);
            panel.Quantity = BuildQuantity(column);
            panel.Filters = BuildFilters(crafting.transform, template, tabColors);
            panel.Filters.Search = BuildSearch(crafting.transform);

            PrefabUtility.SaveAsPrefabAsset(crafting, CraftingPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(crafting);
            PrefabUtility.UnloadPrefabContents(right);
        }
        // Reloaded after the crafting prefab is saved, so its nested copy is the new one.
        right = PrefabUtility.LoadPrefabContents(RightPanelPath);
        try
        {
            BuildPlainBackground(right);
            PrefabUtility.SaveAsPrefabAsset(right, RightPanelPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(right);
        }
        Debug.Log("CRAFT PANEL AUTHORING DONE");
    }

    // Search field above the recipe list: the construction window's Auga search field, without the
    // auto-focus helper (it grabbed the keyboard every frame, see ConstructionSearchFocusFix).
    static TMP_InputField BuildSearch(Transform crafting)
    {
        var old = crafting.Find("RecipeSearch");
        if (old) Object.DestroyImmediate(old.gameObject);
        var construction = PrefabUtility.LoadPrefabContents("Assets/OverhaulBuild/AugaConstruction.prefab");
        try
        {
            var source = construction.GetComponentsInChildren<Transform>(true).First(t => t.name == "AugaSearch").gameObject;
            var go = Object.Instantiate(source, crafting, false);
            go.name = "RecipeSearch";
            foreach (var submit in go.GetComponentsInChildren<GuiInputFieldSubmit>(true)) Object.DestroyImmediate(submit);
            Debug.Log("RecipeSearch components: " + string.Join(", ", go.GetComponents<Component>().Select(c => c.GetType().Name)));

            var list = (RectTransform)crafting.Find("LeftColumn");
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
            // The field's dark body spans the recipe list (scrollbar excluded); the end arrows (about
            // 16 units each, inside the sprite) stick out on both sides. The copied field keeps the
            // construction window's scale, hence the division. Low enough to clear the repair button.
            const float arrows = 16;
            var width = list.sizeDelta.x + 2 * arrows;
            rt.sizeDelta = new Vector2(width / rt.localScale.x, 34);
            rt.anchoredPosition = new Vector2(list.anchoredPosition.x - arrows, list.anchoredPosition.y + 36);

            // The background must span the whole list width, not keep the sprite's proportions.
            var background = go.GetComponent<Image>();
            background.preserveAspect = false;
            if (background.sprite && background.sprite.border != Vector4.zero) background.type = Image.Type.Sliced;

            var field = go.GetComponent<TMP_InputField>();
            field.text = "";
            if (field.placeholder is TMP_Text tmp) tmp.text = "$overhaul_craft_search";
            else if (field.placeholder is Text legacy) legacy.text = "$overhaul_craft_search";
            return field;
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(construction);
        }
    }

    // Workbench window without the repair-button notch: the character panel's plain background.
    static void BuildPlainBackground(GameObject right)
    {
        var workbench = right.transform.Find("WorkbenchContent");
        var old = workbench.Find("BackgroundPlain");
        if (old) Object.DestroyImmediate(old.gameObject);
        var notched = workbench.Find("Background").gameObject;
        var plain = Object.Instantiate(right.transform.Find("DefaultContent/Background").gameObject, workbench, false);
        plain.name = "BackgroundPlain";
        plain.transform.SetSiblingIndex(notched.transform.GetSiblingIndex() + 1);
        plain.SetActive(false);
        var controller = right.GetComponent<WorkbenchPanelController>();
        controller.NotchedBackground = notched;
        controller.PlainBackground = plain;

        // Both backgrounds are cut behind each visible filter diamond (centres set by RecipeFilterBar).
        var filters = right.GetComponentInChildren<RecipeFilterBar>(true);
        filters.Notches.Clear();
        foreach (var background in new[] { notched, plain })
        {
            var notches = background.GetComponent<AugaPanelNotches>() ?? background.AddComponent<AugaPanelNotches>();
            notches.CentersFromTopLeft = new Vector2[0];
            filters.Notches.Add(notches);
        }
    }

    // Item info scroll area loses one row at the bottom for the quantity selector (only once).
    static void ShortenItemInfo(Transform column, GameObject crafting)
    {
        if (column.Find("CraftQuantity")) return;
        foreach (var path in new[] { "ItemInfo/TooltipScrollContainer", "ItemInfo/ScrollBar" })
        {
            var rt = (RectTransform)column.Find(path);
            rt.sizeDelta -= new Vector2(0, RowShift);
            rt.anchoredPosition += new Vector2(0, RowShift / 2);
        }
    }

    static CraftQuantity BuildQuantity(Transform column)
    {
        var old = column.Find("CraftQuantity");
        if (old) Object.DestroyImmediate(old.gameObject);
        var bottom = (RectTransform)column.Find("BottomBar");

        var row = new GameObject("CraftQuantity", typeof(RectTransform));
        var rt = (RectTransform)row.transform;
        rt.SetParent(column, false);
        rt.SetSiblingIndex(bottom.GetSiblingIndex());
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(bottom.sizeDelta.x, 34);
        rt.anchoredPosition = bottom.anchoredPosition + new Vector2(0, bottom.sizeDelta.y / 2 + 4 + 17);

        var multicraft = column.Find("BottomBar/Multicraft");
        var countBg = Object.Instantiate(multicraft.Find("CountBG").gameObject, rt, false);
        countBg.name = "AmountBG";
        var bgRt = (RectTransform)countBg.transform;
        bgRt.anchoredPosition = Vector2.zero;
        bgRt.sizeDelta = new Vector2(72, 32);
        var sourceText = multicraft.Find("amountText").GetComponent<TMP_Text>();
        var amount = Object.Instantiate(sourceText.gameObject, rt, false).GetComponent<TMP_Text>();
        amount.name = "Amount";
        var amountRt = amount.rectTransform;
        amountRt.anchoredPosition = Vector2.zero;
        amountRt.sizeDelta = new Vector2(72, 32);
        amount.alignment = TextAlignmentOptions.Center;
        amount.enableAutoSizing = false;
        amount.fontSize = 22;
        amount.text = "1";

        var quantity = row.AddComponent<CraftQuantity>();
        quantity.AmountText = amount;
        var sound = column.Find("BottomBar/CraftButton").GetComponent<ButtonSfx>();
        quantity.MinButton = AugaDiamondButton.Create(rt, "Min", AugaDiamondButton.Min, new Vector2(-102, 0), sound);
        quantity.MinusButton = AugaDiamondButton.Create(rt, "Minus", AugaDiamondButton.Minus, new Vector2(-66, 0), sound);
        quantity.PlusButton = AugaDiamondButton.Create(rt, "Plus", AugaDiamondButton.Plus, new Vector2(66, 0), sound);
        quantity.MaxButton = AugaDiamondButton.Create(rt, "Max", AugaDiamondButton.Max, new Vector2(102, 0), sound);
        return quantity;
    }

    static RecipeFilterBar BuildFilters(Transform crafting, GameObject template, TabButton tabColors)
    {
        var old = crafting.Find("RecipeFilters");
        if (old) Object.DestroyImmediate(old.gameObject);

        var bar = new GameObject("RecipeFilters", typeof(RectTransform));
        var rt = (RectTransform)bar.transform;
        rt.SetParent(crafting, false);
        // TabContent sits 10.5 units inside the panel border: centre the diamonds on the left border,
        // below the repair button.
        rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0.5f, 1);
        rt.anchoredPosition = new Vector2(-14f, -72.25f);
        rt.sizeDelta = new Vector2(42, 760);
        var layout = bar.AddComponent<VerticalLayoutGroup>();
        layout.spacing = 2;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlWidth = layout.childControlHeight = false;
        layout.childForceExpandWidth = layout.childForceExpandHeight = false;

        var filterBar = bar.AddComponent<RecipeFilterBar>();
        foreach (var (category, sprite, itemIcon) in Filters)
        {
            var go = Object.Instantiate(template, rt, false);
            go.name = "Filter_" + category;
            ((RectTransform)go.transform).sizeDelta = new Vector2(42, 42);
            var element = go.GetComponent<LayoutElement>();
            element.preferredWidth = element.preferredHeight = 42;
            // The template is the Character tab: its tooltip title must become the filter name.
            var tooltip = go.GetComponent<UITooltip>();
            tooltip.m_topic = "$overhaul_craft_filter_" + category.ToString().ToLowerInvariant();
            tooltip.m_text = "";

            var tab = go.GetComponent<TabButton>();
            tab.BackgroundColor = tabColors.BackgroundColor;
            tab.SelectedBackgroundColor = tabColors.SelectedBackgroundColor;
            tab.IconColor = itemIcon ? new Color(0.7f, 0.7f, 0.7f, 0.85f) : tabColors.IconColor;
            tab.SelectedIconColor = itemIcon ? Color.white : tabColors.SelectedIconColor;

            var icon = (RectTransform)go.transform.Find("Icon");
            if (itemIcon)
            {
                // Item icons are full-colour pictures: inset them inside the diamond.
                icon.anchorMin = icon.anchorMax = new Vector2(0.5f, 0.5f);
                icon.sizeDelta = new Vector2(28, 28);
                icon.anchoredPosition = Vector2.zero;
                tab.Icon.sprite = null;
                tab.Icon.preserveAspect = true;
            }
            else
            {
                tab.Icon.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(sprite);
                tab.Icon.preserveAspect = true;
            }
            if (category == RecipeCategory.Health || category == RecipeCategory.Stamina || category == RecipeCategory.Eitr)
            {
                icon.anchorMin = icon.anchorMax = new Vector2(0.5f, 0.5f);
                icon.sizeDelta = new Vector2(24, 24);
                icon.anchoredPosition = Vector2.zero;
                tab.IconColor = new Color(0.75f, 0.75f, 0.75f, 0.9f);
                tab.SelectedIconColor = Color.white;
            }
            if (category == RecipeCategory.Balanced) BalancedIcon(icon, tab);
            tab.SetSelected(category == RecipeCategory.All);
            tab.SetColor();
            filterBar.Buttons.Add(tab);
            filterBar.Categories.Add(category);
            if (itemIcon) filterBar.ItemIconCategories.Add(category);
        }
        return filterBar;
    }

    // Balanced food: the three stat icons in a small triangle (stamina on top, health and eitr below).
    static void BalancedIcon(RectTransform icon, TabButton tab)
    {
        icon.anchorMin = icon.anchorMax = new Vector2(0.5f, 0.5f);
        icon.sizeDelta = new Vector2(14, 14);
        icon.anchoredPosition = new Vector2(0, 6);
        tab.IconColor = Color.white;
        tab.SelectedIconColor = Color.white;
        foreach (var (name, sprite, x) in new[] { ("Health", "Assets/Sprites/PlayerHealth.png", -8f), ("Eitr", "Assets/Sprites/PlayerEitr.png", 8f) })
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(icon.parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(14, 14);
            rt.anchoredPosition = new Vector2(x, -6);
            var image = go.GetComponent<Image>();
            image.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(sprite);
            image.preserveAspect = true;
            image.raycastTarget = false;
        }
    }
}
