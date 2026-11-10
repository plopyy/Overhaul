using HarmonyLib;

namespace Overhaul.AI
{
    // World events and spawns that follow the server's progress instead of the player's (user, 2026-11-10: new players were
    // attacked by mobs far too strong for them).
    // - Raids: the random events (raids near bases, and the events that start on their own at an interval) never start
    //   while [World] Raids is false. RandEventSystem.HaveGlobalKeys is where both kinds ask whether they may run; the
    //   console's "event" command does not go through it and still works.
    // - Boss-key spawns: the world spawns that only appear once a global key is set (bosses defeated on the server) stay
    //   away while [World] BossKeySpawns is false: within SpawnSystem.UpdateSpawnList a named global key reads as unset.
    //   Ordinary biome spawns are unchanged.
    internal static class WorldEvents
    {
        static bool RaidsOn => Utility.OverhaulConfig.Raids?.Value ?? false;
        static bool KeySpawnsOn => Utility.OverhaulConfig.BossKeySpawns?.Value ?? false;

        [HarmonyPatch(typeof(RandEventSystem), "HaveGlobalKeys")]
        static class NoRaids
        {
            static bool Prefix(ref bool __result)
            {
                if (RaidsOn) return true;
                __result = false;
                return false;
            }
        }

        static bool inSpawnList;

        [HarmonyPatch(typeof(SpawnSystem), "UpdateSpawnList")]
        static class SpawnListScope
        {
            static void Prefix() => inSpawnList = true;
            static void Finalizer() => inSpawnList = false;
        }

        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.GetGlobalKey), typeof(string))]
        static class NoKeySpawns
        {
            static bool Prefix(string name, ref bool __result)
            {
                if (!inSpawnList || KeySpawnsOn || string.IsNullOrEmpty(name)) return true;
                __result = false;
                return false;
            }
        }
    }
}
