using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using UnityEngine;
using EffectRegistry = Overhaul.Effects.Effects;

namespace Overhaul.Leveling
{
    /// <summary>
    /// Progress of one class: its own experience, level, free points
    /// and the points spent in each skill.
    /// </summary>
    internal sealed class ClassProgress
    {
        /// <summary>Only saved reference: level and current experience are rebuilt from it.</summary>
        public long TotalExperience;
        public long CurrentExperience;
        public int Level = 1;
        public int AvailablePoints;

        /// <summary>Path chosen in this class, or null while none is chosen.</summary>
        public string ChosenPathID;

        /// <summary>Skill ID -> points spent in that skill.</summary>
        public readonly Dictionary<string, int> SkillPoints = new Dictionary<string, int>();

        /// <summary>Highest class level.</summary>
        public const int MaxLevel = 50;

        /// <summary>One class point per level: a freshly chosen class starts at level 1 with 1 point.</summary>
        public static int TotalPointsAtLevel(int level) => level;

        /// <summary>Experience from a class level to the next: 40 x level^2.5 (about 4.7 million to reach level 50).</summary>
        public static long ExperienceToNextLevel(int level) => (long)Math.Round(40.0 * Math.Pow(level, 2.5));

        /// <summary>Class experience table (steeper than the leveling's), capped at MaxLevel.</summary>
        public static void Calculate(long total, out int level, out long current)
        {
            level = 1; current = Math.Max(0, total);
            while (level < MaxLevel)
            {
                long cost = ExperienceToNextLevel(level);
                if (current < cost) return;
                current -= cost; level++;
            }
            current = 0;
        }

        /// <summary>Adds experience; levels gained give their points. Experience is never lost here.</summary>
        public void AddExperience(long amount)
        {
            if (amount <= 0) return;
            TotalExperience = amount > long.MaxValue - TotalExperience ? long.MaxValue : TotalExperience + amount;
            Calculate(TotalExperience, out int level, out long current);
            int gained = level - Level;
            Level = level;
            CurrentExperience = current;
            if (gained > 0) AvailablePoints += gained;
        }

        /// <summary>
        /// Full rebuild on load, from the total experience, in case the experience table changed.
        /// Only a lower level (table changed) gives every skill point back and clears the path;
        /// a higher or equal level keeps the spent points. Returns true when the level dropped.
        /// </summary>
        public bool RecalculateLevel()
        {
            if (TotalExperience < 0) TotalExperience = 0;
            Calculate(TotalExperience, out int level, out long current);
            bool dropped = level < Level;
            Level = level;
            CurrentExperience = current;
            int spent = SkillPoints.Values.Sum();
            if (dropped || spent > TotalPointsAtLevel(Level))
            {
                SkillPoints.Clear();
                ChosenPathID = null;
                spent = 0;
            }
            AvailablePoints = TotalPointsAtLevel(Level) - spent;
            return dropped;
        }
    }

    /// <summary>
    /// Holds the player's class data: which class is active,
    /// and the separate progress of every class they have played.
    /// </summary>
    internal sealed class ClassSystem
    {
        /// <summary>Save format version, raised when the layout changes.</summary>
        public int Version = 1;

        /// <summary>ID of the active class, or null while no class is chosen.</summary>
        public string ActiveClassID;

        /// <summary>Class ID -> progress of that class.</summary>
        public readonly Dictionary<string, ClassProgress> Classes = new Dictionary<string, ClassProgress>();

        [JsonIgnore]
        public bool HasClass => !string.IsNullOrEmpty(ActiveClassID);

        /// <summary>Progress of the active class, or null while no class is chosen.</summary>
        [JsonIgnore]
        public ClassProgress Active => HasClass ? Get(ActiveClassID) : null;

        /// <summary>Active buffs, skill ID -> buff. Not saved: buffs end on logout.</summary>
        [JsonIgnore]
        public readonly Dictionary<string, ClassBuff> Buffs = new Dictionary<string, ClassBuff>();

        /// <summary>Called every frame for the local player, deltaTime in seconds.</summary>
        public void Tick(float deltaTime)
        {
            List<string> expired = null;
            foreach (var entry in Buffs)
            {
                entry.Value.Remaining -= deltaTime;
                if (entry.Value.Remaining <= 0) (expired ?? (expired = new List<string>())).Add(entry.Key);
            }
            if (expired != null)
            {
                foreach (string skillID in expired) Buffs.Remove(skillID);
                EffectRegistry.Invalidate();
            }
        }

        /// <summary>
        /// Starts the buff of a skill, or restarts its timer when it is already active.
        /// Stackable buffs gain one stack, up to the skill's MaxStacks.
        /// </summary>
        public ClassBuff AddBuff(ClassSkill skill, float duration)
        {
            if (!Buffs.TryGetValue(skill.ID, out var buff))
            {
                buff = new ClassBuff { Skill = skill };
                Buffs[skill.ID] = buff;
            }
            else if (buff.Stacks < skill.Data.MaxStacks) buff.Stacks++;
            buff.Duration = duration;
            buff.Remaining = duration;
            EffectRegistry.Invalidate();
            return buff;
        }

        public bool HasBuff(ClassSkill skill) => Buffs.ContainsKey(skill.ID);

        /// <summary>Stacks of a skill's buff, 0 when it is not active.</summary>
        public int GetBuffStacks(ClassSkill skill) => Buffs.TryGetValue(skill.ID, out var buff) ? buff.Stacks : 0;

        public void RemoveBuff(ClassSkill skill)
        {
            if (Buffs.Remove(skill.ID)) EffectRegistry.Invalidate();
        }

        /// <summary>Recalculates every class on character load.</summary>
        public void RecalculateLevels()
        {
            foreach (var progress in Classes.Values) progress.RecalculateLevel();
        }

        /// <summary>Progress of a class, created empty the first time it is asked for.</summary>
        public ClassProgress Get(string classID)
        {
            if (!Classes.TryGetValue(classID, out var progress))
            {
                progress = new ClassProgress { AvailablePoints = ClassProgress.TotalPointsAtLevel(1) };
                Classes[classID] = progress;
            }
            return progress;
        }
    }

    /// <summary>A timed effect on the player, shown as a small icon above the health bar.</summary>
    internal sealed class ClassBuff
    {
        /// <summary>Skill that gives this buff: icon, name and max stacks come from it.</summary>
        public ClassSkill Skill;
        /// <summary>Full duration and time left, in seconds.</summary>
        public float Duration;
        public float Remaining;
        public int Stacks = 1;
    }

    /// <summary>Place of a skill in its path.</summary>
    internal enum ClassSkillKind
    {
        /// <summary>One of the 3 skills of a row (4 rows per path).</summary>
        Normal,
        /// <summary>Path ultimate, on top of the tree.</summary>
        Ultimate,
        /// <summary>Path active skill, left half of the bottom bar.</summary>
        Active,
        /// <summary>Path passive, right half of the bottom bar.</summary>
        Passive
    }

    /// <summary>
    /// Data of one class skill, read from the embedded JSON: fixed and shared by every player.
    /// What a player has spent in it is stored in ClassProgress.SkillPoints.
    /// </summary>
    internal sealed class ClassSkillData
    {
        /// <summary>Unique skill ID, key of ClassProgress.SkillPoints.</summary>
        public string ID;
        public string ClassID;
        /// <summary>Path of the class this skill belongs to.</summary>
        public string PathID;
        [JsonConverter(typeof(StringEnumConverter))]
        public ClassSkillKind Kind;

        /// <summary>Normal skills only: x = column (0..2), y = row from the bottom (0..3).</summary>
        public Vector2Int Cell;

        /// <summary>Maximum points in this skill; each point costs 1 class point.</summary>
        public int MaxRank = 1;

        /// <summary>Max stacks of this skill's buff; 1 = not stackable.</summary>
        public int MaxStacks = 1;

        /// <summary>Bonuses given while the skill has points (permanent).</summary>
        public List<ClassSkillEffect> Effects = new List<ClassSkillEffect>();

        /// <summary>Bonuses given while the skill's buff is active, multiplied by its stacks.</summary>
        public List<ClassSkillEffect> BuffEffects = new List<ClassSkillEffect>();

        /// <summary>Translation keys of the displayed texts.</summary>
        public string NameKey;
        public string DescriptionKey;
        public string Icon;
    }

    /// <summary>One bonus of a skill: an effect of the registry (Effects.cfg) and its value at each rank.</summary>
    internal sealed class ClassSkillEffect
    {
        /// <summary>Effect ID in the registry, e.g. "CritChance", "HealthMax".</summary>
        public string EffectID;

        /// <summary>Value at each rank: Values[0] = rank 1, Values[1] = rank 2...</summary>
        public float[] Values;

        /// <summary>Value at a given rank, 0 at rank 0; ranks past the list keep the last value.</summary>
        public float Get(int rank)
        {
            if (rank <= 0 || Values == null || Values.Length == 0) return 0;
            return Values[Math.Min(rank, Values.Length) - 1];
        }
    }

    /// <summary>
    /// How a skill works in game. One subclass per skill that needs its own behaviour;
    /// a plain bonus skill can use this class as it is.
    /// </summary>
    internal class ClassSkill
    {
        public readonly ClassSkillData Data;

        public ClassSkill(ClassSkillData data) => Data = data;

        public string ID => Data.ID;

        /// <summary>Points the player has in this skill, 0 when not learned or not in the active class.</summary>
        public int GetRank(ClassSystem classes)
        {
            var progress = classes?.Active;
            if (progress == null || classes.ActiveClassID != Data.ClassID) return 0;
            return progress.SkillPoints.TryGetValue(Data.ID, out int rank) ? rank : 0;
        }

        /// <summary>Adds this skill's permanent bonuses at a given rank to the registry totals.</summary>
        public virtual void AddEffects(int rank, Dictionary<string, float> totals)
        {
            foreach (var effect in Data.Effects) EffectRegistry.Add(totals, effect.EffectID, effect.Get(rank));
        }

        /// <summary>Adds the bonuses of this skill's active buff to the registry totals.</summary>
        public virtual void AddBuffEffects(int rank, int stacks, Dictionary<string, float> totals)
        {
            foreach (var effect in Data.BuffEffects) EffectRegistry.Add(totals, effect.EffectID, effect.Get(rank) * stacks);
        }
    }

    /// <summary>Layout of the embedded Classes.json.</summary>
    internal sealed class ClassesFile
    {
        public List<ClassData> Classes = new List<ClassData>();
        public List<ClassSkillData> Skills = new List<ClassSkillData>();
    }

    /// <summary>One class of the choice screen: emblem, colour, texts and its three paths.</summary>
    internal sealed class ClassData
    {
        public string ID;
        /// <summary>Rune sprite of the emblem ("Rune_" + Emblem in the window prefabs).</summary>
        public string Emblem;
        public string Color;
        /// <summary>False: shown locked (padlock and chain) in the choice grid.</summary>
        public bool Pickable;
        public string NameKey, RoleKey, DescriptionKey;
        public List<ClassPathData> Paths = new List<ClassPathData>();

        public ClassPathData Path(string pathID) => Paths.Find(p => p.ID == pathID);
    }

    /// <summary>One path of a class: colour, texts and the five gauges of the choice screen (1..5).</summary>
    internal sealed class ClassPathData
    {
        public string ID;
        public string Color;
        public string NameKey, DescriptionKey;
        /// <summary>Melee, ranged, survival, area damage, difficulty.</summary>
        public int[] Gauges;
    }

    /// <summary>
    /// Spending rules of the trees: only the bottom row is open at first; 5 points in a row open the next row of
    /// that path; the chosen path's ultimate is learned by itself with 5 points in its top row, which opens every
    /// row of every path. A point can only be taken back when it closes no row (or ultimate) still in use.
    /// </summary>
    internal static class ClassRules
    {
        public const int RowNeed = 5;

        static int Rank(ClassProgress p, ClassSkill s) => p.SkillPoints.TryGetValue(s.ID, out int r) ? r : 0;

        public static IEnumerable<ClassSkill> PathSkills(string classID, string pathID) =>
            ClassSkills.All.Values.Where(s => s.Data.ClassID == classID && s.Data.PathID == pathID);

        public static int RowSum(ClassProgress p, string classID, string pathID, int row) =>
            PathSkills(classID, pathID).Where(s => s.Data.Kind == ClassSkillKind.Normal && s.Data.Cell.y == row).Sum(s => Rank(p, s));

        /// <summary>The chosen path's ultimate, learned once its top row holds 5 points.</summary>
        public static bool UltimateLearned(ClassProgress p, string classID) =>
            p.ChosenPathID != null && RowSum(p, classID, p.ChosenPathID, 3) >= RowNeed;

        public static bool UltimateLearned(ClassProgress p, string classID, string pathID) => p.ChosenPathID == pathID && UltimateLearned(p, classID);

        public static bool RowOpen(ClassProgress p, string classID, string pathID, int row) =>
            p.ChosenPathID != null && (row == 0 || UltimateLearned(p, classID) || RowSum(p, classID, pathID, row - 1) >= RowNeed);

        public static bool CanAdd(ClassProgress p, ClassSkill s) =>
            s.Data.Kind == ClassSkillKind.Normal && RowOpen(p, s.Data.ClassID, s.Data.PathID, s.Data.Cell.y) && p.AvailablePoints > 0 && Rank(p, s) < s.Data.MaxRank;

        public static bool CanRemove(ClassProgress p, ClassSkill s)
        {
            if (p.ChosenPathID == null || s.Data.Kind != ClassSkillKind.Normal || Rank(p, s) == 0) return false;
            string c = s.Data.ClassID, path = s.Data.PathID; int row = s.Data.Cell.y;
            bool ult = UltimateLearned(p, c, path);
            if (UltimateLearned(p, c)) return !(ult && row == 3 && RowSum(p, c, path, 3) - 1 < RowNeed);
            if (row < 3 && RowSum(p, c, path, row) - 1 < RowNeed && PathSkills(c, path).Any(o => o.Data.Kind == ClassSkillKind.Normal && o.Data.Cell.y > row && Rank(p, o) > 0)) return false;
            return true;
        }

        public static bool Add(ClassProgress p, ClassSkill s)
        {
            if (!CanAdd(p, s)) return false;
            p.SkillPoints[s.ID] = Rank(p, s) + 1; p.AvailablePoints--;
            return true;
        }

        public static bool Remove(ClassProgress p, ClassSkill s)
        {
            if (!CanRemove(p, s)) return false;
            int r = Rank(p, s) - 1;
            if (r > 0) p.SkillPoints[s.ID] = r; else p.SkillPoints.Remove(s.ID);
            p.AvailablePoints++;
            return true;
        }

        /// <summary>Every point back and no path chosen, so another one can be picked.</summary>
        public static void ResetAll(ClassProgress p)
        {
            p.SkillPoints.Clear(); p.ChosenPathID = null;
            p.AvailablePoints = ClassProgress.TotalPointsAtLevel(p.Level);
        }
    }

    /// <summary>
    /// Every class skill, built once from the embedded Classes.json and found by its ID.
    /// </summary>
    internal static class ClassSkills
    {
        private const string Resource = "Overhaul.Assets.Classes.json";

        /// <summary>Skill ID -> subclass to build for skills with their own behaviour.</summary>
        private static readonly Dictionary<string, Func<ClassSkillData, ClassSkill>> Behaviours =
            new Dictionary<string, Func<ClassSkillData, ClassSkill>>();

        private static Dictionary<string, ClassSkill> all;
        private static List<ClassData> classes;

        /// <summary>Classes of the choice screen, in grid order.</summary>
        public static List<ClassData> Classes { get { if (all == null) all = Load(); return classes; } }

        public static ClassData GetClass(string classID) => classID == null ? null : Classes.Find(c => c.ID == classID);

        /// <summary>Registers the class bonuses as a source of the effect registry.</summary>
        public static void Initialize() => EffectRegistry.Register(AddBonuses);

        /// <summary>Effect source: permanent bonuses of the active class, then active buffs.</summary>
        private static void AddBonuses(Player player, Dictionary<string, float> totals)
        {
            var character = OverhaulCharacter.Get(player);
            if (character == null || !character.Ready || character.InvalidClassSave) return;
            var classes = character.Classes;
            var progress = classes.Active;
            if (progress == null) return;

            foreach (var points in progress.SkillPoints)
            {
                var skill = Get(points.Key);
                if (skill != null && skill.Data.ClassID == classes.ActiveClassID) skill.AddEffects(points.Value, totals);
            }
            foreach (var buff in classes.Buffs.Values)
            {
                int rank = buff.Skill.GetRank(classes);
                if (rank > 0) buff.Skill.AddBuffEffects(rank, buff.Stacks, totals);
            }
        }

        /// <summary>Skill ID -> skill. Loaded on first use.</summary>
        public static Dictionary<string, ClassSkill> All => all ?? (all = Load());

        /// <summary>Skill with this ID, or null when it does not exist.</summary>
        public static ClassSkill Get(string skillID) =>
            skillID != null && All.TryGetValue(skillID, out var skill) ? skill : null;

        private static Dictionary<string, ClassSkill> Load()
        {
            string json = Jotunn.Utils.AssetUtils.LoadTextFromResources(Resource, typeof(ClassSkills).Assembly)
                ?? throw new InvalidOperationException("Missing embedded " + Resource);
            var file = JsonConvert.DeserializeObject<ClassesFile>(json);
            classes = file.Classes ?? new List<ClassData>();

            var skills = new Dictionary<string, ClassSkill>();
            foreach (var data in file.Skills)
            {
                if (string.IsNullOrEmpty(data.ID) || skills.ContainsKey(data.ID))
                    throw new InvalidOperationException("Classes.json: missing or duplicate skill ID '" + data.ID + "'");
                skills[data.ID] = Behaviours.TryGetValue(data.ID, out var build) ? build(data) : new ClassSkill(data);
            }
            return skills;
        }
    }
}
