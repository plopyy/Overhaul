using System;
using System.IO;
using AugaUnity;
using UnityEditor;
using UnityEngine;

public static class ConstructionSearchFocusFix
{
    public static void Run()
    {
        const string path = "Assets/OverhaulBuild/AugaConstruction.prefab";
        var root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            var field = root.GetComponent<BuildUi>().m_searchField;
            var submit = field.GetComponent<GuiInputFieldSubmit>();
            if (!submit) throw new InvalidOperationException("Expected inherited search auto-focus component.");
            UnityEngine.Object.DestroyImmediate(submit);
            PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        AugaCompatibilityBundleBuild.Run();
        File.Copy("AssetBundles/augaassets", "../../../Overhaul/AugaIntegration/Assets/augaassets", true);
        File.WriteAllText("../../../Tools/AugaWork/construction-search-focus.txt",
            "Removed GuiInputFieldSubmit from construction search prefab. Native search input and callbacks retained. Bundle rebuilt.");
    }
}
