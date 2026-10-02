using System.IO;
using System.Linq;
using AugaUnity;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public static class AmmoSlotsAuthoring
{
    public static void Run()
    {
        const string inventoryPath="Assets/Prefabs/Inventory_screen.prefab";
        var root=PrefabUtility.LoadPrefabContents(inventoryPath);
        try
        {
            var panel=(RectTransform)root.transform.Find("root/Player/AugaEquipment");
            var food=(RectTransform)panel.Find("QuickSlots");
            if(!panel.Find("AmmoSlots"))
            {
                var ammo=Object.Instantiate(food.gameObject,panel,false);ammo.name="AmmoSlots";
                // Both rows use a fixed top anchor, independent of inventory capacity.
                var a=(RectTransform)ammo.transform;
                a.anchorMin=a.anchorMax=new Vector2(.5f,1);a.anchoredPosition=new Vector2(0,-305);
            }
            food.anchorMin=food.anchorMax=new Vector2(.5f,1);food.anchoredPosition=new Vector2(0,-375);
            panel.sizeDelta=new Vector2(panel.sizeDelta.x,432);
            PrefabUtility.SaveAsPrefabAsset(root,inventoryPath);
        }
        finally {PrefabUtility.UnloadPrefabContents(root);}
        const string settingsPath="Assets/Prefabs/AugaSettings.prefab";
        root=PrefabUtility.LoadPrefabContents(settingsPath);
        try
        {
            var controller=root.GetComponentInChildren<AugaModsSettings>(true);
            var content=controller.transform.Find("Overhaul")??controller.transform.Find("EQS");
            var existing=content.Find("AmmoCycle");
            var row=existing?existing.GetComponent<AugaBindingDisplay>():Object.Instantiate(controller.Displays[0],content,false);
            row.name="AmmoCycle";var rect=(RectTransform)row.transform;
            rect.anchoredPosition=new Vector2(rect.anchoredPosition.x,content.name=="Overhaul"?-192:-144);
            var button=row.GetComponentInChildren<Button>(true);
            button.onClick=new Button.ButtonClickedEvent();UnityEventTools.AddIntPersistentListener(button.onClick,controller.BeginBinding,3);
            foreach(var t in row.GetComponentsInChildren<TMP_Text>(true))if(!t.transform.IsChildOf(button.transform))t.text="$auga_mods_ammo_cycle";
            var tooltip=row.GetComponent<UITooltip>();
            if(tooltip){var so=new SerializedObject(tooltip);so.FindProperty("m_topic").stringValue="$auga_mods_ammo_cycle";so.FindProperty("m_text").stringValue="$auga_mods_ammo_tip";so.ApplyModifiedPropertiesWithoutUndo();}
            controller.BindButtons=controller.BindButtons.Take(3).Concat(new[]{button}).ToArray();
            controller.Displays=controller.Displays.Take(3).Concat(new[]{row}).ToArray();
            var notice=(RectTransform)content.Find("Notice");notice.anchoredPosition=new Vector2(notice.anchoredPosition.x,content.name=="Overhaul"?-260:-225);
            PrefabUtility.SaveAsPrefabAsset(root,settingsPath);
        }
        finally {PrefabUtility.UnloadPrefabContents(root);}
        AugaCompatibilityBundleBuild.Run();
        File.Copy("AssetBundles/augaassets","../../../Overhaul/AugaIntegration/Assets/augaassets",true);
    }
}
