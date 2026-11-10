using BepInEx.Bootstrap;

namespace Overhaul.Performance
{
    // SteadyFrame, FarLands and WeatherOverhaul (ru.ivest) installed beside Overhaul (for comparisons): our own versions of
    // what they do step aside while they are loaded, so the two never run together. Smooth frames for SteadyFrame, the far
    // view (and its air) for FarLands, the weather for WeatherOverhaul. Checked each time (a dictionary lookup), as the
    // plugins finish loading after Overhaul.
    internal static class OtherMods
    {
        internal static bool SteadyFrame => Chainloader.PluginInfos.ContainsKey("ru.ivest.steadyframe");
        internal static bool FarLands => Chainloader.PluginInfos.ContainsKey("ru.ivest.farlands");
        internal static bool WeatherOverhaul => Chainloader.PluginInfos.ContainsKey("ru.ivest.weatheroverhaul");
    }
}
