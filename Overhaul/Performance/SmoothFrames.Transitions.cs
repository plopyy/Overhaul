using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Overhaul.Utility;

namespace Overhaul.Performance
{
    // Smooth frames, transitions: portal travel and dungeon doors, kept to the game's own arrival checks but without its
    // fixed waits. While the screen is black ("loading"):
    // - the destination's terrain is asked for first (and before that, while you walk up to a portal);
    // - the area is created close-first and in front of the camera first, buildings before distant rocks, trees and bushes;
    //   only the 3x3 zones the arrival needs are created before you arrive, the rest right after;
    // - zones and objects are created in bigger batches (nobody sees the frame rate behind a black screen);
    // - you arrive as soon as the ground and the buildings around the arrival point stopped coming in, instead of after
    //   the game's 2 s minimum and 8 s portal wait; the game's 15 s limit stays;
    // - if the ground under you finished building only after you arrived, you are put back on it (no fall).
    // Teleport cooldown 0.5 s instead of 2 (still enough not to bounce straight back through the portal).
    internal static partial class SmoothFrames
    {
        internal static bool Transitions = true;

        // ---- Loading state ----
        internal static bool Loading { get; private set; }
        internal static bool LoadingDistant { get; private set; }
        internal static Vector3 LoadingTarget { get; private set; }
        static Vector3 loadingFrom;
        static float catchUpUntil = -1f;
        internal static bool CatchingUp => Time.realtimeSinceStartup <= catchUpUntil;
        static bool LoadingOn => Loading && On(Transitions);

        static void TickTransitions()
        {
            var player = Player.m_localPlayer;
            bool now = player && player.m_teleporting;
            if (now && !Loading)
            {
                LoadingTarget = player.m_teleportTargetPos; loadingFrom = player.m_teleportFromPos; LoadingDistant = player.m_distantTeleport;
                floorArmed = false;
                if (On(Transitions)) AskTerrain(LoadingTarget, true);
            }
            if (!now && Loading)
            {
                catchUpUntil = Time.realtimeSinceStartup + 6f;
                StreamingSkip.Version++;
                if (On(Transitions) && LoadingDistant) ArmGroundWatch();
            }
            Loading = now;
            if (now && On(Transitions)) { PromoteTerrain(LoadingTarget); RushZones(); }
            HarvestTerrain();
            GroundWatch(player);
        }

        // ---- Destination terrain first ----
        static readonly List<Vector3> wantedTiles = new List<Vector3>();
        static Heightmap zoneHeightmap;

        static bool ZoneTileShape(out int width, out float scale, out bool distant)
        {
            width = 0; scale = 0; distant = false;
            if (!zoneHeightmap) zoneHeightmap = ZoneSystem.instance && ZoneSystem.instance.m_zonePrefab ? ZoneSystem.instance.m_zonePrefab.GetComponentInChildren<Heightmap>() : null;
            if (!zoneHeightmap) return false;
            width = zoneHeightmap.m_width; scale = zoneHeightmap.m_scale; distant = zoneHeightmap.m_isDistantLod;
            return true;
        }

        // Queues the 3x3 zones around a point with the terrain builder (cheap if built or queued already).
        static void AskTerrain(Vector3 point, bool remember)
        {
            var builder = HeightmapBuilder.instance; var world = WorldGenerator.instance;
            if (builder == null || world == null || !ZoneTileShape(out int width, out float scale, out bool distant)) return;
            var zone = ZoneSystem.GetZone(point);
            if (remember) wantedTiles.Clear();
            for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                {
                    var center = ZoneSystem.GetZonePos(new Vector2s(zone.x + x, zone.y + y));
                    builder.IsTerrainReady(center, width, scale, distant, world);
                    if (remember) wantedTiles.Add(center);
                }
        }

        // Moves the destination's queued tiles to the front of the builder's queue.
        static void PromoteTerrain(Vector3 point)
        {
            var builder = HeightmapBuilder.instance;
            if (builder == null) return;
            var zone = ZoneSystem.GetZone(point);
            lock (builder.m_lock)
            {
                var queue = builder.m_toBuild;
                // Index 0 may be the tile being built right now; promoted tiles go behind it.
                int front = Mathf.Min(1, queue.Count);
                for (int i = front; i < queue.Count; i++)
                {
                    var tile = ZoneSystem.GetZone(queue[i].m_center);
                    if (Mathf.Abs(tile.x - zone.x) > 1 || Mathf.Abs(tile.y - zone.y) > 1) continue;
                    if (i != front) { var data = queue[i]; queue.RemoveAt(i); queue.Insert(front, data); }
                    front++;
                }
            }
        }

        // Finished destination tiles go into the terrain memory, so the builder's small ready list cannot drop them.
        static void HarvestTerrain()
        {
            var builder = HeightmapBuilder.instance;
            if (wantedTiles.Count == 0 || builder == null || !On(TerrainMemory)) return;
            lock (builder.m_lock)
                for (int i = builder.m_ready.Count - 1; i >= 0; i--)
                {
                    var data = builder.m_ready[i];
                    foreach (var center in wantedTiles)
                        if (Mathf.Approximately(center.x, data.m_center.x) && Mathf.Approximately(center.z, data.m_center.z))
                        { builder.m_ready.RemoveAt(i); Remember(data); break; }
                }
        }

        // Walking up to a portal (30 m): its destination's terrain is prepared already.
        // Each portal checks itself regularly (UpdatePortal); the nearest one within 30 m warms its destination.
        static TeleportWorld warmedPortal;
        static Vector3 warmedTarget;
        static float warmedDistance;

        [HarmonyPatch(typeof(TeleportWorld), "UpdatePortal")]
        private static class Warm
        {
            private static void Postfix(TeleportWorld __instance)
            {
                var player = Player.m_localPlayer;
                if (!player || player.m_teleporting || !On(Transitions) || __instance.m_nview == null || !__instance.m_nview.IsValid()) return;
                float distance = Vector3.Distance(player.transform.position, __instance.transform.position);
                if (distance > 30f) { if (warmedPortal == __instance) warmedPortal = null; return; }
                // Another portal takes over only when clearly closer.
                if (warmedPortal && warmedPortal != __instance && distance >= warmedDistance * 0.8f) return;
                var id = __instance.m_nview.GetZDO().GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
                if (id.IsNone()) return;
                var target = ZDOMan.instance.GetZDO(id);
                if (target == null) { ZDOMan.instance.RequestZDO(id); return; }
                warmedDistance = distance;
                var position = target.GetPosition();
                if (warmedPortal == __instance && (position - warmedTarget).sqrMagnitude < 0.01f) return;
                warmedPortal = __instance; warmedTarget = position;
                AskTerrain(position, true);
            }
        }

        // ---- Zones in batches while loading ----
        static void RushZones()
        {
            if (!LoadingDistant || !ZoneSystem.instance || !ZNet.instance) return;
            var watch = Stopwatch.StartNew();
            var reference = ZNet.instance.GetReferencePosition();
            while (watch.Elapsed.TotalMilliseconds < 50 && ZoneSystem.instance.CreateLocalZones(reference)) { }
        }

        // ---- Objects in bigger batches while loading ----
        // The game creates at most 100 objects per pass behind a loading screen; it may create as many as fit in 100 ms,
        // from the measured cost of an object.
        static double msPerObject = 0.2;
        static long createStart;
        static int createdBefore;

        public static int LoadingBatch() =>
            LoadingOn ? Mathf.Clamp((int)(100.0 / Math.Max(0.01, msPerObject)), 100, 20000) : 100;

        [HarmonyPatch(typeof(ZNetScene), "CreateObjects")]
        private static class ObjectBatch
        {
            private static void Prefix(ZNetScene __instance) { createStart = Stopwatch.GetTimestamp(); createdBefore = __instance.m_instances.Count; }

            private static void Postfix(ZNetScene __instance)
            {
                int made = __instance.m_instances.Count - createdBefore;
                if (made < 5) return;
                double ms = (Stopwatch.GetTimestamp() - createStart) * 1000.0 / Stopwatch.Frequency / made;
                msPerObject = msPerObject * 0.8 + ms * 0.2;
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                int replaced = 0;
                foreach (var instruction in code)
                {
                    if ((instruction.opcode == OpCodes.Ldc_I4_S || instruction.opcode == OpCodes.Ldc_I4) && Convert.ToInt32(instruction.operand) == 100)
                    { replaced++; yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SmoothFrames), nameof(LoadingBatch))).MoveLabelsFrom(instruction); }
                    else yield return instruction;
                }
                if (replaced != 1) Log.LogWarning("[Performance] Loading batch: expected one limit of 100 in ZNetScene.CreateObjects, replaced " + replaced);
            }
        }

        // ---- Close and in-view first, buildings before distant nature, only the needed area while loading ----
        public static float CreateOrder(Vector3 reference, Vector3 position, ZDO zdo)
        {
            float distance = Utils.DistanceSqr(reference, position);
            if (!LoadingOn) return distance;
            if (Deferred(zdo, position)) return distance + 1e9f;
            var camera = Utils.GetMainCamera();
            if (!camera) return distance;
            var towards = position - camera.transform.position;
            if (towards.sqrMagnitude < 0.001f) return distance;
            float facing = Vector3.Dot(camera.transform.forward, towards.normalized);
            return distance * Mathf.Lerp(4f, 1f, (facing + 1f) * 0.5f);
        }

        static readonly Dictionary<int, bool> builtPrefab = new Dictionary<int, bool>();
        static bool Deferred(ZDO zdo, Vector3 position)
        {
            if (zdo == null || zdo.Type != ZDO.ObjectType.Default) return false;
            var target = ZoneSystem.GetZone(LoadingTarget); var zone = ZoneSystem.GetZone(position);
            if (Mathf.Abs(zone.x - target.x) <= 1 && Mathf.Abs(zone.y - target.y) <= 1) return false;
            int hash = zdo.GetPrefab();
            if (!builtPrefab.TryGetValue(hash, out bool built))
            {
                var prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab(hash) : null;
                built = !prefab || prefab.GetComponent<Piece>() || prefab.GetComponent<WearNTear>() || prefab.GetComponent<TerrainComp>() || prefab.GetComponent<TerrainModifier>();
                builtPrefab[hash] = built;
            }
            return !built;
        }

        public static void SortCreation(List<ZDO> list, Comparison<ZDO> game)
        {
            if (!LoadingOn) { list.Sort(game); return; }
            list.Sort((a, b) =>
            {
                bool da = a.m_tempSortValue >= 1e9f, db = b.m_tempSortValue >= 1e9f;
                if (da != db) return da ? 1 : -1;
                return game(a, b);
            });
        }

        [HarmonyPatch(typeof(ZNetScene), "CreateObjectsSorted")]
        private static class CreationOrder
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                var distance = AccessTools.Method(typeof(Utils), nameof(Utils.DistanceSqr));
                var position = AccessTools.Method(typeof(ZDO), nameof(ZDO.GetPosition));
                var sort = AccessTools.Method(typeof(List<ZDO>), nameof(List<ZDO>.Sort), new[] { typeof(Comparison<ZDO>) });
                var list = new List<CodeInstruction>(code);
                bool order = false, sorted = false;
                for (int i = 2; i < list.Count; i++)
                {
                    // ... zdo; call GetPosition; call DistanceSqr  ->  ... zdo; call GetPosition; <load zdo>; call CreateOrder
                    if (!order && list[i].Calls(distance) && list[i - 1].Calls(position) && list[i - 2].IsLdloc())
                    {
                        list.Insert(i, new CodeInstruction(list[i - 2].opcode, list[i - 2].operand));
                        list[i + 1] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SmoothFrames), nameof(CreateOrder)));
                        order = true;
                    }
                    else if (!sorted && list[i].Calls(sort))
                    {
                        list[i] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SmoothFrames), nameof(SortCreation))).MoveLabelsFrom(list[i]);
                        sorted = true;
                    }
                }
                if (!order || !sorted) Log.LogWarning("[Performance] Creation order: game code changed (order " + order + ", sort " + sorted + ")");
                return list;
            }
        }

        // Only the 3x3 zones the arrival waits for are created while loading (unless the destination is next to where you were).
        public static SimulationDistance StreamingDistance(ZNet net)
        {
            var synced = net.GetSyncedSimulationDistance();
            if (!LoadingOn) return synced;
            var from = ZoneSystem.GetZone(loadingFrom); var to = ZoneSystem.GetZone(LoadingTarget);
            int near = synced.NearSimulationDistance - 1;
            if (near >= 0 && Mathf.Abs(to.x - from.x) <= near && Mathf.Abs(to.y - from.y) <= near) return synced;
            var narrow = SimulationDistance.GetSimulationDistance(0);
            return narrow.NearSimulationDistance < synced.NearSimulationDistance ? narrow : synced;
        }

        [HarmonyPatch(typeof(ZNetScene), "CreateDestroyObjects")]
        private static class NarrowWhileLoading
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                var synced = AccessTools.Method(typeof(ZNet), nameof(ZNet.GetSyncedSimulationDistance));
                int replaced = 0;
                foreach (var instruction in code)
                {
                    if (instruction.Calls(synced)) { replaced++; yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SmoothFrames), nameof(StreamingDistance))).MoveLabelsFrom(instruction); }
                    else yield return instruction;
                }
                if (replaced != 1) Log.LogWarning("[Performance] Narrow loading area: expected one simulation distance read in ZNetScene.CreateDestroyObjects, replaced " + replaced);
            }
        }

        // ---- Arrival ----
        // Minimum wait before being moved: none for portals, 1 s for dungeon doors (the game: 2 s for both).
        public static float ArrivalMinimum()
        {
            if (!On(Transitions)) return 2f;
            var player = Player.m_localPlayer;
            return player && player.m_distantTeleport ? 0f : 1f;
        }

        // The game's 8 s portal wait goes: our floor check below decides instead.
        public static float PortalWait() => On(Transitions) ? 0f : 8f;

        // The floor check: the floor must be found, then the ground and buildings around the arrival must have stopped
        // coming in: on the host at once; else 0.75 s without a new one, or 1 s (floor at most 1.5 m below) / 3 s if
        // nothing came at all; at most 10 s.
        static bool floorArmed;
        static Vector3 floorPoint;
        static float floorSince, floorGrewAt, nextCount;
        static int floorKnown, floorKnownAtStart;
        static readonly List<ZDO> nearObjects = new List<ZDO>();

        public static bool ArrivalFloor(ZoneSystem zones, Vector3 point, out float height)
        {
            bool found = zones.FindFloor(point, out height);
            var player = Player.m_localPlayer;
            if (!On(Transitions) || !player || !player.m_distantTeleport) { floorArmed = false; return found; }
            float now = Time.realtimeSinceStartup;
            if (!floorArmed || (point - floorPoint).sqrMagnitude > 0.01f)
            {
                floorArmed = true; floorPoint = point; floorSince = now; floorGrewAt = now; nextCount = now + 0.2f;
                floorKnown = floorKnownAtStart = CountSupports(point);
            }
            if (!found) return false;
            if (ZNet.instance == null || ZNet.instance.IsServer()) return true;
            if (now >= nextCount)
            {
                nextCount = now + 0.2f;
                int known = CountSupports(point);
                if (known > floorKnown) { floorKnown = known; floorGrewAt = now; }
            }
            float waited = now - floorSince;
            if (waited > 10f) return true;
            // The server says the player holds the arrival's ground and buildings: arrive. It is given 3 s to say so
            // before the objects' own coming in decides.
            if (ArrivalConfirmed(point)) return true;
            if (waited < 3f) return false;
            bool shallow = point.y - height <= 1.5f;
            bool settled = floorKnown > floorKnownAtStart ? now - floorGrewAt >= 0.75f : waited >= (shallow ? 1f : 3f);
            return settled && (shallow || ZNetScene.instance == null || ZNetScene.instance.IsAreaReady(point));
        }

        static int CountSupports(Vector3 point)
        {
            if (ZDOMan.instance == null) return 0;
            nearObjects.Clear();
            ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(point), new SimulationDistance(1, 0, false), nearObjects, null);
            int count = 0;
            foreach (var zdo in nearObjects)
                if (zdo.Type >= ZDO.ObjectType.Solid && (zdo.GetPosition() - point).sqrMagnitude <= 256f) count++;
            return count;
        }

        [HarmonyPatch(typeof(Player), "UpdateTeleport")]
        private static class Arrival
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                var findFloor = AccessTools.Method(typeof(ZoneSystem), nameof(ZoneSystem.FindFloor));
                int minimum = 0, wait = 0, floor = 0;
                foreach (var instruction in code)
                {
                    if (instruction.opcode == OpCodes.Ldc_R4 && (float)instruction.operand == 2f && minimum == 0)
                    { minimum++; yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SmoothFrames), nameof(ArrivalMinimum))).MoveLabelsFrom(instruction); }
                    else if (instruction.opcode == OpCodes.Ldc_R4 && (float)instruction.operand == 8f && wait == 0)
                    { wait++; yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SmoothFrames), nameof(PortalWait))).MoveLabelsFrom(instruction); }
                    else if (instruction.Calls(findFloor))
                    { floor++; yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SmoothFrames), nameof(ArrivalFloor))).MoveLabelsFrom(instruction); }
                    else yield return instruction;
                }
                if (minimum != 1 || wait != 1 || floor != 1) Log.LogWarning("[Performance] Arrival: game code changed (2 s " + minimum + ", 8 s " + wait + ", floor " + floor + ")");
            }
        }

        public static float TeleportCooldown() => On(Transitions) ? 0.5f : 2f;

        [HarmonyPatch(typeof(Player), nameof(Player.TeleportTo))]
        private static class Cooldown
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                var field = AccessTools.Field(typeof(Player), "m_teleportCooldown");
                var list = new List<CodeInstruction>(code);
                int replaced = 0;
                for (int i = 0; i + 1 < list.Count; i++)
                    if (list[i].LoadsField(field) && list[i + 1].opcode == OpCodes.Ldc_R4 && (float)list[i + 1].operand == 2f)
                    { list[i + 1] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SmoothFrames), nameof(TeleportCooldown))).MoveLabelsFrom(list[i + 1]); replaced++; }
                if (replaced != 1) Log.LogWarning("[Performance] Teleport cooldown: game code changed, replaced " + replaced);
                return list;
            }
        }

        // ---- Ground finished after arrival ----
        // For 5 s after a portal arrival: if the ground at the arrival point rose (it finished building late) and you stand
        // below it within 4 m, you are put back on top, without fall damage.
        static bool groundArmed;
        static Vector3 groundPoint;
        static float groundBase, groundUntil;

        static void ArmGroundWatch()
        {
            groundArmed = ZoneSystem.instance && ZoneSystem.instance.GetGroundHeight(LoadingTarget, out groundBase);
            groundPoint = LoadingTarget;
            groundUntil = Time.realtimeSinceStartup + 5f;
        }

        static void GroundWatch(Player player)
        {
            if (!groundArmed) return;
            if (!player || Time.realtimeSinceStartup > groundUntil || !On(Transitions)) { groundArmed = false; return; }
            var position = player.transform.position;
            if (new Vector2(position.x - groundPoint.x, position.z - groundPoint.z).sqrMagnitude > 16f) { groundArmed = false; return; }
            if (!ZoneSystem.instance.GetGroundHeight(groundPoint, out float ground) || ground < groundBase + 0.25f) return;
            if (!ZoneSystem.instance.GetGroundHeight(position, out float under)) return;
            groundArmed = false;
            if (position.y >= under - 0.05f) return;
            position.y = under + 0.1f;
            player.transform.position = position;
            if (player.m_body) player.m_body.linearVelocity = Vector3.zero;
            player.m_maxAirAltitude = position.y;
        }
    }
}
