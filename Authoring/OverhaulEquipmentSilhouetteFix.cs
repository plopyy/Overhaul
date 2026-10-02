using UnityEditor;
using UnityEngine;

public static class OverhaulEquipmentSilhouetteFix
{
    public static void Run()
    {
        const string path = "Assets/Prefabs/Inventory_screen.prefab";
        var root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            var panel = root.transform.Find("root/Player/AugaEquipment");
            var body = panel.Find("Paperdoll");
            // The opaque Background used to be drawn AFTER the character.
            // Keep the original sprite behind the slots, but in front of the panel.
            body.SetAsLastSibling();
            body.SetSiblingIndex(panel.Find("Background").GetSiblingIndex() + 1);
            PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        AugaCompatibilityBundleBuild.Run();
        OverhaulEquipmentSilhouettePreview.Equipment();
    }
}
