using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Overhaul.Dungeons
{
    internal static class BossData
    {
        internal sealed class Entry
        {
            public int Stars;
            public float HealthBonusPercent;
            public float Scale = 1;
            public float AttackSpeed = 2;
        }
        private const string FileName = "DungeonBosses.cfg";
        private static Dictionary<string, Entry> entries;
        private static IEnumerable<string> Names => BossEncounter.Roster.Concat(BossEncounter.SwampRoster).Concat(BossEncounter.MountainRoster).Concat(BossEncounter.MistlandsRoster);

        internal static void Initialize()
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(BossData).Assembly.Location), FileName);
            if (!File.Exists(path)) File.WriteAllText(path, Utility.ConfigSections.Defaults(FileName));
            entries = ReadAndMigrate(path);
        }

        internal static Dictionary<string, Entry> ReadAndMigrate(string path)
        {
            var result = Read(path); // Fully validate before writing anything.
            if (File.ReadAllLines(path).Any(l => l.Trim() == "[Stars]"))
            {
                if (!File.Exists(path + ".legacy.bak")) File.Copy(path, path + ".legacy.bak");
                var text = new StringBuilder("# Dungeon-room guardians only. New spawns use these data.\n# HealthBonusPercent: +100 doubles final HP. Scale: 1 keeps final size.\n\n");
                foreach (var name in Names)
                {
                    var e = result[name];
                    text.AppendLine("[" + name + "]").AppendLine("Stars = " + e.Stars)
                        .AppendLine("HealthBonusPercent = " + e.HealthBonusPercent.ToString(CultureInfo.InvariantCulture))
                        .AppendLine("Scale = " + e.Scale.ToString(CultureInfo.InvariantCulture))
                        .AppendLine("AttackSpeed = " + e.AttackSpeed.ToString(CultureInfo.InvariantCulture)).AppendLine();
                }
                File.WriteAllText(path, text.ToString());
            }
            else
            {
                // Insert only missing fields, preserving custom values and comments.
                var lines=File.ReadAllLines(path).ToList();bool changed=false;
                bool addedSurtling=!lines.Any(l=>l.Trim()=="[Surtling]");
                if(addedSurtling)
                {
                    lines.AddRange(new[]{"","[Surtling]","Stars = 3","HealthBonusPercent = 500","Scale = 2","AttackSpeed = 2"});
                    changed=true;
                }
                bool addedMistlands=!lines.Any(l=>l.Trim()=="[SeekerBrute]");
                if(addedMistlands){lines.AddRange(new[]{"","[SeekerBrute]","Stars = 3","HealthBonusPercent = 100","Scale = 2","AttackSpeed = 2"});changed=true;}
                for(int i=lines.Count-1;i>=0;i--)
                {
                    if(!lines[i].Trim().StartsWith("["))continue;
                    int end=i+1;while(end<lines.Count && !lines[end].Trim().StartsWith("["))end++;
                    bool present=lines.Skip(i+1).Take(end-i-1).Any(l=>l.Split('=')[0].Trim()=="AttackSpeed");
                    if(!present){lines.Insert(i+1,"AttackSpeed = 2");changed=true;}
                }
                if(changed)
                {
                    string backup=path+(addedMistlands?".mistlands.bak":addedSurtling?".surtling.bak":".attack-speed.bak");
                    if(!File.Exists(backup))File.Copy(path,backup);
                    File.WriteAllLines(path,lines);
                }
            }
            return result;
        }

        internal static Dictionary<string, Entry> Read(string path)
        {
            var result = new Dictionary<string, Entry>(StringComparer.Ordinal);
            var expected = new HashSet<string>(Names, StringComparer.Ordinal);
            var fields = new Dictionary<string, HashSet<string>>();
            string section = null;
            bool legacy = false;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                void Invalid() => throw new InvalidDataException("Invalid dungeon boss data in " + path + ": " + line);
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2);
                    if (section == "Stars") { if (legacy || result.Count > 0) Invalid(); legacy = true; continue; }
                    if (legacy || !expected.Contains(section) || result.ContainsKey(section)) Invalid();
                    result.Add(section, new Entry()); fields.Add(section, new HashSet<string>()); continue;
                }
                int separator = line.IndexOf('=');
                if (separator < 1 || section == null) Invalid();
                string key = line.Substring(0, separator).Trim(), value = line.Substring(separator + 1).Trim();
                if (legacy)
                {
                    if (!expected.Contains(key) || result.ContainsKey(key) || !int.TryParse(value, out int n) || n < 0 || n > 3) Invalid();
                    result.Add(key, new Entry { Stars = int.Parse(value, CultureInfo.InvariantCulture) }); continue;
                }
                if (!fields[section].Add(key)) Invalid();
                if (key == "Stars")
                {
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 0 || n > 3) Invalid();
                    result[section].Stars = n;
                }
                else
                {
                    if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float n) || float.IsNaN(n) || float.IsInfinity(n)) Invalid();
                    if (key == "HealthBonusPercent" && n >= 0) result[section].HealthBonusPercent = n;
                    else if (key == "Scale" && n > 0) result[section].Scale = n;
                    else if (key == "AttackSpeed" && n >= .1f && n <= 10f) result[section].AttackSpeed = n;
                    else Invalid();
                }
            }
            // This guardian was added after the original roster. Existing files keep all their custom values.
            if(!result.ContainsKey("Surtling"))result.Add("Surtling",new Entry{Stars=3,HealthBonusPercent=500,Scale=2,AttackSpeed=2});
            if(!result.ContainsKey("SeekerBrute"))result.Add("SeekerBrute",new Entry{Stars=3,HealthBonusPercent=100,Scale=2,AttackSpeed=2});
            if (result.Count != expected.Count || (!legacy && fields.Values.Any(f => !f.IsSupersetOf(new[] { "Stars", "HealthBonusPercent", "Scale" }))))
                throw new InvalidDataException("Missing dungeon boss entries or fields in " + path);
            return result;
        }
        internal static Entry For(string prefab)
        {
            if (entries == null) Initialize();
            return entries[DungeonPolicy.PrefabName(prefab)];
        }
        internal static int StarsFor(string prefab) => For(prefab).Stars;
        internal static float AttackSpeedFor(string prefab)
        {
            if(entries==null)Initialize();
            return entries.TryGetValue(DungeonPolicy.PrefabName(prefab),out var entry)?entry.AttackSpeed:2;
        }
    }
}
