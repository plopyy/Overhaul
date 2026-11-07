using HarmonyLib;

namespace Overhaul.Storage
{
    // Every item added without a target slot (loot, pickup, shortcut moves, takes, splits...) goes to the first
    // free slot from the top, in the bag and in every chest. Vanilla filled from the bottom for most item types.
    [HarmonyPatch(typeof(Inventory), "TopFirst")]
    internal static class ChestItemPlacement
    {
        private static void Postfix(ref bool __result) => __result = true;
    }
}
