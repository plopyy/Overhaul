using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;

namespace Overhaul.Leveling
{
    public sealed class StatRule { public int MaxRank = 100; public double PerPoint; }
    public sealed class MonsterRule { public string Biome, Category; }
    public sealed class LevelingRules
    {
        public int MaxLevel = 200, PointsPerLevel = 4, PassiveLevel = 20;
        public double CombatSeconds = 15, PassiveCooldownMinutes = 30, ParticipationSeconds = 60, RewardRange = 80;
        public double CriticalMultiplier = 1.5, VitalityHealth = 10, VitalityRegen = 3;
        public Dictionary<string, double> Biomes = new Dictionary<string, double>();
        public Dictionary<string, double> Categories = new Dictionary<string, double>();
        public Dictionary<string, double> Stars = new Dictionary<string, double>();
        public Dictionary<string, StatRule> Stats = new Dictionary<string, StatRule>();
        public Dictionary<string, MonsterRule> Monsters = new Dictionary<string, MonsterRule>();
    }

    public static class LevelingConfig
    {
        public static readonly string[] StatIds = { "carry", "critical", "bonus_loot", "projectile", "block", "poison_resist", "fall_resist", "durability", "food", "health_regen", "eitr_regen", "eitr_cost", "element_poison", "element_frost", "element_fire", "element_spirit" };
        public static readonly string[] Passives = { "unburdened", "feather", "artisan", "vitality" };
        public static LevelingRules Current = new LevelingRules();
        internal static LevelingRules Local;
        internal static string DirectoryPath => Path.GetDirectoryName(typeof(LevelingConfig).Assembly.Location);
        internal static readonly string[] Files = { "Leveling.cfg", "BiomeExperience.cfg", "MonsterCategories.cfg", "LevelingStats.cfg", "LevelingPassives.cfg" };

        internal static void Initialize()
        {
            foreach (string file in Files)
            {
                string path = Path.Combine(DirectoryPath, file);
                if (!File.Exists(path)) File.WriteAllText(path, Utility.ConfigSections.Defaults(file));
            }
            Current = Load(DirectoryPath); Local = Current;
        }

        public static LevelingRules Load(string directory)
        {
            var rules = new LevelingRules();
            var general = Parse(Path.Combine(directory, "Leveling.cfg"));
            double Value(Dictionary<string,string> map, string key, double def, double min, double max)
            {
                if (!map.TryGetValue(key, out string text)) return def;
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) || double.IsNaN(result) || double.IsInfinity(result) || result < min || result > max)
                    throw new InvalidDataException("Invalid leveling setting: " + key);
                return result;
            }
            rules.MaxLevel = (int)Value(general,"Progression.MaxLevel",200,2,10000);
            rules.PointsPerLevel = (int)Value(general,"Progression.PointsPerLevel",4,1,100);
            rules.CombatSeconds = Value(general,"Progression.CombatSeconds",15,1,600);
            rules.ParticipationSeconds = Value(general,"Rewards.ParticipationSeconds",60,1,600);
            rules.RewardRange = Value(general,"Rewards.Range",80,1,500);
            rules.CriticalMultiplier = Value(general,"Combat.CriticalMultiplier",1.5,1,10);
            foreach (string id in new[]{"Normal","Captain","Elite","Boss"}) rules.Categories[id] = Value(general,"Categories."+id,1,0,1000);
            foreach (string id in new[]{"0","1","2"}) rules.Stars[id] = Value(general,"Stars."+id,1,0,1000);
            var biomes = Parse(Path.Combine(directory,"BiomeExperience.cfg"));
            foreach (var entry in biomes) rules.Biomes[entry.Key.Substring(entry.Key.IndexOf('.')+1)] = Value(biomes,entry.Key,0,0,1000000000);
            var stats = Parse(Path.Combine(directory,"LevelingStats.cfg"));
            foreach (string id in StatIds)
            {
                bool element=id.StartsWith("element_");
                // Keep the saved-character ID stable; expose its actual element in the data file.
                // Old files remain valid. If both sections exist, the new section takes precedence.
                string configId=id=="element_spirit" && stats.Keys.Any(k=>k.StartsWith("element_lightning.",StringComparison.Ordinal))?"element_lightning":id;
                int max=(int)Value(stats,configId+".MaxRank",element?50:100,0,10000);
                double gain=Value(stats,configId+".PerPoint",element?1:0,0,id=="carry" || element?1000:1);
                // Migrate the old shipped percentage defaults when an existing config is retained.
                if(element && max==100 && Math.Abs(gain-.002)<.0000001)gain=1;
                rules.Stats[id]=new StatRule{MaxRank=element?Math.Min(50,max):max,PerPoint=gain};
            }
            foreach (var entry in Parse(Path.Combine(directory,"MonsterCategories.cfg")))
            {
                string[] parts = entry.Value.Split(',').Select(p=>p.Trim()).ToArray();
                if(parts.Length!=2 || !rules.Biomes.ContainsKey(parts[0]) || !rules.Categories.ContainsKey(parts[1])) throw new InvalidDataException("Invalid monster: "+entry.Key);
                rules.Monsters[entry.Key.Substring(entry.Key.IndexOf('.')+1)] = new MonsterRule { Biome=parts[0], Category=parts[1] };
            }
            var passives = Parse(Path.Combine(directory,"LevelingPassives.cfg"));
            rules.PassiveLevel=(int)Value(passives,"Selection.RequiredLevel",20,1,10000);
            rules.PassiveCooldownMinutes=Value(passives,"Selection.CooldownMinutes",30,0,10080);
            rules.VitalityHealth=Value(passives,"Vitality.Health",10,0,1000);
            rules.VitalityRegen=Value(passives,"Vitality.RegenPerSecond",3,0,100);
            return rules;
        }

        private static Dictionary<string,string> Parse(string path)
        {
            var result=new Dictionary<string,string>(); string section="";
            foreach(string raw in File.ReadAllLines(path))
            {
                string line=raw.Trim(); if(line.Length==0 || line.StartsWith("#") || line.StartsWith(";"))continue;
                if(line.StartsWith("[") && line.EndsWith("]")){section=line.Substring(1,line.Length-2);continue;}
                int equals=line.IndexOf('='); if(equals<1)throw new InvalidDataException(path+": "+line);
                result.Add(section+"."+line.Substring(0,equals).Trim(),line.Substring(equals+1).Trim());
            }
            return result;
        }

        public static long Reward(string prefab, int level)
        {
            if(IsBasicBird(prefab))return 5;
            if(!Current.Monsters.TryGetValue(prefab,out MonsterRule rule))return 0;
            double amount=Current.Biomes[rule.Biome]*Current.Categories[rule.Category]*Current.Stars[Math.Max(0,Math.Min(2,level-1)).ToString()];
            return (long)Math.Round(amount,MidpointRounding.AwayFromZero);
        }
        internal static bool IsBasicBird(string prefab)=>prefab=="Crow" || prefab=="Seagal";
    }
}
