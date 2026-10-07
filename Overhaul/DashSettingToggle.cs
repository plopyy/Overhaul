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
            rect.anchoredPosition = new Vector2(row.anchoredPosition.x, row.anchoredPosition.y - 52);
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, row.rect.width);
            var notice = (RectTransform)page.Notice.transform;
            notice.anchoredPosition = new Vector2(notice.anchoredPosition.x, rect.anchoredPosition.y - 44);

            var label = toggle.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
            if (label) label.text = Localization.instance.Localize("$overhaul_dash_setting");
            var tip = toggle.GetComponent<UITooltip>();
            if (tip) { tip.m_topic = "$overhaul_dash_setting"; tip.m_text = "$overhaul_dash_setting_tip"; }
            return toggle;
        }

        [HarmonyPatch(typeof(AugaModsSettings), nameof(AugaModsSettings.Initialize))]
        private static class Initialize
        {
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
