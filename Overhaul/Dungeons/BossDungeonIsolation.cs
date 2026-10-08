using System;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Dungeons
{
    [HarmonyPatch(typeof(ZoneSystem), "SpawnLocation")]
    internal static class BossInteriorPlacement
    {
        internal sealed class State { internal Transform Interior; internal Vector3 Position; }
        private static void Prefix(ZoneSystem.ZoneLocation location, Vector3 pos, ZoneSystem.SpawnMode mode, out State __state)
        {
            __state = null;
            bool boss = BossDungeonLayout.IsSupported(location.m_prefab.Name);
            var current = DungeonRuntime.Current;
            // Boss layouts always use a lane; other interiors only once a reset has moved them into one.
            bool use = mode == ZoneSystem.SpawnMode.Client ? BossInteriorReservation.Lane(BossDungeonLayout.LoadingProxy)
                : current == null ? boss
                : current.Replay ? BossInteriorReservation.Lane(current.Parent)
                : boss || current.InteriorHeight > 0;
            if (!use) return;
            location.m_prefab.Load();
            try
            {
                var loc = location.m_prefab.Asset.GetComponent<Location>();
                var interior = loc ? FindInterior(loc) : null;
                if (!loc || !interior)
                    throw new InvalidOperationException("Dungeon interior transform unavailable");
                __state = new State { Interior = interior, Position = interior.localPosition };
                var proxy = DungeonRuntime.Current?.Parent ?? BossDungeonLayout.LoadingProxy;
                float height = DungeonRuntime.Current != null && DungeonRuntime.Current.InteriorHeight > 0
                    ? DungeonRuntime.Current.InteriorHeight
                    : proxy != null ? BossInteriorReservation.Bounds(proxy).center.y : BossDungeonLayout.Height(pos);
                var p = __state.Position; p.y = height - pos.y;
                var generator = interior.GetComponentInChildren<DungeonGenerator>(true);
                // Native custom interiors zero the generator local position. The authored offset affects bounds, not world height.
                if (generator && !(loc.m_useCustomInteriorTransform && loc.m_interiorTransform && loc.m_generator))
                    p.y -= generator.transform.position.y - interior.position.y;
                // Legacy forest prefabs do not use the newer custom-interior transform mode.
                // Translate their common Interior parent, keeping every native teleport link.
                interior.localPosition = p;
            }
            finally { location.m_prefab.Release(); }
        }
        private static Exception Finalizer(State __state, Exception __exception)
        { if (__state != null && __state.Interior) __state.Interior.localPosition = __state.Position; return __exception; }
        internal static Transform FindInterior(Location location)
        { return location.m_interiorTransform ? location.m_interiorTransform : location.transform.Find("Interior"); }
    }
    [HarmonyPatch(typeof(LocationProxy), "SpawnLocation")]
    internal static class BossProxySpawnPatch
    {
        private static void Prefix(LocationProxy __instance, out ZDO __state)
        { __state = BossDungeonLayout.LoadingProxy; BossDungeonLayout.LoadingProxy = __instance.m_nview ? __instance.m_nview.GetZDO() : null; }
        private static Exception Finalizer(ZDO __state, Exception __exception)
        { BossDungeonLayout.LoadingProxy = __state; return __exception; }
    }
    [HarmonyPatch(typeof(LocationProxy), "Update")]
    internal static class BossProxyMigrationPatch
    {
        private static void Prefix(LocationProxy __instance)
        {
            if (!__instance.m_nview || !__instance.m_nview.IsValid() || !__instance.m_instance) return;
            var data = __instance.m_nview.GetZDO();
            bool lane = BossInteriorReservation.Lane(data);
            var location = __instance.m_instance.GetComponent<Location>();
            var interior = location ? BossInteriorPlacement.FindInterior(location) : null;
            if (!interior) return;
            var generator = interior.GetComponentInChildren<DungeonGenerator>(true);
            float currentHeight = generator ? generator.transform.position.y : interior.position.y;
            // Also refresh an instance still built in a lane the interior has just left.
            if (lane ? Mathf.Abs(currentHeight - BossInteriorReservation.Bounds(data).center.y) < 1f : currentHeight < 11000) return;
            // A reset keeps the surface proxy. Refresh its static teleport links after the commit.
            UnityEngine.Object.Destroy(__instance.m_instance);
            __instance.m_instance = null; __instance.m_locationNeedsSpawn = true;
        }
    }
    [HarmonyPatch(typeof(Location), "Awake")]
    internal static class BossInteriorEnvironmentPatch
    {
        private static void Postfix(Location __instance)
        {
            var interior = BossInteriorPlacement.FindInterior(__instance);
            if (!interior || interior.position.y < 11000) return;
            foreach (Transform child in __instance.transform)
            {
                var environment = child.GetComponent<EnvZone>();
                if (!environment || environment.m_environment != __instance.m_interiorEnvironment) continue;
                var proxy = DungeonRuntime.Current?.Parent ?? BossDungeonLayout.LoadingProxy;
                var bounds = proxy != null ? BossInteriorReservation.Bounds(proxy) : BossDungeonLayout.BoundsFor(__instance.transform.position);
                var center = bounds.center;
                var generator = interior.GetComponentInChildren<DungeonGenerator>(true);
                center.y = generator ? generator.transform.position.y : interior.position.y;
                bounds.center = center;
                child.position = bounds.center; child.rotation = Quaternion.identity; child.localScale = bounds.size;
            }
        }
    }
    [HarmonyPatch(typeof(Location), "GetZoneLocation", new Type[] { typeof(Vector3) })]
    internal static class BossInteriorLocationLookupPatch
    {
        private static bool Prefix(Vector3 point, ref Location __result)
        {
            if (point.y < 11000) return true;
            foreach (var location in Location.s_allLocations)
            {
                if (!location) continue;
                var proxy = location.GetComponentInParent<LocationProxy>();
                if (!proxy || !proxy.m_nview || !proxy.m_nview.IsValid()) continue;
                var data = proxy.m_nview.GetZDO();
                if (!BossInteriorReservation.Lane(data) || !BossDungeonLayout.InLane(data, point)) continue;
                __result = location; return false;
            }
            return true;
        }
    }
}


