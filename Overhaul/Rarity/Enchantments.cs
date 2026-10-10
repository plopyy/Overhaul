using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Overhaul.Rarity
{
    // Enchantments of an item: rolled with its rarity, valued by the item's biome, and fed to the effect registry
    // while the item is equipped.
    internal static class Enchantments
    {
        internal static void Initialize()
        {
            Effects.Effects.Register(Equipment);
            RarityConfig.Changed += () => { Biomes.Clear(); Prefabs.Clear(); Effects.Effects.Invalidate(); };
        }

        // Item prefab name -> biome order, and item name token -> prefab name, rebuilt when the rarity file changes.
        private static readonly Dictionary<string, int> Biomes = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> Prefabs = new Dictionary<string, string>(StringComparer.Ordinal);

        // The biome listing the item, or -1: an item listed in no biome is left out of the rarity system.
        // prefab: for an ItemDrop prefab's own item data, which has no drop prefab.
        internal static int Biome(ItemDrop.ItemData item, string prefab = null)
        {
            prefab = prefab ?? Prefab(item);
            if (prefab == null) return -1;
            if (Biomes.Count == 0)
                foreach (var biome in RarityConfig.Current.Biomes)
                    foreach (string name in biome.Items)
                        if (!Biomes.ContainsKey(name)) Biomes[name] = biome.Order;
                        else Utility.Log.LogWarning("RaritySystem.cfg: " + name + " is listed in several biomes, the first one is used");
            return Biomes.TryGetValue(prefab, out int order) ? order : -1;
        }

        // Prefab name of an item: its drop prefab, else (item data of a prefab, crafting preview) the item prefab with the same name.
        internal static string Prefab(ItemDrop.ItemData item)
        {
            if (item == null) return null;
            if (item.m_dropPrefab) return item.m_dropPrefab.name;
            if (Prefabs.Count == 0 && ObjectDB.instance)
                foreach (var prefab in ObjectDB.instance.m_items)
                {
                    var drop = prefab ? prefab.GetComponent<ItemDrop>() : null;
                    if (drop && !Prefabs.ContainsKey(drop.m_itemData.m_shared.m_name)) Prefabs[drop.m_itemData.m_shared.m_name] = prefab.name;
                }
            return item.m_shared != null && Prefabs.TryGetValue(item.m_shared.m_name, out string name) ? name : null;
        }

        // Gear categories for EnchantItemTypes. AllWeapons and Armor stand for their whole group.
        private static readonly string[] Known = { "Melee", "Bow", "Staff", "Shield", "Helmet", "Chest", "Legs", "Cape", "Trinket", "Tool" };
        private static readonly Dictionary<string, string[]> Groups = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "AllWeapons", new[] { "Melee", "Bow", "Staff" } },
            { "Armor", new[] { "Helmet", "Chest", "Legs", "Cape" } },
        };

        internal static HashSet<string> Categories(IEnumerable<string> names)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in names)
            {
                if (Groups.TryGetValue(name, out var group)) set.UnionWith(group);
                else if (Known.Contains(name, StringComparer.OrdinalIgnoreCase)) set.Add(name);
                else Utility.Log.LogWarning("Effects.cfg: unknown EnchantItemTypes value " + name + " (" + string.Join(", ", Known.Concat(Groups.Keys)) + ")");
            }
            return set;
        }

        // The gear categories of an item: tools (pickaxes, fishing rod, hammer...), staffs (magic skills), bows and
        // crossbows, melee weapons, shields, the armour slots and trinkets. One-handed axes are both melee weapons and tools.
        internal static string[] Categories(ItemDrop.ItemData item)
        {
            var shared = item.m_shared;
            if (shared.m_skillType == Skills.SkillType.Axes && shared.m_itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon) return new[] { "Melee", "Tool" };
            return new[] { Category(item) };
        }

        private static string Category(ItemDrop.ItemData item)
        {
            var shared = item.m_shared;
            switch (shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.Shield: return "Shield";
                case ItemDrop.ItemData.ItemType.Helmet: return "Helmet";
                case ItemDrop.ItemData.ItemType.Chest: return "Chest";
                case ItemDrop.ItemData.ItemType.Legs: return "Legs";
                case ItemDrop.ItemData.ItemType.Shoulder: return "Cape";
                case ItemDrop.ItemData.ItemType.Trinket: return "Trinket";
                case ItemDrop.ItemData.ItemType.Tool: return "Tool";
            }
            switch (shared.m_skillType)
            {
                case Skills.SkillType.Pickaxes: case Skills.SkillType.Fishing: return "Tool";
                case Skills.SkillType.ElementalMagic: case Skills.SkillType.BloodMagic: return "Staff";
            }
            if (shared.m_itemType == ItemDrop.ItemData.ItemType.Bow) return "Bow";
            // The scythe harvests: it counts as a tool.
            return shared.m_name == "$item_scythe" ? "Tool" : "Melee";
        }

        // Enchantment lines for the rarity: distinct effects of Effects.cfg with EnchantRarityMin at most as strong as the
        // rarity and allowed on the item's category. keep: current lines to keep where they still hold a valid enchantment
        // (only the empty or invalid ones are drawn). Lines left without a candidate stay empty.
        internal static string[] Roll(ItemDrop.ItemData item, RarityDef rarity, string[] keep = null)
        {
            var data = RarityConfig.Current;
            string[] categories = Categories(item);
            var pool = Effects.EffectConfig.Current.Effects.Values.Where(e => e.Enchantment
                && (data.Rarity(e.EnchantRarityMin)?.Order ?? int.MaxValue) <= rarity.Order
                && (e.EnchantItemTypes.Count == 0 || categories.Any(e.EnchantItemTypes.Contains))).ToList();
            var lines = new string[rarity.EnchantCount];
            for (int i = 0; i < lines.Length; i++)
            {
                string kept = keep != null && i < keep.Length ? keep[i] : null;
                var valid = kept == null ? null : pool.FirstOrDefault(e => string.Equals(e.Id, kept, StringComparison.OrdinalIgnoreCase));
                if (valid != null) { lines[i] = valid.Id; pool.Remove(valid); }
            }
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i] != null) continue;
                if (pool.Count == 0) { lines[i] = ItemRarity.EmptyEnchant; continue; }
                int pick = UnityEngine.Random.Range(0, pool.Count);
                lines[i] = pool[pick].Id;
                pool.RemoveAt(pick);
            }
            return lines;
        }

        // Gear whose enchantment lines are empty (made before the enchantments existed, or whose enchantment left
        // Effects.cfg) or do not match its rarity's line count gets the missing lines drawn. Returns whether it changed.
        internal static bool Fill(ItemDrop.ItemData item)
        {
            var rarity = ItemRarity.Of(item);
            if (rarity == null || ItemRarity.IsCatalyst(item, out _) || item.m_customData == null) return false;
            string[] current = ItemRarity.Enchants(item);
            string[] lines = Roll(item, rarity, current);
            if (lines.SequenceEqual(current)) return false;
            if (lines.Length > 0) item.m_customData[ItemRarity.EnchantKey] = string.Join(",", lines);
            else item.m_customData.Remove(ItemRarity.EnchantKey);
            return true;
        }

        // The local player's gear is completed when the character loads and whenever an item is equipped.
        [HarmonyLib.HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class FillOnSpawn
        {
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer) return;
                int count = __instance.GetInventory().GetAllItems().Count(Fill);
                if (count > 0) { Effects.Effects.Invalidate(); Utility.Log.LogInfo("Enchantments completed on " + count + " items"); }
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem))]
        private static class FillOnEquip
        {
            private static void Prefix(Humanoid __instance, ItemDrop.ItemData item) { if (__instance == Player.m_localPlayer && item != null && Fill(item)) Effects.Effects.Invalidate(); }
        }

        // The item's enchantments with their effect and value, empty lines left out.
        internal static IEnumerable<(Effects.EffectDef Effect, float Value)> Of(ItemDrop.ItemData item)
        {
            int biome = -1;
            foreach (string id in ItemRarity.Enchants(item))
            {
                if (id == ItemRarity.EmptyEnchant || !Effects.EffectConfig.Current.Effects.TryGetValue(id, out var effect) || !effect.Enchantment) continue;
                if (biome < 0) biome = Biome(item);
                yield return (effect, effect.EnchantValue(biome));
            }
        }

        // Value of one effect on this very item (Indestructible protects only the item carrying it).
        internal static float ItemValue(ItemDrop.ItemData item, string effect) =>
            Of(item).Where(e => string.Equals(e.Effect.Id, effect, StringComparison.OrdinalIgnoreCase)).Sum(e => e.Value);

        private static void Equipment(Player player, Dictionary<string, float> totals)
        {
            foreach (var item in player.GetInventory().GetEquippedItems())
                foreach (var line in Of(item)) Effects.Effects.Add(totals, line.Effect.Id, line.Value);
        }
    }
}
