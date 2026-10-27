using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Overhaul
{
    // The Overhaul logo under the game's own on the main menu (OverhaulMenuLogo, OverhaulLab): placed inside the game's logo
    // image, whose frame its anchors are set in, drawn over it.
    [HarmonyPatch(typeof(FejdStartup), "Start")]
    internal static class MenuLogo
    {
        private static void Postfix(FejdStartup __instance)
        {
            var prefab = Auga.Auga.Assets.OverhaulMenuLogo;
            if (!prefab) return;
            // The game's current logo: the active image directly under Menu/Logo named like a logo ("Logo DeepNorth1 .0";
            // the old "LOGO" next to it is switched off, and an Overhaul logo hung on it stayed hidden).
            var root = __instance.transform.Find("Menu/Logo");
            var logo = root ? root.Cast<Transform>()
                .Where(t => t.gameObject.activeInHierarchy && t.name.ToLowerInvariant().Contains("logo"))
                .Select(t => t.GetComponent<Image>()).FirstOrDefault(i => i && i.enabled && i.sprite) : null;
            if (!logo) { Utility.Log.LogWarning("Menu logo: the game's logo was not found"); return; }
            if (logo.transform.Find(prefab.name)) return;
            Object.Instantiate(prefab, logo.transform, false).name = prefab.name;
        }
    }
}
