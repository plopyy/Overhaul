using System;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace Overhaul
{
    // Epic Loot draws rarity backgrounds by transpiling the native InventoryGrid.UpdateGui, which the
    // Auga grid replaces entirely. Call its public API after every grid update instead (no assembly
    // reference: Epic Loot stays optional). The call is idempotent, also for empty cells.
    internal static class EpicLootVisuals
    {
        internal const string Guid = "randyknapp.mods.epicloot";
        internal static bool Loaded => Chainloader.PluginInfos.ContainsKey(Guid);
        private static Func<GameObject, GameObject, ItemDrop.ItemData, bool, bool> apply;

        [HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.UpdateGui))]
        private static class GridBackgrounds
        {
            private static bool Prepare()
            {
                if (!Loaded) return false;
                MethodInfo method = Type.GetType("EpicLoot.API, EpicLoot")?.GetMethod("ApplyMagicItemBackground", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(GameObject), typeof(GameObject), typeof(ItemDrop.ItemData), typeof(bool) }, null);
                if (method == null) { Utility.Log.LogWarning("Epic Loot : API.ApplyMagicItemBackground introuvable, pas de fond de rarete dans l'inventaire"); return false; }
                apply = (Func<GameObject, GameObject, ItemDrop.ItemData, bool, bool>)Delegate.CreateDelegate(typeof(Func<GameObject, GameObject, ItemDrop.ItemData, bool, bool>), method);
                return true;
            }

            private static void Postfix(InventoryGrid __instance)
            {
                Inventory inventory = __instance.GetInventory();
                if (inventory == null) return;
                foreach (var element in __instance.m_elements)
                {
                    if (!element || !element.m_equiped) continue;
                    ItemDrop.ItemData item = element.m_used ? inventory.GetItemAt(element.Position.x, element.Position.y) : null;
                    try { apply(element.gameObject, element.m_equiped.gameObject, item, true); }
                    catch (Exception e) { Utility.Log.LogWarning("Epic Loot : fond de rarete impossible : " + e.Message); apply = (a, b, c, d) => false; return; }
                }
            }
        }
    }
}
