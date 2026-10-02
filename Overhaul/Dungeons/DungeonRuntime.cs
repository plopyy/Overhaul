using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Overhaul.Utility;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Overhaul.Dungeons
{
    /// <summary>Server-owned, whole-location replacement. The location proxy (entrance) is retained.
    /// Work is staged using the native Ghost path before any old ZDO is destroyed. A journal on the
    /// proxy makes the commit recoverable and is saved in the same world snapshot as its objects.</summary>
    internal static partial class DungeonRuntime
    {
        internal static readonly int SeedKey = "overhaul_dungeon_seed".GetStableHashCode();
        internal static readonly int OwnerKey = "overhaul_dungeon_owner".GetStableHashCode();
        internal static readonly int EpochKey = "overhaul_dungeon_epoch".GetStableHashCode();
        internal static readonly int MinKey = "overhaul_dungeon_min".GetStableHashCode();
        internal static readonly int MaxKey = "overhaul_dungeon_max".GetStableHashCode();
        internal static readonly int LastKey = "overhaul_dungeon_last_utc".GetStableHashCode();
        internal static readonly int VisitStartedKey = "overhaul_dungeon_first_visit_utc_v1".GetStableHashCode();
        internal static readonly int RequestedKey = "overhaul_dungeon_reset_requested".GetStableHashCode();
        internal static readonly int JournalKey = "overhaul_dungeon_commit_v2".GetStableHashCode();
        internal static readonly int IdentityKey = "overhaul_dungeon_identity".GetStableHashCode();
        internal static readonly int PendingKey = "overhaul_dungeon_pending".GetStableHashCode();
        private static readonly int TrackedKey = "overhaul_dungeon_tracked_v1".GetStableHashCode();
        private static readonly System.Random Seeds = new System.Random();
        internal static Capture Current;
        private static ZDOMan session;
        private static readonly List<ZDO> proxies = new List<ZDO>();
        private static readonly Queue<ZDOID> queue = new Queue<ZDOID>();
        private static readonly HashSet<ZDOID> queued = new HashSet<ZDOID>();
        private static int scanIndex;
        private static float nextTick;
        private static bool scanning;
        private static string lastLocationConfig;
        private static HashSet<string> selected = new HashSet<string>();
        private static readonly Dictionary<ZDOID, string> reports = new Dictionary<ZDOID, string>();

        private static Preparation preparation;
        private sealed class Preparation
        {
            internal ZDOID Id;
            internal ZoneSystem.ZoneLocation Location;
            internal float Deadline;
            internal bool Recovery;
            internal bool Manual;
        }

        internal sealed class Capture
        {
            internal bool Staging, Replay;
            internal int Seed, Epoch;
            internal float InteriorHeight;
            internal ZDO Parent;
            internal readonly List<ZDOID> Objects = new List<ZDOID>();
            internal readonly HashSet<GameObject> Views = new HashSet<GameObject>();
            internal readonly List<Bounds> Volumes = new List<Bounds>();
            internal readonly Dictionary<int, ZDO> PreviousGenerators = new Dictionary<int, ZDO>();
        }

        internal static void ConfigureGeneration(DungeonGenerator generator, ref int seed)
        {
            string name = DungeonPolicy.PrefabName(generator.name);
            ZDO zdo = generator.m_nview == null ? null : generator.m_nview.GetZDO();
            if (Current != null && Current.Replay)
            {
                ZDO previous;
                if (Current.PreviousGenerators.TryGetValue(name.GetStableHashCode(), out previous))
                {
                    seed = previous.GetInt(SeedKey, seed);
                    generator.m_minRooms = previous.GetInt(MinKey, generator.m_minRooms);
                    generator.m_maxRooms = previous.GetInt(MaxKey, generator.m_maxRooms);
                }
            }
            else
            {
                if (DungeonPolicy.ScaledGenerators.Contains(name))
                    DungeonPolicy.Scale(generator.m_minRooms, generator.m_maxRooms, OverhaulConfig.RoomMultiplier.Value,
                        out generator.m_minRooms, out generator.m_maxRooms);
                if (Current != null && Current.Staging)
                {
                    ZDO previous;
                    int old = Current.PreviousGenerators.TryGetValue(name.GetStableHashCode(), out previous) ? previous.GetInt(SeedKey, seed) : seed;
                    seed = DungeonPolicy.NextSeed(old, unchecked(Current.Seed ^ name.GetStableHashCode()));
                }
            }
            if (zdo == null) return;
            zdo.Set(SeedKey, seed);
            zdo.Set(MinKey, generator.m_minRooms);
            zdo.Set(MaxKey, generator.m_maxRooms);
            generator.m_hasGeneratedSeed = true;
            generator.m_generatedSeed = seed;
        }

        internal static void FinishInitialCapture(Capture capture)
        {
            ZDO proxy = capture.Objects.Select(id => ZDOMan.instance.GetZDO(id)).FirstOrDefault(z => z != null && z.GetInt(ZDOVars.s_location, 0) != 0);
            if (proxy == null) return;
            proxy.Set(VisitStartedKey, 0L);
            proxy.Set(TrackedKey, true);
            Identity(proxy);
            BossDungeonLayout.CommitMarker(proxy, capture.Objects.Select(id => ZDOMan.instance.GetZDO(id)));
            foreach (ZDOID id in capture.Objects)
            {
                ZDO zdo = ZDOMan.instance.GetZDO(id);
                if (zdo != null && zdo != proxy) Inherit(proxy, zdo);
            }
            RegisterVisitSite(proxy);
        }

        internal static void Inherit(ZDO parent, ZDO child)
        {
            if (parent == null || child == null || !child.IsOwner()) return;
            string owner = parent.GetString(OwnerKey, "");
            if (owner.Length == 0 && parent.GetBool(TrackedKey, false)) owner = Identity(parent);
            if (owner.Length == 0) return;
            child.Set(OwnerKey, owner);
            child.Set(EpochKey, parent.GetInt(EpochKey, 0));
            Identity(child);
        }

        internal static string Identity(ZDO zdo)
        {
            string value = zdo.GetString(IdentityKey, "");
            if (value.Length == 0) { value = Guid.NewGuid().ToString("N"); zdo.Set(IdentityKey, value); }
            return value;
        }

        internal static Capture BeginChildCapture(ZNetView view)
        {
            if (Current != null || !view || !view.IsValid() || !view.IsOwner() || view.GetZDO().GetString(OwnerKey, "").Length == 0) return null;
            Current = new Capture { Parent = view.GetZDO() };
            return Current;
        }

        internal static void EndChildCapture(Capture capture)
        {
            if (capture == null) return;
            try { foreach (ZDOID id in capture.Objects) Inherit(capture.Parent, ZDOMan.instance.GetZDO(id)); }
            finally { Current = null; }
        }

        internal static void RecordRoomVolumes()
        {
            if (Current == null || !Current.Staging) return;
            foreach (Room room in DungeonGenerator.m_placedRooms)
            {
                Vector3 half = new Vector3(room.m_size.x, room.m_size.y, room.m_size.z) * 0.5f;
                Bounds bounds = new Bounds(room.transform.position, Vector3.zero);
                for (int x = -1; x <= 1; x += 2)
                    for (int y = -1; y <= 1; y += 2)
                        for (int z = -1; z <= 1; z += 2)
                            bounds.Encapsulate(room.transform.TransformPoint(new Vector3(half.x * x, half.y * y, half.z * z)));
                bounds.Expand(16);
                Current.Volumes.Add(bounds);
            }
        }

        internal static void ClearSession()
        {
            EndPreparation();
            session = null; Current = null; scanIndex = 0; nextTick = 0; scanning = false;
            proxies.Clear(); queue.Clear(); queued.Clear(); reports.Clear();
            visitSites.Clear(); visitBounds.Clear(); visitIssues.Clear(); nextVisitPoll = 0; nextSweep = 0; failureUntil = 0;
        }

        internal static string RequestManualReset()
        {
            if (!ZNet.instance || !ZNet.instance.IsServer() || ZDOMan.instance == null || !ZoneSystem.instance)
                return "Overhaul : aucun monde serveur disponible.";
            var enabled = new HashSet<string>(OverhaulConfig.ResetLocations.Value.Split(',').Select(s => s.Trim()).Where(DungeonPolicy.Supported.Contains));
            int count = 0;
            int prefab = ZoneSystem.instance.m_locationProxyPrefab.name.GetStableHashCode();
            foreach (ZDO proxy in ZDOMan.instance.m_objectsByID.Values)
            {
                if (proxy.GetPrefab() != prefab) continue;
                var location = ZoneSystem.instance.GetLocation(proxy.GetInt(ZDOVars.s_location, 0));
                if (location == null || !enabled.Contains(location.m_prefab.Name)) continue;
                reports.Remove(proxy.m_uid);
                if (preparation != null && preparation.Id == proxy.m_uid)
                {
                    Log.LogInfo("Dungeon " + location.m_prefab.Name + " " + proxy.m_uid + " : chargement deja en cours");
                    count++;
                    continue;
                }
                proxy.SetOwner(ZNet.GetUID());
                proxy.Set(RequestedKey, true);
                QueueReset(proxy.m_uid);
                Log.LogInfo("Dungeon " + location.m_prefab.Name + " " + proxy.m_uid + " : demande admin enregistree");
                count++;
            }
            nextTick = 0;
            string message = "Overhaul : reset demande pour " + count + " lieux generes, visites ou non. Une seule tentative par lieu ; en cas de blocage, relancer la commande ou attendre l'echeance normale. Tombes deplacees a l'exterieur.";
            Log.LogInfo(message);
            return message;
        }

        internal static string RequestManualResetAt(Vector3 position)
        {
            if (!ZNet.instance || !ZNet.instance.IsServer() || ZDOMan.instance == null || !ZoneSystem.instance)
                return "Overhaul : aucun monde serveur disponible.";
            if (float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z) ||
                float.IsInfinity(position.x) || float.IsInfinity(position.y) || float.IsInfinity(position.z))
                return "Overhaul : position du joueur invalide.";
            ZDO target;
            try { target = FindManualResetTarget(position); }
            catch (Exception e) { Log.LogError("Dungeon ciblage : " + e); return "Overhaul : impossible de verifier le donjon actuel ; aucun reset demande."; }
            if (target == null) return "Overhaul : aucun donjon genere identifiable a cette position.";
            return RequestManualResetTarget(target);
        }

        internal static string ManualTargetRefusal(ZDO target)
        {
            var location = ZoneSystem.instance.GetLocation(target.GetInt(ZDOVars.s_location, 0));
            if (location == null || !DungeonPolicy.Supported.Contains(location.m_prefab.Name)) return "Overhaul : cible non autorisee.";
            string label = location.m_prefab.Name + " " + target.m_uid;
            var enabled = new HashSet<string>(OverhaulConfig.ResetLocations.Value.Split(',').Select(s => s.Trim()));
            if (!enabled.Contains(location.m_prefab.Name)) return "Overhaul : " + label + " ignore, reset desactive dans la configuration.";

            return null;
        }

        internal static string RequestManualResetTarget(ZDO target)
        {
            string refused = ManualTargetRefusal(target);
            if (refused != null) return refused;
            string label = ZoneSystem.instance.GetLocation(target.GetInt(ZDOVars.s_location, 0)).m_prefab.Name + " " + target.m_uid;
            reports.Remove(target.m_uid);
            if (preparation != null && preparation.Id == target.m_uid) return "Overhaul : " + label + ", chargement deja en cours.";
            target.SetOwner(ZNet.GetUID());
            target.Set(RequestedKey, true);
            QueueReset(target.m_uid);
            nextTick = 0;
            string message = "Overhaul : reset demande uniquement pour " + label + ". Une seule tentative ; la presence d'un joueur bloque le reset.";
            Log.LogInfo(message);
            return message;
        }

        internal static ZDO FindManualResetTarget(Vector3 position)
        {
            int prefab = ZoneSystem.instance.m_locationProxyPrefab.name.GetStableHashCode();
            bool inside = Character.InInterior(position);
            var zone = ZoneSystem.GetZone(position);
            ZDO found = null; float distance = float.PositiveInfinity;
            foreach (var proxy in ZDOMan.instance.m_objectsByID.Values)
            {
                if (proxy.GetPrefab() != prefab) continue;
                var location = ZoneSystem.instance.GetLocation(proxy.GetInt(ZDOVars.s_location, 0));
                if (location == null || !DungeonPolicy.Supported.Contains(location.m_prefab.Name)) continue;
                if (inside)
                {
                    if (position.y >= 11000)
                    {
                        if (proxy.GetInt(BossDungeonLayout.LayoutKey, 0) != 1 || !BossDungeonLayout.InLane(proxy, position)) continue;
                    }
                    // A dungeon can extend across several terrain zones. Its entrance's
                    // zone is not proof of which interior contains the player.
                    else
                    {
                        var entranceZone = ZoneSystem.GetZone(proxy.GetPosition());
                        if (Math.Abs(entranceZone.x - zone.x) > 4 || Math.Abs(entranceZone.y - zone.y) > 4) continue;
                    }
                    var bounds = GetVisitBounds(proxy);
                    if (bounds == null || !Inside(bounds, position)) continue;
                    if (found != null) return null; // Ambiguous interior: do not guess another dungeon.
                    found = proxy;
                }
                else
                {
                    // Entrance distance on the surface, not distance to an interior in the sky.
                    var delta = proxy.GetPosition() - position; delta.y = 0;
                    float candidate = delta.sqrMagnitude;
                    if (candidate < distance) { distance = candidate; found = proxy; }
                }
            }
            return found;
        }

        internal static void Tick()
        {
            if (!ZNet.instance || !ZNet.instance.IsServer() || ZDOMan.instance == null || !ZoneSystem.instance || !ZNetScene.instance || !DungeonDB.instance) return;
            if (session != ZDOMan.instance)
            {
                ClearSession(); session = ZDOMan.instance;
                // One index build at world load, then updated when locations are generated/scanned.
                int prefabHash = ZoneSystem.instance.m_locationProxyPrefab.name.GetStableHashCode();
                foreach (ZDO proxy in session.m_objectsByID.Values)
                    if (proxy.GetPrefab() == prefabHash) RegisterVisitSite(proxy);
            }
            if (Current != null) return;
            if (Time.realtimeSinceStartup < failureUntil) return;
            if (ZNet.instance.m_saveThread != null && ZNet.instance.m_saveThread.IsAlive) return;
            try
            {
                string config = OverhaulConfig.ResetLocations.Value;
                if (config != lastLocationConfig)
                {
                    selected = new HashSet<string>(config.Split(',').Select(s => s.Trim()).Where(DungeonPolicy.Supported.Contains));
                    lastLocationConfig = config;
                }
                float realtime = Time.realtimeSinceStartup;
                long utcTicks = DateTime.UtcNow.Ticks;
                if (realtime >= nextVisitPoll)
                {
                    nextVisitPoll = realtime + 1f;
                    ObserveVisits(utcTicks);
                }
                SweepDueLocations(realtime, utcTicks);
                if (realtime < nextTick) return;
                nextTick = realtime + 1f;
                if (preparation != null) { AdvancePreparation(realtime); return; }
                if (queue.Count > 0)
                {
                    ZDOID id = queue.Dequeue();
                    queued.Remove(id);
                    ZDO proxy = session.GetZDO(id);
                    if (proxy != null) Process(proxy);
                    return;
                }
            }
            catch (Exception e) { EndPreparation(); Log.LogError("Dungeon scheduler: " + e); failureUntil = Time.realtimeSinceStartup + 60f; }
        }

        private static void Process(ZDO proxy)
        {
            // Consume the one-shot command on this pass, including failures to prepare the location.
            bool requested = proxy.GetBool(RequestedKey, false);
            if (requested) proxy.Set(RequestedKey, false);
            ZoneSystem.ZoneLocation location = ZoneSystem.instance.GetLocation(proxy.GetInt(ZDOVars.s_location, 0));
            if (location == null) return;
            string name = location.m_prefab.Name;
            if (!DungeonPolicy.Supported.Contains(name)) return;
            // Finish an already committed transaction even if configuration was disabled afterwards.
            byte[] journal = proxy.GetByteArray(JournalKey, null);
            if (journal != null && journal.Length != 0)
            {
                BeginPreparation(proxy, location, true);
                return;
            }
            long visited = proxy.GetLong(VisitStartedKey, 0);
            if (!requested && visited == 0) return;
            if (!selected.Contains(name)) { if (requested) proxy.Set(RequestedKey, false); return; }
            if (!requested && OverhaulConfig.ResetIntervalHours.Value <= 0) return;
            long now = DateTime.UtcNow.Ticks;
            if (!requested && !DungeonPolicy.IsDue(visited, now, OverhaulConfig.ResetIntervalHours.Value)) return;
            BeginPreparation(proxy, location, false, requested);
        }

        private static void BeginPreparation(ZDO proxy, ZoneSystem.ZoneLocation location, bool recovery, bool manual = false)
        {
            if (!KnownPlayersClear(proxy, location))
            {
                Report(proxy, location.m_prefab.Name, "joueur dans le donjon, chargement evite");
                return;
            }
            Vector2s zone = ZoneSystem.GetZone(proxy.GetPosition());
            if (!ZoneSystem.instance.IsZoneGenerated(zone)) { Report(proxy, location.m_prefab.Name, "secteur non genere"); return; }
            preparation = new Preparation { Id = proxy.m_uid, Location = location, Recovery = recovery, Manual = manual, Deadline = Time.realtimeSinceStartup + 60f };
            queued.Add(proxy.m_uid);
            // Keep our own reference, independent of the native streaming cache lifetime.
            location.m_prefab.LoadAsync();
            Log.LogInfo("Dungeon " + location.m_prefab.Name + " " + proxy.m_uid + " : chargement des assets et du terrain (maximum 60 s)");
            AdvancePreparation(Time.realtimeSinceStartup);
        }

        private static void EndPreparation()
        {
            Preparation work = preparation;
            preparation = null;
            if (work == null) return;
            queued.Remove(work.Id);
            work.Location.m_prefab.Release();
        }

        private static void AdvancePreparation(float realtime)
        {
            Preparation work = preparation;
            if (work == null) return;
            ZDO proxy = session.GetZDO(work.Id);
            if (proxy == null) { EndPreparation(); return; }
            string name = work.Location.m_prefab.Name;
            if (realtime >= work.Deadline)
            {
                Report(proxy, name, "chargement expire apres 60 s, tentative abandonnee");
                EndPreparation();
                return;
            }
            if (!work.Location.m_prefab.IsLoaded) return;
            if (!work.Recovery && (!selected.Contains(name) || (!work.Manual && proxy.GetLong(VisitStartedKey, 0) == 0)))
            { EndPreparation(); return; }
            Vector2s zone = ZoneSystem.GetZone(proxy.GetPosition());
            // Only poll resource preparation; a refused reset is never retried here.
            ZoneSystem.instance.PokeLocalZone(zone);
            if (!ZoneSystem.instance.IsZoneLoaded(zone)) return;
            try
            {
                if (work.Recovery)
                {
                    byte[] journal = proxy.GetByteArray(JournalKey, null);
                    if (journal != null && journal.Length != 0) FinishCommit(proxy, journal);
                }
                else Reset(proxy, work.Location);
            }
            finally { EndPreparation(); }
        }

        private static void Report(ZDO proxy, string name, string reason)
        {
            if (reason.StartsWith("joueur", StringComparison.Ordinal) && playerBlockDetail != null)
                reason += " : " + playerBlockDetail;
            string old;
            if (reports.TryGetValue(proxy.m_uid, out old) && old == reason) return;
            reports[proxy.m_uid] = reason;
            Log.LogInfo("Dungeon " + name + " " + proxy.m_uid + " : reset reporte (" + reason + ")");
        }

        private static void Reset(ZDO proxy, ZoneSystem.ZoneLocation location)
        {
            // A manual command grants one attempt, not a persistent bypass of the visit deadline.
            // Consume it before any occupied/migration/placement failure can return.
            proxy.Set(RequestedKey, false);
            string owner = Identity(proxy);
            string name = location.m_prefab.Name;
            // Stable snapshot of IDs only: ZDOPool can reuse destroyed ZDO instances during staging.
            ZDOID[] world = session.m_objectsByID.Keys.ToArray();
            List<ZDOID> old = world.Where(id => { ZDO z = session.GetZDO(id); return z != null && z != proxy && z.GetString(OwnerKey, "") == owner; }).ToList();
            var previous = new Dictionary<int, ZDO>();
            location.m_prefab.Load();
            Capture staged = null;
            bool committed = false;
            try
            {
                GameObject prefab = location.m_prefab.Asset;
                Location locationComponent = prefab.GetComponent<Location>();
                float exterior = Math.Max(40, locationComponent.m_exteriorRadius + 16);
                DungeonGenerator[] generators = prefab.GetComponentsInChildren<DungeonGenerator>(true);
                foreach (DungeonGenerator generator in generators)
                {
                    int hash = generator.name.GetStableHashCode();
                    ZDO[] matches = world.Select(session.GetZDO).Where(z => z != null && z.GetPrefab() == hash &&
                        ZoneSystem.GetZone(z.GetPosition()) == ZoneSystem.GetZone(proxy.GetPosition())).ToArray();
                    if (matches.Length != 1) { Report(proxy, name, "generateur absent ou ambigu : " + generator.name); return; }
                    previous.Add(hash, matches[0]);
                }
                List<Bounds> volumes = ScopeInteriorVolumes(proxy, BuildVolumes(proxy, prefab, previous.Values, exterior), generators.Length != 0);
                List<Bounds> occupied = OccupiedVolumes(locationComponent, volumes);
                if (!PlayersClear(occupied)) { Report(proxy, name, "joueur dans le donjon"); return; }
                Log.LogInfo("Dungeon " + name + " " + proxy.m_uid + " : debut de preparation du reset");
                if (!proxy.GetBool(TrackedKey, false))
                {
                    Log.LogInfo("Dungeon " + name + " " + proxy.m_uid + " : reconstruction de reference de l'ancien contenu (aucun remplacement a cette etape)");
                    Capture baseline = Stage(proxy, location, previous, true, 0);
                    try
                    {
                        // A saved interior may predate the current room assets/settings. Its old
                        // room plan does not need to be reproducible to replace that interior.
                        // Replay is only used to identify the unchanged surface objects in this case.
                        bool samePlan = ValidateReplay(baseline, previous);
                        if (!samePlan && !locationComponent.m_hasInterior)
                        { Report(proxy, name, "ancien plan exterieur different de la reference : migration refusee"); return; }
                        var candidates = baseline.Objects.Select(session.GetZDO).Where(z => z != null).GroupBy(z => z.GetPrefab()).ToDictionary(g => g.Key, g => g.Select(z => z.GetPosition()).ToArray());
                        foreach (ZDOID id in world)
                        {
                            ZDO zdo = session.GetZDO(id);
                            Vector3[] points;
                            if (zdo == null || zdo == proxy || !candidates.TryGetValue(zdo.GetPrefab(), out points)) continue;
                            if (zdo.GetString(OwnerKey, "").Length != 0 && zdo.GetString(OwnerKey, "") != owner) continue;
                            if (!samePlan && zdo.GetPosition().y > 3000) continue;
                            if (points.Any(p => (p - zdo.GetPosition()).sqrMagnitude < 0.0625f)) old.Add(id);
                        }
                    }
                    finally { DestroyObjects(baseline.Objects); }
                }
                // Collect the existing interior BEFORE resolving spawner connections, including
                // objects from a different saved plan. Adjacent dungeon zones stay outside scope.
                foreach (ZDOID id in world)
                {
                    ZDO zdo = session.GetZDO(id);
                    if (zdo != null && zdo != proxy && InResetInterior(proxy, zdo, occupied, generators.Length != 0)) old.Add(id);
                }
                // CreatureSpawner stores the spawned creature ID, even when it walked outside its room.
                for (int i = 0; i < old.Count; i++)
                {
                    ZDO zdo = session.GetZDO(old[i]);
                    if (zdo == null) continue;
                    ZDOID child = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Spawned);
                    if (!child.IsNone() && session.GetZDO(child) != null && !old.Contains(child)) old.Add(child);
                }
                int newSeed = DungeonPolicy.NextSeed(proxy.GetInt(SeedKey, proxy.GetInt(ZDOVars.s_seed, 0)), Seeds.Next(int.MinValue + 1, int.MaxValue));
                Log.LogInfo("Dungeon " + name + " " + proxy.m_uid + " : preparation du nouveau plan, seed " + newSeed);
                staged = Stage(proxy, location, previous, false, newSeed);
                ValidateNew(staged, previous.Count);
                volumes.AddRange(staged.Volumes);
                // Cover the actual newly generated extents too (camp layouts can shift within their radius).
                foreach (ZDOID id in staged.Objects)
                {
                    ZDO zdo = session.GetZDO(id);
                    if (zdo != null) volumes.Add(new Bounds(zdo.GetPosition(), Vector3.one * 12));
                }
                // Old volumes were already scoped; retain the entire new reservation after relocation.
                foreach (ZDOID id in world)
                {
                    ZDO zdo = session.GetZDO(id);
                    if (zdo == null || zdo == proxy || !Inside(volumes, zdo.GetPosition())) continue;
                    if (zdo.GetPosition().y > 3000 && !InResetInterior(proxy, zdo, volumes, generators.Length != 0)) continue;
                    string otherOwner = zdo.GetString(OwnerKey, "");
                    if (otherOwner.Length != 0 && otherOwner != owner)
                    {
                        if (zdo.GetPosition().y > 3000) continue; // Never collect another interior's objects.
                        Report(proxy, name, "chevauchement exterieur avec un autre lieu suivi"); return;
                    }
                    if (zdo.GetPosition().y > 3000) old.Add(id);
                }
                occupied = OccupiedVolumes(locationComponent, volumes);
                if (!PlayersClear(occupied)) { Report(proxy, name, "joueur arrive dans le donjon avant validation"); return; }
                var tombs = world.Select(session.GetZDO).Where(z => z != null && IsTomb(z) &&
                    ((Inside(occupied, z.GetPosition()) && (z.GetPosition().y <= 3000 || InResetInterior(proxy, z, occupied, generators.Length != 0))) || old.Contains(z.m_uid))).ToList();
                old = old.Distinct().Where(id => { ZDO z = session.GetZDO(id); return z != null && !IsTomb(z) && !IsPlayer(z); }).ToList();
                List<TombMove> moves = PlanTombMoves(proxy, prefab, tombs, exterior);
                proxy.SetOwner(ZNet.GetUID());
                foreach (ZDOID id in old)
                {
                    ZDO zdo = session.GetZDO(id);
                    if (zdo == null) continue;
                    zdo.SetOwner(ZNet.GetUID());
                    zdo.Set(OwnerKey, owner);
                    zdo.Set(EpochKey, proxy.GetInt(EpochKey, 0));
                }
                byte[] journal = WriteResetJournal(old, staged.Objects, newSeed, staged.Epoch, DateTime.UtcNow.Ticks, occupied, moves);
                proxy.Set(JournalKey, journal); // Commit point: after this, never discard the replacement.
                committed = true;
                if (!FinishCommit(proxy, journal)) return;
                reports.Remove(proxy.m_uid);
                Log.LogInfo("Dungeon " + name + " " + proxy.m_uid + " regenere, seed " + newSeed + ", " + old.Count + " anciens objets / " + staged.Objects.Count + " nouveaux");
            }
            catch (Exception e) { Log.LogError("Dungeon " + name + " " + proxy.m_uid + " : " + e); }
            finally
            {
                if (staged != null && !committed) DestroyObjects(staged.Objects);
                location.m_prefab.Release();
            }
        }

        private static List<Bounds> BuildVolumes(ZDO proxy, GameObject prefab, IEnumerable<ZDO> generators, float exterior)
        {
            var volumes = new List<Bounds> { new Bounds(proxy.GetPosition(), new Vector3(exterior * 2, 300, exterior * 2)) };
            foreach (ZDO zdo in generators)
            {
                GameObject generatorPrefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
                DungeonGenerator generator = generatorPrefab ? generatorPrefab.GetComponent<DungeonGenerator>() : null;
                if (!generator) throw new InvalidOperationException("Missing generator prefab");
                Vector3 center = ZoneSystem.GetZonePos(ZoneSystem.GetZone(zdo.GetPosition()));
                center.y = zdo.GetPosition().y;
                // Deliberately conservative vertical extent: covers custom center offsets and all rooms.
                volumes.Add(zdo.GetInt(BossDungeonLayout.LayoutKey, 0) == 1 ? BossInteriorReservation.Bounds(proxy) : new Bounds(center, generator.m_zoneSize + new Vector3(32, 256, 32)));
            }
            Location loc = prefab.GetComponent<Location>();
            Transform interior = BossInteriorPlacement.FindInterior(loc);
            if (loc.m_hasInterior && interior && proxy.GetInt(BossDungeonLayout.LayoutKey, 0) != 1)
            {
                Vector3 local = prefab.transform.InverseTransformPoint(interior.position);
                Vector3 center = proxy.GetPosition() + proxy.GetRotation() * local;
                volumes.Add(new Bounds(center, Vector3.one * Math.Max(128, loc.m_interiorRadius * 2 + 32)));
            }
            return volumes;
        }

        private static bool Inside(IEnumerable<Bounds> volumes, Vector3 point) { return volumes.Any(b => b.Contains(point)); }

        private static bool InResetInterior(ZDO proxy, ZDO zdo, List<Bounds> volumes, bool generatedRooms)
        {
            string owner = zdo.GetString(OwnerKey, "");
            if (owner.Length != 0 && owner != proxy.GetString(IdentityKey, "")) return false;
            return zdo.GetPosition().y > 3000 && Inside(volumes, zdo.GetPosition()) &&
                (!generatedRooms || BossDungeonLayout.InLane(proxy, zdo.GetPosition()) || ZoneSystem.GetZone(zdo.GetPosition()) == ZoneSystem.GetZone(proxy.GetPosition()));
        }

        private static List<Bounds> ScopeInteriorVolumes(ZDO proxy, List<Bounds> volumes, bool generatedRooms)
        {
            if (!generatedRooms) return volumes;
            Vector3 center = ZoneSystem.GetZonePos(ZoneSystem.GetZone(proxy.GetPosition()));
            var scoped = new List<Bounds>();
            foreach (Bounds volume in volumes)
            {
                if (volume.center.y <= 3000) { scoped.Add(volume); continue; }
                if (BossDungeonLayout.InLane(proxy, volume.center)) { scoped.Add(volume); continue; }
                Vector3 min = volume.min, max = volume.max;
                min.x = Math.Max(min.x, center.x - 32); min.z = Math.Max(min.z, center.z - 32);
                max.x = Math.Min(max.x, center.x + 32); max.z = Math.Min(max.z, center.z + 32);
                if (max.x > min.x && max.z > min.z) scoped.Add(new Bounds((min + max) * 0.5f, max - min));
            }
            return scoped;
        }

        private static Capture Stage(ZDO proxy, ZoneSystem.ZoneLocation location, Dictionary<int, ZDO> previous, bool replay, int seed)
        {
            var capture = new Capture { Staging = true, Replay = replay, Seed = seed, Epoch = proxy.GetInt(EpochKey, 0) + 1, Parent = proxy };
            if (!replay && BossDungeonLayout.IsSupported(location.m_prefab.Name))
                capture.InteriorHeight = BossInteriorReservation.ChooseHeight(proxy);
            foreach (var pair in previous) capture.PreviousGenerators.Add(pair.Key, pair.Value);
            UnityEngine.Random.State random = UnityEngine.Random.state;
            bool ghost = ZNetView.m_ghostInit;
            bool damage = WearNTear.m_randomInitialDamage;
            // Native SpawnLocation temporarily modifies its source prefab. Restore even on exceptions.
            Transform[] transforms = location.m_prefab.Asset.GetComponentsInChildren<Transform>(true);
            Vector3[] positions = transforms.Select(t => t.localPosition).ToArray();
            Quaternion[] rotations = transforms.Select(t => t.localRotation).ToArray();
            bool[] active = transforms.Select(t => t.gameObject.activeSelf).ToArray();
            bool success = false;
            Current = capture;
            try
            {
                ZoneSystem.instance.SpawnLocation(location, proxy.GetInt(ZDOVars.s_seed, 0), proxy.GetPosition(), proxy.GetRotation(), ZoneSystem.SpawnMode.Ghost, new List<GameObject>(), false);
                foreach (ZDOID id in capture.Objects)
                {
                    ZDO zdo = session.GetZDO(id);
                    if (zdo == null) throw new InvalidOperationException("Staged ZDO disappeared");
                    zdo.Set(OwnerKey, Identity(proxy));
                    zdo.Set(EpochKey, capture.Epoch);
                    Identity(zdo);
                    zdo.Set(PendingKey, 1);
                }
                success = true;
                return capture;
            }
            finally
            {
                Current = null;
                foreach (GameObject view in capture.Views) if (view) Object.DestroyImmediate(view);
                for (int i = 0; i < transforms.Length; i++)
                {
                    if (!transforms[i]) continue;
                    transforms[i].localPosition = positions[i]; transforms[i].localRotation = rotations[i]; transforms[i].gameObject.SetActive(active[i]);
                }
                foreach (Room room in DungeonGenerator.m_placedRooms) if (room) Object.DestroyImmediate(room.gameObject);
                DungeonGenerator.m_placedRooms.Clear(); DungeonGenerator.m_openConnections.Clear(); DungeonGenerator.m_doorConnections.Clear();
                UnityEngine.Random.state = random; ZNetView.m_ghostInit = ghost; WearNTear.m_randomInitialDamage = damage;
                if (!success) DestroyObjects(capture.Objects);
            }
        }

        private static bool ValidateReplay(Capture baseline, Dictionary<int, ZDO> previous)
        {
            foreach (var pair in previous)
            {
                ZDO[] found = baseline.Objects.Select(session.GetZDO).Where(z => z != null && z.GetPrefab() == pair.Key).ToArray();
                if (found.Length != 1) return false;
                byte[] expected = pair.Value.GetByteArray(ZDOVars.s_roomData, null);
                byte[] actual = found[0].GetByteArray(ZDOVars.s_roomData, null);
                if (expected == null || actual == null || !expected.SequenceEqual(actual)) return false;
            }
            return baseline.Objects.Count > 0;
        }

        private static void ValidateNew(Capture capture, int expectedGenerators)
        {
            int generators = 0;
            foreach (ZDOID id in capture.Objects)
            {
                ZDO zdo = session.GetZDO(id);
                if (zdo == null) throw new InvalidOperationException("Missing replacement ZDO");
                byte[] rooms = zdo.GetByteArray(ZDOVars.s_roomData, null);
                if (rooms == null) continue;
                if (rooms.Length < 4 || BitConverter.ToInt32(rooms, 0) < 1) throw new InvalidOperationException("Empty generated dungeon");
                generators++;
            }
            if (capture.Objects.Count == 0 || generators != expectedGenerators) throw new InvalidOperationException("Incomplete generated location");
        }

        internal static byte[] WriteJournal(List<ZDOID> old, List<ZDOID> fresh, int seed, int epoch, long ticks)
        {
            ZPackage pkg = new ZPackage();
            pkg.Write(2); pkg.Write(seed); pkg.Write(epoch); pkg.Write(ticks);
            pkg.Write(old.Count); foreach (ZDOID id in old) pkg.Write(Identity(session.GetZDO(id)));
            pkg.Write(fresh.Count); foreach (ZDOID id in fresh) pkg.Write(Identity(session.GetZDO(id)));
            return pkg.GetArray();
        }

        private static bool FinishCommit(ZDO proxy, byte[] journal)
        {
            ZPackage pkg = new ZPackage(journal);
            int version = pkg.ReadInt();
            if (version != 2 && version != 3) throw new InvalidDataException("Unknown dungeon transaction version");
            int seed = pkg.ReadInt(), epoch = pkg.ReadInt();
            long ticks = pkg.ReadLong();
            List<string> old = ReadIds(pkg), fresh = ReadIds(pkg);
            var volumes = new List<Bounds>();
            var moves = new List<TombMove>();
            if (version == 3)
            {
                int count = ReadCount(pkg);
                for (int i = 0; i < count; i++)
                {
                    Vector3 center = ReadPoint(pkg), size = ReadPoint(pkg);
                    if (size.x < 0 || size.y < 0 || size.z < 0) throw new InvalidDataException("Invalid reset bounds");
                    volumes.Add(new Bounds(center, size));
                }
                count = ReadCount(pkg);
                for (int i = 0; i < count; i++) moves.Add(new TombMove { Identity = pkg.ReadString(), Destination = ReadPoint(pkg) });
                if (volumes.Count == 0) throw new InvalidDataException("Missing reset bounds");
            }
            string owner = Identity(proxy);
            if (fresh.Count == 0 || old.Contains(owner) || fresh.Contains(owner) || old.Intersect(fresh).Any()) throw new InvalidDataException("Invalid dungeon transaction IDs");
            if (moves.Any(m => m.Identity.Length != 32 || old.Contains(m.Identity) || fresh.Contains(m.Identity) || m.Identity == owner || m.Destination.y > 3000) ||
                moves.Select(m => m.Identity).Distinct().Count() != moves.Count) throw new InvalidDataException("Invalid tomb relocation IDs");
            var wanted = new HashSet<string>(old.Concat(fresh).Concat(moves.Select(m => m.Identity)));
            // Native ZDOIDs are reassigned on disk load. Resolve our stable keys in the current session.
            var objects = session.m_objectsByID.Values.Where(z => wanted.Contains(z.GetString(IdentityKey, ""))).ToDictionary(z => z.GetString(IdentityKey, ""));
            foreach (string id in old)
            {
                ZDO zdo;
                if (objects.TryGetValue(id, out zdo) && (zdo.GetString(OwnerKey, "") != owner || zdo.GetInt(EpochKey, -1) != epoch - 1))
                    throw new InvalidDataException("Old object ownership does not match transaction");
                if (zdo != null && (IsTomb(zdo) || IsPlayer(zdo)))
                    throw new InvalidDataException("Protected object included in deletion journal");
            }
            foreach (string id in fresh)
            {
                ZDO zdo;
                if (!objects.TryGetValue(id, out zdo) || zdo.GetString(OwnerKey, "") != owner || zdo.GetInt(EpochKey, -1) != epoch)
                    throw new InvalidDataException("Incomplete replacement; old generation retained");
            }
            // Old v2 journals had no occupancy geometry. Preserve recovery using the actual objects.
            if (version == 2) volumes.AddRange(objects.Values.Select(z => new Bounds(z.GetPosition(), Vector3.one * 12)));
            if (!PlayersClear(volumes)) { Report(proxy, "reprise", "joueur dans le donjon"); return false; }
            // A player can die inside while an interrupted transaction is waiting for recovery.
            var extraTombs = session.m_objectsByID.Values.Where(z => IsTomb(z) && Inside(volumes, z.GetPosition()) &&
                !moves.Any(m => m.Identity == z.GetString(IdentityKey, ""))).ToList();
            if (extraTombs.Count != 0)
            {
                var location = ZoneSystem.instance.GetLocation(proxy.GetInt(ZDOVars.s_location, 0));
                if (location == null) throw new InvalidOperationException("Location required to relocate recovery tombs");
                location.m_prefab.Load();
                try
                {
                    GameObject prefab = location.m_prefab.Asset;
                    moves.AddRange(PlanTombMoves(proxy, prefab, extraTombs, Math.Max(40, prefab.GetComponent<Location>().m_exteriorRadius + 16), moves.Select(m => m.Destination)));
                    foreach (ZDO tomb in extraTombs) objects[Identity(tomb)] = tomb;
                    proxy.Set(JournalKey, WriteResetJournalIds(old, fresh, seed, epoch, ticks, volumes, moves));
                }
                finally { location.m_prefab.Release(); }
            }
            foreach (TombMove move in moves)
            {
                ZDO tomb;
                if (objects.TryGetValue(move.Identity, out tomb) && !IsTomb(tomb)) throw new InvalidDataException("Relocation target is not a tomb");
            }
            proxy.SetOwner(ZNet.GetUID());
            BossInteriorReservation.Remember(proxy, volumes);
            // Offline character ZDOs are not occupants. Keep them outside too; private
            // character-file logout points are resolved through persisted history on reconnect.
            foreach (var character in session.m_objectsByID.Values.Where(z => IsPlayer(z) && Inside(volumes, z.GetPosition())).ToArray())
            {
                character.SetOwner(ZNet.GetUID());
                character.SetPosition(DungeonReconnect.ExteriorPoint(proxy));
            }
            foreach (TombMove move in moves)
            {
                ZDO tomb;
                if (objects.TryGetValue(move.Identity, out tomb)) MoveTomb(tomb, move.Destination);
            }
            DestroyObjects(old.Where(objects.ContainsKey).Select(id => objects[id].m_uid).ToArray());
            foreach (string id in fresh) objects[id].Set(PendingKey, 0);
            BossDungeonLayout.CommitMarker(proxy, fresh.Select(id => objects[id]));
            proxy.Set(SeedKey, seed); proxy.Set(EpochKey, epoch); proxy.Set(LastKey, ticks); proxy.Set(TrackedKey, true);
            proxy.Set(VisitStartedKey, 0L);
            visitBounds.Remove(proxy.m_uid);
            proxy.Set(RequestedKey, false);
            proxy.Set(JournalKey, new byte[0]);
            Log.LogInfo("Dungeon " + proxy.m_uid + " : reset termine, " + moves.Count + " tombe(s) deplacee(s) a l'exterieur");
            return true;
        }

        private static List<string> ReadIds(ZPackage pkg)
        {
            int count = pkg.ReadInt();
            if (count < 0 || count > 100000) throw new InvalidDataException("Invalid dungeon object count");
            var ids = new List<string>(count);
            for (int i = 0; i < count; i++) { string id = pkg.ReadString(); if (id.Length != 32) throw new InvalidDataException("Invalid stable identity"); ids.Add(id); }
            return ids;
        }

        private static void DestroyObjects(IEnumerable<ZDOID> ids)
        {
            foreach (ZDOID id in ids)
            {
                ZDO zdo = session.GetZDO(id);
                if (zdo == null) continue;
                zdo.SetOwner(ZNet.GetUID());
                session.DestroyZDO(zdo);
            }
            // The native routed RPC processes the server synchronously and sends destruction to peers.
            // Flush before returning to the game loop / the next save snapshot.
            session.SendDestroyed();
            foreach (ZDOID id in ids) if (session.GetZDO(id) != null) throw new InvalidOperationException("ZDO destruction not acknowledged: " + id);
        }
    }
}
