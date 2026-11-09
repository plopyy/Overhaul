using System.Linq;
using AugaUnity;
using HarmonyLib;
using TMPro;
using UnityEngine.UI;

namespace Overhaul.Performance
{
    // Client graphics choices, saved like the game's own graphics settings (PlatformPrefs), never in a config file:
    // Smooth frames on/off, far view and weather Off / Low / Medium / High (Medium by default).
    internal static class ClientVisuals
    {
        internal enum Level { Off, Low, Medium, High }

        const string FarViewKey = "Overhaul_FarView", WeatherKey = "Overhaul_Weather";
        static int farView = -1, weather = -1;

        internal static Level FarView
        {
            get { if (farView < 0) farView = UnityEngine.Mathf.Clamp(PlatformPrefs.GetInt(FarViewKey, (int)Level.Medium), 0, 3); return (Level)farView; }
            set { farView = (int)value; PlatformPrefs.SetInt(FarViewKey, farView); }
        }

        internal static Level Weather
        {
            get { if (weather < 0) weather = UnityEngine.Mathf.Clamp(PlatformPrefs.GetInt(WeatherKey, (int)Level.Medium), 0, 3); return (Level)weather; }
            set { weather = (int)value; PlatformPrefs.SetInt(WeatherKey, weather); }
        }
    }

    // Settings > Overhaul > Graphics (rows authored in the AugaSettings prefab). Applied with Apply, reverted by Back.
    internal static class GraphicsSettingsPage
    {
        static T Find<T>(AugaModsSettings page, string name) where T : UnityEngine.Component =>
            page ? page.GetComponentsInChildren<T>(true).FirstOrDefault(c => c.name == name) : null;

        static TMP_Dropdown Dropdown(AugaModsSettings page, string row)
        {
            var r = Find<UnityEngine.RectTransform>(page, row);
            return r ? r.GetComponentInChildren<TMP_Dropdown>(true) : null;
        }

        static void Show(AugaModsSettings page)
        {
            var smooth = Find<Toggle>(page, "OverhaulSmoothFrames");
            if (smooth) smooth.SetIsOnWithoutNotify(SmoothFrames.Enabled);
            Fill(Dropdown(page, "OverhaulFarView"), (int)ClientVisuals.FarView);
            Fill(Dropdown(page, "OverhaulWeather"), (int)ClientVisuals.Weather);
        }

        // The prefab holds the option keys; their text is localized here (dropdown options are not localized by the game).
        static void Fill(TMP_Dropdown dropdown, int value)
        {
            if (!dropdown) return;
            if (Localization.instance != null)
                foreach (var option in dropdown.options) if (option.text.StartsWith("$")) option.text = Localization.instance.Localize(option.text);
            dropdown.SetValueWithoutNotify(value);
            dropdown.RefreshShownValue();
        }

        [HarmonyPatch(typeof(AugaModsSettings), nameof(AugaModsSettings.Initialize))]
        private static class Initialize { private static void Postfix(AugaModsSettings __instance) => Show(__instance); }

        [HarmonyPatch(typeof(AugaModsSettings), nameof(AugaModsSettings.OnBack))]
        private static class Revert { private static void Postfix(AugaModsSettings __instance) => Show(__instance); }

        [HarmonyPatch(typeof(AugaModsSettings), nameof(AugaModsSettings.OnOkAsync))]
        private static class Apply
        {
            private static void Prefix(AugaModsSettings __instance)
            {
                var smooth = Find<Toggle>(__instance, "OverhaulSmoothFrames");
                if (smooth && smooth.isOn != SmoothFrames.Enabled) SmoothFrames.Enabled = smooth.isOn;
                var far = Dropdown(__instance, "OverhaulFarView");
                if (far && far.value != (int)ClientVisuals.FarView) ClientVisuals.FarView = (ClientVisuals.Level)far.value;
                var weather = Dropdown(__instance, "OverhaulWeather");
                if (weather && weather.value != (int)ClientVisuals.Weather) ClientVisuals.Weather = (ClientVisuals.Level)weather.value;
            }
        }
    }
}
