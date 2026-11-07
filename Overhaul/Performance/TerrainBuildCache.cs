using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Overhaul.Utility;

namespace Overhaul.Performance
{
    // The game builds terrain tiles on a background thread.
    // Ready cache: it keeps only 16 finished tiles; tiles requested ahead of time could be thrown away just before
    // they are used and then rebuilt while the game waits. 128 are kept (about 10 MB).
    // Waited tiles first: when the game stops and waits for one tile (RequestTerrainSync), that tile was queued
    // behind every distant tile already waiting. It is moved to the front of the queue instead.
    internal static class TerrainBuildCache
    {
        private const int VanillaReady = 16, LargerReady = 128;

        public static int ReadyCap() => PerfMonitor.On(OverhaulConfig.PerfTerrainCache) ? LargerReady : VanillaReady;

        [HarmonyPatch(typeof(HeightmapBuilder), "BuildThread")]
        private static class Cap
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                int replaced = 0;
                foreach (var instruction in code)
                {
                    if ((instruction.opcode == OpCodes.Ldc_I4_S || instruction.opcode == OpCodes.Ldc_I4) && System.Convert.ToInt32(instruction.operand) == VanillaReady)
                    {
                        replaced++;
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TerrainBuildCache), nameof(ReadyCap))).MoveLabelsFrom(instruction);
                    }
                    else yield return instruction;
                }
                if (replaced != 1) Log.LogWarning("[Performance] Terrain ready cache: expected one limit of 16 in HeightmapBuilder.BuildThread, replaced " + replaced);
            }
        }

        [HarmonyPatch(typeof(HeightmapBuilder), nameof(HeightmapBuilder.RequestTerrainSync))]
        private static class WaitedFirst
        {
            private static void Prefix(HeightmapBuilder __instance, Vector3 center, int width, float scale, bool distantLod, WorldGenerator worldGen)
            {
                if (!PerfMonitor.On(OverhaulConfig.PerfTerrainCache)) return;
                lock (__instance.m_lock)
                {
                    foreach (var ready in __instance.m_ready)
                        if (ready.IsEqual(center, width, scale, distantLod, worldGen)) return;
                    var queue = __instance.m_toBuild;
                    int index = queue.FindIndex(b => b.IsEqual(center, width, scale, distantLod, worldGen));
                    // Index 0 may be the tile being built right now; the builder removes it by reference, so the
                    // waited tile goes right after it and is built next.
                    int front = queue.Count > 0 && index != 0 ? 1 : 0;
                    if (index == 0 || index == 1) return;
                    var data = index > 0 ? queue[index] : new HeightmapBuilder.HMBuildData(center, width, scale, distantLod, worldGen);
                    if (index > 0) queue.RemoveAt(index);
                    queue.Insert(Mathf.Min(front, queue.Count), data);
                    PerfMonitor.TerrainPromoted++;
                }
            }
        }
    }
}
