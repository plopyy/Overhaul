using HarmonyLib;
using Overhaul.Utility;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace Overhaul.Dungeons
{
    // Runs on the connecting client (including a local host), before creating its player.
    // Keeping the native loading screen avoids spawning into an obsolete room even briefly.
    [HarmonyPatch(typeof(Game), "FindSpawnPoint")]
    internal static class DungeonReconnect
    {
        private static Game reconnectGame;
        private static Vector3 reconnectSaved;
        private static ZDOID entranceProxy;
        private static Vector3? pendingEntrance;
        private static Vector3? remoteSurface;
        private const string ResolveRequest = "Overhaul_DungeonReconnectQuery";
        private const string ResolveResponse = "Overhaul_DungeonReconnectAnswer";
        private static float nextResolve, nextReport;
        private static string resolutionDetail = "";
        private static float reconnectStarted;
        private static bool surfaceRecovery;
        private static int recoveryRing, recoverySample;
        private static bool Prefix(Game __instance, ref Vector3 point, ref bool usedLogoutPoint, float dt, ref bool __result)
        {
            PlayerProfile profile = __instance.m_playerProfile;
            if (__instance.m_respawnAfterDeath || profile == null || !profile.HaveLogoutPoint()) return true;
            Vector3 saved = profile.GetLogoutPoint();
            if (!Character.InInterior(saved)) return true;
            if (reconnectGame != __instance || reconnectSaved != saved)
            {
                reconnectGame = __instance; reconnectSaved = saved; entranceProxy = ZDOID.None;
                pendingEntrance = null; remoteSurface = null; nextResolve = nextReport = 0;
                reconnectStarted = Time.realtimeSinceStartup;
                surfaceRecovery = false; recoveryRing = recoverySample = 0;
            }

            point = Vector3.zero;
            usedLogoutPoint = false;
            __result = false;
            __instance.m_respawnWait += dt;
            if (!ZNet.instance || !ZNetScene.instance || !ZoneSystem.instance) return false;
            if (surfaceRecovery || Time.realtimeSinceStartup - reconnectStarted >= 10f)
            {
                if (!surfaceRecovery) Log.LogWarning("Dungeon reconnexion : entree introuvable ou indisponible, recherche d'un sol viable a la surface.");
                surfaceRecovery = true;
                Vector3 recovery;
                if (!TryRecoverSurface(saved, out recovery)) return false;
                point = recovery; usedLogoutPoint = true; __result = true;
                profile.ClearLoguoutPoint(); reconnectGame = null;
                Log.LogInfo("Dungeon : reconnexion de secours a la surface " + recovery);
                return false;
            }
            // Resolve before waiting for the old room: a remote boss room can be outside
            // the entrance proxy's streaming range. On a host the saved world identifies it.
            if (entranceProxy.IsNone() && ZDOMan.instance != null && Time.realtimeSinceStartup >= nextResolve)
            {
                nextResolve = Time.realtimeSinceStartup + 1f;
                try
                {
                    var target = FindReconnectProxy(saved);
                    if (target != null) entranceProxy = target.m_uid;
                    else if (!ZNet.instance.IsServer()) ZNet.instance.GetServerPeer()?.m_rpc.Invoke(ResolveRequest, saved);
                }
                catch (System.Exception e) { Log.LogWarning("Dungeon reconnexion : identification en attente : " + e.Message); }
            }
            var proxyData = entranceProxy.IsNone() ? null : ZDOMan.instance.GetZDO(entranceProxy);
            Vector3 loadingPoint = pendingEntrance ?? remoteSurface ?? (proxyData == null ? saved : proxyData.GetPosition());
            ZNet.instance.SetReferencePosition(loadingPoint);
            if (__instance.m_respawnWait <= __instance.m_respawnLoadDuration) return false;

            // Use the original location's zone, not the nearest newly generated room.
            // Location proxies and their static teleport links survive dungeon resets.
            LocationProxy found = null;
            Vector2s zone = ZoneSystem.GetZone(saved);
            foreach (ZNetView view in ZNetScene.instance.m_instances.Values)
            {
                if (!view) continue;
                LocationProxy candidate = view.GetComponent<LocationProxy>();
                if (!candidate) continue;
                var data = view.GetZDO();
                if (!entranceProxy.IsNone())
                {
                    if (data == null || data.m_uid != entranceProxy) continue;
                    found = candidate; break;
                }
                if (saved.y >= 11000)
                {
                    if (!BossInteriorReservation.Lane(data) || !BossDungeonLayout.InLane(data, saved)) continue;
                }
                else if (ZoneSystem.GetZone(view.transform.position) != zone) continue;
                if (found) return false; // Ambiguous location: never guess another dungeon.
                found = candidate;
            }
            Vector3 destination;
            if (!TryGetExteriorEntrance(found, out destination))
            {
                ReportWait("entree du donjon non chargee", loadingPoint);
                return false;
            }
            entranceProxy = found.GetComponent<ZNetView>().GetZDO().m_uid;
            pendingEntrance = destination;
            ZNet.instance.SetReferencePosition(destination);
            if (!ZNetScene.instance.IsAreaReady(destination) || !HasFloor(destination))
            {
                ReportWait("chargement du sol de l'entree", destination);
                return false;
            }

            point = destination + Vector3.up * 0.25f;
            usedLogoutPoint = true;
            profile.ClearLoguoutPoint();
            __result = true;
            reconnectGame = null; pendingEntrance = null; entranceProxy = ZDOID.None;
            Log.LogInfo("Dungeon : reconnexion replacee a l'entree exterieure " + destination);
            return false;
        }

        private static void ReportWait(string reason, Vector3 position)
        {
            if (Time.realtimeSinceStartup < nextReport) return;
            nextReport = Time.realtimeSinceStartup + 10f;
            Log.LogInfo("Dungeon reconnexion : " + reason + " a " + position + " ; " + resolutionDetail);
        }

        private static bool TryRecoverSurface(Vector3 saved, out Vector3 destination)
        {
            destination = Vector3.zero;
            // Concentric rings: prefer the closest viable surface sample, then expand.
            // Keep a sample pinned until its terrain/network objects are ready.
            for (int attempt = 0; attempt < 8; attempt++)
            {
                int count = Mathf.Max(1, recoveryRing * 8);
                float angle = recoverySample * Mathf.PI * 2f / count;
                Vector3 candidate = new Vector3(saved.x + Mathf.Cos(angle) * recoveryRing * 4f,
                    0f, saved.z + Mathf.Sin(angle) * recoveryRing * 4f);
                ZNet.instance.SetReferencePosition(candidate);
                if (!ZNetScene.instance.IsAreaReady(candidate))
                { ReportWait("chargement de la surface de secours", candidate); return false; }
                if (TrySurfacePosition(candidate, out destination)) return true;
                recoverySample++;
                if (recoverySample >= count) { recoverySample = 0; recoveryRing++; }
            }
            return false;
        }

        internal static bool TrySurfacePosition(Vector3 candidate, out Vector3 destination)
        {
            destination = Vector3.zero;
            Physics.SyncTransforms();
            RaycastHit ground;
            // Below every dungeon: only real surface terrain can qualify.
            if (!Physics.Raycast(new Vector3(candidate.x, 2999f, candidate.z), Vector3.down,
                out ground, 6000f, ZoneSystem.instance.m_terrainRayMask, QueryTriggerInteraction.Ignore)) return false;
            if (ground.normal.y < 0.707f || ground.point.y < ZoneSystem.instance.m_waterLevel + 0.5f) return false;
            Vector3 feet = ground.point + Vector3.up * 0.25f;
            if (Physics.CheckCapsule(feet + Vector3.up * 0.4f, feet + Vector3.up * 1.6f,
                0.35f, ZoneSystem.instance.m_solidRayMask, QueryTriggerInteraction.Ignore)) return false;
            // Require support around both feet, not a narrow lip at a cliff edge.
            for (int i = 0; i < 4; i++)
            {
                Vector3 offset = new Vector3(Mathf.Cos(i * Mathf.PI / 2f), 0, Mathf.Sin(i * Mathf.PI / 2f)) * 0.35f;
                RaycastHit support;
                if (!Physics.Raycast(feet + offset + Vector3.up * 0.5f, Vector3.down, out support,
                    1.1f, ZoneSystem.instance.m_terrainRayMask, QueryTriggerInteraction.Ignore) || support.normal.y < 0.707f) return false;
            }
            destination = feet;
            return true;
        }

        internal static ZDO FindReconnectProxy(Vector3 saved)
        {
            // Reconnecting is not a reset or a visit. The old logout point may no longer
            // belong to any current room; the isolated reservation still identifies its exit.
            int prefab = ZoneSystem.instance.m_locationProxyPrefab.name.GetStableHashCode();
            ZDO found = null;
            foreach (var data in ZDOMan.instance.m_objectsByID.Values)
            {
                if (data.GetPrefab() != prefab || !BossInteriorReservation.History(data).Any(b => b.Contains(saved))) continue;
                if (found != null) { resolutionDetail = "anciens interieurs ambigus"; return null; }
                found = data;
            }
            if (found != null) return found;
            var proxies = new List<ZDO>();
            int marked = 0;
            foreach (var proxy in ZDOMan.instance.m_objectsByID.Values)
            {
                if (proxy.GetPrefab() != prefab) continue;
                proxies.Add(proxy);
                if (BossInteriorReservation.Lane(proxy)) marked++;
                if (!BossInteriorReservation.Lane(proxy) ||
                    !BossDungeonLayout.InLane(proxy, saved)) continue;
                if (found != null) { resolutionDetail = "reservations ambigues"; return null; }
                found = proxy;
            }
            resolutionDetail = "proxies=" + proxies.Count + ", marques=" + marked;
            if (found != null) { resolutionDetail += ", reservation=" + found.m_uid; return found; }

            // Older generated interiors may have a different altitude or no marker on the
            // surface proxy. Follow persisted room ownership instead of recomputing a lane.
            int generators = 0, containing = 0;
            if (DungeonDB.instance)
            foreach (var data in ZDOMan.instance.m_objectsByID.Values)
            {
                if (data.GetInt(ZDOVars.s_rooms, 0) == 0 && data.GetByteArray(ZDOVars.s_roomData, null) == null) continue;
                generators++;
                var p = data.GetPosition();
                if (Mathf.Abs(p.x - saved.x) > 400 || Mathf.Abs(p.z - saved.z) > 400 || Mathf.Abs(p.y - saved.y) > 400) continue;
                bool contains = false;
                foreach (var bounds in DungeonRuntime.ReadVisitRooms(data))
                    if (bounds.Contains(saved)) { contains = true; break; }
                if (!contains) continue;
                containing++;
                string owner = data.GetString(DungeonRuntime.OwnerKey, "");
                foreach (var proxy in proxies)
                {
                    if (owner.Length == 0 || proxy.GetString(DungeonRuntime.IdentityKey, "") != owner) continue;
                    if (found != null && found != proxy) { resolutionDetail += ", proprietaires ambigus"; return null; }
                    found = proxy;
                }
            }
            resolutionDetail += ", generateurs=" + generators + ", contenant=" + containing + ", lien=" + (found == null ? "absent" : found.m_uid.ToString());
            if (found == null && saved.y < 11000 && ZNet.instance.IsServer() && DungeonDB.instance)
                return DungeonRuntime.FindManualResetTarget(saved);
            return found;
        }

        internal static Vector3 ExteriorPoint(ZDO proxy)
        {
            var location = ZoneSystem.instance.GetLocation(proxy.GetInt(ZDOVars.s_location, 0));
            if (location == null) return proxy.GetPosition() + Vector3.up;
            location.m_prefab.Load();
            try
            {
                var prefab = location.m_prefab.Asset;
                foreach (var teleport in prefab.GetComponentsInChildren<Teleport>(true))
                {
                    var local = prefab.transform.InverseTransformPoint(teleport.GetTeleportPoint());
                    if (local.y > 3000 || !teleport.m_targetPoint) continue;
                    return proxy.GetPosition() + proxy.GetRotation() * local;
                }
                return proxy.GetPosition() + Vector3.up;
            }
            finally { location.m_prefab.Release(); }
        }

        internal static void Register(ZNetPeer peer)
        {
            peer.m_rpc.Register<Vector3, Quaternion>(DungeonRuntime.SurfaceMoveRequest, DungeonRuntime.ReceiveSurfaceMove);
            peer.m_rpc.Register<Vector3>(ResolveRequest, (rpc, saved) =>
            {
                if (!ZNet.instance || !ZNet.instance.IsServer() || ZNet.instance.GetPeer(rpc) == null || !Character.InInterior(saved)) return;
                try
                {
                    var proxy = FindReconnectProxy(saved);
                    if (proxy != null) rpc.Invoke(ResolveResponse, saved, proxy.m_uid, proxy.GetPosition());
                }
                catch (System.Exception e) { Log.LogWarning("Dungeon reconnexion : " + e.Message); }
            });
            peer.m_rpc.Register<Vector3, ZDOID, Vector3>(ResolveResponse, (rpc, saved, proxy, surface) =>
            {
                if (!ZNet.instance || ZNet.instance.IsServer() || ZNet.instance.GetServerPeer()?.m_rpc != rpc || !reconnectGame || saved != reconnectSaved) return;
                entranceProxy = proxy; remoteSurface = surface;
            });
        }

        internal static bool TryGetExteriorEntrance(LocationProxy proxy, out Vector3 destination)
        {
            destination = Vector3.zero;
            if (!proxy || proxy.m_locationNeedsSpawn || !proxy.m_instance || !proxy.m_instance.activeInHierarchy) return false;
            Teleport target = null;
            foreach (Teleport entrance in proxy.m_instance.GetComponentsInChildren<Teleport>())
            {
                // Resolve using the outside link: no interior geometry needs to load.
                if (!entrance.isActiveAndEnabled || Character.InInterior(entrance.transform.position)) continue;
                if (!entrance.m_targetPoint || !Character.InInterior(entrance.m_targetPoint.transform.position)) continue;
                if (!entrance.m_targetPoint.transform.IsChildOf(proxy.m_instance.transform)) continue;
                if (target && target != entrance) return false;
                target = entrance;
            }
            if (!target) return false;
            destination = target.GetTeleportPoint();
            return !Character.InInterior(destination);
        }

        internal static bool TryGetEntrance(LocationProxy proxy, out Vector3 destination)
        {
            destination = Vector3.zero;
            if (!proxy || proxy.m_locationNeedsSpawn || !proxy.m_instance || !proxy.m_instance.activeInHierarchy) return false;
            Teleport target = null;
            foreach (Teleport entrance in proxy.m_instance.GetComponentsInChildren<Teleport>())
            {
                if (!entrance.isActiveAndEnabled || Character.InInterior(entrance.transform.position)) continue;
                Teleport interior = entrance.m_targetPoint;
                if (!interior || !interior.isActiveAndEnabled || !Character.InInterior(interior.transform.position)) continue;
                if (!interior.transform.IsChildOf(proxy.m_instance.transform)) continue;
                if (target && target != interior) return false;
                target = interior;
            }
            if (!target) return false;
            destination = target.GetTeleportPoint();
            return true;
        }

        internal static bool HasFloor(Vector3 destination)
        {
            // IsAreaReady covers network instances; require actual collision at the entrance too.
            Physics.SyncTransforms();
            return Physics.Raycast(destination + Vector3.up, Vector3.down, 4f,
                ZoneSystem.instance.m_solidRayMask, QueryTriggerInteraction.Ignore);
        }
    }

    [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
    internal static class DungeonReconnectConnection
    {
        private static void Postfix(ZNetPeer peer) { DungeonReconnect.Register(peer); }
    }
}

