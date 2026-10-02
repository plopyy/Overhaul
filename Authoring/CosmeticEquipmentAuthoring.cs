using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Object = UnityEngine.Object;

public static class CosmeticEquipmentAuthoring
{
    static RectTransform Rect(Transform parent, string name, Vector2 position, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rect = (RectTransform)go.transform; rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = position; rect.sizeDelta = size;
        return rect;
    }
    public static void Run()
    {
        const string path = "Assets/Prefabs/Inventory_screen.prefab";
        var root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            var panel = (RectTransform)root.transform.Find("root/Player/AugaEquipment");
            if (!panel.Find("Tabs"))
            {
                foreach (RectTransform child in panel)
                    if (child.name == "Paperdoll" || child.name.StartsWith("Equipment", StringComparison.Ordinal))
                        child.anchoredPosition -= new Vector2(0, 40);
            }
            // Buying inventory rows must never stretch the equipment/cosmetic frame.
            panel.anchorMin = panel.anchorMax = Vector2.one;
            panel.pivot = new Vector2(0,1);
            panel.anchoredPosition = new Vector2(30,10);
            panel.sizeDelta = new Vector2(255,392);
            foreach (var name in new[] { "Tabs", "CosmeticPage" })
                if (panel.Find(name)) Object.DestroyImmediate(panel.Find(name).gameObject);
            var tabs = Rect(panel, "Tabs", new Vector2(10, -5), new Vector2(235, 32));
            var template = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/BuildHud.prefab").transform.Find("DividerLarge/TabContainer/Tabs/Misc");
            for (int i = 0; i < 2; i++)
            {
                var tab = Object.Instantiate(template.gameObject, tabs, false);
                tab.name = i == 0 ? "Equipment" : "Cosmetic"; tab.SetActive(true);
                var rect = (RectTransform)tab.transform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
                rect.anchoredPosition = new Vector2(i * 118, 0); rect.sizeDelta = new Vector2(117, 32);
                foreach (var fitter in tab.GetComponentsInChildren<ContentSizeFitter>(true)) Object.DestroyImmediate(fitter);
                foreach (var text in tab.GetComponentsInChildren<TMP_Text>(true))
                {
                    text.text = i == 0 ? "$overhaul_equipment_tab" : "$overhaul_cosmetic_tab";
                    text.fontSize = 17; text.enableAutoSizing = false; text.alignment = TextAlignmentOptions.Center;
                    text.color = text.transform.IsChildOf(tab.transform.Find("Selected")) ? Color.white : new Color(.45f,.41f,.34f);
                    // The donor's root text is also the button's hit surface.
                    text.raycastTarget = text.gameObject == tab;
                }
                tab.GetComponent<Button>().onClick = new Button.ButtonClickedEvent();
                var colors=tab.GetComponent<Button>().colors;
                colors.normalColor=colors.highlightedColor=colors.selectedColor=colors.disabledColor=Color.white;
                tab.GetComponent<Button>().colors=colors;
                var selected = tab.transform.Find("Selected"); if (selected) selected.gameObject.SetActive(i == 0);
            }
            var page = Rect(panel, "CosmeticPage", Vector2.zero, Vector2.zero);
            page.anchorMax = Vector2.one; page.offsetMin = page.offsetMax = Vector2.zero;
            var body = Object.Instantiate(panel.Find("Paperdoll").gameObject, page, false);
            body.name = "Paperdoll"; ((RectTransform)body.transform).anchoredPosition = new Vector2(0, -60);
            var positions = new[] { new Vector2(127.5f,-95), new Vector2(92.5f,-165), new Vector2(127.5f,-235), new Vector2(162.5f,-165) };
            for (int i=0;i<4;i++) Rect(page,"Slot"+i,positions[i],Vector2.zero);
            var note = Rect(page,"Description",new Vector2(15,-295),new Vector2(225,55));
            var label=note.gameObject.AddComponent<TextMeshProUGUI>();
            var donor=template.GetComponentInChildren<TMP_Text>(true);
            label.font=donor.font;label.fontSharedMaterial=donor.fontSharedMaterial;
            label.text="$overhaul_cosmetic_hint";label.fontSize=17;label.alignment=TextAlignmentOptions.Center;
            label.color=new Color(.82f,.77f,.67f);label.raycastTarget=false;
            page.gameObject.SetActive(false);
            PrefabUtility.SaveAsPrefabAsset(root,path);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        AugaCompatibilityBundleBuild.Run();
        File.Copy("AssetBundles/augaassets","../../../Overhaul/AugaIntegration/Assets/augaassets",true);
    }
}
