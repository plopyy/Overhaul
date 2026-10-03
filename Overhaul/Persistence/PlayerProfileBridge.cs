using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Overhaul.Persistence
{
    // One-time profile extraction and creation of a transient, non-file-backed profile at login.
    // No native profile file is read or written here; the selected appearance/profile stays untouched.
    internal static class PlayerProfileBridge
    {
        internal static PlayerChange[] Capture(PlayerProfile profile, long world)
        {
            var rows = new List<PlayerChange> {
                State("player_id", profile.m_playerID), State("player_name", text: profile.m_playerName),
                State("first_spawn", profile.m_firstSpawn), State("created_at", profile.m_dateCreated.Ticks),
                State("used_cheats", profile.m_usedCheats), State("start_seed", text: profile.m_startSeed)
            };
            if (profile.m_worldData.TryGetValue(world, out var data))
            {
                if (data.m_haveCustomSpawnPoint) rows.Add(Point("bed", data.m_spawnPoint));
                if (data.m_haveLogoutPoint) rows.Add(Point("logout", data.m_logoutPoint));
                if (data.m_haveDeathPoint) rows.Add(Point("death", data.m_deathPoint));
                rows.Add(Point("home", data.m_homePoint));
                // Preserve compressed import bytes until worker-side map decoding; avoid any main-thread recompression.
                if (data.m_mapData != null)
                    for (int i = 0; i < data.m_mapData.Length; i += 65536)
                        rows.Add(new PlayerChange("map", false, "native_import", i / 65536, data.m_mapData.Skip(i).Take(65536).ToArray()));
            }
            for (int difficulty = 0; difficulty < profile.m_playerStats.Length; difficulty++)
            {
                var stats = profile.m_playerStats[difficulty];
                foreach (var pair in stats.m_stats.Where(p => p.Value != 0))
                    rows.Add(Stat(difficulty, "values", ((int)pair.Key).ToString(CultureInfo.InvariantCulture), pair.Value));
                foreach (var category in Categories(stats))
                    foreach (var pair in category.Value) rows.Add(Stat(difficulty, category.Key, pair.Key, pair.Value));
            }
            return rows.ToArray();
        }
        internal static PlayerChange[] DecodeImportedMap(IEnumerable<PlayerChange> imported)
        {
            var rows = imported.ToArray();
            var chunks = rows.Where(r => r.Table == "map" && (string)r.Values[0] == "native_import").Select(r => r.Values).OrderBy(r => Convert.ToInt32(r[1])).ToArray();
            if (chunks.Length == 0) return rows;
            if (chunks.Length > 256 || chunks.Where((r, i) => Convert.ToInt32(r[1]) != i).Any()) throw new System.IO.InvalidDataException("Invalid imported map chunks");
            byte[] native = chunks.SelectMany(r => (byte[])r[2]).ToArray();
            return rows.Where(r => r.Table != "map" || (string)r.Values[0] != "native_import").Concat(PlayerMapFormat.Decode(native)).ToArray();
        }
        internal static PlayerProfile Restore(IEnumerable<PlayerChange> source, long world, byte[] playerData, byte[] mapData)
        {
            var rows = source.Where(r => !r.Delete).ToArray();
            var state = rows.Where(r => r.Table == "state").Select(r => r.Values).ToDictionary(r => (string)r[0]);
            var profile = new PlayerProfile(null, FileHelpers.FileSource.Local);
            if (!state.TryGetValue("player_id", out var id) || Convert.ToInt64(id[1]) == 0) throw new System.IO.InvalidDataException("Missing persistent character ID");
            profile.m_playerID = Convert.ToInt64(id[1]);
            profile.m_playerName = state.TryGetValue("player_name", out var name) ? (string)name[3] : "Stranger";
            profile.m_firstSpawn = !state.TryGetValue("first_spawn", out var first) || Convert.ToBoolean(first[1]);
            if (state.TryGetValue("created_at", out var created)) profile.m_dateCreated = new DateTime(Convert.ToInt64(created[1]));
            if (state.TryGetValue("used_cheats", out var cheats)) profile.m_usedCheats = Convert.ToBoolean(cheats[1]);
            if (state.TryGetValue("start_seed", out var seed)) profile.m_startSeed = (string)seed[3];
            profile.m_playerData = playerData == null ? null : (byte[])playerData.Clone();
            var data = new PlayerProfile.WorldPlayerData { m_mapData = mapData == null ? null : (byte[])mapData.Clone() };
            profile.m_worldData.Add(world, data);
            foreach (var row in rows.Where(r => r.Table == "spawn").Select(r => r.Values))
            {
                var point = new Vector3(Convert.ToSingle(row[1]), Convert.ToSingle(row[2]), Convert.ToSingle(row[3]));
                switch ((string)row[0])
                {
                    case "bed": data.m_spawnPoint = point; data.m_haveCustomSpawnPoint = true; break;
                    case "logout": data.m_logoutPoint = point; data.m_haveLogoutPoint = true; break;
                    case "death": data.m_deathPoint = point; data.m_haveDeathPoint = true; break;
                    case "home": data.m_homePoint = point; break;
                    default: throw new System.IO.InvalidDataException("Unknown character spawn point");
                }
            }
            foreach (var row in rows.Where(r => r.Table == "knowledge").Select(r => r.Values))
            {
                string kind = (string)row[0]; if (!kind.StartsWith("statistics:", StringComparison.Ordinal)) continue;
                string[] parts = kind.Split(':');
                if (parts.Length != 3 || !int.TryParse(parts[1], out int difficulty) || difficulty < 0 || difficulty >= profile.m_playerStats.Length)
                    throw new System.IO.InvalidDataException("Invalid statistics category");
                var stats = profile.m_playerStats[difficulty];
                float value = float.Parse((string)row[2], CultureInfo.InvariantCulture);
                if (float.IsNaN(value) || float.IsInfinity(value)) throw new System.IO.InvalidDataException("Invalid player statistic");
                if (parts[2] == "values") stats.m_stats[(PlayerStatType)int.Parse((string)row[1], CultureInfo.InvariantCulture)] = value;
                else if (Categories(stats).TryGetValue(parts[2], out var category)) category[(string)row[1]] = value;
                else throw new System.IO.InvalidDataException("Unknown player statistics category");
            }
            return profile;
        }
        private static PlayerChange State(string key, object integer = null, string text = null) => new PlayerChange("state", false, key, integer, null, text, null);
        private static PlayerChange Point(string name, Vector3 p) => new PlayerChange("spawn", false, name, p.x, p.y, p.z);
        private static PlayerChange Stat(int difficulty, string category, string key, float value) =>
            new PlayerChange("knowledge", false, "statistics:" + difficulty + ":" + category, key, value.ToString("R", CultureInfo.InvariantCulture));
        private static Dictionary<string, Dictionary<string, float>> Categories(PlayerProfile.PlayerStats stats)
        {
            var result = new Dictionary<string, Dictionary<string, float>> {
                {"worlds", stats.m_knownWorlds}, {"world_keys", stats.m_knownWorldKeys}, {"commands", stats.m_knownCommands},
                {"pickup", stats.m_itemPickupStats}, {"craft", stats.m_itemCraftStats}, {"pickable", stats.m_pickableStats},
                {"food", stats.m_foodEatenStats}, {"pieces", stats.m_piecesPlacedStats}
            };
            for (int i = 0; i < stats.m_enemyStats.Length; i++) result.Add("enemy_" + i, stats.m_enemyStats[i]);
            return result;
        }
    }
}
