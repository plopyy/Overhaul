using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Overhaul.Dungeons
{
    internal static partial class DungeonRuntime
    {
        internal sealed class TombMove
        {
            internal string Identity;
            internal Vector3 Destination;
        }

        private static bool IsTomb(ZDO zdo)
        {
            GameObject prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            return prefab && prefab.GetComponent<TombStone>();
        }

        private static bool IsPlayer(ZDO zdo)
        {
            GameObject prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            return prefab && prefab.GetComponent<Player>();
        }

        private static List<Bounds> OccupiedVolumes(Location location, List<Bounds> volumes)
        {
            // Instanced interiors are high above the world. An outside player at the same X/Z
            // must not block a reset. Outdoor camps/towers instead use their own world volumes.
            if (location.m_hasInterior)
            {
                var interior = volumes.Where(b => b.center.y > 3000).ToList();
                if (interior.Count == 0) throw new InvalidOperationException("Interior bounds unavailable");
                return interior;
            }
            return volumes;
        }

        private static bool KnownPlayersClear(ZDO proxy, ZoneSystem.ZoneLocation location)
        {
            // Reuse visit geometry without requesting any assets or terrain. After a restart
            // this cache may be empty; already resident prefabs can still supply reset bounds.
            List<Bounds> volumes;
            if (visitBounds.TryGetValue(proxy.m_uid, out volumes)) return Surface(volumes) || PlayersClear(volumes);
            if (!location.m_prefab.IsLoaded) return true; // Unknown: full check remains mandatory.
            GameObject prefab = location.m_prefab.Asset;
            if (!prefab) return true;
            Location loc = prefab.GetComponent<Location>();
            if (!loc) return true;
            var hashes = new HashSet<int>(prefab.GetComponentsInChildren<DungeonGenerator>(true).Select(g => g.name.GetStableHashCode()));
            Vector2s zone = ZoneSystem.GetZone(proxy.GetPosition());
            var generators = session.m_objectsByID.Values.Where(z => hashes.Contains(z.GetPrefab()) && ZoneSystem.GetZone(z.GetPosition()) == zone).ToList();
            if (generators.Count != hashes.Count || generators.Any(z => !ZNetScene.instance.GetPrefab(z.GetPrefab()))) return true;
            volumes = ScopeInteriorVolumes(proxy, BuildVolumes(proxy, prefab, generators, Math.Max(40, loc.m_exteriorRadius + 16)), hashes.Count != 0);
            if (loc.m_hasInterior && !volumes.Any(b => b.center.y > 3000)) return true;
            List<Bounds> occupied = OccupiedVolumes(loc, volumes);
            return Surface(occupied) || PlayersClear(occupied);
        }

        internal const string SurfaceMoveRequest = "Overhaul_SurfaceResetMove";

        // Surface sites never wait for players: anyone standing in the reset area is moved just outside it.
        private static bool Surface(List<Bounds> volumes) { return volumes.All(b => b.center.y <= 3000); }

        private static bool OccupantsClear(ZDO proxy, List<Bounds> volumes)
        {
            if (!Surface(volumes)) return PlayersClear(volumes);
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                ZDO character = peer.m_characterID.IsNone() ? null : session.GetZDO(peer.m_characterID);
                Vector3 position = character != null ? character.GetPosition() : peer.GetRefPos();
                if ((character == null && !peer.IsReady()) || !Inside(volumes, position)) continue;
                Vector3 exit = SurfaceExit(proxy, volumes, position);
                peer.m_rpc.Invoke(SurfaceMoveRequest, exit, Quaternion.LookRotation(exit - proxy.GetPosition() + Vector3.forward * 0.001f));
                Utility.Log.LogInfo("Dungeon " + proxy.m_uid + " : joueur " + peer.m_playerName + " deplace hors de la zone du reset vers " + exit);
            }
            if (Player.m_localPlayer && Inside(volumes, Player.m_localPlayer.transform.position))
            {
                Vector3 exit = SurfaceExit(proxy, volumes, Player.m_localPlayer.transform.position);
                Player.m_localPlayer.TeleportTo(exit, Player.m_localPlayer.transform.rotation, false);
            }
            return true;
        }

        private static Vector3 SurfaceExit(ZDO proxy, List<Bounds> volumes, Vector3 position)
        {
            Vector3 center = proxy.GetPosition();
            Vector3 direction = position - center; direction.y = 0;
            if (direction.sqrMagnitude < 0.01f) direction = proxy.GetRotation() * Vector3.forward;
            direction.Normalize();
            Vector3 point = center;
            for (float distance = 0; distance < 400; distance += 2)
            {
                point = center + direction * distance; point.y = position.y;
                if (!Inside(volumes, point)) break;
            }
            point += direction * 6;
            float height;
            point.y = ZoneSystem.instance.GetGroundHeight(point, out height) ? height : WorldGenerator.instance.GetHeight(point.x, point.z);
            point.y = Math.Max(point.y, ZoneSystem.instance.m_waterLevel) + 0.5f;
            return point;
        }

        internal static void ReceiveSurfaceMove(ZRpc rpc, Vector3 point, Quaternion rotation)
        {
            if (!ZNet.instance || ZNet.instance.IsServer() || ZNet.instance.GetServerPeer()?.m_rpc != rpc || !Player.m_localPlayer) return;
            Player.m_localPlayer.TeleportTo(point, rotation, false);
        }

        private static string playerBlockDetail;
        private static bool PlayersClear(List<Bounds> volumes)
        {
            playerBlockDetail = null;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                ZDO character = peer.m_characterID.IsNone() ? null : session.GetZDO(peer.m_characterID);
                if (character != null && Inside(volumes, character.GetPosition()))
                { playerBlockDetail = "personnage reseau " + character.m_uid + " a " + character.GetPosition(); return false; }
                // Reference positions also cover transitions already announced to the server.
                // A connection elsewhere does not freeze the entire world's dungeon resets.
                if (character == null && peer.IsReady() && Inside(volumes, peer.GetRefPos()))
                { playerBlockDetail = "reference de chargement du joueur " + peer.m_characterID + " a " + peer.GetRefPos(); return false; }
            }
            if (Player.m_localPlayer && Inside(volumes, Player.m_localPlayer.transform.position))
            { playerBlockDetail = "personnage local a " + Player.m_localPlayer.transform.position; return false; }
            return true;
        }

        private static List<TombMove> PlanTombMoves(ZDO proxy, GameObject prefab, List<ZDO> tombs, float exterior, IEnumerable<Vector3> reserved = null)
        {
            var moves = new List<TombMove>();
            if (tombs.Count == 0) return moves;
            Vector3 entrance = proxy.GetPosition();
            Vector3 forward = proxy.GetRotation() * Vector3.forward;
            bool foundEntrance = false;
            foreach (Teleport teleport in prefab.GetComponentsInChildren<Teleport>(true))
            {
                Vector3 local = prefab.transform.InverseTransformPoint(teleport.transform.position);
                Vector3 candidate = proxy.GetPosition() + proxy.GetRotation() * local;
                if (candidate.y > 3000 || !teleport.m_targetPoint) continue;
                entrance = candidate;
                forward = proxy.GetRotation() * prefab.transform.InverseTransformDirection(teleport.transform.forward);
                foundEntrance = true;
                break;
            }
            forward.y = 0;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            forward.Normalize();
            float startRadius = foundEntrance ? 8f : exterior + 8f;
            int mask = ZoneSystem.instance.m_solidRayMask | ZoneSystem.instance.m_terrainRayMask;
            var existing = session.m_objectsByID.Values.Where(IsTomb).Select(z => z.GetPosition()).ToList();
            if (reserved != null) existing.AddRange(reserved);
            Physics.SyncTransforms();
            foreach (ZDO tomb in tombs.OrderBy(z => Identity(z)))
            {
                bool placed = false;
                for (int ring = 0; ring < 8 && !placed; ring++)
                {
                    float radius = startRadius + ring * 3f;
                    int samples = Math.Max(16, Mathf.CeilToInt(2 * Mathf.PI * radius / 3f));
                    for (int i = 0; i < samples; i++)
                    {
                        // Start in front of the entrance, then explore both sides around it.
                        int step = i % 2 == 0 ? i / 2 : -(i + 1) / 2;
                        Vector3 point = entrance + Quaternion.Euler(0, step * 360f / samples, 0) * forward * radius;
                        RaycastHit hit;
                        if (!Physics.Raycast(point + Vector3.up * 32f, Vector3.down, out hit, 80f, mask, QueryTriggerInteraction.Ignore)) continue;
                        if (hit.normal.y < 0.65f || Math.Abs(hit.point.y - entrance.y) > 12f || hit.point.y < ZoneSystem.instance.m_waterLevel) continue;
                        point = hit.point + Vector3.up * 0.5f;
                        if (existing.Any(p => (p - point).sqrMagnitude < 9f)) continue;
                        if (Physics.CheckCapsule(point + Vector3.up * 0.3f, point + Vector3.up * 1.3f, 0.65f, mask, QueryTriggerInteraction.Ignore)) continue;
                        moves.Add(new TombMove { Identity = Identity(tomb), Destination = point });
                        existing.Add(point);
                        placed = true;
                        break;
                    }
                }
                if (!placed) throw new InvalidOperationException("Reset reporte : aucun emplacement exterieur libre pour les tombes");
            }
            return moves;
        }

        private static void MoveTomb(ZDO tomb, Vector3 destination)
        {
            // Keep the same ZDO: inventory bytes, owner name/ID and death timestamp are untouched.
            tomb.SetOwner(ZNet.GetUID());
            tomb.Set(ZDOVars.s_spawnPoint, destination); // Native PositionCheck must not return it inside.
            tomb.Set(ZDOVars.s_velHash, Vector3.zero);
            tomb.Set(ZDOVars.s_bodyVelHash, Vector3.zero);
            tomb.Set(ZDOVars.s_bodyAVelHash, Vector3.zero);
            tomb.Set(OwnerKey, "");
            tomb.Set(PendingKey, 0);
            tomb.SetPosition(destination);
            GameObject instance = ZNetScene.instance.FindInstance(tomb.m_uid);
            if (instance)
            {
                instance.transform.position = destination;
                Rigidbody body = instance.GetComponent<Rigidbody>();
                if (body)
                {
                    body.position = destination;
                    body.linearVelocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
                Physics.SyncTransforms();
            }
            session.ForceSendZDO(tomb.m_uid);
        }

        private static byte[] WriteResetJournal(List<ZDOID> old, List<ZDOID> fresh, int seed, int epoch, long ticks, List<Bounds> volumes, List<TombMove> moves)
        {
            return WriteResetJournalIds(old.Select(id => Identity(session.GetZDO(id))).ToList(),
                fresh.Select(id => Identity(session.GetZDO(id))).ToList(), seed, epoch, ticks, volumes, moves);
        }

        private static byte[] WriteResetJournalIds(List<string> old, List<string> fresh, int seed, int epoch, long ticks, List<Bounds> volumes, List<TombMove> moves)
        {
            var pkg = new ZPackage();
            pkg.Write(3); pkg.Write(seed); pkg.Write(epoch); pkg.Write(ticks);
            pkg.Write(old.Count); foreach (string id in old) pkg.Write(id);
            pkg.Write(fresh.Count); foreach (string id in fresh) pkg.Write(id);
            pkg.Write(volumes.Count); foreach (Bounds volume in volumes) { pkg.Write(volume.center); pkg.Write(volume.size); }
            pkg.Write(moves.Count); foreach (TombMove move in moves) { pkg.Write(move.Identity); pkg.Write(move.Destination); }
            return pkg.GetArray();
        }

        private static int ReadCount(ZPackage pkg)
        {
            int count = pkg.ReadInt();
            if (count < 0 || count > 100000) throw new InvalidDataException("Invalid reset journal count");
            return count;
        }

        private static Vector3 ReadPoint(ZPackage pkg)
        {
            Vector3 point = pkg.ReadVector3();
            if (float.IsNaN(point.sqrMagnitude) || float.IsInfinity(point.sqrMagnitude)) throw new InvalidDataException("Invalid reset position");
            return point;
        }
    }
}
