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
