using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Performance
{
    // Server summary of the trees far away, for Far voxels: the client asks the server, 16 zones at a time, for the trees
    // that really stand there (the prefabs it has voxel models for), with their position, rotation and scale, so trees cut,
    // replanted or grown since the world was made show as they are. A zone never generated (nobody went there) is flagged
    // and the client keeps placing the game's original vegetation itself, exact since nothing there was ever touched.
    // Zones are asked again every 5 minutes while in range. Works the same on a dedicated server and a hosted world.
    internal static class FarTrees
    {
        const string RequestRpc = "Overhaul_FarTreesRequest", DataRpc = "Overhaul_FarTreesData";
        const int ZonesPerRequest = 16;
        const float RequestEvery = .5f, RefreshAfter = 300f;

        internal sealed class Summary
        {
            internal bool Generated; internal float Time;
            internal readonly List<(int Hash, Vector3 Position, Quaternion Rotation, float Scale)> Trees = new List<(int, Vector3, Quaternion, float)>();
        }

        static readonly Dictionary<Vector2Int, Summary> summaries = new Dictionary<Vector2Int, Summary>();
        static readonly HashSet<Vector2Int> wanted = new HashSet<Vector2Int>(), pending = new HashSet<Vector2Int>();
        internal static readonly HashSet<Vector2Int> Changed = new HashSet<Vector2Int>();
        static float nextRequest;
        internal static int Received, Asked;

        [HarmonyPatch(typeof(Game), "Start")]
        private static class Register
        {
            private static void Postfix()
            {
                ZRoutedRpc.instance.Register<ZPackage>(RequestRpc, OnRequest);
                ZRoutedRpc.instance.Register<ZPackage>(DataRpc, OnData);
                Clear();
            }
        }

        internal static void Clear() { summaries.Clear(); wanted.Clear(); pending.Clear(); Changed.Clear(); Received = Asked = 0; }

        // ---- Client ----
        // The summary of a zone if the server sent one; asks for it (again, if old) otherwise.
        internal static bool TryGet(Vector2Int zone, out Summary summary)
        {
            if (summaries.TryGetValue(zone, out summary))
            {
                if (Time.time - summary.Time > RefreshAfter && !pending.Contains(zone)) wanted.Add(zone);
                return true;
            }
            if (!pending.Contains(zone)) wanted.Add(zone);
            return false;
        }

        internal static void Tick(ICollection<int> hashes)
        {
            if (wanted.Count == 0 || Time.time < nextRequest || ZRoutedRpc.instance == null) return;
            nextRequest = Time.time + RequestEvery;
            var pkg = new ZPackage();
            pkg.Write(hashes.Count); foreach (int h in hashes) pkg.Write(h);
            var batch = new List<Vector2Int>();
            foreach (var z in wanted) { batch.Add(z); if (batch.Count >= ZonesPerRequest) break; }
            pkg.Write(batch.Count);
            foreach (var z in batch) { pkg.Write(z.x); pkg.Write(z.y); wanted.Remove(z); pending.Add(z); }
            Asked += batch.Count;
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RequestRpc, pkg);
        }

        static void OnData(long sender, ZPackage pkg)
        {
            if (ZNet.instance && !ZNet.instance.IsServer() && sender != ZRoutedRpc.instance.GetServerPeerID()) return; // only the server's
            int zones = pkg.ReadInt();
            for (int i = 0; i < zones; i++)
            {
                var id = new Vector2Int(pkg.ReadInt(), pkg.ReadInt());
                var s = new Summary { Generated = pkg.ReadBool(), Time = Time.time };
                int trees = pkg.ReadInt();
                for (int t = 0; t < trees; t++) s.Trees.Add((pkg.ReadInt(), pkg.ReadVector3(), pkg.ReadQuaternion(), pkg.ReadSingle()));
                bool had = summaries.ContainsKey(id);
                summaries[id] = s; pending.Remove(id); Received++;
                if (had) Changed.Add(id);
            }
        }

        // ---- Server ----
        static readonly List<ZDO> found = new List<ZDO>();
        static readonly HashSet<ZoneSystem.SectorIndex> visited = new HashSet<ZoneSystem.SectorIndex>();

        static void OnRequest(long sender, ZPackage pkg)
        {
            if (!ZNet.instance || !ZNet.instance.IsServer() || ZDOMan.instance == null || !ZoneSystem.instance) return;
            var hashes = new HashSet<int>();
            int n = Mathf.Min(pkg.ReadInt(), 256);
            for (int i = 0; i < n; i++) hashes.Add(pkg.ReadInt());
            int zones = Mathf.Min(pkg.ReadInt(), ZonesPerRequest);
            var reply = new ZPackage();
            reply.Write(zones);
            for (int i = 0; i < zones; i++)
            {
                int x = pkg.ReadInt(), y = pkg.ReadInt();
                var sector = new Vector2s(x, y);
                bool generated = ZoneSystem.instance.m_generatedZones.Contains(sector);
                reply.Write(x); reply.Write(y); reply.Write(generated);
                found.Clear(); visited.Clear();
                if (generated) ZDOMan.instance.FindObjects(sector, found, visited);
                int countPos = reply.GetPos(); int count = 0; reply.Write(0);
                foreach (var zdo in found)
                {
                    if (zdo == null || !hashes.Contains(zdo.GetPrefab())) continue;
                    reply.Write(zdo.GetPrefab()); reply.Write(zdo.GetPosition()); reply.Write(zdo.GetRotation()); reply.Write(Scale(zdo));
                    count++;
                }
                int end = reply.GetPos(); reply.SetPos(countPos); reply.Write(count); reply.SetPos(end);
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, DataRpc, reply);
        }

        // Scale as ZNetView applies it: the "scale" vector if set, else the scalar, else the prefab's own.
        static float Scale(ZDO zdo)
        {
            var v = zdo.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
            if (v != Vector3.zero) return v.x;
            var prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            return zdo.GetFloat(ZDOVars.s_scaleScalarHash, prefab ? prefab.transform.localScale.x : 1f);
        }
    }
}
