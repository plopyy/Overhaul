using HarmonyLib;

namespace Overhaul.Patches
{
    // Suppress the emitter coroutine, including each restart when processing resumes.
    // Keep the smelter, its production effects and all other Radiators untouched.
    [HarmonyPatch(typeof(Radiator), "OnEnable")]
    internal static class EitrRefineryRadiation
    {
        private static bool Prefix(Radiator __instance)
        {
            var smelter = __instance.GetComponentInParent<Smelter>(true);
            if(smelter && Utils.GetPrefabName(smelter.gameObject) == "eitrrefinery") return false;
            var item = __instance.GetComponentInParent<ItemDrop>(true);
            return !item || Utils.GetPrefabName(item.gameObject) != "Eitr";
        }
    }
}
