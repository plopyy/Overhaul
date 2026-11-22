using System.Collections.Generic;
using HarmonyLib;

namespace Overhaul.AI
{
    // Deep North felt empty (user, 2026-11-22): frost trolls and elakings spawn five times as often, one at a time instead of
    // in groups. The world spawn entries are shared assets, so each one is tuned once, the first time a spawn system wakes.
    // The cap per zone keeps at least the native group size, so the count around the player can still reach what a native
    // group gave, just spread out over time.
    internal static class DeepNorthSpawns
    {
        const float RateMultiplier = 5f;
        static readonly HashSet<SpawnSystem.SpawnData> tuned = new HashSet<SpawnSystem.SpawnData>();

        static bool Target(string prefab) => prefab == "TrollFrost" || prefab.StartsWith("Elaking");

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
                        data.m_spawnInterval = UnityEngine.Mathf.Max(1f, data.m_spawnInterval / RateMultiplier);
                        if (data.m_maxSpawned > 0) data.m_maxSpawned = UnityEngine.Mathf.Max(data.m_maxSpawned, data.m_groupSizeMax);
                        data.m_groupSizeMin = data.m_groupSizeMax = 1;
                    }
                }
            }
        }
    }
}
