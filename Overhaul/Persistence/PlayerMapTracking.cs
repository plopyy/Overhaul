using System;
using System.Collections;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace Overhaul.Persistence
{
    // Bind only after the managed character's initial map has loaded. No automatic activation:
    // the admission/transport layer must own the lifecycle and durable acknowledgements.
    internal static class PlayerMapTracking
    {
        private static readonly ConditionalWeakTable<Minimap, PlayerMapTracker> trackers =
            new ConditionalWeakTable<Minimap, PlayerMapTracker>();

        internal static PlayerMapTracker Bind(Minimap map)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (trackers.TryGetValue(map, out _)) throw new InvalidOperationException("Map session already bound");
            var tracker = new PlayerMapTracker(map.m_textureSize);
            trackers.Add(map, tracker);
            return tracker;
        }

        internal static void Unbind(Minimap map) { if (!ReferenceEquals(map, null)) trackers.Remove(map); }
        internal static PlayerMapTracker.Capture Read(Minimap map, int maxBlocks = 32)
        {
            if (!trackers.TryGetValue(map, out var tracker)) throw new InvalidOperationException("Map session is not bound");
            return tracker.Read(map.m_explored, map.m_exploredOthers, maxBlocks);
        }

        [HarmonyPatch(typeof(Minimap), "Explore", new[] { typeof(int), typeof(int) })]
        private static class Explore
        {
            private static void Postfix(Minimap __instance, int x, int y, bool __result)
            {
                if (__result && trackers.TryGetValue(__instance, out var tracker)) tracker.Changed(false, x, y);
            }
        }

        [HarmonyPatch(typeof(Minimap), "ExploreOthers")]
        private static class ExploreOthers
        {
            private static void Postfix(Minimap __instance, int x, int y, bool __result)
            {
                if (__result && trackers.TryGetValue(__instance, out var tracker)) tracker.Changed(true, x, y);
            }
        }

        [HarmonyPatch(typeof(Minimap), "Reset")]
        private static class Reset
        {
            private static void Postfix(Minimap __instance)
            { if (trackers.TryGetValue(__instance, out var tracker)) tracker.Reset(false); }
        }

        [HarmonyPatch(typeof(Minimap), "ResetSharedMapData")]
        private static class ResetShared
        {
            private static void Postfix(Minimap __instance)
            { if (trackers.TryGetValue(__instance, out var tracker)) tracker.Reset(true); }
        }

        [HarmonyPatch(typeof(Minimap), "ResetAndExplore", new[] { typeof(BitArray), typeof(BitArray) })]
        private static class Replace
        {
            private static void Postfix(Minimap __instance)
            { if (trackers.TryGetValue(__instance, out var tracker)) tracker.Reset(false); }
        }
    }
}
