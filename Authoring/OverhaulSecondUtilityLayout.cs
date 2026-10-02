using UnityEditor;
using UnityEngine;

public static class OverhaulSecondUtilityLayout
{
    public static void Run()
    {
        const string path = "Assets/Prefabs/Inventory_screen.prefab";
        var root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            var panel = root.transform.Find("root/Player/AugaEquipment");
            // Only visual anchors move. EQS save indices stay Utility=12,
            // Trinket=13, Utility2=14, preserving old character inventories.
            ((RectTransform)panel.Find("Equipment6")).anchoredPosition = new Vector2(200,-125);
            ((RectTransform)panel.Find("Equipment5")).anchoredPosition = new Vector2(200,-195);
            PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        AugaCompatibilityBundleBuild.Run();
    }
}
