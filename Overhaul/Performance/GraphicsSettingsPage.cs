using System.Linq;
using AugaUnity;
using HarmonyLib;
using TMPro;
using UnityEngine.UI;

namespace Overhaul.Performance
{
    // Client optimisation choices of Settings > Overhaul > Graphics, saved like the game's own graphics settings: a general
    // switch, and four groups under it. The general switch off turns every optimisation off but keeps each group's choice,
    // so turning it back on restores them as they were.
    internal static class ClientPerf
    {
        static readonly System.Collections.Generic.Dictionary<string, int> cache = new System.Collections.Generic.Dictionary<string, int>();

        static bool Get(string key) { if (!cache.TryGetValue(key, out int v)) cache[key] = v = PlatformPrefs.GetInt(key, 1); return v == 1; }
        static void Set(string key, bool value) { cache[key] = value ? 1 : 0; PlatformPrefs.SetInt(key, value ? 1 : 0); }

        // The saved choices themselves (what the checkboxes show).
        internal static bool AllChosen { get => Get("Overhaul_Optimisations"); set => Set("Overhaul_Optimisations", value); }
        internal static bool MergeChosen { get => Get("Overhaul_OptMerge"); set => Set("Overhaul_OptMerge", value); }
        internal static bool ProgressiveChosen { get => Get("Overhaul_OptProgressive"); set => Set("Overhaul_OptProgressive", value); }
        internal static bool BackgroundChosen { get => Get("Overhaul_OptBackground"); set => Set("Overhaul_OptBackground", value); }
        internal static bool CacheChosen { get => Get("Overhaul_OptCache"); set => Set("Overhaul_OptCache", value); }

        // What is in effect.
        internal static bool All => AllChosen;
        internal static bool MergePieces => All && MergeChosen;
        internal static bool Progressive => All && ProgressiveChosen;
        internal static bool Background => All && BackgroundChosen;
        internal static bool Cache => All && CacheChosen;
    }

    // One optimisation's on/off: what the settings give, unless `o_opti <name> on|off` set it for this session.
    internal sealed class PerfSwitch
    {
        readonly System.Func<bool> setting;
        bool? session;
        internal PerfSwitch(System.Func<bool> setting) { this.setting = setting; }
        internal bool Value { get => session ?? (ClientPerf.All && setting()); set => session = value; }
    }

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

        // Parts of the far view and of the weather, each on by default except the far grass.
        static readonly System.Collections.Generic.Dictionary<string, int> parts = new System.Collections.Generic.Dictionary<string, int>();
        internal static bool Part(string key, bool byDefault = true)
        {
            if (!parts.TryGetValue(key, out int v)) parts[key] = v = PlatformPrefs.GetInt(key, byDefault ? 1 : 0);
            return v == 1;
        }
        internal static void SetPart(string key, bool value) { parts[key] = value ? 1 : 0; PlatformPrefs.SetInt(key, value ? 1 : 0); }

        // What is in effect: the general Optimisations switch off turns the far view and the weather off too.
        // With FarLands or WeatherOverhaul themselves installed, ours are off (OtherMods).
        internal static Level FarViewInEffect => ClientPerf.All && !OtherMods.FarLands ? FarView : Level.Off;
        internal static Level WeatherInEffect => ClientPerf.All && !OtherMods.WeatherOverhaul ? Weather : Level.Off;

        internal static bool FarBuildings => Part("Overhaul_FarBuildings");
        internal static bool FarLights => Part("Overhaul_FarLights");
        internal static bool FarShadows => Part("Overhaul_FarShadows");
        internal static bool FarGrass => Part("Overhaul_FarGrass", false);
        internal static bool Clouds => Part("Overhaul_Clouds");
        internal static bool LightShafts => Part("Overhaul_LightShafts");
        internal static bool Lightning => Part("Overhaul_Lightning");
        internal static bool Snow => Part("Overhaul_Snow");
        internal static bool Puddles => Part("Overhaul_Puddles");
        internal static bool Fireflies => Part("Overhaul_Fireflies");
    }

    // Settings > Overhaul > Optimisations / Far view / Weather (rows authored in the AugaSettings prefab).
    // Applied with Apply, reverted by Back. The optimisation rows are greyed while the general switch is unticked.
    internal static class GraphicsSettingsPage
    {
        // Checkbox rows: prefab name, saved value, how to save it.
        static readonly (string Name, System.Func<bool> Get, System.Action<bool> Set)[] Checks =
        {
            ("OverhaulOptimisations", () => ClientPerf.AllChosen, v => ClientPerf.AllChosen = v),
            ("OverhaulSmoothFrames", () => SmoothFrames.Enabled, v => SmoothFrames.Enabled = v),
            ("OverhaulOptMerge", () => ClientPerf.MergeChosen, v => ClientPerf.MergeChosen = v),
            ("OverhaulOptProgressive", () => ClientPerf.ProgressiveChosen, v => ClientPerf.ProgressiveChosen = v),
            ("OverhaulOptBackground", () => ClientPerf.BackgroundChosen, v => ClientPerf.BackgroundChosen = v),
            ("OverhaulOptCache", () => ClientPerf.CacheChosen, v => ClientPerf.CacheChosen = v),
            ("OverhaulFarBuildings", () => ClientVisuals.FarBuildings, v => ClientVisuals.SetPart("Overhaul_FarBuildings", v)),
            ("OverhaulFarLights", () => ClientVisuals.FarLights, v => ClientVisuals.SetPart("Overhaul_FarLights", v)),
            ("OverhaulFarShadows", () => ClientVisuals.FarShadows, v => ClientVisuals.SetPart("Overhaul_FarShadows", v)),
            ("OverhaulFarGrass", () => ClientVisuals.FarGrass, v => ClientVisuals.SetPart("Overhaul_FarGrass", v)),
            ("OverhaulClouds", () => ClientVisuals.Clouds, v => ClientVisuals.SetPart("Overhaul_Clouds", v)),
            ("OverhaulLightShafts", () => ClientVisuals.LightShafts, v => ClientVisuals.SetPart("Overhaul_LightShafts", v)),
            ("OverhaulLightning", () => ClientVisuals.Lightning, v => ClientVisuals.SetPart("Overhaul_Lightning", v)),
            ("OverhaulSnow", () => ClientVisuals.Snow, v => ClientVisuals.SetPart("Overhaul_Snow", v)),
            ("OverhaulPuddles", () => ClientVisuals.Puddles, v => ClientVisuals.SetPart("Overhaul_Puddles", v)),
            ("OverhaulFireflies", () => ClientVisuals.Fireflies, v => ClientVisuals.SetPart("Overhaul_Fireflies", v)),
        };

        static readonly string[] UnderGeneral = Checks.Select(c => c.Name).Where(n => n != "OverhaulOptimisations").Concat(new[] { "OverhaulFarView", "OverhaulWeather" }).ToArray();

        static T Find<T>(AugaModsSettings page, string name) where T : UnityEngine.Component =>
            page ? page.GetComponentsInChildren<T>(true).FirstOrDefault(c => c.name == name) : null;

        static TMP_Dropdown Dropdown(AugaModsSettings page, string row)
        {
            var r = Find<UnityEngine.RectTransform>(page, row);
            return r ? r.GetComponentInChildren<TMP_Dropdown>(true) : null;
        }

        static void Show(AugaModsSettings page)
        {
            foreach (var (name, get, _) in Checks) { var t = Find<Toggle>(page, name); if (t) t.SetIsOnWithoutNotify(get()); }
            Fill(Dropdown(page, "OverhaulFarView"), (int)ClientVisuals.FarView);
            Fill(Dropdown(page, "OverhaulWeather"), (int)ClientVisuals.Weather);
            var general = Find<Toggle>(page, "OverhaulOptimisations");
            if (general)
            {
                general.onValueChanged.RemoveListener(OnGeneral);
                general.onValueChanged.AddListener(OnGeneral);
                Grey(page, general.isOn);
            }
        }

        static void OnGeneral(bool on)
        {
            var page = UnityEngine.Object.FindFirstObjectByType<AugaModsSettings>(UnityEngine.FindObjectsInactive.Include);
            if (page) Grey(page, on);
        }

        // The rows under the general switch: greyed and not clickable while it is unticked; their ticks stay as chosen.
        static void Grey(AugaModsSettings page, bool on)
        {
            foreach (var name in UnderGeneral)
            {
                var row = Find<UnityEngine.RectTransform>(page, name);
                if (!row) continue;
                foreach (var s in row.GetComponentsInChildren<Selectable>(true)) s.interactable = on;
                var group = row.GetComponent<UnityEngine.CanvasGroup>() ?? row.gameObject.AddComponent<UnityEngine.CanvasGroup>();
                group.alpha = on ? 1f : 0.4f;
            }
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
                foreach (var (name, get, set) in Checks) { var t = Find<Toggle>(__instance, name); if (t && t.isOn != get()) set(t.isOn); }
                var far = Dropdown(__instance, "OverhaulFarView");
                if (far && far.value != (int)ClientVisuals.FarView) ClientVisuals.FarView = (ClientVisuals.Level)far.value;
                var weather = Dropdown(__instance, "OverhaulWeather");
                if (weather && weather.value != (int)ClientVisuals.Weather) ClientVisuals.Weather = (ClientVisuals.Level)weather.value;
            }
        }
    }
}
