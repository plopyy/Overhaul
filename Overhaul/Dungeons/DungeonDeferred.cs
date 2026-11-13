using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Overhaul.Performance;
using Overhaul.Utility;

namespace Overhaul.Dungeons
{
    // Dungeon interiors generated at the first entry, not when their zone is first loaded. The game generated every crypt,
    // cave and mine around a player as soon as its zone came into range (rooms, chests, monsters, guardian), whether anyone
    // went in or not: a hitch near each new dungeon and a world save full of interiors nobody saw.
    // Now, for the dungeons the reset system handles (DungeonPolicy.Supported), the first spawn of the location leaves its
    // generator ungenerated and marks the entrance (proxy) "interior deferred". Taking the door asks the server for the
    // interior; the door's loading screen waits (ZNetScene.IsAreaReady is held false for that interior, 60 s at most) while
    // the server runs a reset in first generation mode (assets loaded, location staged, transaction journal, guardian), then
    // clears the mark. Dungeons generated before keep their interior; the other families are generated as before.
    // Switch (server): o_opti deferred on|off (dungeons already deferred are still generated at their first entry).
    internal static partial class DungeonRuntime
    {
        internal static readonly int DeferredKey = "overhaul_dungeon_deferred_v1".GetStableHashCode();
        const string FirstRpc = "Overhaul_DungeonFirstEntry";
        static readonly Queue<ZDOID> firstQueue = new Queue<ZDOID>();
        static bool DeferOn => PerfMonitor.On(OverhaulConfig.PerfDeferredDungeons);

        // ---- Server: the generator left ungenerated at the location's first spawn ----
        [HarmonyPatch(typeof(DungeonGenerator), "Generate", new System.Type[] { typeof(int), typeof(ZoneSystem.SpawnMode) })]
        static class SkipFirstGeneration
        {
            [HarmonyPriority(Priority.Low)] // after DungeonGeneratePatch: its seed and room counts are recorded on the generator
            static bool Prefix(ZoneSystem.SpawnMode mode)
            {
                var capture = Current;
                // Only inside the initial spawn of a supported location (DungeonLocationPatch), never a reset's staging.
                if (capture == null || capture.Staging || capture.Parent != null || mode == ZoneSystem.SpawnMode.Client || !DeferOn) return true;
                capture.Deferred = true;
                return false;
            }
        }

        // ---- Client: the door asks for the interior, the loading screen waits for it ----
        static readonly Dictionary<Vector2s, float> waiting = new Dictionary<Vector2s, float>();
        const float WaitLimit = 60f;

        static ZDO ProxyAt(Vector2s zone)
        {
            if (ZDOMan.instance == null || !ZoneSystem.instance) return null;
            int hash = ZoneSystem.instance.m_locationProxyPrefab.name.GetStableHashCode();
            var found = new List<ZDO>();
            ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(0, 0, false), found, null);
            foreach (var zdo in found) if (zdo.GetPrefab() == hash) return zdo;
            return null;
        }

        [HarmonyPatch(typeof(Teleport), nameof(Teleport.Interact))]
        static class DoorAsks
        {
            static void Prefix(Teleport __instance)
            {
                if (!__instance.m_targetPoint || Character.InInterior(__instance.transform.position) || !Character.InInterior(__instance.m_targetPoint.transform.position)) return;
                var zone = ZoneSystem.GetZone(__instance.transform.position);
                var proxy = ProxyAt(zone);
                if (proxy == null || !proxy.GetBool(DeferredKey, false)) return;
                waiting[ZoneSystem.GetZone(__instance.m_targetPoint.transform.position)] = Time.time;
                Log.LogInfo("[Dungeons] interior asked at the first entry (" + proxy.m_uid + ")");
                if (ZNet.instance && ZNet.instance.IsServer()) AskFirst(proxy.m_uid);
                else ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), FirstRpc, proxy.m_uid);
            }
        }

        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.IsAreaReady))]
        static class WaitForInterior
        {
            static void Postfix(Vector3 point, ref bool __result)
            {
                if (!__result || point.y <= 3000f || waiting.Count == 0) return;
                var zone = ZoneSystem.GetZone(point);
                if (!waiting.TryGetValue(zone, out float since)) return;
                var proxy = ProxyAt(ZoneSystem.GetZone(new Vector3(point.x, 0f, point.z)));
                if (proxy != null && proxy.GetBool(DeferredKey, false) && Time.time - since < WaitLimit) { __result = false; return; }
                if (proxy != null && proxy.GetBool(DeferredKey, false)) { Log.LogWarning("[Dungeons] interior not generated after " + WaitLimit + " s, arriving anyway"); waiting.Remove(zone); return; }
                // Generated: the interior may stand in another height lane than the one the door pointed to when it was
                // used (the entrance's local copy is rebuilt for it, BossProxyMigrationPatch). The arrival follows the
                // rebuilt door's target before the player is let in.
                if (Retarget(zone) && Time.time - since < WaitLimit) { __result = false; return; }
                waiting.Remove(zone);
            }
        }

        // The teleporting local player's arrival moved to the current entrance door target of that zone; true if it moved
        // (or the door is not rebuilt yet).
        static bool Retarget(Vector2s zone)
        {
            var player = Player.m_localPlayer;
            if (!player || !player.IsTeleporting()) return false;
            Teleport door = null;
            foreach (var t in Object.FindObjectsByType<Teleport>(FindObjectsSortMode.None))
                if (t.m_targetPoint && !Character.InInterior(t.transform.position) && Character.InInterior(t.m_targetPoint.transform.position) && ZoneSystem.GetZone(t.transform.position) == zone) { door = t; break; }
            if (!door) return true; // the entrance is being rebuilt
            var target = door.m_targetPoint.GetTeleportPoint();
            if ((target - player.m_teleportTargetPos).sqrMagnitude < 1f) return false;
            Log.LogInfo("[Dungeons] arrival moved to the generated interior: " + player.m_teleportTargetPos.ToString("0") + " -> " + target.ToString("0"));
            player.m_teleportTargetPos = target; player.m_teleportTargetRot = door.m_targetPoint.transform.rotation;
            player.transform.position = target;
            return true;
        }

        // ---- Server: the request queued, first generations before scheduled resets ----
        static void AskFirst(ZDOID id)
        {
            if (!ZNet.instance || !ZNet.instance.IsServer() || ZDOMan.instance == null) return;
            var proxy = ZDOMan.instance.GetZDO(id);
            if (proxy == null || !proxy.GetBool(DeferredKey, false) || firstQueue.Contains(id) || (preparation != null && preparation.Id == id)) return;
            firstQueue.Enqueue(id);
            nextTick = 0f; // at the next tick, not in a second
        }

        [HarmonyPatch(typeof(ZRoutedRpc), MethodType.Constructor, typeof(bool))]
        static class RegisterFirst
        {
            static void Postfix(ZRoutedRpc __instance) => __instance.Register<ZDOID>(FirstRpc, (sender, id) => AskFirst(id));
        }

        [HarmonyPatch(typeof(ZNet), "Awake")]
        static class ClearFirst { static void Postfix() { firstQueue.Clear(); waiting.Clear(); } }
    }
}
