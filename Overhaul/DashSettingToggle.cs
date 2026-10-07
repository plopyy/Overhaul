using System.Linq;
using AugaUnity;
using HarmonyLib;
using Overhaul.Utility;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Overhaul
{
    // "Dash" checkbox in Settings > Mods, cloned at runtime from the native-styled
    // "Auto run" checkbox of the Misc tab. Applied with Apply, reverted by Back.
    internal static class DashSettingToggle
    {
        private const string Name = "OverhaulDash";

        private static Toggle Find(AugaModsSettings page) =>
            page && page.Notice ? page.Notice.transform.parent.Find(Name)?.GetComponent<Toggle>() : null;

        private static Toggle Create(AugaModsSettings page)
        {
            var content = page.Notice.transform.parent;
            var root = page.transform.root;
            var template = root.GetComponentsInChildren<Toggle>(true).FirstOrDefault(t => t.name == "ToggleAutoRun");
            if (!template || page.Displays == null || page.Displays.Length == 0) return null;

            var toggle = Object.Instantiate(template, content, false);
            toggle.name = Name;
            toggle.onValueChanged = new Toggle.ToggleEvent(); // drop the auto-run wiring
            toggle.group = null;

            // Same column as the shortcut rows, right below the last one; the help text moves down.
            var row = (RectTransform)page.Displays[page.Displays.Length - 1].transform;
            var rect = (RectTransform)toggle.transform;
            rect.anchorMin = row.anchorMin; rect.anchorMax = row.anchorMax; rect.pivot = row.pivot;
            rect.anchoredPosition = new Vector2(row.anchoredPosition.x, row.anchoredPosition.y - RowSpacing);
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, row.rect.width);
            var notice = (RectTransform)page.Notice.transform;
            notice.anchoredPosition = new Vector2(notice.anchoredPosition.x, rect.anchoredPosition.y - RowSpacing);

            var label = toggle.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
            if (label) label.text = Localization.instance.Localize("$overhaul_dash_setting");
            var tip = toggle.GetComponent<UITooltip>();
            if (tip) { tip.m_topic = "$overhaul_dash_setting"; tip.m_text = "$overhaul_dash_setting_tip"; }
            return toggle;
        }

        // Shortcut index 6 ("Take all"), read/written by EquipmentQuickSlotsCompatibility.
        private const int TakeAllIndex = 6;
        private const string TakeAllRow = "TakeAll";

        // Appends the "Take all" shortcut row, cloned from the "Move an object" row above it.
        private static void AddTakeAllRow(AugaModsSettings page)
        {
            if (!page.Notice || page.Displays == null || page.Displays.Length != TakeAllIndex) return;
            var content = page.Notice.transform.parent;
            if (content.Find(TakeAllRow)) return;
            var source = page.Displays[TakeAllIndex - 1];
            var row = Object.Instantiate(source, content, false);
            row.name = TakeAllRow;
            var button = row.GetComponentInChildren<Button>(true);
            button.onClick = new Button.ButtonClickedEvent(); // drop the copied "Move an object" binding
            button.onClick.AddListener(() => page.BeginBinding(TakeAllIndex));
            foreach (var text in row.GetComponentsInChildren<TMP_Text>(true))
                if (!text.transform.IsChildOf(button.transform)) text.text = Localization.instance.Localize("$overhaul_take_all_binding");
            var tip = row.GetComponent<UITooltip>();
            if (tip) { tip.m_topic = "$overhaul_take_all_binding"; tip.m_text = "$overhaul_take_all_binding_tip"; }
            page.BindButtons = page.BindButtons.Concat(new[] { button }).ToArray();
            page.Displays = page.Displays.Concat(new[] { row }).ToArray();
            // Tighter rows so the extra row, the Dash checkbox and the help text still fit above the buttons.
            var top = ((RectTransform)page.Displays[0].transform).anchoredPosition.y;
            for (int i = 0; i < page.Displays.Length; i++)
            {
                var r = (RectTransform)page.Displays[i].transform;
                r.anchoredPosition = new Vector2(r.anchoredPosition.x, top - RowSpacing * i);
            }
        }
        private const float RowSpacing = 42;

        [HarmonyPatch(typeof(AugaModsSettings), nameof(AugaModsSettings.Initialize))]
        private static class Initialize
        {
            private static void Prefix(AugaModsSettings __instance) => AddTakeAllRow(__instance);

            private static void Postfix(AugaModsSettings __instance)
            {
                var toggle = Find(__instance) ?? Create(__instance);
                if (toggle) toggle.SetIsOnWithoutNotify(OverhaulConfig.DashEnabled.Value);
            }
        }

        [HarmonyPatch(typeof(AugaModsSettings), nameof(AugaModsSettings.OnBack))]
        private static class Revert
        {
            private static void Postfix(AugaModsSettings __instance)
            {
                var toggle = Find(__instance);
                if (toggle) toggle.SetIsOnWithoutNotify(OverhaulConfig.DashEnabled.Value);
            }
        }

        [HarmonyPatch(typeof(AugaModsSettings), nameof(AugaModsSettings.OnOkAsync))]
        private static class Apply
        {
            private static void Prefix(AugaModsSettings __instance)
            {
                var toggle = Find(__instance);
                if (toggle && toggle.isOn != OverhaulConfig.DashEnabled.Value) OverhaulConfig.DashEnabled.Value = toggle.isOn;
            }
        }

        // Gamepad: the checkbox sits between the last shortcut and the Back/Apply buttons.
        [HarmonyPatch(typeof(AugaModsSettings), "UpdateNavigation")]
        private static class Navigation
        {
            private static void Postfix(AugaModsSettings __instance, Button ___backButton, Button ___applyButton)
            {
                var toggle = Find(__instance);
                if (!toggle || !___backButton || !___applyButton || __instance.BindButtons == null || __instance.BindButtons.Length == 0) return;
                var last = __instance.BindButtons[__instance.BindButtons.Length - 1];
                var nav = last.navigation; nav.selectOnDown = toggle; last.navigation = nav;
                toggle.navigation = new UnityEngine.UI.Navigation
                {
                    mode = UnityEngine.UI.Navigation.Mode.Explicit,
                    selectOnUp = last, selectOnDown = ___applyButton, selectOnLeft = __instance.EqsTab
                };
                GuiUtils.SetNavigationUp(___backButton, toggle);
                GuiUtils.SetNavigationUp(___applyButton, toggle);
            }
        }
    }
}
