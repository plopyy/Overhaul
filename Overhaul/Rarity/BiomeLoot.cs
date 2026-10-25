using System;
using System.Linq;
using UnityEngine;

namespace Overhaul.Rarity
{
    // Gear loot by biome: a creature (biome and rank from MonsterCategories.cfg) may drop one item of its biome's list
    // (Biome sections), with a rarity drawn from its rank's BiomeLoot section only, whatever the biome. A biome boss drops an
    // item of the next biome; the boss of the last biome uses FinalBoss and drops from its own. Dungeon guardians drop no gear:
    // their chest always holds one item of the dungeon's biome (Chest section).
    internal static class BiomeLoot
    {
        internal const string Chest = "Chest", FinalBoss = "FinalBoss";

        internal static void Creature(string creature, Vector3 center)
        {
            if (creature == null || !Leveling.LevelingConfig.Current.Monsters.TryGetValue(creature, out var rule)) return;
            var biomes = RarityConfig.Current.Biomes;
            int biome = biomes.FindIndex(b => string.Equals(b.Id, rule.Biome, StringComparison.OrdinalIgnoreCase));
            if (biome < 0) return;
            string rank = rule.Category;
            if (string.Equals(rank, "Boss", StringComparison.OrdinalIgnoreCase))
            {
                if (biome + 1 < biomes.Count) biome++;
                else rank = FinalBoss;
            }
            if (!RarityConfig.Current.BiomeLoots.TryGetValue(rank, out var loot) || UnityEngine.Random.Range(0f, 100f) >= loot.Chance) return;
            for (int i = 0; i < loot.Quantity; i++)
            {
                string item = Pick(biomes[biome].Id);
                if (item != null) ItemRarity.Drop(item, ItemRarity.Roll(loot.Weights), center);
            }
        }

        // One gear item of the biome with a rarity of the Chest section, or null.
        internal static ItemDrop.ItemData ChestItem(string biome)
        {
            if (!RarityConfig.Current.BiomeLoots.TryGetValue(Chest, out var loot)) return null;
            string name = Pick(biome);
            GameObject prefab = name != null ? ObjectDB.instance.GetItemPrefab(name) : null;
            if (!prefab) return null;
            var item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
            item.m_dropPrefab = prefab;
            item.m_stack = 1;
            item.m_worldLevel = (byte)Game.m_worldLevel;
            item.m_durability = item.GetMaxDurability();
            ItemRarity.Assign(item, ItemRarity.Roll(loot.Weights));
            return item;
        }

        // A random item of the biome's list among those the game knows, the BiomeLootExceptions left out.
        static string Pick(string biome)
        {
            var def = RarityConfig.Current.Biomes.FirstOrDefault(b => string.Equals(b.Id, biome, StringComparison.OrdinalIgnoreCase));
            if (def == null || !ObjectDB.instance) return null;
            var items = def.Items.Where(i => !RarityConfig.Current.BiomeLootExceptions.Contains(i) && ObjectDB.instance.GetItemPrefab(i)).ToArray();
            return items.Length > 0 ? items[UnityEngine.Random.Range(0, items.Length)] : null;
        }
    }
}
