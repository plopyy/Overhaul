using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Overhaul.Replication
{
    // Rework phase 1: the local profile is copied into the character ZDO, so the server holds the
    // player's data in memory through native ZDO replication. The client stays the authority; the
    // server only reads. The .fch save is unchanged and the ZDO is discarded natively on logout.
    internal static class PlayerReplica
    {
        internal sealed class Domain
        {
            internal string Name;
            internal int Key;
            internal Func<Player, byte[]> Capture;
            internal Func<byte[], string> Describe;
        }

        // One entry per replicated part of the profile; added one at a time and checked separately.
        internal static readonly List<Domain> Domains = new List<Domain>
        {
            // Everything the native .fch save stores for the character (Player.Save): inventory, skills,
            // recipes, stations, materials, trophies, food, health, mod custom data... The map is not included.
            new Domain
            {
                Name = "player",
                Key = "overhaul_replica_player_v1".GetStableHashCode(),
                Capture = player => { var package = new ZPackage(); player.Save(package); return package.GetArray(); },
                Describe = bytes => (bytes.Length / 1024f).ToString("0.0") + " Ko",
            },
            new Domain
            {
                Name = "skills",
                Key = "overhaul_replica_skills_v1".GetStableHashCode(),
                Capture = player => { var package = new ZPackage(); player.m_skills.Save(package); return package.GetArray(); },
                Describe = DescribeSkills,
            },
        };

        private const float ClientInterval = 1f, ServerInterval = 5f;
        private static float nextClient, nextServer;
        private static readonly Dictionary<int, byte[]> written = new Dictionary<int, byte[]>();
        private static ZDOID writtenFor = ZDOID.None;
        // Server view: last data revision read per character and domain.
        private static readonly Dictionary<ZDOID, Dictionary<int, uint>> seen = new Dictionary<ZDOID, Dictionary<int, uint>>();

        internal static bool Enabled => Utility.OverhaulConfig.PlayerReplication != null && Utility.OverhaulConfig.PlayerReplication.Value;

        internal static void Tick()
        {
            if (!Enabled || !ZNet.instance) return;
            float now = Time.realtimeSinceStartup;
            if (now >= nextClient) { nextClient = now + ClientInterval; Write(); }
            if (ZNet.instance.IsServer() && now >= nextServer) { nextServer = now + ServerInterval; Read(); }
        }

        // Client: write each domain whose content changed since the last write.
        private static void Write()
        {
            Player player = Player.m_localPlayer;
            if (!player || !player.m_nview || !player.m_nview.IsValid() || !player.m_nview.IsOwner()) return;
            ZDO zdo = player.m_nview.GetZDO();
            if (zdo.m_uid != writtenFor) { written.Clear(); writtenFor = zdo.m_uid; } // New body: send everything again.
            foreach (Domain domain in Domains)
            {
                byte[] bytes;
                try { bytes = domain.Capture(player); }
                catch (Exception e) { Utility.Log.LogError("Replica " + domain.Name + " : capture impossible : " + e); continue; }
                byte[] previous;
                if (written.TryGetValue(domain.Key, out previous) && previous.SequenceEqual(bytes)) continue;
                zdo.Set(domain.Key, bytes);
                written[domain.Key] = bytes;
            }
        }

        // Server: decode what arrived, so a malformed or missing copy shows up in the log.
        private static void Read()
        {
            var alive = new HashSet<ZDOID>();
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer.m_characterID.IsNone()) continue;
                ZDO zdo = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (zdo == null) continue;
                alive.Add(zdo.m_uid);
                Dictionary<int, uint> revisions;
                if (!seen.TryGetValue(zdo.m_uid, out revisions)) seen[zdo.m_uid] = revisions = new Dictionary<int, uint>();
                foreach (Domain domain in Domains)
                {
                    byte[] bytes = zdo.GetByteArray(domain.Key, null);
                    if (bytes == null) continue;
                    uint revision;
                    if (revisions.TryGetValue(domain.Key, out revision) && revision == zdo.DataRevision) continue;
                    bool first = !revisions.ContainsKey(domain.Key);
                    revisions[domain.Key] = zdo.DataRevision;
                    string summary;
                    try { summary = domain.Describe(bytes); }
                    catch (Exception e) { Utility.Log.LogWarning("Replica " + peer.m_playerName + " " + domain.Name + " : copie illisible : " + e.Message); continue; }
                    if (first) Utility.Log.LogInfo("Replica " + peer.m_playerName + " " + domain.Name + " recue : " + summary);
                }
            }
            // Characters gone with their ZDO: forget them (no server save in this phase).
            foreach (ZDOID id in seen.Keys.Where(id => !alive.Contains(id)).ToArray()) seen.Remove(id);
        }

        // Same layout as Skills.Save: version, count, then type, level and accumulator.
        internal static Dictionary<Skills.SkillType, float> ReadSkills(byte[] bytes)
        {
            var package = new ZPackage(bytes);
            package.ReadInt();
            int count = package.ReadInt();
            if (count < 0 || count > 1000) throw new InvalidOperationException("nombre de competences invalide : " + count);
            var result = new Dictionary<Skills.SkillType, float>();
            for (int i = 0; i < count; i++)
            {
                var type = (Skills.SkillType)package.ReadInt();
                float level = package.ReadSingle();
                package.ReadSingle();
                result[type] = level;
            }
            return result;
        }

        private static string DescribeSkills(byte[] bytes)
        {
            var skills = ReadSkills(bytes);
            return skills.Count + " competence(s), meilleure " + (skills.Count == 0 ? "-" : skills.OrderByDescending(s => s.Value).First().Key + " " + skills.Max(s => s.Value).ToString("0.0"));
        }
    }
}
