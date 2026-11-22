using System.Diagnostics;
using HarmonyLib;
using Overhaul.Utility;
using UnityEngine;

namespace Overhaul.Dungeons
{
    // Diagnostic (user, 2026-11-22: players dropped while a dungeon generates for the first time, logs of the bad session
    // lost): every dungeon generation and room spawn logs where it ran (server/client), the location, the mode, the rooms,
    // the objects created and how long the main thread was held, so the next drop leaves a trace on whoever froze.
    internal static class DungeonTiming
    {
        struct Mark { public long Ticks; public int Objects; }

        static Mark Begin() => new Mark { Ticks = Stopwatch.GetTimestamp(), Objects = ZDOMan.instance != null ? ZDOMan.instance.NrOfObjects() : 0 };

        static void End(DungeonGenerator gen, string step, Mark mark, string extra)
        {
            double ms = (Stopwatch.GetTimestamp() - mark.Ticks) * 1000.0 / Stopwatch.Frequency;
            int objects = ZDOMan.instance != null ? ZDOMan.instance.NrOfObjects() - mark.Objects : 0;
            var location = gen ? gen.GetComponentInParent<Location>() : null;
            string where = ZNet.instance && ZNet.instance.IsServer() ? "server" : "client";
            int peers = ZNet.instance ? ZNet.instance.GetPeers().Count : 0;
            string line = "[Dungeons] timing " + step + " on " + where + ": " + (location ? Utils.GetPrefabName(location.gameObject) : gen ? gen.name : "?")
                + " at " + (gen ? gen.transform.position.ToString("0") : "?") + extra + ", " + objects + " objects, " + ms.ToString("0") + " ms, " + peers + " peers";
            if (ms >= 500) Log.LogWarning(line); else Log.LogInfo(line);
        }

        [HarmonyPatch(typeof(DungeonGenerator), "Generate", new System.Type[] { typeof(int), typeof(ZoneSystem.SpawnMode) })]
        static class GenerateTime
        {
            [HarmonyPriority(Priority.First)] static void Prefix(out Mark __state) => __state = Begin();
            static void Finalizer(DungeonGenerator __instance, ZoneSystem.SpawnMode mode, Mark __state)
                => End(__instance, "generate", __state, ", mode " + mode + ", " + (DungeonGenerator.m_placedRooms?.Count ?? 0) + " rooms");
        }

        [HarmonyPatch(typeof(DungeonGenerator), "Spawn")]
        static class SpawnTime
        {
            [HarmonyPriority(Priority.First)] static void Prefix(out Mark __state) => __state = Begin();
            static void Finalizer(DungeonGenerator __instance, Mark __state) => End(__instance, "spawn", __state, "");
        }
    }
}
