using AugaUnity;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

// Simple station window (one inventory):
// - the label block on the left only repeated the title, so it goes and the inventory area spans
//   the window (room for the iron cooking rack's 5 places);
// - the window is a vertical box sized to its content: title, divider, inventory (as many rows as
//   the station has, GridRowsLayout), divider, buttons. The frame stretches behind them.
// Idempotent.
public static class StationSingleWindowAuthoring
{
    const string Path = "Assets/StationWindowProposals/StationSingleLateral_Separation2.prefab";
    const float Margin = 20;

    public static void Run()
    {
        var root = PrefabUtility.LoadPrefabContents(Path);
        try
        {
            foreach (var name in new[] { "MaterialLabelBackground", "MaterialLabel", "MaterialSeparator" })
            {
                var child = root.transform.Find(name);
                if (child) Object.DestroyImmediate(child.gameObject);
            }
            var window = (RectTransform)root.transform;

            // Frame: behind everything, the whole window.
            var frame = (RectTransform)root.transform.Find("Frame");
            frame.anchorMin = Vector2.zero;
            frame.anchorMax = Vector2.one;
            frame.offsetMin = frame.offsetMax = Vector2.zero;
            Element(frame.gameObject).ignoreLayout = true;

            // Inventory area: full width; its height comes from the grid's rows.
            var area = (RectTransform)root.transform.Find("MaterialInventoryArea");
            area.sizeDelta = new Vector2(window.sizeDelta.x - 2 * Margin, area.sizeDelta.y);
            var grid = area.GetComponentInChildren<InventoryGrid>(true);
            var gridRect = (RectTransform)grid.transform;
            gridRect.anchorMin = new Vector2(gridRect.anchorMin.x, 0);
            gridRect.anchorMax = new Vector2(gridRect.anchorMax.x, 1);
            gridRect.offsetMin = new Vector2(gridRect.offsetMin.x, 0);
            gridRect.offsetMax = new Vector2(gridRect.offsetMax.x, 0);
            var rows = Get<GridRowsLayout>(area.gameObject);
            rows.Grid = grid;
            rows.MaxRows = Mathf.RoundToInt(area.sizeDelta.y / grid.m_elementSpace);

            // Buttons row: the close button, and the buttons the game adds beside it (Take all...).
            var buttons = root.transform.Find("Buttons") as RectTransform;
            var close = (RectTransform)(buttons ? buttons.Find("Close") : root.transform.Find("Close"));
            if (!buttons)
            {
                buttons = new GameObject("Buttons", typeof(RectTransform)).GetComponent<RectTransform>();
                buttons.SetParent(root.transform, false);
                buttons.anchorMin = buttons.anchorMax = buttons.pivot = new Vector2(0, 1);
                buttons.sizeDelta = new Vector2(window.sizeDelta.x, close.sizeDelta.y);
                close.SetParent(buttons, false);
                close.anchoredPosition = new Vector2(close.anchoredPosition.x, 0);
            }

            // Order and fixed heights of the stacked parts.
            var order = new[] { "Title", "HeaderDivider", "MaterialInventoryArea", "FooterDivider", "Buttons" };
            for (int i = 0; i < order.Length; i++)
            {
                var part = (RectTransform)root.transform.Find(order[i]);
                part.SetSiblingIndex(i + 1);
                if (part != area) Element(part.gameObject).preferredHeight = part.sizeDelta.y;
            }

            var layout = Get<VerticalLayoutGroup>(root);
            layout.padding = new RectOffset(0, 0, 18, 18);
            layout.spacing = 10;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = false;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;
            var fitter = Get<ContentSizeFitter>(root);
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            PrefabUtility.SaveAsPrefabAsset(root, Path);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
        Debug.Log("STATION SINGLE WINDOW AUTHORING DONE");
    }

    static LayoutElement Element(GameObject go) => Get<LayoutElement>(go);

    // GetComponent's missing result is a Unity fake null: `??` would not see it.
    static T Get<T>(GameObject go) where T : Component
    {
        var component = go.GetComponent<T>();
        return component ? component : go.AddComponent<T>();
    }
}
