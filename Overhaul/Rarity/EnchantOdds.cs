using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Overhaul.Rarity
{
    // Enchanting odds: a trophy's tier comes from the creature dropping it (MonsterCategories.cfg: biome and rank). A normal
    // creature gives its biome's tier (Biome sections order, Meadows = 1), captains and elites CaptainTiers/EliteTiers more,
    // bosses BossTiers more, capped at the last EnchantTier section. A trophy several creatures drop takes the best tier.
    internal static class EnchantOdds
    {
        internal sealed class Source { internal string Creature, Biome, Rank; internal int Tier; }
        static Dictionary<string, Source> trophies;
        static RarityData built;

        // Trophy prefab name -> its best source, rebuilt when the rarity file changes.
        static Dictionary<string, Source> Trophies()
        {
            if (trophies != null && built == RarityConfig.Current) return trophies;
            var map = new Dictionary<string, Source>(StringComparer.Ordinal);
            built = RarityConfig.Current;
            if (!ZNetScene.instance) return trophies = map;
            foreach (var rule in Leveling.LevelingConfig.Current.Monsters)
            {
                var creature = ZNetScene.instance.GetPrefab(rule.Key); var drops = creature ? creature.GetComponent<CharacterDrop>() : null;
                if (!drops) continue;
                int tier = Tier(rule.Value.Biome, rule.Value.Category);
                if (tier <= 0) continue;
                foreach (var drop in drops.m_drops)
                {
                    var item = drop.m_prefab ? drop.m_prefab.GetComponent<ItemDrop>() : null;
                    if (!item || item.m_itemData.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Trophy) continue;
                    if (map.TryGetValue(drop.m_prefab.name, out var known) && known.Tier >= tier) continue;
                    map[drop.m_prefab.name] = new Source { Creature = rule.Key, Biome = rule.Value.Biome, Rank = rule.Value.Category, Tier = tier };
                }
            }
            return trophies = map;
        }

        static int Tier(string biome, string rank)
        {
            var data = RarityConfig.Current;
            var def = data.Biomes.FirstOrDefault(b => string.Equals(b.Id, biome, StringComparison.OrdinalIgnoreCase));
            if (def == null || data.EnchantTiers.Count == 0) return 0;
            int tier = def.Order + 1;
            if (string.Equals(rank, "Captain", StringComparison.OrdinalIgnoreCase)) tier += data.CaptainTiers;
            else if (string.Equals(rank, "Elite", StringComparison.OrdinalIgnoreCase)) tier += data.EliteTiers;
            else if (string.Equals(rank, "Boss", StringComparison.OrdinalIgnoreCase)) tier += data.BossTiers;
            return Mathf.Min(tier, data.EnchantTiers.Keys.Max());
        }

        internal static Source Of(ItemDrop.ItemData trophy)
        {
            if (trophy == null || trophy.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Trophy) return null;
            string prefab = trophy.m_dropPrefab ? trophy.m_dropPrefab.name : Enchantments.Prefab(trophy);
            return prefab != null && Trophies().TryGetValue(prefab, out var source) ? source : null;
        }

        // Odds in percent per rarity (Rarity order) for the tier; the nearest lower tier stands in for a missing one.
        internal static float[] Odds(int tier)
        {
            var data = RarityConfig.Current; float[] weights = null;
            foreach (var pair in data.EnchantTiers) if (pair.Key <= tier) weights = pair.Value;
            if (weights == null) return new float[data.Rarities.Count];
            var odds = new float[data.Rarities.Count]; float total = 0;
            for (int i = 0; i < odds.Length; i++) total += odds[i] = i < weights.Length && i > 0 ? Mathf.Max(0, weights[i]) : 0;
            if (total > 0) for (int i = 0; i < odds.Length; i++) odds[i] = odds[i] * 100f / total;
            return odds;
        }

        internal static RarityDef Roll(int tier)
        {
            var odds = Odds(tier); var rarities = RarityConfig.Current.Rarities;
            float pick = UnityEngine.Random.Range(0f, odds.Sum());
            for (int i = 0; i < odds.Length; i++) { if (odds[i] <= 0) continue; pick -= odds[i]; if (pick <= 0) return rarities[i]; }
            for (int i = odds.Length - 1; i >= 0; i--) if (odds[i] > 0) return rarities[i];
            return null;
        }
    }
}
