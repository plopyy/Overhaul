using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Overhaul.Utility;
using UnityEngine;

namespace Overhaul.Dungeons
{
    internal static partial class DungeonRuntime
    {
        internal const float SweepIntervalSeconds = 300f;
        private static float nextSweep, nextVisitPoll, failureUntil;
        private static readonly Dictionary<Vector2s, HashSet<ZDOID>> visitSites = new Dictionary<Vector2s, HashSet<ZDOID>>();
        private static readonly Dictionary<ZDOID, List<Bounds>> visitBounds = new Dictionary<ZDOID, List<Bounds>>();
        private static readonly Dictionary<ZDOID, string> visitIssues = new Dictionary<ZDOID, string>();

        private static void RegisterVisitSite(ZDO proxy)
        {
            if (!ZoneSystem.instance) return;
            var location = ZoneSystem.instance.GetLocation(proxy.GetInt(ZDOVars.s_location, 0));
            if (location == null || !DungeonPolicy.Supported.Contains(location.m_prefab.Name)) return;
            Vector2s zone = ZoneSystem.GetZone(proxy.GetPosition());
            HashSet<ZDOID> sites;
            if (!visitSites.TryGetValue(zone, out sites)) visitSites[zone] = sites = new HashSet<ZDOID>();
            sites.Add(proxy.m_uid);
        }

        private static void QueueReset(ZDOID id)
        {
            if (preparation != null && preparation.Id == id) return;
            if (queued.Add(id)) queue.Enqueue(id);
        }

        private static bool EligibleForSweep(ZDO proxy, long utcTicks)
        {
            var location = ZoneSystem.instance.GetLocation(proxy.GetInt(ZDOVars.s_location, 0));
            if (location == null || !DungeonPolicy.Supported.Contains(location.m_prefab.Name)) return false;
            byte[] journal = proxy.GetByteArray(JournalKey, null);
            if (journal != null && journal.Length != 0) return true; // Complete a transaction already started.
            if (!selected.Contains(location.m_prefab.Name)) return false;
            long visited = proxy.GetLong(VisitStartedKey, 0);
            if (visited == 0) return false; // Manual requests keep their separate one-shot queue.
            // Manual requests enter the queue directly. They must never override the deadline
            // during a periodic sweep, including requests saved by an older plugin version.
            return DungeonPolicy.IsDue(visited, utcTicks, OverhaulConfig.ResetIntervalHours.Value);
        }

        private static void SweepDueLocations(float realtime, long utcTicks)
        {
            if (!scanning)
            {
                if (realtime < nextSweep) return;
                nextSweep = realtime + SweepIntervalSeconds;
                proxies.Clear(); scanIndex = 0; scanning = true;
            }
            // One global sweep, split over frames by the native iterator; no per-location timer.
            if (!session.GetAllZDOsWithPrefabIterative(ZoneSystem.instance.m_locationProxyPrefab.name, proxies, ref scanIndex)) return;
            foreach (ZDO proxy in proxies.Where(p => p != null && p.IsValid()).GroupBy(p => p.m_uid).Select(g => g.First()))
            {
                RegisterVisitSite(proxy);
                if (EligibleForSweep(proxy, utcTicks)) QueueReset(proxy.m_uid);
            }
            proxies.Clear(); scanning = false;
        }

        private static void ObserveVisits(long utcTicks)
        {
            // This small shared poll detects entry, not expiry. Only nearby candidates are checked.
            // Use real character positions, not camera/reference positions, as proof of a visit.
            var positions = new List<Vector3>();
            if (Player.m_localPlayer) positions.Add(Player.m_localPlayer.transform.position);
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (!peer.IsReady() || peer.m_characterID.IsNone()) continue;
                ZDO character = session.GetZDO(peer.m_characterID);
                if (character != null) positions.Add(character.GetPosition());
            }
            foreach (Vector3 position in positions)
            {
                Vector2s zone = ZoneSystem.GetZone(position);
                // Supported native dungeon volumes fit within four neighboring 64 m zones.
                for (int x = -4; x <= 4; x++) for (int y = -4; y <= 4; y++)
                {
                    HashSet<ZDOID> candidates;
                    if (!visitSites.TryGetValue(new Vector2s(zone.x + x, zone.y + y), out candidates)) continue;
                    foreach (ZDOID id in candidates)
                    {
                        ZDO proxy = session.GetZDO(id);
                        if (proxy == null || proxy.GetLong(VisitStartedKey, 0) != 0) continue;
                        byte[] journal = proxy.GetByteArray(JournalKey, null);
                        if (journal != null && journal.Length != 0) continue;
                        try
                        {
                            List<Bounds> volumes = GetVisitBounds(proxy);
                            if (volumes != null) StartFirstVisit(proxy, position, volumes, utcTicks);
                        }
                        catch (Exception e)
                        {
                            string previous;
                            if (visitIssues.TryGetValue(id, out previous) && previous == e.Message) continue;
                            visitIssues[id] = e.Message;
                            var location = ZoneSystem.instance.GetLocation(proxy.GetInt(ZDOVars.s_location, 0));
                            Log.LogInfo("Dungeon " + (location == null ? "inconnu" : location.m_prefab.Name) + " " + id +
                                " : detection de visite indisponible (" + e.Message + "). Ceci n'est pas une tentative de reset.");
                        }
                    }
                }
            }
        }

        private static bool StartFirstVisit(ZDO proxy, Vector3 position, List<Bounds> volumes, long utcTicks)
        {
            if (proxy.GetLong(VisitStartedKey, 0) != 0 || !Inside(volumes, position)) return false;
            long minute = utcTicks - utcTicks % TimeSpan.TicksPerMinute;
            if (minute <= 0) return false;
            proxy.SetOwner(ZNet.GetUID());
            proxy.Set(VisitStartedKey, minute);
            visitIssues.Remove(proxy.m_uid);
            Log.LogInfo("Dungeon " + proxy.m_uid + " : premiere visite, delai de reset demarre (UTC " + new DateTime(minute, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm") + ")");
            return true;
        }

        private static List<Bounds> GetVisitBounds(ZDO proxy)
        {
            List<Bounds> volumes;
            if (visitBounds.TryGetValue(proxy.m_uid, out volumes)) return volumes;
            var location = ZoneSystem.instance.GetLocation(proxy.GetInt(ZDOVars.s_location, 0));
            if (location == null || ZoneSystem.instance.ShouldDelayProxyLocationSpawning(location.Hash)) return null;
            location.m_prefab.Load();
            try
            {
                GameObject prefab = location.m_prefab.Asset;
                if (!prefab) return null;
                Location loc = prefab.GetComponent<Location>();
                var hashes = new HashSet<int>(prefab.GetComponentsInChildren<DungeonGenerator>(true).Select(g => g.name.GetStableHashCode()));
                Vector2s zone = ZoneSystem.GetZone(proxy.GetPosition());
                var generators = session.m_objectsByID.Values.Where(z => hashes.Contains(z.GetPrefab()) && ZoneSystem.GetZone(z.GetPosition()) == zone).ToList();
                // Retry when the location's generation/network initialization is complete.
                if (hashes.Count != generators.Count) return null;
                if (loc.m_hasInterior && generators.Count > 0)
                {
                    // Reset safety bounds include a margin; using those for visits would mark a
                    // neighboring, untouched dungeon as visited. Use its saved rooms instead.
                    volumes = new List<Bounds>();
                    foreach (ZDO generator in generators) volumes.AddRange(ReadVisitRooms(generator));
                    if (volumes.Count == 0) return null;
                }
                else volumes = OccupiedVolumes(loc, BuildVolumes(proxy, prefab, generators, Math.Max(40, loc.m_exteriorRadius + 16)));
                visitBounds[proxy.m_uid] = volumes;
                return volumes;
            }
            finally { location.m_prefab.Release(); }
        }

        internal static List<Bounds> ReadVisitRooms(ZDO generator)
        {
            byte[] data = generator.GetByteArray(ZDOVars.s_roomData, null);
            ZPackage pkg = data == null ? null : new ZPackage(data);
            int count = pkg == null ? generator.GetInt(ZDOVars.s_rooms, 0) : pkg.ReadInt();
            if (count < 0 || count > 10000) throw new InvalidDataException("Invalid saved room count for visit detection");
            var result = new List<Bounds>();
            for (int i = 0; i < count; i++)
            {
                string key = "room" + i;
                int hash = pkg == null ? generator.GetInt(key, 0) : pkg.ReadInt();
                Vector3 position = pkg == null ? generator.GetVec3(key + "_pos", Vector3.zero) : pkg.ReadVector3();
                // Native BinaryWriter Utils.Write(Quaternion) stores three Euler angles,
                // unlike ZPackage.Write(Quaternion), which stores four quaternion components.
                Quaternion rotation = pkg == null ? generator.GetQuaternion(key + "_rot", Quaternion.identity) : Quaternion.Euler(pkg.ReadVector3());
                DungeonDB.RoomData room = DungeonDB.instance.GetRoom(hash);
                if (room == null) throw new InvalidDataException("Unknown saved room for visit detection");
                room.m_prefab.Load();
                try
                {
                    Vector3 size = room.RoomInPrefab.m_size;
                    Vector3 x = rotation * Vector3.right * size.x, y = rotation * Vector3.up * size.y, z = rotation * Vector3.forward * size.z;
                    Vector3 extent = new Vector3(Math.Abs(x.x) + Math.Abs(y.x) + Math.Abs(z.x), Math.Abs(x.y) + Math.Abs(y.y) + Math.Abs(z.y), Math.Abs(x.z) + Math.Abs(y.z) + Math.Abs(z.z));
                    // Small tolerance for player feet/door thresholds, not an entire adjacent zone.
                    result.Add(new Bounds(position, extent + Vector3.one * 2f));
                }
                finally { room.m_prefab.Release(); }
            }
            return result;
        }
    }
}
