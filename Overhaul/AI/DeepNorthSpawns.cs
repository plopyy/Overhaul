using System.Collections.Generic;
using HarmonyLib;

namespace Overhaul.AI
{
    // Deep North felt empty (user, 2026-11-22): more live instances of a few mobs (cap per spawn entry x5 for frost trolls
    // and barkas, x3 for elakings, x2 for moose) and they come back a bit faster (spawn interval / 1.5). Chance and group
    // size stay native. The world spawn entries are shared assets, so each one is tuned once, when a spawn system wakes.
    internal static class DeepNorthSpawns
    {
        const float RateMultiplier = 1.5f;
        static readonly HashSet<SpawnSystem.SpawnData> tuned = new HashSet<SpawnSystem.SpawnData>();

        static int Density(string prefab) => prefab == "Spawner_TrollFrost" || prefab == "TrollFrost" || prefab == "Barka" ? 5 : prefab.StartsWith("Elaking") ? 3 : prefab == "Moose" ? 2 : 0;

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
                        if (data?.m_prefab == null || (data.m_biome & Heightmap.Biome.DeepNorth) == 0 || !tuned.Add(data)) continue;
                        int density = Density(data.m_prefab.name); if (density == 0) continue;
                        if (data.m_maxSpawned > 0) data.m_maxSpawned *= density;
                        data.m_spawnInterval /= RateMultiplier;
                    }
                }
            }
        }
    }
}
