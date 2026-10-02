using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Overhaul
{
    // Scope the guarantee to the standing tree's destruction, never logs or other loot.
    [HarmonyPatch(typeof(TreeBase), "RPC_Damage")]
    internal static class TreeSeedDrops
    {
        [ThreadStatic] internal static DropTable Current;
        private static readonly HashSet<string> Seeds = new HashSet<string>(StringComparer.Ordinal)
        {
            "BeechSeeds", "BirchSeeds", "Acorn", "FirCone", "PineCone", "FirConeFrost"
        };

        private static void Prefix(TreeBase __instance, out DropTable __state)
        {
            __state = Current;
            Current = __instance.m_dropWhenDestroyed;
        }

        private static Exception Finalizer(DropTable __state, Exception __exception)
        {
            Current = __state;
            return __exception;
        }

        internal static bool IsSeed(GameObject item)
        {
            return item && Seeds.Contains(item.name.Replace("(Clone)", ""));
        }

        [HarmonyPatch(typeof(DropTable), nameof(DropTable.GetDropList), new Type[0])]
        internal static class GuaranteedSeeds
        {
            private static void Postfix(DropTable __instance, List<GameObject> __result)
            {
                if (!ReferenceEquals(Current, __instance)) return;
                GameObject seed = null;
                foreach (var entry in __instance.m_drops)
                {
                    if (IsSeed(entry.m_item)) { seed = entry.m_item; break; }
                }
                // Species without a native seed keep their existing loot.
                if (!seed) return;
                __result.RemoveAll(IsSeed);
                int count = UnityEngine.Random.Range(1, 4);
                for (int i = 0; i < count; i++) __result.Add(seed);
            }
        }
    }
}
