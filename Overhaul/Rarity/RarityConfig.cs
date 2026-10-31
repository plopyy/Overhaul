using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Rarity
{
    // RaritySystem.cfg drives the whole system: rarities (in file order, weakest first), biomes,
    // catalysts, loot lists and the creatures using them. Any section added to the file is taken into account.
    // The server's file is the reference: it is sent to every client when its character spawns.
    internal sealed class RarityDef
    {
        internal string Id, NameKey, Aura;
        internal int Order, EnchantCount, CatalystSlots, MythicCount;
        internal float BaseStatBonus;
        internal bool CanBeSet;
        internal Color[] Colors = { Color.white };
        internal Color Color => Colors[0];
        // The Catalyst rarity belongs to catalyst items only: never drawn for loot or enchanting.
        internal bool ForCatalysts => string.Equals(Id, ItemRarity.CatalystRarity, StringComparison.OrdinalIgnoreCase);
        // Gradient order: the first colour in the middle, the others alternately on each side (2, 1, 3 for three colours).
        internal Color[] Gradient
        {
            get
            {
                if (Colors.Length < 2) return Colors;
                var left = new List<Color>(); var right = new List<Color>();
                for (int i = 1; i < Colors.Length; i++) (i % 2 == 1 ? left : right).Add(Colors[i]);
                left.Reverse();
                return left.Concat(new[] { Colors[0] }).Concat(right).ToArray();
            }
        }
        // Text in the rarity colour. A multicolour rarity marks the text with a reserved colour that the
        // tooltip's TextGradient replaces with the 45° gradient.
        internal const string GradientMark = "#010203";
        internal string Paint(string text) => "<color=" + (Colors.Length > 1 ? GradientMark : "#" + ColorUtility.ToHtmlStringRGB(Color)) + ">" + text + "</color>";
        internal string Name => RarityConfig.Localize(NameKey);
    }

    // A mythic enchantment: a special power with its own code (Rarity.MythicEffects), rolled on gear of a rarity with
    // MythicEnchantCount, among the ones allowed on its category. Value: one per biome (a single value for every biome),
    // written in the description where it has {0}.
    internal sealed class MythicDef
    {
        internal string Id, NameKey, DescriptionKey;
        internal HashSet<string> ItemTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal float[] Values = new float[0];
        internal float Value(int biome) => Values.Length == 0 ? 0 : Values[Mathf.Clamp(biome, 0, Values.Length - 1)];
        internal string Name => RarityConfig.Localize(NameKey);
        internal string Description(float value) => RarityConfig.Localize(DescriptionKey).Replace("{0}", value.ToString("0.#"));
    }

    // A biome, in progression order, with the gear items belonging to it (the only ones in the rarity system).
    internal sealed class BiomeDef
    {
        internal string Id;
        internal int Order;
        internal HashSet<string> Items = new HashSet<string>(StringComparer.Ordinal);
    }

    // What a catalyst does once socketed in worn gear (Catalyst sections): its effect, fixed values and permanent bonuses.
    internal sealed class CatalystDef
    {
        internal string Id, Effect, Dot;
        internal string[] Items = new string[0];
        internal readonly Dictionary<string, float> Values = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        internal (string Effect, float Value)[] Bonuses = new (string, float)[0];
        internal float Get(string key, float fallback = 0) => Values.TryGetValue(key, out float value) ? value : fallback;
    }

    internal sealed class LootListDef
    {
        internal string Id;
        internal float[] Weights = new float[0];
        internal int Quantity = 1;
        internal float Chance = 100;
        internal string[] Items = new string[0];
    }

    internal sealed class RarityData
    {
        internal readonly List<RarityDef> Rarities = new List<RarityDef>();
        internal readonly List<BiomeDef> Biomes = new List<BiomeDef>();
        internal readonly Dictionary<string, MythicDef> Mythics = new Dictionary<string, MythicDef>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> Catalysts = new Dictionary<string, string>(StringComparer.Ordinal);
        internal readonly Dictionary<string, CatalystDef> CatalystEffects = new Dictionary<string, CatalystDef>(StringComparer.Ordinal);
        internal readonly Dictionary<string, LootListDef> LootLists = new Dictionary<string, LootListDef>(StringComparer.OrdinalIgnoreCase);
        // Gear loot by biome, per rank (Normal, Captain, Elite, Boss, FinalBoss, Chest): chance, quantity and rarity weights.
        internal readonly Dictionary<string, LootListDef> BiomeLoots = new Dictionary<string, LootListDef>(StringComparer.OrdinalIgnoreCase);
        // Gear of the Biome lists that the biome loot never drops (BiomeLootExceptions); loot lists and enchanting still use it.
        internal readonly HashSet<string> BiomeLootExceptions = new HashSet<string>(StringComparer.Ordinal);
        internal readonly Dictionary<string, List<string>> MobLoots = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        // Enchanting table: odds per tier (EnchantTier_N, rarity weights in Rarity order), line change cost, tiers added by rank.
        internal readonly SortedDictionary<int, float[]> EnchantTiers = new SortedDictionary<int, float[]>();
        internal (string Item, int Amount)[] RerollCost = new (string, int)[0];
        internal int CaptainTiers = 1, EliteTiers = 1, BossTiers = 2;
        internal string Text = "";

        internal RarityDef Rarity(string id) => id == null ? null : Rarities.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    internal static class RarityConfig
    {
        internal const string FileName = "RaritySystem.cfg";
        private const string RequestRpc = "Overhaul_RarityRequest", ConfigRpc = "Overhaul_RarityConfig";
        internal static RarityData Local, Current = new RarityData();
        internal static event Action Changed;

        internal static void Initialize()
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(RarityConfig).Assembly.Location), FileName);
            if (!File.Exists(path)) File.WriteAllText(path, Utility.ConfigSections.Defaults(FileName));
            else { RenameOldRarities(path); AddCatalystRarity(path); AddEnchantSections(path); AddBiomeLootSections(path); AddBiomeLootExceptions(path);
                AddAfterLast(path, "[LootList_", "[LootList_AskArmor]"); AddAfterLast(path, "[MobLoot_", "[MobLoot_Asksvin]"); AddCatalystEffects(path); AddAccessories(path);
                // Ricochet Axe's Value was a bounce count before the three-axe throw, now a damage percent.
                OldMythicValue(path, "RicochetAxe", "[2,2,3,3,3,4,4,5]", "[40,45,50,55,60,65,70,80]");
                OldMythicValue(path, "SpectralDagger", "[10,11,12,13,14,15,16,18]", "[35,35,35,35,35,35,35,35]");
                // Prospector now breaks the whole rock; the amber pearl drops precious items.
                OldMythicValue(path, "Prospector", "[10,12,14,16,18,20,22,25]", "[25,25,25,25,25,25,25,25]");
                OldSection(path, "[Catalyst_AmberPearl]", "Effect = FirstStrike\nValue = 10");
                OldLineCounts(path);
                OldSection(path, "[Catalyst_HardAntler]", "Effect = ThunderStrike\nCount = 4\nDamage = 10"); }
            try { Current = Local = Parse(File.ReadAllText(path)); }
            catch (Exception e) { Utility.Log.LogError("RaritySystem.cfg unreadable: " + e.Message); }
        }

        // Files made before the 2.3.0 rarity names: each [Rarity_] section takes its tier's new name, translation key and,
        // when still the old default, colour (see ItemRarity.OldRarities).
        private static void RenameOldRarities(string path)
        {
            string text = File.ReadAllText(path);
            if (!text.Contains("[Rarity_Uncommon]")) return;
            // Old section: its old default colour and the new one of its tier.
            var colours = new Dictionary<string, (string Old, string New)>
                { { "Uncommon", ("#79f776", "#79f776") }, { "Magic", ("#71a5f6", "#71a5f6") }, { "Rare", ("#fad340", "#c43dff") }, { "Epic", ("#c43dff", "#fad340") }, { "Legendary", ("#fd5c3d", "#ff2a2a") } };
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\[Rarity_(\w+)\]([^\[]*)", m =>
            {
                string old = m.Groups[1].Value, id = ItemRarity.NewRarity(old), body = m.Groups[2].Value;
                if (id == old) return m.Value;
                body = System.Text.RegularExpressions.Regex.Replace(body, @"(?m)^Name = overhaul_rarity_\w+", "Name = overhaul_rarity_" + id.ToLowerInvariant());
                body = System.Text.RegularExpressions.Regex.Replace(body, @"(?mi)^UIColor = " + colours[old].Old + @"[ \t]*(?=\r?$)", "UIColor = " + colours[old].New);
                return "[Rarity_" + id + "]" + body;
            });
            File.WriteAllText(path, text);
            Utility.Log.LogInfo("RaritySystem.cfg: rarities renamed (Magic, Rare, Epic, Legendary, Runic)");
        }

        // Files made before the enchanting table get its sections (and only those) from the defaults.
        private static void AddEnchantSections(string path)
        {
            string text = File.ReadAllText(path);
            if (text.Contains("[EnchantTable]")) return;
            string defaults = Utility.ConfigSections.Defaults(FileName);
            int title = defaults.IndexOf("# === Enchanting table ===", StringComparison.Ordinal);
            if (title < 0) return;
            // From the "# ====" line above the title.
            int start = title > 1 ? defaults.LastIndexOf('\n', title - 2) + 1 : 0;
            File.WriteAllText(path, text.TrimEnd() + Environment.NewLine + Environment.NewLine + defaults.Substring(start));
            Utility.Log.LogInfo("RaritySystem.cfg: enchanting table sections added");
        }

        // Files made before the biome loot get its sections from the defaults, before the loot lists.
        private static void AddBiomeLootSections(string path)
        {
            string text = File.ReadAllText(path);
            if (text.Contains("[BiomeLoot_")) return;
            string defaults = Utility.ConfigSections.Defaults(FileName);
            int title = defaults.IndexOf("# === Biome loot ===", StringComparison.Ordinal), next = defaults.IndexOf("# === LootLists ===", StringComparison.Ordinal);
            if (title < 2 || next < title) return;
            // From the "# ====" line above each title.
            int start = defaults.LastIndexOf('\n', title - 2) + 1, end = defaults.LastIndexOf('\n', next - 2) + 1;
            string block = defaults.Substring(start, end - start).TrimEnd();
            int at = text.IndexOf("# === LootLists ===", StringComparison.Ordinal);
            at = at > 1 ? text.LastIndexOf('\n', at - 2) + 1 : text.Length;
            File.WriteAllText(path, text.Substring(0, at).TrimEnd() + Environment.NewLine + Environment.NewLine + block + Environment.NewLine + Environment.NewLine + text.Substring(at).TrimStart('\r', '\n'));
            Utility.Log.LogInfo("RaritySystem.cfg: biome loot sections added");
        }

        // A section of the defaults missing from the file (the Ask set, its Asksvin) goes after the last section of its kind.
        private static void AddAfterLast(string path, string kind, string header)
        {
            string text = File.ReadAllText(path), defaults = Utility.ConfigSections.Defaults(FileName);
            int from = defaults.IndexOf(header, StringComparison.Ordinal), last = text.LastIndexOf(kind, StringComparison.Ordinal);
            if (text.Contains(header) || from < 0 || last < 0) return;
            string block = defaults.Substring(from, NextBlock(defaults, from) - from).TrimEnd();
            int at = NextBlock(text, last);
            File.WriteAllText(path, text.Substring(0, at).TrimEnd() + Environment.NewLine + Environment.NewLine + block + Environment.NewLine + Environment.NewLine + text.Substring(at).TrimStart('\r', '\n'));
            Utility.Log.LogInfo("RaritySystem.cfg: " + header + " added");
        }

        // Files made before the biome loot exceptions get the (empty) section from the defaults, after the last BiomeLoot one.
        // Accessories, enchantable like the rest of the gear: one biome each, never dropped by the biome loot.
        private static readonly (string Biome, string Item)[] Accessories = { ("BlackForest", "BeltStrength"), ("Swamp", "Wishbone"), ("Mistlands", "Demister"), ("Mistlands", "HelmetDverger"), ("Mistlands", "Overhaul_SpiritCirclet") };
        private static void AddAccessories(string path)
        {
            string text = File.ReadAllText(path), before = text;
            foreach (var (biome, item) in Accessories)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(text, @"(?m)^\s+" + item + @"\s*$")) continue;
                var list = System.Text.RegularExpressions.Regex.Match(text, @"\[Biome_" + biome + @"\]\s*Items =[ \t]*\r?\n");
                if (!list.Success) continue;
                text = text.Insert(list.Index + list.Length, "  " + item + Environment.NewLine);
                var exceptions = System.Text.RegularExpressions.Regex.Match(text, @"\[BiomeLootExceptions\]\s*ItemList =[ \t]*([^\r\n]*)");
                if (exceptions.Success) text = text.Insert(exceptions.Index + exceptions.Length, (exceptions.Groups[1].Length > 0 ? ", " : "") + item);
            }
            if (text != before) { File.WriteAllText(path, text); Utility.Log.LogInfo("RaritySystem.cfg: accessories added"); }
        }

        private static void AddBiomeLootExceptions(string path)
        {
            string text = File.ReadAllText(path);
            if (text.Contains("[BiomeLootExceptions]")) return;
            string defaults = Utility.ConfigSections.Defaults(FileName);
            int from = defaults.IndexOf("[BiomeLootExceptions]", StringComparison.Ordinal), last = text.LastIndexOf("[BiomeLoot_", StringComparison.Ordinal);
            if (from < 0 || last < 0) return;
            // With the comment lines just above it.
            while (from > 1) { int line = defaults.LastIndexOf('\n', from - 2) + 1; if (!defaults.Substring(line).StartsWith("#") || defaults.Substring(line).StartsWith("# ===")) break; from = line; }
            string block = defaults.Substring(from, NextBlock(defaults, defaults.IndexOf("[BiomeLootExceptions]", StringComparison.Ordinal)) - from).TrimEnd();
            int at = NextBlock(text, last);
            File.WriteAllText(path, text.Substring(0, at).TrimEnd() + Environment.NewLine + Environment.NewLine + block + Environment.NewLine + Environment.NewLine + text.Substring(at).TrimStart('\r', '\n'));
            Utility.Log.LogInfo("RaritySystem.cfg: biome loot exceptions section added");
        }

        // Files still holding the old default enchantment line counts (Rare 1, Epic 2, Legendary 3, Runic 4) get the new ones
        // (2, 3, 4, 5); a file where any of them was set by hand is left as it is.
        private static void OldLineCounts(string path)
        {
            string text = File.ReadAllText(path);
            var counts = new[] { ("Rare", 1, 2), ("Epic", 2, 3), ("Legendary", 3, 4), ("Runic", 4, 5) };
            var line = new System.Text.RegularExpressions.Regex[counts.Length];
            for (int i = 0; i < counts.Length; i++)
            {
                line[i] = new System.Text.RegularExpressions.Regex(@"(\[Rarity_" + counts[i].Item1 + @"\][^\[]*?EnchantBonusCount\s*=\s*)" + counts[i].Item2 + @"\b");
                if (!line[i].IsMatch(text)) return;
            }
            for (int i = 0; i < counts.Length; i++) text = line[i].Replace(text, "${1}" + counts[i].Item3, 1);
            File.WriteAllText(path, text);
            Utility.Log.LogInfo("RaritySystem.cfg: enchantment lines per rarity updated to the new defaults");
        }

        // A section still holding its old default body (lines given with \n) gets the defaults' new one; one set by hand stays.
        private static void OldSection(string path, string header, string before)
        {
            string text = File.ReadAllText(path), defaults = Utility.ConfigSections.Defaults(FileName);
            int at = text.IndexOf(header, StringComparison.Ordinal), from = defaults.IndexOf(header, StringComparison.Ordinal);
            if (at < 0 || from < 0) return;
            int stop = NextBlock(text, at);
            if (text.Substring(at, stop - at).Replace("\r\n", "\n").Trim() != header + "\n" + before) return;
            string block = defaults.Substring(from, NextBlock(defaults, from) - from).TrimEnd();
            File.WriteAllText(path, text.Substring(0, at) + block + Environment.NewLine + Environment.NewLine + text.Substring(stop).TrimStart('\r', '\n'));
            Utility.Log.LogInfo("RaritySystem.cfg: " + header + " updated to its new default");
        }

        // A mythic whose default Value changed: a file still holding the old default list gets the new one (values set by
        // hand are left as they are).
        private static void OldMythicValue(string path, string mythic, string before, string after)
        {
            string text = File.ReadAllText(path), old = "Value = " + before;
            int at = text.IndexOf("[MythicEnchant_" + mythic + "]", StringComparison.Ordinal);
            if (at < 0) return;
            int stop = NextBlock(text, at), value = text.IndexOf(old, at, StringComparison.Ordinal);
            if (value < 0 || value > stop) return;
            File.WriteAllText(path, text.Substring(0, value) + "Value = " + after + text.Substring(value + old.Length));
            Utility.Log.LogInfo("RaritySystem.cfg: " + mythic + " values updated to the new defaults");
        }

        // Files made before the Catalyst rarity get its section from the defaults, after their last rarity.
        private static void AddCatalystRarity(string path)
        {
            string section = "[Rarity_" + ItemRarity.CatalystRarity + "]";
            string text = File.ReadAllText(path);
            if (text.Contains(section)) return;
            string defaults = Utility.ConfigSections.Defaults(FileName);
            int from = defaults.IndexOf(section, StringComparison.Ordinal);
            if (from < 0) return;
            int to = NextBlock(defaults, from);
            string block = defaults.Substring(from, to - from).TrimEnd();
            int last = text.LastIndexOf("[Rarity_", StringComparison.Ordinal);
            int at = last < 0 ? text.Length : NextBlock(text, last);
            File.WriteAllText(path, text.Substring(0, at).TrimEnd() + Environment.NewLine + Environment.NewLine + block + Environment.NewLine + Environment.NewLine + text.Substring(at).TrimStart('\r', '\n'));
            Utility.Log.LogInfo("RaritySystem.cfg: Catalyst rarity added");
        }

        // Files made before the catalyst effects: every Catalyst section without an effect takes the default one's settings
        // (sections that already have an effect, set by hand, are left as they are).
        private static void AddCatalystEffects(string path)
        {
            string text = File.ReadAllText(path), defaults = Utility.ConfigSections.Defaults(FileName);
            int changed = 0, from = 0;
            while ((from = defaults.IndexOf("[Catalyst_", from, StringComparison.Ordinal)) >= 0)
            {
                int end = NextBlock(defaults, from);
                string block = defaults.Substring(from, end - from).TrimEnd();
                from = end;
                string header = block.Substring(0, block.IndexOf(']') + 1);
                int at = text.IndexOf(header, StringComparison.Ordinal);
                if (at < 0)
                {
                    // A catalyst the file does not list yet (the orange and yellow gems): added after its last catalyst.
                    int last = text.LastIndexOf("[Catalyst_", StringComparison.Ordinal);
                    int place = last < 0 ? text.Length : NextBlock(text, last);
                    text = text.Substring(0, place).TrimEnd() + Environment.NewLine + Environment.NewLine + block + Environment.NewLine + Environment.NewLine + text.Substring(place).TrimStart('\r', '\n');
                    changed++;
                    continue;
                }
                int stop = NextBlock(text, at);
                string current = text.Substring(at, stop - at);
                bool hasEffect = current.Split('\n').Any(l => l.Trim().StartsWith("Effect", StringComparison.Ordinal) && l.Contains('=') && l.Substring(l.IndexOf('=') + 1).Trim().Length > 0);
                if (hasEffect)
                {
                    // Values added to a catalyst since (a new effect of the same catalyst).
                    var known = current.Split('\n').Where(l => l.Contains('=')).Select(l => l.Substring(0, l.IndexOf('=')).Trim()).ToList();
                    var missing = block.Split('\n').Skip(1).Where(l => l.Contains('=') && !known.Contains(l.Substring(0, l.IndexOf('=')).Trim())).Select(l => l.TrimEnd()).ToList();
                    if (missing.Count == 0) continue;
                    int after = text.IndexOf('\n', at) + 1;
                    text = text.Substring(0, after) + string.Join(Environment.NewLine, missing) + Environment.NewLine + text.Substring(after);
                    changed++;
                    continue;
                }
                text = text.Substring(0, at) + block + Environment.NewLine + Environment.NewLine + text.Substring(stop).TrimStart('\r', '\n');
                changed++;
            }
            if (changed == 0) return;
            File.WriteAllText(path, text);
            Utility.Log.LogInfo("RaritySystem.cfg: effects added to " + changed + " catalysts");
        }

        // End of the section starting at index: the next section header or comment line, or the end of the text.
        private static int NextBlock(string text, int index)
        {
            int line = text.IndexOf('\n', index);
            while (line >= 0 && line + 1 < text.Length)
            {
                char c = text[line + 1];
                if (c == '[' || c == '#') return line + 1;
                line = text.IndexOf('\n', line + 1);
            }
            return text.Length;
        }

        private static void Use(RarityData data) { Current = data; Changed?.Invoke(); }

        // Name keys are translation keys, written with or without the leading $.
        internal static string Localize(string key) =>
            string.IsNullOrEmpty(key) ? "" : Localization.instance != null ? Localization.instance.Localize("$" + key.TrimStart('$')) : key;

        // [Section] blocks of key = value lines, in file order.
        internal static List<KeyValuePair<string, Dictionary<string, string>>> Sections(string text)
        {
            var sections = new List<KeyValuePair<string, Dictionary<string, string>>>();
            Dictionary<string, string> current = null;
            string last = null;
            foreach (string raw in text.Split('\n'))
            {
                // Only whole lines are comments: colours are written with a #.
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                if (line.StartsWith("[") && line.EndsWith("]") && !line.Contains("="))
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); last = null;
                    sections.Add(new KeyValuePair<string, Dictionary<string, string>>(line.Substring(1, line.Length - 2).Trim(), current));
                    continue;
                }
                int equal = line.IndexOf('=');
                if (current != null && equal > 0) current[last = line.Substring(0, equal).Trim()] = line.Substring(equal + 1).Trim();
                // A line without "=" continues the previous key's list (one item per line).
                else if (current != null && last != null) current[last] = current[last].Length == 0 ? line : current[last] + ", " + line;
            }
            return sections;
        }

        internal static RarityData Parse(string text)
        {
            var data = new RarityData { Text = text };
            foreach (var section in Sections(text))
            {
                int underscore = section.Key.IndexOf('_');
                string type = underscore > 0 ? section.Key.Substring(0, underscore) : section.Key;
                string id = underscore > 0 ? section.Key.Substring(underscore + 1) : "";
                var v = section.Value;
                switch (type.ToLowerInvariant())
                {
                    case "rarity":
                        data.Rarities.Add(new RarityDef
                        {
                            Id = id, Order = data.Rarities.Count,
                            NameKey = Get(v, "Name", id),
                            Aura = Get(v, "AuraAssetName", null),
                            EnchantCount = Int(v, "EnchantBonusCount"), CatalystSlots = Int(v, "CatalystSlot"), MythicCount = Int(v, "MythicEnchantCount"),
                            BaseStatBonus = Float(v, "BaseStatBonus"),
                            CanBeSet = Get(v, "CanBeSet", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
                            Colors = Get(v, "UIColor", "#ffffff").Split(',').Select(c => ColorUtility.TryParseHtmlString(c.Trim(), out Color color) ? color : Color.white).ToArray()
                        });
                        break;
                    case "mythicenchant":
                        data.Mythics[id] = new MythicDef
                        {
                            Id = id, NameKey = Get(v, "Name", id), DescriptionKey = Get(v, "Description", ""),
                            ItemTypes = Enchantments.Categories(List(Get(v, "ItemTypes", ""))),
                            Values = List(Get(v, "Value", "")).Select(w => ParseFloat(w)).ToArray()
                        };
                        break;
                    case "biome":
                        data.Biomes.Add(new BiomeDef
                        {
                            Id = id, Order = data.Biomes.Count,
                            Items = new HashSet<string>(List(Get(v, "Items", "")), StringComparer.Ordinal)
                        });
                        break;
                    case "catalyst":
                        data.Catalysts[id] = ItemRarity.CatalystRarity;
                        var def = new CatalystDef { Id = id, Effect = Get(v, "Effect", null), Dot = Get(v, "Dot", null) };
                        def.Items = List(Get(v, "Items", ""));
                        foreach (var key in new[] { "Value", "Count", "Damage", "Threshold", "Chance", "Duration", "Cooldown", "Stacks", "Min", "Max", "Chop", "Mine", "NovaChance", "NovaDamage", "NovaRadius", "WeakChance", "WeakValue", "WeakStacks", "WeakDuration", "ShardEvery", "ShardMax", "ShardDamage" })
                            if (v.ContainsKey(key)) def.Values[key] = Float(v, key);
                        // Files made before the frost nova (its description shows these).
                        if (def.Effect == "ColdWalker") foreach (var (key, value) in new[] { ("NovaChance", 15f), ("NovaDamage", 35f), ("NovaRadius", 6f), ("ShardEvery", 3f), ("ShardMax", 5f), ("ShardDamage", 10f) }) if (!def.Values.ContainsKey(key)) def.Values[key] = value;
                        if (def.Effect == "LavaWalker") foreach (var (key, value) in new[] { ("WeakChance", 25f), ("WeakValue", 10f), ("WeakStacks", 5f), ("WeakDuration", 4f) }) if (!def.Values.ContainsKey(key)) def.Values[key] = value;
                        def.Bonuses = List(Get(v, "Bonus", "")).Select(b => b.Split(':')).Where(p => p.Length == 2).Select(p => (p[0].Trim(), ParseFloat(p[1].Trim()))).ToArray();
                        if (def.Effect != null || def.Bonuses.Length > 0) data.CatalystEffects[id] = def;
                        break;
                    case "lootlist":
                        data.LootLists[id] = new LootListDef
                        {
                            Id = id,
                            Weights = List(Get(v, "Rarity", "")).Select(w => ParseFloat(w)).ToArray(),
                            Quantity = Int(v, "Quantity", 1),
                            Chance = Float(v, "LootChance", Float(v, "LootChange", 100)),
                            Items = List(Get(v, "ItemList", ""))
                        };
                        break;
                    case "biomeloot":
                        data.BiomeLoots[id] = new LootListDef
                        {
                            Id = id,
                            Weights = List(Get(v, "Rarity", "")).Select(w => ParseFloat(w)).ToArray(),
                            Quantity = Int(v, "Quantity", 1),
                            Chance = Float(v, "LootChance", 100)
                        };
                        break;
                    case "biomelootexceptions":
                        foreach (string item in List(Get(v, "ItemList", ""))) data.BiomeLootExceptions.Add(item);
                        break;
                    case "enchanttable":
                        data.RerollCost = List(Get(v, "RerollCost", "")).Select(c => c.Split(':')).Where(p => p.Length == 2)
                            .Select(p => (p[0].Trim(), int.TryParse(p[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0)).Where(c => c.Item2 > 0).ToArray();
                        data.CaptainTiers = Int(v, "CaptainTiers", 1); data.EliteTiers = Int(v, "EliteTiers", 1); data.BossTiers = Int(v, "BossTiers", 2);
                        break;
                    case "enchanttier":
                        if (int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tier)) data.EnchantTiers[tier] = List(Get(v, "Rarity", "")).Select(w => ParseFloat(w)).ToArray();
                        break;
                    case "mobloot":
                        if (!data.MobLoots.TryGetValue(id, out var lists)) data.MobLoots[id] = lists = new List<string>();
                        lists.AddRange(List(Get(v, "LootList", "")));
                        break;
                    default:
                        Utility.Log.LogWarning("RaritySystem.cfg: unknown section [" + section.Key + "]");
                        break;
                }
            }
            return data;
        }

        // Names that match nothing in the game are reported once the game data is loaded.
        internal static void Validate(RarityData data)
        {
            if (!ObjectDB.instance || !ZNetScene.instance) return;
            foreach (var catalyst in data.Catalysts)
            {
                if (!ObjectDB.instance.GetItemPrefab(catalyst.Key)) Utility.Log.LogWarning("RaritySystem.cfg: unknown catalyst item " + catalyst.Key);
                if (data.Rarity(catalyst.Value) == null) Utility.Log.LogWarning("RaritySystem.cfg: no [Rarity_" + catalyst.Value + "] section for catalyst " + catalyst.Key);
            }
            foreach (var list in data.LootLists.Values)
            {
                foreach (string item in list.Items) if (!ObjectDB.instance.GetItemPrefab(item)) Utility.Log.LogWarning("RaritySystem.cfg: unknown item " + item + " in loot list " + list.Id);
                int drawn = data.Rarities.Count(r => !r.ForCatalysts);
                if (list.Weights.Length > 0 && list.Weights.Length != drawn) Utility.Log.LogWarning("RaritySystem.cfg: loot list " + list.Id + " has " + list.Weights.Length + " rarity weights for " + drawn + " rarities");
            }
            foreach (string item in data.BiomeLootExceptions)
                if (!ObjectDB.instance.GetItemPrefab(item)) Utility.Log.LogWarning("RaritySystem.cfg: unknown item " + item + " in BiomeLootExceptions");
            foreach (var loot in data.BiomeLoots.Values)
            {
                int drawn = data.Rarities.Count(r => !r.ForCatalysts);
                if (loot.Weights.Length != drawn) Utility.Log.LogWarning("RaritySystem.cfg: biome loot " + loot.Id + " has " + loot.Weights.Length + " rarity weights for " + drawn + " rarities");
            }
            foreach (var mob in data.MobLoots)
            {
                if (!ZNetScene.instance.GetPrefab(mob.Key)) Utility.Log.LogWarning("RaritySystem.cfg: unknown creature " + mob.Key);
                foreach (string list in mob.Value) if (!data.LootLists.ContainsKey(list)) Utility.Log.LogWarning("RaritySystem.cfg: unknown loot list " + list + " for " + mob.Key);
            }
            foreach (var rarity in data.Rarities)
                if (!string.IsNullOrEmpty(rarity.Aura) && !LootAura.Has(rarity.Aura)) Utility.Log.LogWarning("RaritySystem.cfg: unknown aura " + rarity.Aura + " for " + rarity.Id);
            foreach (var effect in Effects.EffectConfig.Current.Effects.Values)
                if (!string.IsNullOrEmpty(effect.EnchantRarityMin) && data.Rarity(effect.EnchantRarityMin) == null)
                    Utility.Log.LogWarning("Effects.cfg: unknown rarity " + effect.EnchantRarityMin + " in EnchantRarityMin of " + effect.Id + ", it is never rolled");
            foreach (var mythic in data.Mythics.Values)
                if (!MythicEffects.Known(mythic.Id)) Utility.Log.LogWarning("RaritySystem.cfg: mythic enchantment " + mythic.Id + " has no code yet, it is never rolled");
            foreach (var biome in data.Biomes)
                foreach (string item in biome.Items) if (!ObjectDB.instance.GetItemPrefab(item)) Utility.Log.LogWarning("RaritySystem.cfg: unknown item " + item + " in biome " + biome.Id);
            BiomeReport.Write();
        }

        // "[a, b]" or "a, b" lists.
        internal static string[] List(string value) => (value ?? "").Trim('[', ']').Split(',').Select(i => i.Trim()).Where(i => i.Length > 0).ToArray();
        internal static string Get(Dictionary<string, string> v, string key, string fallback) => v.TryGetValue(key, out string value) && value.Length > 0 ? value : fallback;
        internal static int Int(Dictionary<string, string> v, string key, int fallback = 0) => int.TryParse(Get(v, key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
        internal static float Float(Dictionary<string, string> v, string key, float fallback = 0) => v.TryGetValue(key, out string value) && value.Length > 0 ? ParseFloat(value, fallback) : fallback;
        internal static float ParseFloat(string value, float fallback = 0) => float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float result) ? result : fallback;

        // Network: the client asks once its character is in the world, the server answers with its file.
        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        private static class Connection
        {
            private static void Postfix(ZNet __instance, ZNetPeer peer)
            {
                if (__instance.IsServer()) peer.m_rpc.Register(RequestRpc, rpc => rpc.Invoke(ConfigRpc, Current.Text));
                else peer.m_rpc.Register<string>(ConfigRpc, (rpc, text) =>
                {
                    try { Use(Parse(text)); Validate(Current); Utility.Log.LogInfo("Rarity system received from the server: " + Current.Rarities.Count + " rarities"); }
                    catch (Exception e) { Utility.Log.LogWarning("Rarity system from the server rejected: " + e.Message); }
                });
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class Spawned
        {
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer || !ZNet.instance) return;
                if (ZNet.instance.IsServer()) { Use(Local ?? Current); Validate(Current); return; }
                ZNet.instance.GetServerRPC()?.Invoke(RequestRpc);
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Awake))]
        private static class LocalRules { private static void Prefix() { if (Local != null) Use(Local); } }
    }
}
