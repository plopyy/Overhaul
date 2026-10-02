using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Overhaul.Dungeons
{
    internal static class BossDungeonLayout
    {
        internal const string RoomName = "Overhaul_ForestCrypt_BossRoom";
        internal const string SwampRoomName = "Overhaul_SunkenCrypt_BossRoom";
        internal const string MistlandsRoomName = "Overhaul_Mistlands_BossRoom";
        internal const string MountainRoomName = "Overhaul_MountainCave_BossRoom";
        internal const string PassageName = "Overhaul_ForestCrypt_BossPassage";
        internal static readonly int LayoutKey = "overhaul_forest_layout_v1".GetStableHashCode();
        internal static bool Planning;
        internal static ZDO LoadingProxy;
        private static AssetBundle bundle, swampBundle, mountainBundle;
        private static CustomRoom arena, passage, swampArena, mountainArena, mistlandsArena;
        internal static bool IsSwamp(DungeonGenerator generator) { return DungeonPolicy.PrefabName(generator.name) == "DG_SunkenCrypt"; }
        internal static bool IsMountain(DungeonGenerator generator) { return DungeonPolicy.PrefabName(generator.name) == "DG_Cave"; }
        internal static bool IsMistlands(DungeonGenerator generator) { return DungeonPolicy.PrefabName(generator.name) == "DG_DvergrTown"; }
        private static CustomRoom ArenaFor(DungeonGenerator generator) { return IsMistlands(generator) ? mistlandsArena : IsMountain(generator) ? mountainArena : IsSwamp(generator) ? swampArena : arena; }
        internal static bool IsSupported(string name) { return IsForest(name) || name == "SunkenCrypt4" || name == "MountainCave02" || name == "Mistlands_DvergrTownEntrance1" || name == "Mistlands_DvergrTownEntrance2"; }
        private static GameObject templates;
        internal static bool IsForest(string name) { return name == "Crypt2" || name == "Crypt3" || name == "Crypt4"; }
        internal static bool Enabled(DungeonGenerator generator)
        {
            if (DungeonPolicy.PrefabName(generator.name) != "DG_ForestCrypt" && !IsSwamp(generator) && !IsMountain(generator) && !IsMistlands(generator)) return false;
            if (generator.transform.position.y < 11000) return false;
            if (DungeonRuntime.Current != null && DungeonRuntime.Current.Replay)
                return DungeonRuntime.Current.Parent.GetInt(LayoutKey, 0) == 1;
            return true;
        }
        internal static float Height(Vector3 position)
        {
            var zone = ZoneSystem.GetZone(position);
            // Nearest rooms sharing a lane are 384m apart, wider than 256m + safety margin.
            int x = ((zone.x % 6) + 6) % 6, z = ((zone.y % 6) + 6) % 6;
            return 12000 + (x + 6 * z) * 128;
        }
        internal static Bounds BoundsFor(Vector3 position)
        {
            var center = ZoneSystem.GetZonePos(ZoneSystem.GetZone(position)); center.y = Height(position);
            return new Bounds(center, new Vector3(288, 96, 288));
        }
        internal static bool InLane(ZDO proxy, Vector3 position) { return BossInteriorReservation.Bounds(proxy).Contains(position); }
        internal static void Initialize()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Overhaul.Assets.overhaul_bossroom"))
            {
                if (stream == null) throw new InvalidOperationException("Boss room resource missing");
                var bytes = new byte[stream.Length]; stream.Read(bytes, 0, bytes.Length);
                bundle = AssetBundle.LoadFromMemory(bytes);
            }
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Overhaul.Assets.overhaul_swampbossroom"))
            {
                if (stream == null) throw new InvalidOperationException("Swamp boss room resource missing");
                var bytes = new byte[stream.Length]; stream.Read(bytes, 0, bytes.Length);
                swampBundle = AssetBundle.LoadFromMemory(bytes);
            }
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Overhaul.Assets.overhaul_mountainbossroom"))
            {
                if (stream == null) throw new InvalidOperationException("Mountain boss room resource missing");
                var bytes = new byte[stream.Length]; stream.Read(bytes, 0, bytes.Length);
                mountainBundle = AssetBundle.LoadFromMemory(bytes);
            }
            DungeonManager.OnVanillaRoomsAvailable += RegisterRooms;
        }
        private static void RegisterRooms()
        {
            if (arena != null) return;
            templates=new GameObject("OverhaulBossTemplates");templates.SetActive(false);Object.DontDestroyOnLoad(templates);
            var prefab = Object.Instantiate(bundle.LoadAsset<GameObject>(RoomName),templates.transform,false);prefab.name=RoomName;
            var passagePrefab=Object.Instantiate(bundle.LoadAsset<GameObject>(PassageName),templates.transform,false);passagePrefab.name=PassageName;
            BossNativeMaterials.Bind(prefab,passagePrefab);
            InstallMushrooms(prefab,PrefabManager.Instance.GetPrefab("Pickable_Mushroom_yellow"));
            var visual = prefab.transform.Find("RewardChestVisual");
            if (visual) visual.gameObject.SetActive(false);
            arena = new CustomRoom(prefab, false, new RoomConfig { ThemeName = "ForestCrypt", Enabled = false });
            passage = new CustomRoom(passagePrefab, false, new RoomConfig { ThemeName = "ForestCrypt", Enabled = false });
            if (!DungeonManager.Instance.AddCustomRoom(arena) || !DungeonManager.Instance.AddCustomRoom(passage))
                throw new InvalidOperationException("Cannot register mandatory boss rooms");
            var swampPrefab = Object.Instantiate(swampBundle.LoadAsset<GameObject>(SwampRoomName), templates.transform, false); swampPrefab.name=SwampRoomName;
            BossNativeMaterials.Bind(swampPrefab);
            InstallMushrooms(swampPrefab,PrefabManager.Instance.GetPrefab("Pickable_Mushroom_yellow"));
            swampPrefab.transform.Find("RewardChestVisual").gameObject.SetActive(false);
            swampArena = new CustomRoom(swampPrefab, false, new RoomConfig { ThemeName = "SunkenCrypt", Enabled = false });
            if (!DungeonManager.Instance.AddCustomRoom(swampArena)) throw new InvalidOperationException("Cannot register swamp boss room");
            var mountainPrefab = Object.Instantiate(mountainBundle.LoadAsset<GameObject>(MountainRoomName), templates.transform, false); mountainPrefab.name=MountainRoomName;
            BossNativeMaterials.Bind(mountainPrefab);
            mountainPrefab.transform.Find("RewardChestVisual").gameObject.SetActive(false);
            mountainArena = new CustomRoom(mountainPrefab, false, new RoomConfig { ThemeName = "Cave", Enabled = false });
            if (!DungeonManager.Instance.AddCustomRoom(mountainArena)) throw new InvalidOperationException("Cannot register mountain boss room");
            var mistlandsPrefab=MistlandsBossRoom.Create(templates.transform);
            mistlandsArena=new CustomRoom(mistlandsPrefab,false,new RoomConfig {ThemeName="DvergerTown",Enabled=false});
            if(!DungeonManager.Instance.AddCustomRoom(mistlandsArena))throw new InvalidOperationException("Cannot register Mistlands boss room");
        }
        internal static void InstallMushrooms(GameObject room,GameObject source)
        {
            if(!source||!source.GetComponent<Pickable>()||!source.GetComponent<ZNetView>())throw new InvalidOperationException("Native yellow mushroom unavailable");
            foreach(var marker in room.GetComponentsInChildren<Transform>(true).Where(t=>t.name.StartsWith("WildMushroom_Patch")).ToArray())
            {
                foreach(var child in marker.Cast<Transform>().ToArray())Object.DestroyImmediate(child.gameObject);
                var pickable=Object.Instantiate(source,marker,false);pickable.name="Pickable_Mushroom_yellow";
                pickable.transform.localPosition=Vector3.zero;pickable.transform.localRotation=Quaternion.identity;pickable.SetActive(true);
            }
        }
        internal static void CommitMarker(ZDO proxy, IEnumerable<ZDO> objects)
        {
            if (objects.Any(z => z != null && z.GetInt(LayoutKey, 0) == 1)) proxy.Set(LayoutKey, 1);
            foreach (var zdo in objects.Where(z => z != null && z.GetInt(LayoutKey, 0) == 1))
            {
                float height = zdo.GetFloat(BossInteriorReservation.HeightKey, 0);
                if (height > 0) { proxy.Set(BossInteriorReservation.HeightKey, height); break; }
            }
        }
        private sealed class Placement
        {
            internal DungeonDB.RoomData Data; internal Vector3 Position; internal Quaternion Rotation; internal int Order;
        }
        private sealed class Door
        {
            internal string Type; internal Vector3 Position; internal Quaternion Rotation;
        }
        internal static void Generate(DungeonGenerator generator, ZoneSystem.SpawnMode mode)
        {
            var arena = ArenaFor(generator);
            if (arena == null || arena.RoomData == null || passage == null || passage.RoomData == null)
                throw new InvalidOperationException("Mandatory boss rooms not registered");
            var oldSize = generator.m_zoneSize;
            var plan = new List<Placement>(); var doors = new List<Door>();
            try
            {
                Plan(generator);
                foreach (var room in DungeonGenerator.m_placedRooms)
                    plan.Add(new Placement { Data = DungeonDB.instance.GetRoom(room.GetHash()), Position = room.transform.position, Rotation = room.transform.rotation, Order = room.m_placeOrder });
                foreach (var door in DungeonGenerator.m_doorConnections)
                    doors.Add(new Door { Type = door.m_type, Position = door.transform.position, Rotation = door.transform.rotation });
                if (plan.Count(p => p.Data == arena.RoomData) != 1 || plan.Any(p => p.Data == null))
                    throw new InvalidOperationException("Invalid boss dungeon plan");
                ClearPlan(generator);
                Planning = false;
                Room bossRoom = null;
                foreach (var p in plan)
                {
                    var room = generator.PlaceRoom(p.Data, p.Position, p.Rotation, null, mode);
                    room.m_placeOrder = p.Order;
                    if (p.Data == arena.RoomData) bossRoom = room;
                }
                DungeonGenerator.m_doorConnections.Clear();
                // Native door selection, with temporary connection transforms surviving until placed.
                var doorRoot = new GameObject("BossDungeonDoorPlacement");
                try
                {
                    foreach (var d in doors)
                    {
                        var go = new GameObject("Connection"); go.transform.SetParent(doorRoot.transform);
                        go.transform.SetPositionAndRotation(d.Position, d.Rotation);
                        var connection = go.AddComponent<RoomConnection>(); connection.m_type = d.Type;
                        DungeonGenerator.m_doorConnections.Add(connection);
                    }
                    generator.PlaceDoors(mode);
                    if (!IsMountain(generator) && !IsMistlands(generator))
                    {
                        var doorDef = generator.FindDoorType("");
                        if (doorDef == null || !doorDef.m_prefab) throw new InvalidOperationException("Boss door unavailable");
                        var door = Object.Instantiate(doorDef.m_prefab, bossRoom.transform.Find("DoorSpawn").position, bossRoom.transform.rotation);
                        if (mode == ZoneSystem.SpawnMode.Ghost) Object.Destroy(door);
                    }
                    BossEncounter.Spawn(bossRoom, mode);
                    generator.m_nview.GetZDO().Set(LayoutKey, 1);
                    generator.m_nview.GetZDO().Set(BossInteriorReservation.HeightKey, generator.transform.position.y);
                }
                finally { DungeonGenerator.m_doorConnections.Clear(); Object.DestroyImmediate(doorRoot); }
            }
            finally { Planning = false; generator.m_zoneSize = oldSize; }
        }
        internal static void Plan(DungeonGenerator generator)
        {
            // Native custom-interior generation subtracts the authored vertical offset
            // from m_zoneCenter. Our isolated volume is centered on the actual generator.
            generator.m_zoneCenter.y = generator.transform.position.y;
            Planning = true;
            try
            {
                for (int attempt = 0; attempt < 128; attempt++)
                {
                    ClearPlan(generator);
                    UnityEngine.Random.InitState(unchecked(generator.m_generatedSeed + attempt * 104729));
                    generator.m_zoneSize = new Vector3(128, IsSwamp(generator) || IsMountain(generator) || IsMistlands(generator) ? 80 : 64, 128);
                    generator.PlaceStartRoom(ZoneSystem.SpawnMode.Client);
                    generator.PlaceRooms(ZoneSystem.SpawnMode.Client);
                    // Native minimum is an early-stop threshold, not a guaranteed room count.
                    if (DungeonGenerator.m_placedRooms.Count < 5) continue;
                    generator.m_zoneSize = new Vector3(256, IsSwamp(generator) || IsMountain(generator) || IsMistlands(generator) ? 80 : 64, 256);
                    if (!AttachArena(generator)) continue;
                    generator.PlaceEndCaps(ZoneSystem.SpawnMode.Client);
                    if (IsMountain(generator) || IsMistlands(generator))
                    {
                        // Native cave end caps with a zero-width reservation bypass the
                        // placement collision test. Validate again after closing branches,
                        // rather than allowing an upper-floor cap to cut into the cavern.
                        var boss = DungeonGenerator.m_placedRooms.Single(r => r.GetHash() == (IsMistlands(generator)?MistlandsRoomName:MountainRoomName).GetStableHashCode());
                        DungeonGenerator.m_placedRooms.Remove(boss);
                        bool blocked = generator.TestCollision(boss, boss.transform.position, boss.transform.rotation);
                        DungeonGenerator.m_placedRooms.Add(boss);
                        if (blocked) continue;
                    }
                    return;
                }
                throw new InvalidOperationException("Aucun plan connecte avec salle de boss : generation annulee");
            }
            finally { Planning = false; }
        }
        private static bool AttachArena(DungeonGenerator generator)
        {
            var arena = ArenaFor(generator);
            var nativePassage=DungeonDB.instance.GetRoom((IsMistlands(generator)?"dvergr_corridor01":IsMountain(generator)?"cave_new_corridor01":IsSwamp(generator)?"sunkencrypt_new_Corridor2":"forestcrypt_Corridor1").GetStableHashCode());
            if(nativePassage==null)throw new InvalidOperationException("Native forest crypt corridor unavailable");
            // Shortest path through the final graph: cycles must not create an entrance shortcut.
            var depths = DistancesFromEntrance();
            string connectionType=IsMistlands(generator)?"dvergr":"";
            var candidates = DungeonGenerator.m_openConnections.Where(c => !c.m_entrance && c.m_type == connectionType && c.m_placeOrder >= 3)
                .Where(c => depths.ContainsKey(c.GetComponentInParent<Room>()) && depths[c.GetComponentInParent<Room>()] >= 3)
                .OrderByDescending(c => depths[c.GetComponentInParent<Room>()]).ToArray();
            foreach (var start in candidates)
            {
                int roomCount = DungeonGenerator.m_placedRooms.Count;
                var open = DungeonGenerator.m_openConnections.ToArray(); var doors = DungeonGenerator.m_doorConnections.ToArray();
                var current = start;
                for (int extension = 0; extension <= 24; extension++)
                {
                    if (IsSwamp(generator))
                    {
                        // Ascend through the native staircase; never substitute a flat corridor.
                        var stairData = DungeonDB.instance.GetRoom("sunkencrypt_new_Stair1".GetStableHashCode());
                        if (stairData == null) throw new InvalidOperationException("Native swamp staircase unavailable");
                        var lower = stairData.RoomInPrefab.GetConnections().OrderBy(c=>c.transform.localPosition.y).First();
                        generator.CalculateRoomPosRot(lower,current.transform.position,current.transform.rotation*Quaternion.Euler(0,180,0),out var stairPos,out var stairRot);
                        if (generator.TestCollision(stairData.RoomInPrefab,stairPos,stairRot)) break;
                        var stair = generator.PlaceRoom(stairData,stairPos,stairRot,current,ZoneSystem.SpawnMode.Client);
                        DungeonGenerator.m_openConnections.Remove(current);
                        current = stair.GetConnections().OrderByDescending(c=>c.transform.position.y).First();
                    }
                    if (generator.PlaceRoom(current, arena.RoomData, ZoneSystem.SpawnMode.Client))
                    { DungeonGenerator.m_doorConnections.Remove(current); return true; }
                    if (IsSwamp(generator)) continue;
                    if (extension == 24 || !generator.PlaceRoom(current, nativePassage, ZoneSystem.SpawnMode.Client)) break;
                    var room = DungeonGenerator.m_placedRooms.Last();
                    var from=current.transform.position;var direction=-current.transform.forward;
                    current = room.GetConnections().Where(c=>c.m_type==connectionType && Vector3.Distance(c.transform.position,from)>.1f)
                        .OrderByDescending(c=>Vector3.Dot((c.transform.position-from).normalized,direction)).First();
                }
                for (int i = DungeonGenerator.m_placedRooms.Count - 1; i >= roomCount; i--)
                { Object.DestroyImmediate(DungeonGenerator.m_placedRooms[i].gameObject); DungeonGenerator.m_placedRooms.RemoveAt(i); }
                DungeonGenerator.m_openConnections.Clear(); DungeonGenerator.m_openConnections.AddRange(open);
                DungeonGenerator.m_doorConnections.Clear(); DungeonGenerator.m_doorConnections.AddRange(doors);
            }
            return false;
        }
        internal static Dictionary<Room, int> DistancesFromEntrance()
        {
            var rooms = DungeonGenerator.m_placedRooms;
            var adjacency = rooms.ToDictionary(r => r, r => new HashSet<Room>());
            // Quarter-metre stair ports lie on rounding boundaries: compare actual
            // distances instead of splitting matching ports into adjacent grid cells.
            var ports = rooms.SelectMany(r => r.GetConnections().Where(c => !c.m_entrance)).ToArray();
                foreach (var a in ports) foreach (var b in ports)
                {
                    if (a == b || (a.transform.position-b.transform.position).sqrMagnitude >= .01f || a.m_type != b.m_type || Vector3.Dot(a.transform.forward, b.transform.forward) > -.99f) continue;
                    var ra = a.GetComponentInParent<Room>(); var rb = b.GetComponentInParent<Room>();
                    if (ra != rb) adjacency[ra].Add(rb);
                }
            var distance = new Dictionary<Room, int>(); var queue = new Queue<Room>();
            foreach (var room in rooms.Where(r => r.m_entrance)) { distance[room] = 0; queue.Enqueue(room); }
            while (queue.Count != 0)
            {
                var current = queue.Dequeue();
                foreach (var next in adjacency[current]) if (!distance.ContainsKey(next)) { distance[next] = distance[current] + 1; queue.Enqueue(next); }
            }
            return distance;
        }
        private static void ClearPlan(DungeonGenerator generator)
        {
            generator.Clear(); DungeonGenerator.m_placedRooms.Clear(); DungeonGenerator.m_openConnections.Clear(); DungeonGenerator.m_doorConnections.Clear();
        }
    }
    // The native upper stair port is at x=7.75, inside its declared [-8,8] box.
    // Use the actual connection plane as the reservation boundary. Geometry and ports
    // stay untouched, so existing floors and walls still join without a 25 cm gap.
    [HarmonyPatch(typeof(DungeonGenerator), "TestCollision")]
    internal static class SwampStairCollisionPatch
    {
        private static void Reservation(Room room, Vector3 position, Quaternion rotation, out Vector3 size, out Vector3 center)
        {
            size=room.m_size;center=position;
            if(DungeonPolicy.PrefabName(room.name)!="sunkencrypt_new_Stair1")return;
            size.x-=.25f;center+=rotation*new Vector3(-.125f,0,0);
        }
        private static bool Prefix(DungeonGenerator __instance,Room room,Vector3 pos,Quaternion rot,ref bool __result)
        {
            if(!BossDungeonLayout.IsSwamp(__instance)||!BossDungeonLayout.Enabled(__instance))return true;
            if(!__instance.IsInsideDungeon(room,pos,rot)){__result=true;return false;}
            Reservation(room,pos,rot,out var size,out var center);
            __instance.m_colliderA.size=size-Vector3.one*.1f;
            foreach(var other in DungeonGenerator.m_placedRooms)
            {
                Reservation(other,other.transform.position,other.transform.rotation,out var otherSize,out var otherCenter);
                __instance.m_colliderB.size=otherSize;
                if(Physics.ComputePenetration(__instance.m_colliderA,center,rot,__instance.m_colliderB,otherCenter,other.transform.rotation,out _,out _))
                {__result=true;return false;}
            }
            __result=false;return false;
        }
    }
    [HarmonyPatch(typeof(DungeonGenerator), "GenerateDungeon")]
    internal static class BossLayoutPatch
    {
        private static bool Prefix(DungeonGenerator __instance, ZoneSystem.SpawnMode mode)
        { if (!BossDungeonLayout.Enabled(__instance) || mode == ZoneSystem.SpawnMode.Client) return true; BossDungeonLayout.Generate(__instance, mode); return false; }
    }
    [HarmonyPatch(typeof(DungeonGenerator), "PlaceRoom", new Type[] { typeof(DungeonDB.RoomData), typeof(Vector3), typeof(Quaternion), typeof(RoomConnection), typeof(ZoneSystem.SpawnMode) })]
    internal static class BossPlanningPatch
    {
        private static void Postfix(DungeonGenerator __instance, Room __result, RoomConnection fromConnection, ZoneSystem.SpawnMode mode)
        {
            if (!BossDungeonLayout.Planning || mode != ZoneSystem.SpawnMode.Client) return;
            __result.m_placeOrder = fromConnection ? fromConnection.m_placeOrder + 1 : 0;
            DungeonGenerator.m_placedRooms.Add(__result); __instance.AddOpenConnections(__result, fromConnection);
        }
    }
}
