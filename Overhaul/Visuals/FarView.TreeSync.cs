using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using Overhaul.Utility;

namespace Overhaul.Visuals
{
    // Far view, forest: the trees really standing. Every client works out the world's first tree placement from its seed
    // (the base of the far forest); the server, which holds every tree of the zones generated so far, compares them with
    // that same placement and pushes only what differs: trees cut down (removed) and trees grown since or that the
    // placement misses (added). Sent to each player when they join, then as trees fall or grow, like the game sends its
    // objects; the client never asks. Zones never generated have no difference: their trees are the base.
    internal static partial class FarView
    {
        const float DeltaMatch = .5f;
        struct Removed { internal int Hash; internal float X, Z; }
        struct Added { internal int Hash; internal float X, Y, Z, Scale, Yaw; }
        sealed class ZoneDelta { internal Removed[] Removed = new Removed[0]; internal Added[] Added = new Added[0]; }
        // Client: the differences received, per zone (read by the forest worker).
        static readonly ConcurrentDictionary<(int, int), ZoneDelta> treeDeltas = new ConcurrentDictionary<(int, int), ZoneDelta>();

        internal static class TreeSync
        {
            // Lock: false = the far forest is the seed's placement only, the server sends nothing.
            internal static bool Enabled = true;
            const string Rpc = "Overhaul_FarTrees";
            const float Match = 1.5f, NearCheckEvery = 10f, SendEvery = 1f, NewZonesEvery = 15f;
            const double BudgetMs = 2.0;
            const int ZonesPerPacket = 256, PeerReach = 3;

            // ---------------------------------------------------------------- client
            internal static void Register(ZNetPeer peer)
            {
                if (!Enabled || !ZNet.instance || ZNet.instance.IsServer()) return;
                peer.m_rpc.Register<ZPackage>(Rpc, (rpc, pkg) => Receive(pkg));
            }
            static void Receive(ZPackage pkg)
            {
                try
                {
                    int zones = pkg.ReadInt();
                    for (int i = 0; i < zones; i++)
                    {
                        var key = (pkg.ReadInt(), pkg.ReadInt());
                        var d = new ZoneDelta { Removed = new Removed[pkg.ReadInt()] };
                        for (int r = 0; r < d.Removed.Length; r++) d.Removed[r] = new Removed { Hash = pkg.ReadInt(), X = pkg.ReadSingle(), Z = pkg.ReadSingle() };
                        d.Added = new Added[pkg.ReadInt()];
                        for (int a = 0; a < d.Added.Length; a++) d.Added[a] = new Added { Hash = pkg.ReadInt(), X = pkg.ReadSingle(), Y = pkg.ReadSingle(), Z = pkg.ReadSingle(), Scale = pkg.ReadSingle(), Yaw = pkg.ReadSingle() };
                        Store(key, d);
                    }
                }
                catch (Exception e) { Log.LogWarning("[FarView] far trees from the server not read: " + e.Message); }
            }
            // A zone's differences in; the forest cell holding it is placed again.
            static void Store((int, int) zone, ZoneDelta d)
            {
                if (d.Removed.Length == 0 && d.Added.Length == 0) treeDeltas.TryRemove(zone, out _); else treeDeltas[zone] = d;
                var cell = (FloorDiv(zone.Item1, CellZones), FloorDiv(zone.Item2, CellZones));
                if (forestCells.TryRemove(cell, out _)) { cellJobs.Add((cell.Item1, cell.Item2, forestEpoch)); Interlocked.Increment(ref forestVersion); }
            }
            static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

            // ---------------------------------------------------------------- server
            static ZNet net;
            static Rule[] serverRules;
            static HashSet<int> treeHashes;
            static int serverSeed;
            static Thread worker;
            static readonly ConcurrentQueue<(int, int)> baseJobs = new ConcurrentQueue<(int, int)>();
            static readonly ConcurrentDictionary<(int, int), Tree[]> bases = new ConcurrentDictionary<(int, int), Tree[]>();
            static readonly HashSet<(int, int)> queued = new HashSet<(int, int)>(), toCompare = new HashSet<(int, int)>(), changed = new HashSet<(int, int)>();
            static readonly Dictionary<(int, int), ZoneDelta> deltas = new Dictionary<(int, int), ZoneDelta>();
            static readonly HashSet<long> sentTo = new HashSet<long>();
            static float nextNear, nextSend, nextNewZones;
            static int knownZones;

            internal static void Tick()
            {
                if (!Enabled) return;
                if (ZNet.instance != net) Reset();
                if (!net || !net.IsServer() || !ZoneSystem.instance || WorldGenerator.instance == null || ZDOMan.instance == null) return;
                if (serverRules == null && !Start()) return;
                float now = Time.time;
                // Zones generated since: their placement worked out, then compared.
                if (now >= nextNewZones) { nextNewZones = now + NewZonesEvery; QueueGenerated(); }
                // Around every player: trees grown, planted or cut there.
                if (now >= nextNear) { nextNear = now + NearCheckEvery; MarkNearPlayers(); }
                Compare();
                if (now >= nextSend) { nextSend = now + SendEvery; Send(); }
            }

            static void Reset()
            {
                net = ZNet.instance;
                serverRules = null; treeHashes = null; while (baseJobs.TryDequeue(out _)) { }
                bases.Clear(); queued.Clear(); toCompare.Clear(); changed.Clear(); deltas.Clear(); sentTo.Clear(); knownZones = 0;
                treeDeltas.Clear();   // client: a new session starts from the base again
            }

            static bool Start()
            {
                if (!TreeRules(out var prefabs, out var r)) return false;
                serverRules = r; serverSeed = WorldGenerator.instance.GetSeed();
                treeHashes = new HashSet<int>(prefabs.Select(p => p.name.GetStableHashCode()));
                ZDOMan.instance.m_onZDODestroyed += OnDestroyed;
                if (worker == null) { worker = new Thread(Work) { IsBackground = true, Name = "Overhaul far trees (server)" }; worker.Start(); }
                Log.LogInfo("[FarView] far trees sync: " + prefabs.Count + " kinds (" + string.Join(", ", prefabs.Select(p => p.name)) + ")");
                return true;
            }

            static void QueueGenerated()
            {
                var zones = ZoneSystem.instance.m_generatedZones;
                if (zones.Count == knownZones) return;
                knownZones = zones.Count;
                foreach (var z in zones) { var key = ((int)z.x, (int)z.y); if (queued.Add(key)) baseJobs.Enqueue(key); }
            }

            static void MarkNearPlayers()
            {
                foreach (var peer in net.GetPeers()) if (peer.IsReady()) MarkAround(peer.GetRefPos());
                if (Player.m_localPlayer) MarkAround(Player.m_localPlayer.transform.position);
            }
            static void MarkAround(Vector3 p)
            {
                var c = ZoneSystem.GetZone(p);
                for (int x = -PeerReach; x <= PeerReach; x++) for (int z = -PeerReach; z <= PeerReach; z++) Mark((c.x + x, c.y + z));
            }
            static void Mark((int, int) zone) { if (bases.ContainsKey(zone)) toCompare.Add(zone); }

            static void OnDestroyed(ZDO zdo)
            {
                if (zdo == null || treeHashes == null || !treeHashes.Contains(zdo.GetPrefab())) return;
                var z = ZoneSystem.GetZone(zdo.GetPosition()); Mark((z.x, z.y));
            }

            // Worker: the first placement of each generated zone.
            static void Work()
            {
                var list = new List<Tree>();
                while (true)
                {
                    try
                    {
                        if (!baseJobs.TryDequeue(out var zone)) { Thread.Sleep(50); continue; }
                        var w = WorldGenerator.instance; var r = serverRules; if (w == null || r == null) continue;
                        list.Clear(); PlaceZone(w, r, serverSeed, zone.Item1, zone.Item2, list);
                        bases[zone] = list.ToArray();
                        lock (fresh) fresh.Add(zone);
                    }
                    catch (ThreadAbortException) { return; }
                    catch (Exception e) { Log.LogWarning("[FarView] far trees placement failed: " + e.Message); }
                }
            }
            static readonly List<(int, int)> fresh = new List<(int, int)>();

            // Main thread, within a budget: zones' trees standing against their first placement.
            static readonly List<ZDO> standing = new List<ZDO>();
            static void Compare()
            {
                lock (fresh) { foreach (var z in fresh) toCompare.Add(z); fresh.Clear(); }
                if (toCompare.Count == 0) return;
                var clock = Stopwatch.StartNew();
                var done = new List<(int, int)>();
                foreach (var zone in toCompare)
                {
                    if (clock.Elapsed.TotalMilliseconds > BudgetMs) break;
                    done.Add(zone);
                    if (!bases.TryGetValue(zone, out var baseTrees)) continue;
                    var d = Delta(zone, baseTrees);
                    deltas.TryGetValue(zone, out var before);
                    if (Same(before, d)) continue;
                    if (d.Removed.Length == 0 && d.Added.Length == 0) deltas.Remove(zone); else deltas[zone] = d;
                    changed.Add(zone);
                    if (!net.IsDedicated()) Store(zone, d);   // the host's own far forest
                }
                foreach (var z in done) toCompare.Remove(z);
            }
            static ZoneDelta Delta((int, int) zone, Tree[] baseTrees)
            {
                standing.Clear();
                var index = ZoneSystem.SectorToIndex(zone.Item1, zone.Item2);
                var sector = (int)index.Sector;
                var bySector = ZDOMan.instance.m_objectsBySector;
                if (sector >= 0 && sector < bySector.Length && bySector[sector] != null)
                    foreach (var zdo in bySector[sector]) if (treeHashes.Contains(zdo.GetPrefab())) standing.Add(zdo);
                var removed = new List<Removed>(); var added = new List<Added>();
                var matched = new bool[standing.Count];
                foreach (var t in baseTrees)
                {
                    bool found = false;
                    for (int i = 0; i < standing.Count; i++)
                    {
                        if (matched[i] || standing[i].GetPrefab() != t.Hash) continue;
                        var p = standing[i].GetPosition();
                        if (Mathf.Abs(p.x - t.Position.x) < Match && Mathf.Abs(p.z - t.Position.z) < Match) { matched[i] = found = true; break; }
                    }
                    if (!found) removed.Add(new Removed { Hash = t.Hash, X = t.Position.x, Z = t.Position.z });
                }
                for (int i = 0; i < standing.Count; i++)
                {
                    if (matched[i]) continue;
                    var zdo = standing[i]; var p = zdo.GetPosition();
                    float scale = zdo.GetFloat(ZDOVars.s_scaleScalarHash, zdo.GetVec3(ZDOVars.s_scaleHash, Vector3.one).y);
                    added.Add(new Added { Hash = zdo.GetPrefab(), X = p.x, Y = p.y, Z = p.z, Scale = scale, Yaw = zdo.GetRotation().eulerAngles.y * Mathf.Deg2Rad });
                }
                return new ZoneDelta { Removed = removed.ToArray(), Added = added.ToArray() };
            }
            static bool Same(ZoneDelta a, ZoneDelta b)
            {
                if (a == null) return b.Removed.Length == 0 && b.Added.Length == 0;
                if (a.Removed.Length != b.Removed.Length || a.Added.Length != b.Added.Length) return false;
                for (int i = 0; i < a.Removed.Length; i++) if (a.Removed[i].Hash != b.Removed[i].Hash || a.Removed[i].X != b.Removed[i].X || a.Removed[i].Z != b.Removed[i].Z) return false;
                for (int i = 0; i < a.Added.Length; i++) if (a.Added[i].Hash != b.Added[i].Hash || a.Added[i].X != b.Added[i].X || a.Added[i].Z != b.Added[i].Z) return false;
                return true;
            }

            // Every difference to a player who just joined; then the zones changed, to everyone.
            static void Send()
            {
                var ready = net.GetPeers().Where(p => p.IsReady() && p.m_rpc != null).ToList();
                sentTo.RemoveWhere(uid => !ready.Any(p => p.m_uid == uid));
                foreach (var peer in ready)
                    if (sentTo.Add(peer.m_uid)) Push(peer, deltas.Keys.ToList());
                if (changed.Count == 0) return;
                var zones = changed.ToList(); changed.Clear();
                foreach (var peer in ready) Push(peer, zones);
            }
            static void Push(ZNetPeer peer, List<(int, int)> zones)
            {
                for (int start = 0; start < zones.Count; start += ZonesPerPacket)
                {
                    var pkg = new ZPackage(); int n = Math.Min(ZonesPerPacket, zones.Count - start);
                    pkg.Write(n);
                    for (int i = start; i < start + n; i++)
                    {
                        var z = zones[i]; deltas.TryGetValue(z, out var d); d = d ?? new ZoneDelta();
                        pkg.Write(z.Item1); pkg.Write(z.Item2);
                        pkg.Write(d.Removed.Length); foreach (var r in d.Removed) { pkg.Write(r.Hash); pkg.Write(r.X); pkg.Write(r.Z); }
                        pkg.Write(d.Added.Length); foreach (var a in d.Added) { pkg.Write(a.Hash); pkg.Write(a.X); pkg.Write(a.Y); pkg.Write(a.Z); pkg.Write(a.Scale); pkg.Write(a.Yaw); }
                    }
                    peer.m_rpc.Invoke(Rpc, pkg);
                }
            }
        }

        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        static class TreeSyncConnection { static void Postfix(ZNetPeer peer) => TreeSync.Register(peer); }
    }
}
