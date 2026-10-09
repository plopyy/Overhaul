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
        // Health, stamina and eitr gained once by the native trinket burst, given as maximum values instead.
        internal static readonly System.Collections.Generic.Dictionary<string, Vector3> MaxBonus = new System.Collections.Generic.Dictionary<string, Vector3>();

        [HarmonyPatch(typeof(Player), nameof(Player.GetTotalFoodValue))]
        private static class MaxValues
        {
            private static void Postfix(Player __instance, ref float hp, ref float stamina, ref float eitr)
            {
                if (MaxBonus.Count == 0 || __instance.GetInventory() == null) return;
                foreach (ItemDrop.ItemData item in __instance.GetInventory().GetEquippedItems())
                {
                    StatusEffect effect = item.m_shared.m_equipStatusEffect;
                    if (effect && MaxBonus.TryGetValue(effect.name, out Vector3 bonus)) { hp += bonus.x; stamina += bonus.y; eitr += bonus.z; }
                }
            }
        }

        [HarmonyPatch(typeof(SE_Stats), nameof(SE_Stats.GetTooltipString))]
        private static class MaxValuesTooltip
        {
            private static void Postfix(SE_Stats __instance, ref string __result)
            {
                if (!MaxBonus.TryGetValue(__instance.name, out Vector3 bonus)) return;
                if (bonus.x > 0) __result += "\n$overhaul_trinket_max_health: <color=orange>+" + bonus.x.ToString("0") + "</color>";
                if (bonus.y > 0) __result += "\n$overhaul_trinket_max_stamina: <color=orange>+" + bonus.y.ToString("0") + "</color>";
                if (bonus.z > 0) __result += "\n$overhaul_trinket_max_eitr: <color=orange>+" + bonus.z.ToString("0") + "</color>";
            }
        }

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
                        // A one-off gain made sense for a short burst; worn permanently it becomes a maximum bonus.
                        if (permanent is SE_Stats stats && (stats.m_healthUpFront > 0 || stats.m_staminaUpFront > 0 || stats.m_eitrUpFront > 0))
                        {
                            MaxBonus[permanent.name] = new Vector3(stats.m_healthUpFront, stats.m_staminaUpFront, stats.m_eitrUpFront);
                            stats.m_healthUpFront = 0; stats.m_staminaUpFront = 0; stats.m_eitrUpFront = 0;
                        }
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
