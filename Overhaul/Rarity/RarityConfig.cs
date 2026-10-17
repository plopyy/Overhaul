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
        internal readonly Dictionary<string, LootListDef> LootLists = new Dictionary<string, LootListDef>(StringComparer.OrdinalIgnoreCase);
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
            else AddEnchantSections(path);
            try { Current = Local = Parse(File.ReadAllText(path)); }
            catch (Exception e) { Utility.Log.LogError("RaritySystem.cfg unreadable: " + e.Message); }
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
                        data.Catalysts[id] = Get(v, "Rarity", null);
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
                if (data.Rarity(catalyst.Value) == null) Utility.Log.LogWarning("RaritySystem.cfg: unknown rarity " + catalyst.Value + " for catalyst " + catalyst.Key);
            }
            foreach (var list in data.LootLists.Values)
            {
                foreach (string item in list.Items) if (!ObjectDB.instance.GetItemPrefab(item)) Utility.Log.LogWarning("RaritySystem.cfg: unknown item " + item + " in loot list " + list.Id);
                if (list.Weights.Length != data.Rarities.Count) Utility.Log.LogWarning("RaritySystem.cfg: loot list " + list.Id + " has " + list.Weights.Length + " rarity weights for " + data.Rarities.Count + " rarities");
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
