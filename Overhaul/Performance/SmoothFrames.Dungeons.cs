using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;
using Overhaul.Utility;

namespace Overhaul.Performance
{
    // Smooth frames, dungeons: when a dungeon's zone comes in, the game places all its rooms in one frame (40-110 ms near
    // crypts and caves). The rooms are placed in the game's order with its own PlaceRoom, within 4 ms per frame (at least
    // one per frame); each room is the same as the game makes it (PlaceRoom keeps its own random seed per room). The
    // zone keeps counting as loading, and the rooms' assets stay held, until the last room is in. Behind a loading
    // screen everything is placed at once.
    internal static partial class SmoothFrames
    {
        internal static bool DungeonSpread = true;
        const double DungeonBudgetMs = 4;

        sealed class RoomJob { internal DungeonGenerator Generator; internal DungeonGenerator.RoomPlacementData[] Rooms; internal int Next; }
        static readonly List<RoomJob> roomJobs = new List<RoomJob>();
        static bool releasing;

        static RoomJob JobOf(DungeonGenerator generator)
        {
            foreach (var job in roomJobs) if (job.Generator == generator) return job;
            return null;
        }

        static bool BehindLoadingScreen => !Player.m_localPlayer || Player.m_localPlayer.m_teleporting;

        [HarmonyPatch(typeof(DungeonGenerator), "Spawn")]
        private static class SpreadRooms
        {
            private static bool Prefix(DungeonGenerator __instance)
            {
                var rooms = __instance.m_loadedRooms;
                if (!On(DungeonSpread) || BehindLoadingScreen || rooms == null || rooms.Length <= 1) return true;
                ZLog.Log("Spawning dungeon");
                var job = new RoomJob { Generator = __instance, Rooms = rooms };
                roomJobs.Add(job);
                Place(job, false);
                return false;
            }
        }

        // While its rooms are being placed, the game's own release (right after Spawn) waits: the rooms' assets must stay.
        [HarmonyPatch(typeof(DungeonGenerator), "ReleaseHeldReferences")]
        private static class HoldWhilePlacing
        {
            private static bool Prefix(DungeonGenerator __instance) => releasing || JobOf(__instance) == null;
        }

        [HarmonyPatch(typeof(DungeonGenerator), "OnDestroy")]
        private static class DropJob
        {
            private static void Prefix(DungeonGenerator __instance) { var job = JobOf(__instance); if (job != null) roomJobs.Remove(job); }
        }

        static void TickDungeons()
        {
            if (roomJobs.Count == 0) return;
            if (!ZoneSystem.instance) { roomJobs.Clear(); return; }
            bool all = !On(DungeonSpread) || BehindLoadingScreen;
            foreach (var job in roomJobs.ToArray())
            {
                if (!job.Generator) { roomJobs.Remove(job); continue; }
                Place(job, all);
            }
        }

        static void Place(RoomJob job, bool all)
        {
            try
            {
                var watch = Stopwatch.StartNew();
                do
                {
                    var room = job.Rooms[job.Next++];
                    job.Generator.PlaceRoom(room.m_roomData, room.m_position, room.m_rotation, null, ZoneSystem.SpawnMode.Client);
                }
                while (job.Next < job.Rooms.Length && (all || watch.Elapsed.TotalMilliseconds < DungeonBudgetMs));
                if (job.Next >= job.Rooms.Length) Finish(job);
            }
            catch (Exception e)
            {
                Log.LogWarning("[Performance] Dungeon rooms: placing failed, finishing the game's way: " + e.Message);
                try { while (job.Next < job.Rooms.Length) { var room = job.Rooms[job.Next++]; job.Generator.PlaceRoom(room.m_roomData, room.m_position, room.m_rotation, null, ZoneSystem.SpawnMode.Client); } }
                catch (Exception again) { Log.LogWarning("[Performance] Dungeon rooms: " + again.Message); }
                Finish(job);
            }
        }

        // The end of the game's Spawn, then the release it would have done.
        static void Finish(RoomJob job)
        {
            roomJobs.Remove(job);
            if (!job.Generator) return;
            try { SnapToGround.SnappAll(); job.Generator.m_loadedRooms = null; }
            finally { releasing = true; try { job.Generator.ReleaseHeldReferences(); } finally { releasing = false; } }
        }
    }
}
