using System.Collections.Generic;
using HarmonyLib;

namespace Overhaul.AI
{
    // Deep North felt empty (user, 2026-11-22): five times as many frost trolls and barkas can be around at once. Only the
    // cap of live instances per spawn entry is raised; spawn interval, chance and group size stay native. The world spawn
    // entries are shared assets, so each one is tuned once, the first time a spawn system wakes.
    internal static class DeepNorthSpawns
    {
        const int DensityMultiplier = 5;
        static readonly HashSet<SpawnSystem.SpawnData> tuned = new HashSet<SpawnSystem.SpawnData>();

        static bool Target(string prefab) => prefab == "TrollFrost" || prefab == "Barka";

        [HarmonyPatch(typeof(SpawnSystem), nameof(SpawnSystem.Awake))]
        static class Tune
        {
            static void Prefix(SpawnSystem __instance)
            {
                foreach (var list in __instance.m_spawnLists)
                {
                    if (!list) continue;
                    foreach (var data in list.m_spawners)
                    {
                        if (data?.m_prefab == null || (data.m_biome & Heightmap.Biome.DeepNorth) == 0 || !Target(data.m_prefab.name) || !tuned.Add(data)) continue;
                        if (data.m_maxSpawned > 0) data.m_maxSpawned *= DensityMultiplier;
                    }
                }
            }
        }
    }
}
