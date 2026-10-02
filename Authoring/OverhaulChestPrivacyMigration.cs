using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using AugaUnity;

public static class OverhaulChestPrivacyMigration
{
    static ColorBlock ink;
    static void Part(GameObject button, Transform group, string name, float x, float y, float w, float h)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image)); go.transform.SetParent(group, false);
        var r = (RectTransform)go.transform; r.anchorMin = r.anchorMax = r.pivot = Vector2.one * .5f;
        r.anchoredPosition = new Vector2(x,y); r.sizeDelta = new Vector2(w,h);
        var im = go.GetComponent<Image>(); im.color = ink.normalColor; im.raycastTarget = false;
        var colors = button.AddComponent<ColorButtonTextValues>(); colors.Text = im; colors.TextColors = ink;
    }
    public static void Run()
    {
        const string path = "Assets/Prefabs/Inventory_screen.prefab";
        var root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            var panel = root.transform.Find("root/Container");
            if(panel.Find("Privacy")) UnityEngine.Object.DestroyImmediate(panel.Find("Privacy").gameObject);
            var button = UnityEngine.Object.Instantiate(panel.Find("Sort").gameObject, panel, false); button.name = "Privacy";
            foreach(var c in button.GetComponents<ColorButtonTextValues>()) UnityEngine.Object.DestroyImmediate(c);
            foreach(Transform child in button.transform.Cast<Transform>().ToArray()) UnityEngine.Object.DestroyImmediate(child.gameObject);
            var sorter = button.GetComponent<AugaInventorySorter>(); if(sorter) UnityEngine.Object.DestroyImmediate(sorter);
            button.GetComponent<Button>().onClick = new Button.ButtonClickedEvent();
            ((RectTransform)button.transform).anchoredPosition = new Vector2(-18,-234);
            ink = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/ButtonSmall.prefab").GetComponent<ColorButtonTextValues>().TextColors;
            foreach(bool locked in new[]{false,true})
            {
                var group = new GameObject(locked ? "Locked" : "Public",typeof(RectTransform)); group.transform.SetParent(button.transform,false);
                var r = (RectTransform)group.transform; r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = r.offsetMax = Vector2.zero;
                Part(button,r,"Body",0,-4,14,9);
                Part(button,r,"ShackleLeft",-4,3,2,7);
                Part(button,r,"ShackleTop",0,7,10,2);
                Part(button,r,"ShackleRight",4,locked?3:6,2,locked?7:3);
                group.SetActive(!locked);
            }
            var tip = button.GetComponent<UITooltip>(); tip.m_topic = "$chest_public"; tip.m_text = "$chest_make_private";
            foreach(string name in new[]{"Bkg","selected_frame","Darken"})
            {
                var layer=panel.Find(name); var rect=(RectTransform)layer;
                layer.GetComponent<AugaPanelNotches>().CentersFromTopLeft = new[]{"Privacy","Sort","StackAll","TakeAll"}.Select(n=> {
                    var p=layer.InverseTransformPoint(panel.Find(n).position);return new Vector2(p.x-rect.rect.xMin,rect.rect.yMax-p.y);
                }).ToArray();
            }
            PrefabUtility.SaveAsPrefabAsset(root,path);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        AugaCompatibilityBundleBuild.Run();
        AugaQuickInventoryPreview.Containers();
        AugaQuickInventoryPreview.Privacy();
    }
}
