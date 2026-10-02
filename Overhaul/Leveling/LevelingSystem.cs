using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace Overhaul.Leveling
{
    public sealed class OverhaulCharacterData
    {
        public int Version = 1;
        public long TotalExperience, CurrentExperience;
        public int Level = 1, AvailableStatPoints;
        public Dictionary<string, int> AllocatedStats = new Dictionary<string, int>();
        public string Passive = "";
        public long PassiveChangeAfterUtc;
        public OverhaulCharacterData Clone() => JsonConvert.DeserializeObject<OverhaulCharacterData>(JsonConvert.SerializeObject(this));
    }

    public static class LevelingSystem
    {
        public static long GetExperienceToNextLevel(int currentLevel)
        {
            if (currentLevel < 1) throw new ArgumentOutOfRangeException(nameof(currentLevel));
            return checked(25L * currentLevel * (currentLevel + 3L));
        }

        public static void Calculate(long total, out int level, out long current)
        {
            level = 1; current = Math.Max(0, total);
            while (level < LevelingConfig.Current.MaxLevel)
            {
                long cost = GetExperienceToNextLevel(level);
                if (current < cost) return;
                current -= cost; level++;
            }
            current = 0;
        }

        public static int Budget(int level) => checked((level - 1) * LevelingConfig.Current.PointsPerLevel);

        public static bool Reconcile(OverhaulCharacterData data)
        {
            if (data == null || data.Version != 1 || data.TotalExperience < 0) throw new InvalidOperationException("Unsupported or invalid character progression");
            Calculate(data.TotalExperience, out int level, out long current);
            bool changed = data.Level != level || data.CurrentExperience != current;
            data.Level = level; data.CurrentExperience = current;
            if (data.AllocatedStats == null) data.AllocatedStats = new Dictionary<string, int>();
            if (changed || !ValidAllocation(data.AllocatedStats, Budget(level))) data.AllocatedStats.Clear();
            data.AvailableStatPoints = Budget(level) - data.AllocatedStats.Values.Sum();
            if (level < LevelingConfig.Current.PassiveLevel || !LevelingConfig.Passives.Contains(data.Passive)) data.Passive = "";
            return changed;
        }

        public static void AddExperience(OverhaulCharacterData data, long amount)
        {
            if (amount <= 0) return;
            data.TotalExperience = amount > long.MaxValue - data.TotalExperience ? long.MaxValue : data.TotalExperience + amount;
            Calculate(data.TotalExperience, out int level, out long current);
            data.Level = level; data.CurrentExperience = current;
            data.AvailableStatPoints = Budget(level) - data.AllocatedStats.Values.Sum();
        }

        public static long AdjustExperience(OverhaulCharacterData data, long amount)
        {
            if(data==null || data.Version!=1 || data.TotalExperience<0)throw new InvalidOperationException("Invalid progression");
            long before=data.TotalExperience;int previousLevel=data.Level;
            // Comparing before adding also handles long.MinValue without negating it.
            data.TotalExperience=amount<0 ? (amount < -before ? 0 : before+amount)
                : (amount>long.MaxValue-before ? long.MaxValue : before+amount);
            Calculate(data.TotalExperience,out int level,out long current);
            data.Level=level;data.CurrentExperience=current;
            if(data.AllocatedStats==null)data.AllocatedStats=new Dictionary<string,int>();
            if(level<previousLevel || !ValidAllocation(data.AllocatedStats,Budget(level)))data.AllocatedStats.Clear();
            data.AvailableStatPoints=Budget(level)-data.AllocatedStats.Values.Sum();
            if(level<LevelingConfig.Current.PassiveLevel)data.Passive="";
            return data.TotalExperience-before;
        }

        public static bool ValidAllocation(Dictionary<string, int> values, int budget)
        {
            long sum = 0; int elements = 0;
            foreach (var entry in values)
            {
                if (!LevelingConfig.Current.Stats.TryGetValue(entry.Key, out StatRule rule) || entry.Value < 0 || entry.Value > rule.MaxRank) return false;
                sum += entry.Value;
                if (entry.Key.StartsWith("element_") && entry.Value > 0) elements++;
            }
            return sum <= budget && elements <= 1;
        }

        public static bool Allocate(OverhaulCharacterData data, Dictionary<string, int> values, bool combat)
        {
            if (combat || values == null || !ValidAllocation(values, Budget(data.Level))) return false;
            foreach (var old in data.AllocatedStats)
                if (!values.TryGetValue(old.Key, out int rank) || rank < old.Value) return false;
            data.AllocatedStats = new Dictionary<string, int>(values);
            data.AvailableStatPoints = Budget(data.Level) - values.Values.Sum();
            return true;
        }

        public static bool Reset(OverhaulCharacterData data, bool combat)
        {
            if (combat) return false;
            data.AllocatedStats.Clear(); data.AvailableStatPoints = Budget(data.Level); return true;
        }

        public static bool ChoosePassive(OverhaulCharacterData data, string id, bool combat, long utc)
        {
            if (combat || data.Level < LevelingConfig.Current.PassiveLevel || !LevelingConfig.Passives.Contains(id)
                || id == data.Passive || utc < data.PassiveChangeAfterUtc) return false;
            data.Passive = id;
            data.PassiveChangeAfterUtc = utc + TimeSpan.FromMinutes(LevelingConfig.Current.PassiveCooldownMinutes).Ticks;
            return true;
        }
    }
}
