using System.Collections.Generic;

namespace AugaUnity
{
    // What a crafting station actually offers, read from ObjectDB recipes: the upgrade tab and the
    // repair button are only shown at stations whose recipes produce upgradable / repairable items
    // (not at the preparation table, cauldron, mead ketill...).
    public static class StationRecipes
    {
        private struct Offer { public bool Upgrades, Repairs; }

        private static readonly Dictionary<string, Offer> Cache = new Dictionary<string, Offer>();
        private static ObjectDB _db;
        private static int _recipeCount = -1;

        public static bool OffersUpgrades(CraftingStation station) => !station || station.m_upgrader || Get(station).Upgrades;
        public static bool OffersRepairs(CraftingStation station) => !station || Get(station).Repairs;

        private static Offer Get(CraftingStation station)
        {
            var db = ObjectDB.instance;
            if (!db) return new Offer { Upgrades = true, Repairs = true };
            if (db != _db || db.m_recipes.Count != _recipeCount)
            {
                Cache.Clear();
                _db = db;
                _recipeCount = db.m_recipes.Count;
            }
            if (Cache.TryGetValue(station.m_name, out var offer)) return offer;

            foreach (var recipe in db.m_recipes)
            {
                if (!recipe || !recipe.m_item) continue;
                var required = recipe.m_craftingStation ? recipe.m_craftingStation : recipe.m_repairStation;
                if (!required || required.m_name != station.m_name) continue;
                var shared = recipe.m_item.m_itemData.m_shared;
                offer.Upgrades |= shared.m_maxQuality > 1;
                offer.Repairs |= shared.m_canBeReparied;
            }
            Cache[station.m_name] = offer;
            return offer;
        }
    }
}
