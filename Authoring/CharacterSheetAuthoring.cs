using AugaUnity;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

// Character tab (TabContent_PlayerPanel): the health / stamina / eitr blocks, the food and the active effects give way
// to a scrolling sheet of the character's cumulated bonuses (CharacterSheet). The old blocks stay in the prefab,
// inactive (their components may still be referenced); the active effects' spawner is disabled.
// Templates reuse the tab's own section header, food name font and tooltip; the scrollbar is the skills tab's.
// Idempotent: rerunning rebuilds the sheet.
public static class CharacterSheetAuthoring
{
    const string PanelPath = "Assets/Prefabs/TabContent_PlayerPanel.prefab";
    const string SkillsPath = "Assets/Prefabs/TabContent_Skills.prefab";
    const float Width = 494, RowHeight = 22, ColumnGap = 14;

    public static void Run()
    {
        var root = PrefabUtility.LoadPrefabContents(PanelPath);
        var skills = PrefabUtility.LoadPrefabContents(SkillsPath);
        try
        {
            var old = root.transform.Find("Sheet");
            if (old) Object.DestroyImmediate(old.gameObject);
            var oldBar = root.transform.Find("SheetScrollBar");
            if (oldBar) Object.DestroyImmediate(oldBar.gameObject);

            var font = root.transform.Find("Food/FoodStatus/FoodPanel/FoodName").GetComponent<Text>().font;
            var headerSource = root.transform.Find("Effects/Header").gameObject;
            var tooltipSource = root.transform.Find("Food/FoodStatus/FoodPanel").GetComponent<UITooltip>();
            foreach (var name in new[] { "BasicStatus", "Food", "Effects" }) root.transform.Find(name).gameObject.SetActive(false);
            root.GetComponent<AugaStatusEffects>().enabled = false;

            // Scroll view over the tab, below its top edge.
            var sheet = New("Sheet", root.transform);
            sheet.anchorMin = Vector2.zero;
            sheet.anchorMax = Vector2.one;
            sheet.offsetMin = new Vector2(0, 10);
            sheet.offsetMax = new Vector2(0, -12);
            var viewport = New("Viewport", sheet);
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = viewport.offsetMax = Vector2.zero;
            viewport.gameObject.AddComponent<RectMask2D>();
            // Invisible hit area, so the wheel scrolls between rows too.
            var hit = viewport.gameObject.AddComponent<Image>();
            hit.color = new Color(0, 0, 0, 0);

            var content = New("Content", viewport);
            content.anchorMin = content.anchorMax = new Vector2(0.5f, 1);
            content.pivot = new Vector2(0.5f, 1);
            content.sizeDelta = new Vector2(Width, 0);
            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 4;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var bar = Object.Instantiate(skills.transform.Find("ScrollBar").gameObject, root.transform, false);
            bar.name = "SheetScrollBar";
            var barRect = (RectTransform)bar.transform;
            barRect.anchoredPosition = new Vector2(Width / 2 + 16, 0);
            barRect.sizeDelta = new Vector2(barRect.sizeDelta.x, 740);

            var scroll = sheet.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30;
            scroll.verticalScrollbar = bar.GetComponent<Scrollbar>();
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            // Templates (inactive, kept under the sheet).
            var templates = New("Templates", sheet);
            templates.gameObject.SetActive(false);
            var header = Object.Instantiate(headerSource, templates, false);
            header.name = "Header";
            header.AddComponent<LayoutElement>().preferredHeight = 26;

            var grid = New("Grid", templates).gameObject;
            var g = grid.AddComponent<GridLayoutGroup>();
            g.cellSize = new Vector2((Width - ColumnGap) / 2, RowHeight);
            g.spacing = new Vector2(ColumnGap, 2);
            g.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            g.constraintCount = 2;

            var row = New("Row", templates).gameObject;
            var background = row.AddComponent<Image>();
            background.color = new Color(0, 0, 0, 0.18f);
            Label(row.transform, "Label", font, TextAnchor.MiddleLeft, new Color(0.78f, 0.72f, 0.62f));
            Label(row.transform, "Value", font, TextAnchor.MiddleRight, Color.white);
            var tooltip = row.AddComponent<UITooltip>();
            EditorUtility.CopySerialized(tooltipSource, tooltip);
            tooltip.m_topic = tooltip.m_text = "";
            tooltip.enabled = false;

            var controller = sheet.gameObject.AddComponent<CharacterSheet>();
            controller.Content = content;
            controller.HeaderTemplate = header;
            controller.GridTemplate = grid;
            controller.RowTemplate = row;

            PrefabUtility.SaveAsPrefabAsset(root, PanelPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(skills);
            PrefabUtility.UnloadPrefabContents(root);
        }
        Debug.Log("CHARACTER SHEET AUTHORING DONE");
    }

    static RectTransform New(string name, Transform parent)
    {
        var rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rt.SetParent(parent, false);
        return rt;
    }

    static void Label(Transform parent, string name, Font font, TextAnchor anchor, Color color)
    {
        var rt = New(name, parent);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(8, 0);
        rt.offsetMax = new Vector2(-8, 0);
        var text = rt.gameObject.AddComponent<Text>();
        text.font = font;
        text.fontSize = 15;
        text.alignment = anchor;
        text.color = color;
        text.raycastTarget = false;
    }
}
