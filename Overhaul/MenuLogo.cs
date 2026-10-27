using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Overhaul
{
    // The Overhaul logo on the main menu (OverhaulMenuLogo, OverhaulLab), right under the game's "Deep North" logo: placed in
    // the game's Menu/Logo, which covers the screen, the prefab's anchors being screen fractions; drawn over the rest.
    [HarmonyPatch(typeof(FejdStartup), "Start")]
    internal static class MenuLogo
    {
        private static void Postfix(FejdStartup __instance)
        {
            var prefab = Auga.Auga.Assets.OverhaulMenuLogo;
            if (!prefab) return;
            var root = __instance.transform.Find("Menu/Logo");
            if (!root) { Utility.Log.LogWarning("Menu logo: the game's Menu/Logo was not found"); return; }
            if (root.Find(prefab.name)) return;
            Object.Instantiate(prefab, root, false).name = prefab.name;
        }
    }
}
