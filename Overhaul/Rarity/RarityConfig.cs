using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Rarity
{
    // RaritySystem.cfg drives the whole system: rarities (in file order, weakest first), enchantments, biomes,
    // catalysts, loot lists and the creatures using them. Any section added to the file is taken into account.
    // The server's file is the reference: it is sent to every client when its character spawns.
    internal sealed class RarityDef
    {
        internal string Id, NameKey, Aura;
        internal int Order, EnchantCount, CatalystSlots;
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

    // An enchantment gives its effect (an Effects.cfg id, which also gives its name) with one value per biome of the item, in biome order;
    // a single value applies to every biome. It can be rolled on items of RarityMin or any stronger rarity.
    internal sealed class EnchantDef
    {
        internal string Id, RarityMin, Effect;
        internal float[] Values = new float[0];
        internal float Value(int biome) => Values.Length == 0 ? 0 : Values[Mathf.Clamp(biome, 0, Values.Length - 1)];
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
        internal readonly Dictionary<string, EnchantDef> Enchants = new Dictionary<string, EnchantDef>(StringComparer.OrdinalIgnoreCase);
        internal readonly List<BiomeDef> Biomes = new List<BiomeDef>();
        internal readonly Dictionary<string, string> Catalysts = new Dictionary<string, string>(StringComparer.Ordinal);
        internal readonly Dictionary<string, LootListDef> LootLists = new Dictionary<string, LootListDef>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, List<string>> MobLoots = new Dictionary<string, List<string>>(StringComparer.Ordinal);
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
            try { Current = Local = Parse(File.ReadAllText(path)); }
            catch (Exception e) { Utility.Log.LogError("RaritySystem.cfg unreadable: " + e.Message); }
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
            foreach (string raw in text.Split('\n'))
            {
                // Only whole lines are comments: colours are written with a #.
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                if (line.StartsWith("[") && line.EndsWith("]") && !line.Contains("="))
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sections.Add(new KeyValuePair<string, Dictionary<string, string>>(line.Substring(1, line.Length - 2).Trim(), current));
                    continue;
                }
                int equal = line.IndexOf('=');
                if (current != null && equal > 0) current[line.Substring(0, equal).Trim()] = line.Substring(equal + 1).Trim();
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
                            EnchantCount = Int(v, "EnchantBonusCount"), CatalystSlots = Int(v, "CatalystSlot"),
                            BaseStatBonus = Float(v, "BaseStatBonus"),
                            CanBeSet = Get(v, "CanBeSet", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
                            Colors = Get(v, "UIColor", "#ffffff").Split(',').Select(c => ColorUtility.TryParseHtmlString(c.Trim(), out Color color) ? color : Color.white).ToArray()
                        });
                        break;
                    case "enchant":
                        data.Enchants[id] = new EnchantDef
                        {
                            Id = id, RarityMin = Get(v, "RarityMin", null),
                            Effect = Get(v, "Effect", Get(v, "Effet", "")),
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
            foreach (var enchant in data.Enchants.Values)
            {
                if (enchant.Id == ItemRarity.EmptyEnchant) continue;
                if (data.Rarity(enchant.RarityMin) == null) Utility.Log.LogWarning("RaritySystem.cfg: unknown rarity " + enchant.RarityMin + " for enchantment " + enchant.Id);
                if (!Effects.EffectConfig.Current.Effects.ContainsKey(enchant.Effect ?? "")) Utility.Log.LogWarning("RaritySystem.cfg: enchantment " + enchant.Id + " has no known effect (" + enchant.Effect + "), it is never rolled");
            }
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
