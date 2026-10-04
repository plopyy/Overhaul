using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameSpawnPoint
    {
        private sealed class Resolution
        {
            internal PlayerChange Logout, Bed;
            internal long Character;
            internal int Stage;
            internal Vector3 Point;
            internal bool HavePoint, Ready, Pending;
            internal double NextCheck;
            internal readonly List<PlayerChange> Rejected = new List<PlayerChange>();
        }
        private static readonly Dictionary<ZRpc, Resolution> pending = new Dictionary<ZRpc, Resolution>();
        internal static IEnumerable<Vector3> Areas => pending.Values.Where(r => r.HavePoint).Select(r => r.Point);
        internal static void Forget(ZRpc rpc) {pending.Remove(rpc);GameArrivalRuntime.Forget(rpc);}
        internal static void Clear() {pending.Clear();GameArrivalRuntime.Clear();}
        internal static int Kind(ZRpc rpc) => pending.TryGetValue(rpc,out var resolution)&&resolution.Ready ? resolution.Stage : -1;

        internal static bool Resolve(ZRpc rpc, PlayerSnapshot state, out Vector3 point)
        {
            point = Vector3.zero;
            if (!Game.instance || !ZoneSystem.instance || !ZNetScene.instance || state == null) return false;
            if (!pending.TryGetValue(rpc, out var resolution))
            {
                var character = state.Rows.FirstOrDefault(r => r.Table == "state" && (string)r.Values[0] == "player_id");
                resolution = new Resolution
                {
                    Logout = GameDeathProgress.IsDead(state) ? null : state.Rows.FirstOrDefault(r => r.Table == "spawn" && (string)r.Values[0] == "logout"),
                    Bed = state.Rows.FirstOrDefault(r => r.Table == "spawn" && (string)r.Values[0] == "bed"),
                    Character = character == null ? 0 : Convert.ToInt64(character.Values[1])
                };
                pending.Add(rpc, resolution);
            }
            if (resolution.Ready) { point = resolution.Point; return true; }
            if (Time.timeAsDouble < resolution.NextCheck) return false;
            resolution.NextCheck = Time.timeAsDouble + .1;
            for (; resolution.Stage < 3; resolution.Stage++)
            {
                var row = resolution.Stage == 0 ? resolution.Logout : resolution.Bed;
                if (resolution.Stage < 2)
                {
                    if (row == null) continue;
                    var values = row.Values;
                    resolution.Point = new Vector3(Convert.ToSingle(values[1]), Convert.ToSingle(values[2]), Convert.ToSingle(values[3]));
                }
                else
                {
                    if (!ZoneSystem.instance.GetLocationIcon(Game.instance.m_StartLocation, out resolution.Point)) return false;
                    resolution.Point += Vector3.up * 2;
                }
                resolution.HavePoint = true;
                // These centers are included in the server scene before an avatar
                // exists. Waiting for terrain must not depend on that avatar spawning.
                if (!ZNetScene.instance.IsAreaReady(resolution.Point)) return false;
                if (resolution.Stage == 0)
                {
                    if (!ZoneSystem.instance.GetGroundHeight(resolution.Point, out float ground))
                    { resolution.Rejected.Add(row); continue; }
                    resolution.Point.y = Mathf.Max(resolution.Point.y, ground) + .25f;
                }
                else if (resolution.Stage == 1)
                {
                    Bed bed = null;
                    foreach (var view in ZNetScene.instance.m_instances.Values)
                    {
                        var candidate = view ? view.GetComponent<Bed>() : null;
                        if (candidate && candidate.m_spawnPoint && candidate.GetOwner() == resolution.Character && resolution.Character != 0 &&
                            Vector3.Distance(candidate.GetSpawnPoint(), resolution.Point) < 1f)
                        { bed = candidate; break; }
                    }
                    if (!bed) { resolution.Rejected.Add(row); continue; }
                    resolution.Point = bed.GetSpawnPoint();
                }
                resolution.Ready = true; point = resolution.Point; return true;
            }
            return false;
        }

        internal static void Tick(ZRpc rpc, ZDO actor, PlayerServerActions actions)
        {
            if (actor == null || !pending.TryGetValue(rpc, out var resolution) || !resolution.Ready || resolution.Pending) return;
            if (resolution.Rejected.Count == 0 && resolution.Stage == 0) { pending.Remove(rpc); return; }
            resolution.Pending = true;
            if (!actions.Enqueue(state =>
            {
                // A bed selected after spawning must not be removed by a delayed
                // correction for the previous bed.
                var rows = resolution.Rejected.Where(old => state.Rows.Any(row => row.SameKey(old) && row.Values.SequenceEqual(old.Values)))
                    .Select(old => new PlayerChange("spawn", true, old.Values[0])).ToList();
                if(resolution.Stage!=0)
                {
                    var home=new PlayerChange("spawn",false,"home",(double)resolution.Point.x,(double)resolution.Point.y,(double)resolution.Point.z);
                    if(!state.Rows.Any(row=>row.SameKey(home)&&row.Values.SequenceEqual(home.Values)))rows.Add(home);
                }
                if (rows.Count == 0) return null;
                return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(Guid.NewGuid().ToString("N"), state.Revision, rows), new Dictionary<long, ObjectRecord>()), () => { });
            }, () => { if (pending.TryGetValue(rpc, out var current) && ReferenceEquals(current, resolution)) pending.Remove(rpc); })) resolution.Pending = false;
        }
    }
}
