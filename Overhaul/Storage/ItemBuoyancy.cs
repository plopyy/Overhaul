using HarmonyLib;
using UnityEngine;

namespace Overhaul.Storage
{
    [HarmonyPatch(typeof(ItemDrop), "Awake")]
    internal static class ItemBuoyancy
    {
        // Install before ItemDrop caches its Floating reference and before the
        // first water trigger. This also covers saved drops when their zone loads.
        internal static void Prefix(ItemDrop __instance)
        {
            if (!__instance.GetComponent<Rigidbody>() || !__instance.GetComponent<ZNetView>() ||
                !__instance.GetComponentInChildren<Collider>(true)) return;
            var floating = __instance.GetComponent<Floating>();
            if (!floating) floating = __instance.gameObject.AddComponent<Floating>();
            floating.enabled = true;
        }
    }
}
