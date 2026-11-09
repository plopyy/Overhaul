using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Overhaul.Utility;

namespace Overhaul.Performance
{
    // Smooth frames, multiplayer: a portal's destination delivered early. The client tells the server where the portal it
    // walks up to (or just went through) leads; the server queues the destination's ground edits, buildings, rocks and
    // trees ("solid" and "terrain" objects) for that player right away, sends that player's world data four times faster
    // for 8 s, and tells the client once the player holds all of them (ground edits of the 3x3 zones, solid objects within
    // 32 m). The arrival check then releases at once instead of waiting for the objects to stop coming. Creatures and
    // items follow in the game's own order, after the floors they stand on. Server and clients always run the same
    // Overhaul, so both sides have it.
    internal static partial class SmoothFrames
    {
        const string AheadRpc = "Overhaul_PortalAhead", ReadyRpc = "Overhaul_ArrivalReady";

        // ---- Client ----
        static Vector3 lastAsked = new Vector3(float.NaN, 0, 0);
        static float nextAsk;
        static readonly List<(Vector3 Point, float At)> confirmed = new List<(Vector3, float)>();

        static void AskAhead(Vector3 target, bool force)
        {
            if (!On(Transitions) || !ZNet.instance || ZNet.instance.IsServer()) return;
            if (!force && (Time.realtimeSinceStartup < nextAsk || (target - lastAsked).sqrMagnitude < 1f)) return;
            var rpc = ZNet.instance.GetServerRPC();
            if (rpc == null) return;
            nextAsk = Time.realtimeSinceStartup + 1f;
            lastAsked = target;
            rpc.Invoke(AheadRpc, target);
        }

        internal static bool ArrivalConfirmed(Vector3 point)
        {
            float now = Time.realtimeSinceStartup;
            foreach (var (p, at) in confirmed)
                if (now - at < 30f && new Vector2(p.x - point.x, p.z - point.z).sqrMagnitude <= 16f) return true;
            return false;
        }

        // ---- Server ----
        sealed class Request { internal Vector3 Point; internal float HeardAt, NextCheck; internal bool Queued; }
        static readonly Dictionary<ZRpc, List<Request>> requests = new Dictionary<ZRpc, List<Request>>();
        static readonly Dictionary<long, float> boostUntil = new Dictionary<long, float>();
        static readonly List<ZDO> neighbourhood = new List<ZDO>();
        static readonly List<ZRpc> finished = new List<ZRpc>();

        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        private static class Connect
        {
            private static void Postfix(ZNet __instance, ZNetPeer peer)
            {
                if (__instance.IsServer())
                    peer.m_rpc.Register<Vector3>(AheadRpc, (rpc, target) =>
                    {
                        if (Mathf.Abs(target.x) > 12000f || Mathf.Abs(target.z) > 12000f || Mathf.Abs(target.y) > 12000f) return;
                        if (!requests.TryGetValue(rpc, out var list)) requests[rpc] = list = new List<Request>();
                        float now = Time.realtimeSinceStartup;
                        foreach (var r in list) if ((r.Point - target).sqrMagnitude < 1f) { r.HeardAt = now; return; }
                        if (list.Count >= 4) list.RemoveAt(0);
                        list.Add(new Request { Point = target, HeardAt = now });
                    });
                else
                    peer.m_rpc.Register<Vector3>(ReadyRpc, (rpc, point) =>
                    {
                        if (confirmed.Count >= 4) confirmed.RemoveAt(0);
                        confirmed.Add((point, Time.realtimeSinceStartup));
                    });
            }
        }

        static void TickNetwork()
        {
            var player = Player.m_localPlayer;
            if (player && player.m_teleporting && player.m_distantTeleport && !ArrivalConfirmed(player.m_teleportTargetPos)) AskAhead(player.m_teleportTargetPos, false);
            else if (warmedPortal) AskAhead(warmedTarget, false);
            if (requests.Count == 0 || !ZNet.instance || !ZNet.instance.IsServer() || ZDOMan.instance == null) return;
            float now = Time.realtimeSinceStartup;
            finished.Clear();
            foreach (var pair in requests)
            {
                var peer = FindPeer(pair.Key);
                if (peer == null) { finished.Add(pair.Key); continue; }
                var list = pair.Value;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var request = list[i];
                    if (now - request.HeardAt > 30f) { list.RemoveAt(i); continue; }
                    if (!request.Queued)
                    {
                        request.Queued = true;
                        if (On(Transitions)) QueueSupports(peer, request.Point);
                        boostUntil[peer.m_peer.m_uid] = now + 8f;
                    }
                    if (now < request.NextCheck) continue;
                    request.NextCheck = now + 0.1f;
                    if (Holds(peer, request.Point)) { pair.Key.Invoke(ReadyRpc, request.Point); list.RemoveAt(i); }
                }
                if (list.Count == 0) finished.Add(pair.Key);
            }
            foreach (var rpc in finished) requests.Remove(rpc);
        }

        static ZDOMan.ZDOPeer FindPeer(ZRpc rpc)
        {
            foreach (var peer in ZDOMan.instance.m_peers) if (peer.m_peer != null && peer.m_peer.m_rpc == rpc) return peer;
            return null;
        }

        static void Supports(Vector3 point)
        {
            neighbourhood.Clear();
            ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(point), new SimulationDistance(1, 0, false), neighbourhood, null);
        }

        static void QueueSupports(ZDOMan.ZDOPeer peer, Vector3 point)
        {
            Supports(point);
            foreach (var zdo in neighbourhood) if (zdo.Type >= ZDO.ObjectType.Solid) peer.m_forceSend.Add(zdo.m_uid);
        }

        // Holds every ground edit of the 3x3 zones and every solid object within 32 m.
        static bool Holds(ZDOMan.ZDOPeer peer, Vector3 point)
        {
            Supports(point);
            foreach (var zdo in neighbourhood)
            {
                if (zdo.Type < ZDO.ObjectType.Solid) continue;
                if (zdo.Type == ZDO.ObjectType.Solid && new Vector2(zdo.GetPosition().x - point.x, zdo.GetPosition().z - point.z).sqrMagnitude > 1024f) continue;
                if (!peer.m_zdos.ContainsKey(zdo.m_uid)) return false;
            }
            return true;
        }

        // World data sent to a player: the game allows about 10 KB queued per send; four times that during a boost.
        public static int SendWindow(ZDOMan.ZDOPeer peer)
        {
            if (peer?.m_peer == null || !boostUntil.TryGetValue(peer.m_peer.m_uid, out float until)) return 10240;
            if (Time.realtimeSinceStartup > until || !On(Transitions)) { boostUntil.Remove(peer.m_peer.m_uid); return 10240; }
            return 40960;
        }

        [HarmonyPatch(typeof(ZDOMan), "SendZDOs")]
        private static class SendBoost
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                int replaced = 0;
                foreach (var instruction in code)
                {
                    if ((instruction.opcode == OpCodes.Ldc_I4 || instruction.opcode == OpCodes.Ldc_I4_S) && System.Convert.ToInt32(instruction.operand) == 10240)
                    {
                        replaced++;
                        yield return new CodeInstruction(OpCodes.Ldarg_1).MoveLabelsFrom(instruction);
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SmoothFrames), nameof(SendWindow)));
                    }
                    else yield return instruction;
                }
                if (replaced != 2) Log.LogWarning("[Performance] Send boost: expected two 10 KB windows in ZDOMan.SendZDOs, replaced " + replaced);
            }
        }
    }
}
