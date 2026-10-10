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
            RarityConfig.Changed += () => { Biomes.Clear(); Effects.Effects.Invalidate(); };
        }

        // Item prefab name -> biome order, rebuilt when the rarity file changes.
        private static readonly Dictionary<string, int> Biomes = new Dictionary<string, int>(StringComparer.Ordinal);

        // The biome listing the item, else the strongest biome among its recipe materials, else the first one.
        internal static int Biome(ItemDrop.ItemData item)
        {
            string prefab = item?.m_dropPrefab ? item.m_dropPrefab.name : null;
            if (prefab == null) return 0;
            if (Biomes.TryGetValue(prefab, out int order)) return order;
            var biomes = RarityConfig.Current.Biomes;
            var forced = biomes.FirstOrDefault(b => b.Items.Contains(prefab));
            if (forced != null) order = forced.Order;
            else
            {
                order = 0;
                Recipe recipe = ObjectDB.instance ? ObjectDB.instance.GetRecipe(item) : null;
                if (recipe != null && recipe.m_resources != null)
                    foreach (var requirement in recipe.m_resources)
                    {
                        if (!requirement.m_resItem) continue;
                        string material = requirement.m_resItem.gameObject.name;
                        foreach (var biome in biomes) if (biome.Order > order && biome.Materials.Contains(material)) order = biome.Order;
                    }
            }
            if (ObjectDB.instance) Biomes[prefab] = order;
            return order;
        }

        // Distinct enchantments allowed for the rarity (RarityMin at most as strong) and with a known effect;
        // lines left without a candidate stay empty.
        internal static string[] Roll(RarityDef rarity)
        {
            var data = RarityConfig.Current;
            var pool = data.Enchants.Values.Where(e => e.Id != ItemRarity.EmptyEnchant
                && (data.Rarity(e.RarityMin)?.Order ?? int.MaxValue) <= rarity.Order
                && Effects.EffectConfig.Current.Effects.ContainsKey(e.Effect ?? "")).ToList();
            var lines = new string[rarity.EnchantCount];
            for (int i = 0; i < lines.Length; i++)
            {
                if (pool.Count == 0) { lines[i] = ItemRarity.EmptyEnchant; continue; }
                int pick = UnityEngine.Random.Range(0, pool.Count);
                lines[i] = pool[pick].Id;
                pool.RemoveAt(pick);
            }
            return lines;
        }

        // The item's enchantments with their effect and value, empty lines left out.
        internal static IEnumerable<(EnchantDef Enchant, Effects.EffectDef Effect, float Value)> Of(ItemDrop.ItemData item)
        {
            int biome = -1;
            foreach (string id in ItemRarity.Enchants(item))
            {
                if (id == ItemRarity.EmptyEnchant || !RarityConfig.Current.Enchants.TryGetValue(id, out var enchant)) continue;
                if (!Effects.EffectConfig.Current.Effects.TryGetValue(enchant.Effect ?? "", out var effect)) continue;
                if (biome < 0) biome = Biome(item);
                yield return (enchant, effect, enchant.Value(biome));
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
