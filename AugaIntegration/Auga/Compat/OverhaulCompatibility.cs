using System;
using System.Reflection;
using AugaUnity;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Auga.Compat
{
    [UiModule(UiModule.RightPanel)]
    [HarmonyPatch(typeof(AugaTabController),nameof(AugaTabController.Awake))]
    internal static class OverhaulCompatibility
    {
        private static void Prefix(AugaTabController __instance)
        {
            var page=__instance.transform.Find("TabContent/TabContent_Overhaul");
            if(!page)return;
            var button=__instance.transform.Find("DefaultContent/TabButtonContainer/Tabs/OverhaulTab").GetComponent<TabButton>();
            var title=__instance.transform.Find("DefaultContent/TitleContainer/OverhaulTitle").GetComponent<TMPro.TMP_Text>();
            if(__instance.TabContents.Contains(page.gameObject))return;
            global::Overhaul.Leveling.LevelingWindow.MountAuga(page.gameObject);
            foreach(Transform card in page.Find("Progression/Stats"))
                foreach(string name in new[]{"Minus","Plus"})
                {
                    var emptyTooltip=card.Find(name).GetComponent<UITooltip>();
                    if(emptyTooltip)UnityEngine.Object.DestroyImmediate(emptyTooltip);
                }
            __instance.TabContents.Insert(1,page.gameObject);__instance.TabButtons.Insert(1,button);__instance.TabTitles.Insert(1,title);
            button.gameObject.SetActive(true);
            var tabs=(RectTransform)button.transform.parent;
            for(int i=0;i<__instance.TabButtons.Count;i++)((RectTransform)__instance.TabButtons[i].transform).anchoredPosition=new Vector2((i-(__instance.TabButtons.Count-1)*.5f)*62,0);
            tabs.anchoredPosition=Vector2.zero;
        }
    }
    [UiModule(UiModule.RightPanel)]
    [HarmonyPatch(typeof(UITooltip),"OnHoverStart")]
    internal static class OverhaulCardTooltipHover
    {
        private static void Prefix(UITooltip __instance,ref GameObject __0)
        {
            var card=__instance.transform;
            if(!card.parent || (card.parent.name!="Stats" && card.parent.name!="Passives"))return;
            for(var parent=card.parent;parent;parent=parent.parent)
                if(parent.name=="TabContent_Overhaul")
                {
                    // Native LateUpdate must monitor the complete card, not the first child hit.
                    __0=card.gameObject;return;
                }
        }
    }
    [UiModule(UiModule.RightPanel)]
    [HarmonyPatch(typeof(InventoryGui),nameof(InventoryGui.OnOpenSkills))]
    internal static class OverhaulSkillsButton
    {
        private static bool Prefix(InventoryGui __instance)
        {
            var wb=__instance.GetComponentInChildren<WorkbenchPanelController>(true);
            if(!wb)return true;
            var tabs=wb.DefaultTabController;
            int index=tabs.TabContents.FindIndex(p=>p && p.name=="TabContent_Skills");
            if(index<0)return true;
            tabs.SelectTab(index);return false;
        }
    }
}

