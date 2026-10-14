using System.Linq;
using AugaUnity;
using HarmonyLib;
using Overhaul.Utility;
using UnityEngine.UI;

namespace Overhaul
{
    // "Dash" checkbox of Settings > Overhaul > Gameplay (part of the AugaSettings prefab). Applied with Apply,
    // reverted by Back.
    internal static class DashSettingToggle
    {
        private const string Name = "OverhaulDash";

        private static Toggle Find(AugaModsSettings page) =>
            page ? page.GetComponentsInChildren<Toggle>(true).FirstOrDefault(t => t.name == Name) : null;

        [HarmonyPatch(typeof(AugaModsSettings), nameof(AugaModsSettings.Initialize))]
        private static class Initialize
        {
            private static void Postfix(AugaModsSettings __instance)
            {
                var toggle = Find(__instance);
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
    }
}
