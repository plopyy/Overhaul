using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Overhaul
{
    // Trinkets give their bonus permanently while worn, like any other equipment, instead of only for a
    // few seconds when the adrenaline bar is full. Adrenaline is disabled: trinkets no longer fill it.
    // With Epic Loot, whose enchantments are built on adrenaline, the bar stays as a resource for them.
    internal static class PermanentTrinkets
    {
        internal static void Apply(ObjectDB database)
        {
            if (!database) return;
            foreach (GameObject prefab in database.m_items)
            {
                ItemDrop.ItemData.SharedData shared = prefab ? prefab.GetComponent<ItemDrop>()?.m_itemData?.m_shared : null;
                if (shared == null || shared.m_itemType != ItemDrop.ItemData.ItemType.Trinket) continue;
                StatusEffect bonus = shared.m_fullAdrenalineSE;
                if (bonus)
                {
                    if (shared.m_equipStatusEffect)
                        Utility.Log.LogWarning("Trinket " + prefab.name + " : deja un effet d'equipement, bonus d'adrenaline " + bonus.name + " non rendu permanent");
                    else
                    {
                        // Same name as the native effect: same icon, same network hash for the other players.
                        StatusEffect permanent = Object.Instantiate(bonus);
                        permanent.name = bonus.name;
                        permanent.m_ttl = 0;
                        shared.m_equipStatusEffect = permanent;
                    }
                    shared.m_fullAdrenalineSE = null;
                }
                if (!EpicLootVisuals.Loaded) shared.m_maxAdrenaline = 0; // No adrenaline bar.
            }
        }

        [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
        private static class OnAwake { private static void Postfix(ObjectDB __instance) => Apply(__instance); }

        [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
        private static class OnCopy { private static void Postfix(ObjectDB __instance) => Apply(__instance); }

        [HarmonyPatch]
        private static class NoAdrenaline
        {
            private static MethodBase Target() => AccessTools.Method(typeof(Player), "AddAdrenaline");
            private static bool Prepare()
            {
                if (EpicLootVisuals.Loaded) return false;
                if (Target() != null) return true;
                Utility.Log.LogWarning("Adrenaline : AddAdrenaline introuvable, adrenaline non desactivee");
                return false;
            }
            private static MethodBase TargetMethod() => Target();
            private static bool Prefix() => false;
        }
    }
}
