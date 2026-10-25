using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Overhaul.Rarity
{
    // Gear loot by biome (creatures without MobLoot sections): a creature (biome and rank from MonsterCategories.cfg) may drop one item of its biome's list
    // (Biome sections), with a rarity drawn from its rank's BiomeLoot section only, whatever the biome. A biome boss drops an
    // item of the next biome; the boss of the last biome uses FinalBoss and drops from its own. Dungeon guardians drop no gear:
    // their chest always holds one item of the dungeon's biome (Chest section).
    internal static class BiomeLoot
    {
        internal const string Chest = "Chest", FinalBoss = "FinalBoss";

        internal static void Creature(string creature, Vector3 center)
        {
            var loot = Rank(creature, out int biome);
            if (loot == null || UnityEngine.Random.Range(0f, 100f) >= loot.Chance) return;
            for (int i = 0; i < loot.Quantity; i++)
            {
                string item = Pick(RarityConfig.Current.Biomes[biome].Id);
                if (item != null) ItemRarity.Drop(item, ItemRarity.Roll(loot.Weights), center);
            }
        }

        // The BiomeLoot section of the creature's rank (chance, quantity, rarity weights) and the biome it drops from, or null.
        // MobLoot creatures drop their own set with these odds too.
        internal static LootListDef Rank(string creature, out int biome)
        {
            biome = -1;
            if (creature == null || !Leveling.LevelingConfig.Current.Monsters.TryGetValue(creature, out var rule)) return null;
            var biomes = RarityConfig.Current.Biomes;
            biome = biomes.FindIndex(b => string.Equals(b.Id, rule.Biome, StringComparison.OrdinalIgnoreCase));
            if (biome < 0) return null;
            string rank = rule.Category;
            if (string.Equals(rank, "Boss", StringComparison.OrdinalIgnoreCase))
            {
                if (biome + 1 < biomes.Count) biome++;
                else rank = FinalBoss;
            }
            return RarityConfig.Current.BiomeLoots.TryGetValue(rank, out var loot) ? loot : null;
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

        // A random item of the biome's list among those the game knows, leaving out the BiomeLootExceptions and the gear of the
        // loot lists that MobLoot creatures drop (a troll set only comes from trolls).
        static string Pick(string biome)
        {
            var data = RarityConfig.Current;
            var def = data.Biomes.FirstOrDefault(b => string.Equals(b.Id, biome, StringComparison.OrdinalIgnoreCase));
            if (def == null || !ObjectDB.instance) return null;
            var mobGear = new HashSet<string>(data.MobLoots.Values.SelectMany(l => l).Where(data.LootLists.ContainsKey).SelectMany(l => data.LootLists[l].Items), StringComparer.Ordinal);
            var items = def.Items.Where(i => !data.BiomeLootExceptions.Contains(i) && !mobGear.Contains(i) && ObjectDB.instance.GetItemPrefab(i)).ToArray();
            return items.Length > 0 ? items[UnityEngine.Random.Range(0, items.Length)] : null;
        }
    }
}
