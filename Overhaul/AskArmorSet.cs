using UnityEngine;
using HarmonyLib;

namespace Overhaul
{
    // Ask armour set (Ashlands medium): keeps its +10 % pierce, gains the lox set's sneak and bow skill bonuses,
    // and loses its stamina cost reductions. The lox values are read from the game's own lox set effect.
    internal static class AskArmorSet
    {
        internal static void Apply(ObjectDB database)
        {
            if (!database) return;
            var ask = SetEffect(database, "ArmorAshlandsMediumChest") as SE_Stats;
            var lox = SetEffect(database, "ArmorLoxChest") as SE_Stats;
            if (!ask || !lox)
            {
                if (database.m_items.Count > 0) Utility.Log.LogWarning("Ask set: Ask or lox set effect not found, Ask set unchanged");
                return;
            }
            ask.m_runStaminaDrainModifier = 0f;
            ask.m_jumpStaminaUseModifier = 0f;
            ask.m_attackStaminaUseModifier = 0f;
            ask.m_skillLevel = lox.m_skillLevel;
            ask.m_skillLevelModifier = lox.m_skillLevelModifier;
            ask.m_skillLevel2 = lox.m_skillLevel2;
            ask.m_skillLevelModifier2 = lox.m_skillLevelModifier2;
        }

        private static StatusEffect SetEffect(ObjectDB database, string item)
        {
            var prefab = database.GetItemPrefab(item);
            return prefab ? prefab.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_setStatusEffect : null;
        }

        [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
        private static class OnAwake { private static void Postfix(ObjectDB __instance) => Apply(__instance); }

        [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
        private static class OnCopy { private static void Postfix(ObjectDB __instance) => Apply(__instance); }
    }
}
