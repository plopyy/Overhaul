using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Overhaul.Utility;

namespace Overhaul.Performance
{
    // ZNetScene checks 30 times a second which networked objects to create or remove around the player.
    // When the last run had nothing to do and nothing that run reads has changed since, the run is skipped:
    // same zone and simulation distance, no change to the sector lists (objects added, moved between sectors,
    // destroyed, loaded), to an object's distant flag, or to the scene's instances. A full run still happens
    // at least twice a second in case a change slipped past the hooks below.
    internal static class StreamingSkip
    {
        private const float FullRunInterval = 0.5f;

        internal static int Version;
        private static bool idle, skipped, broken;
        private static int versionAtIdle, versionBefore;
        private static Vector2s zoneAtIdle;
        private static int nearAtIdle, totalAtIdle;
        private static bool classicAtIdle;
        private static float idleTime;

        private static bool Active => !broken && PerfMonitor.On(OverhaulConfig.PerfStreamingSkip);

        [HarmonyPatch(typeof(ZNetScene), "CreateDestroyObjects")]
        private static class Scan
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix()
            {
                skipped = false;
                if (!Active || !ZNet.instance || !ZoneSystem.instance) { idle = false; return true; }
                if (idle && Version == versionAtIdle && Time.time - idleTime < FullRunInterval)
                {
                    var distance = ZNet.instance.GetSyncedSimulationDistance();
                    if (ZoneSystem.GetZone(ZNet.instance.GetReferencePosition()) == zoneAtIdle && distance.NearSimulationDistance == nearAtIdle
                        && distance.TotalSimulationDistance == totalAtIdle && distance.IsClassic == classicAtIdle)
                    {
                        skipped = true;
                        PerfMonitor.StreamingSkips++;
                        return false;
                    }
                }
                versionBefore = Version;
                PerfMonitor.StreamingRuns++;
                return true;
            }

            private static void Postfix(ZNetScene __instance)
            {
                if (skipped) { skipped = false; return; }
                // Idle only when the run created and removed nothing and every object in range already exists.
                bool done = Active && Version == versionBefore && __instance.m_tempRemoved.Count == 0
                    && AllCreated(__instance.m_tempCurrentObjects) && AllCreated(__instance.m_tempCurrentDistantObjects);
                idle = done;
                if (!done) return;
                var distance = ZNet.instance.GetSyncedSimulationDistance();
                versionAtIdle = Version;
                zoneAtIdle = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition());
                nearAtIdle = distance.NearSimulationDistance;
                totalAtIdle = distance.TotalSimulationDistance;
                classicAtIdle = distance.IsClassic;
                idleTime = Time.time;
            }

            private static bool AllCreated(List<ZDO> zdos)
            {
                for (int i = 0; i < zdos.Count; i++) if (!zdos[i].Created) return false;
                return true;
            }
        }

        // Every change the scan depends on raises Version. A missing hook (after a game update) turns the skip off.
        [HarmonyPatch]
        private static class Changes
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                var targets = new (System.Type Type, string Name)[]
                {
                    (typeof(ZDOMan), "AddToSector"), (typeof(ZDOMan), "InitialAddToSector"), (typeof(ZDOMan), "RemoveFromSector"),
                    (typeof(ZDOMan), "DestroyZDO"), (typeof(ZDOMan), "HandleDestroyedZDO"), (typeof(ZDOMan), "Load"),
                    (typeof(ZDOMan), "LoadChunks"), (typeof(ZDOMan), "ResetBeforeLoad"), (typeof(ZDOMan), "ResetSectorArray"),
                    (typeof(ZNetScene), "AddInstance"), (typeof(ZNetScene), "OnZDODestroyed"), (typeof(ZNetScene), "CreateObject"),
                    (typeof(ZNetScene), "Shutdown"), (typeof(ZNetView), "ResetZDO"),
                };
                foreach (var (type, name) in targets)
                {
                    var method = AccessTools.DeclaredMethod(type, name);
                    if (method == null) { broken = true; Log.LogWarning("[Performance] Streaming skip off: " + type.Name + "." + name + " not found"); continue; }
                    yield return method;
                }
                var destroy = AccessTools.DeclaredMethod(typeof(ZNetScene), "Destroy", new[] { typeof(GameObject) });
                var distant = AccessTools.PropertySetter(typeof(ZDO), nameof(ZDO.Distant));
                // An object marked created or not, and the portal list changing, also change what a run would do.
                var created = AccessTools.PropertySetter(typeof(ZDO), nameof(ZDO.Created));
                var portals = AccessTools.PropertySetter(typeof(ZDOMan), "DirtyPortalObjects");
                if (created != null) yield return created; else Log.LogWarning("[Performance] Streaming skip: ZDO.Created setter not found");
                if (portals != null) yield return portals; else Log.LogWarning("[Performance] Streaming skip: ZDOMan.DirtyPortalObjects setter not found");
                if (destroy == null || distant == null) { broken = true; Log.LogWarning("[Performance] Streaming skip off: ZNetScene.Destroy or ZDO.Distant not found"); }
                if (destroy != null) yield return destroy;
                if (distant != null) yield return distant;
            }

            private static void Postfix() => Version++;
        }
    }
}
