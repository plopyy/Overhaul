using System.Collections.Generic;
using HarmonyLib;

namespace Overhaul.AI
{
    // Deep North felt empty (user, 2026-11-22): its land mobs try to spawn every 2 minutes (native 10 to 25 min, elakings
    // already 2), five times as many can be around at once, and elakings also spawn by day. Chance and group size stay
    // native. Other mobs of the biome are left alone (user does not want to know them yet). The world spawn entries are
    // shared assets, so each one is tuned once, when a spawn system wakes.
    internal static class DeepNorthSpawns
    {
        const float Interval = 120f;
        const int DensityMultiplier = 5;
        static readonly HashSet<string> Targets = new HashSet<string> { "Spawner_TrollFrost", "Barka", "Moose", "Elaking", "ElakingLantern", "Greydwarf_Frozen", "Greydwarf_Shaman_Frozen", "Skeleton_DeepNorth" };
        static readonly HashSet<SpawnSystem.SpawnData> tuned = new HashSet<SpawnSystem.SpawnData>();

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
                        string name = data.m_prefab.name; if (!Targets.Contains(name)) continue;
                        if (data.m_maxSpawned > 0) data.m_maxSpawned *= DensityMultiplier;
                        data.m_spawnInterval = Interval;
                        if (name == "Elaking") data.m_spawnAtDay = true;
                    }
                }
            }
        }
    }
}
