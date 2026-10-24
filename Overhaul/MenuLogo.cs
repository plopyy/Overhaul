using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Overhaul
{
    // The Overhaul logo under the game's own on the main menu (OverhaulMenuLogo, OverhaulLab): placed inside the game's logo
    // image, whose frame its anchors are set in.
    [HarmonyPatch(typeof(FejdStartup), "Start")]
    internal static class MenuLogo
    {
        private static void Postfix(FejdStartup __instance)
        {
            var prefab = Auga.Auga.Assets.OverhaulMenuLogo;
            if (!prefab) return;
            var logo = __instance.GetComponentsInChildren<Image>(true)
                .FirstOrDefault(i => i.sprite && i.sprite.name.ToLowerInvariant().Contains("logo") && i.transform.parent && i.transform.parent.name == "Logo")
                ?? __instance.transform.Find("Menu/Logo")?.GetComponentInChildren<Image>(true);
            if (!logo) { Utility.Log.LogWarning("Menu logo: the game's logo was not found"); return; }
            if (logo.transform.Find(prefab.name)) return;
            Object.Instantiate(prefab, logo.transform, false).name = prefab.name;
        }
    }
}
