using System;
using System.Collections.Generic;
using HarmonyLib;
using Overhaul.Utility;
using UnityEngine;

namespace Overhaul.Dungeons
{
    [HarmonyPatch(typeof(DungeonGenerator), "Generate", new Type[] { typeof(int), typeof(ZoneSystem.SpawnMode) })]
    internal static class DungeonGeneratePatch
    {
        internal sealed class State { internal int Min, Max; }
        private static void Prefix(DungeonGenerator __instance, ref int seed, ZoneSystem.SpawnMode mode, out State __state)
        {
            __state = new State { Min = __instance.m_minRooms, Max = __instance.m_maxRooms };
            if (mode == ZoneSystem.SpawnMode.Client || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            DungeonRuntime.ConfigureGeneration(__instance, ref seed);
        }
        private static Exception Finalizer(DungeonGenerator __instance, State __state, Exception __exception)
        {
            if (__state != null) { __instance.m_minRooms = __state.Min; __instance.m_maxRooms = __state.Max; }
            return __exception;
        }
    }

    [HarmonyPatch(typeof(DungeonGenerator), "GetSeed")]
    internal static class DungeonSeedPatch
    {
        private static void Postfix(DungeonGenerator __instance, ref int __result)
        {
            ZDO zdo = __instance.m_nview == null ? null : __instance.m_nview.GetZDO();
            int seed;
            if (zdo != null && zdo.GetInt(DungeonRuntime.SeedKey, out seed))
            {
                __result = seed;
                __instance.m_generatedSeed = seed;
                __instance.m_hasGeneratedSeed = true;
            }
        }
    }

    [HarmonyPatch(typeof(ZoneSystem), "SpawnLocation")]
    internal static class DungeonLocationPatch
    {
        private static void Prefix(ZoneSystem.ZoneLocation location, ZoneSystem.SpawnMode mode, out DungeonRuntime.Capture __state)
        {
            __state = null;
            if (DungeonRuntime.Current != null || mode == ZoneSystem.SpawnMode.Client || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!DungeonPolicy.Supported.Contains(location.m_prefab.Name)) return;
            __state = new DungeonRuntime.Capture();
            DungeonRuntime.Current = __state;
        }
        private static Exception Finalizer(DungeonRuntime.Capture __state, Exception __exception)
        {
            if (__state == null) return __exception;
            try { if (__exception == null) DungeonRuntime.FinishInitialCapture(__state); }
            catch (Exception e) { Log.LogError("Dungeon tracking: " + e); }
            finally { DungeonRuntime.Current = null; }
            return __exception;
        }
    }

    [HarmonyPatch(typeof(ZoneSystem), "CreateLocationProxy")]
    internal static class DungeonKeepEntrancePatch
    {
        private static bool Prefix() { return DungeonRuntime.Current == null || !DungeonRuntime.Current.Staging; }
    }

    [HarmonyPatch(typeof(ZDOMan), "CreateNewZDO", new Type[] { typeof(Vector3), typeof(int) })]
    internal static class DungeonNewObjectPatch
    {
        private static void Postfix(ZDO __result) { DungeonRuntime.Current?.Objects.Add(__result.m_uid); }
    }

    [HarmonyPatch(typeof(ZNetView), "Awake")]
    internal static class DungeonGhostPatch
    {
        private static void Prefix(out bool __state)
        {
            __state = ZNetView.m_ghostInit;
            if (DungeonRuntime.Current != null && DungeonRuntime.Current.Staging) ZNetView.StartGhostInit();
        }
        private static Exception Finalizer(ZNetView __instance, bool __state, Exception __exception)
        {
            if (DungeonRuntime.Current != null && DungeonRuntime.Current.Staging)
            {
                DungeonRuntime.Current.Views.Add(__instance.gameObject);
                ZNetView.m_ghostInit = __state;
            }
            return __exception;
        }
    }

    // Spawn scopes also execute on the owning client. The parent's persisted identity is inherited.
    [HarmonyPatch(typeof(CreatureSpawner), "Spawn")]
    internal static class DungeonCreaturePatch
    {
        private static void Postfix(CreatureSpawner __instance, ZNetView __result)
        {
            if (__result && __instance.m_nview) DungeonRuntime.Inherit(__instance.m_nview.GetZDO(), __result.GetZDO());
        }
    }

    [HarmonyPatch(typeof(SpawnArea), "SpawnOne")]
    internal static class DungeonSpawnAreaPatch
    {
        private static void Prefix(SpawnArea __instance, out DungeonRuntime.Capture __state)
        {
            __state = DungeonRuntime.BeginChildCapture(__instance.GetComponent<ZNetView>());
        }
        private static Exception Finalizer(DungeonRuntime.Capture __state, Exception __exception)
        {
            DungeonRuntime.EndChildCapture(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(ZNetScene), "CreateObject")]
    internal static class DungeonPendingObjectPatch
    {
        private static bool Prefix(ZDO zdo, ref GameObject __result)
        {
            if (zdo.GetInt(DungeonRuntime.PendingKey, 0) == 0) return true;
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(DungeonGenerator), "Save")]
    internal static class DungeonRoomVolumePatch
    {
        private static void Postfix() { DungeonRuntime.RecordRoomVolumes(); }
    }
}
