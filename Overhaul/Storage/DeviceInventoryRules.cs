using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Storage
{
    internal static class DeviceInventoryRules
    {
        internal sealed class Rule { internal DeviceStore Store; internal bool Fuel; }
        private static readonly ConditionalWeakTable<Inventory, Rule> Rules = new ConditionalWeakTable<Inventory, Rule>();
        internal static void Register(Inventory inventory, DeviceStore store, bool fuel) => Rules.Add(inventory, new Rule { Store = store, Fuel = fuel });
        internal static void Unregister(Inventory inventory) => Rules.Remove(inventory);
        internal static Rule Get(Inventory inventory) => inventory != null && Rules.TryGetValue(inventory, out var rule) && rule.Store ? rule : null;
        private static Rule Active(Inventory inventory)
        {
            var rule = Get(inventory);
            return rule != null && rule.Store.Changing == 0 ? rule : null;
        }
        internal static bool Allows(Inventory inventory, ItemDrop.ItemData item, Vector2i? pos = null)
        {
            var rule = Active(inventory);
            if (rule == null) return true;
            if (pos.HasValue && (pos.Value.x < 0 || pos.Value.y < 0 || pos.Value.x >= (rule.Fuel ? 3 : rule.Store.Adapter.Width) || pos.Value.y >= (rule.Fuel ? 2 : rule.Store.Adapter.Height))) return false;
            return rule.Store.Adapter.Accepts(item, rule.Fuel, pos);
        }
        internal static Vector2i Destination(Inventory inventory, ItemDrop.ItemData item)
        {
            var rule = Get(inventory);
            if (rule == null) return new Vector2i(-1, -1);
            int width = rule.Fuel ? 3 : rule.Store.Adapter.Width, height = rule.Fuel ? 2 : rule.Store.Adapter.Height;
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                var pos = new Vector2i(x, y); var existing = inventory.GetItemAt(x, y);
                if (Allows(inventory, item, pos) && (existing == null || !rule.Store.Adapter.SingleItems && existing.IsSameType(item) && existing.m_quality == item.m_quality && existing.m_stack < existing.m_shared.m_maxStackSize)) return pos;
            }
            return new Vector2i(-1, -1);
        }
        private static ItemDrop.ItemData Resolve(object[] args)
        {
            var item = args.OfType<ItemDrop.ItemData>().FirstOrDefault(); if (item != null) return item;
            var prefab = args.FirstOrDefault() as GameObject;
            if (!prefab && ObjectDB.instance)
            {
                if (args.FirstOrDefault() is string name) prefab = ObjectDB.instance.GetItemPrefab(name);
                else if (args.FirstOrDefault() is int hash) prefab = ObjectDB.instance.GetItemPrefab(hash);
            }
            return prefab ? prefab.GetComponent<ItemDrop>()?.m_itemData : null;
        }
        [HarmonyPatch]
        private static class Insert
        {
            private static IEnumerable<MethodBase> TargetMethods() => typeof(Inventory).GetMethods(AccessTools.all)
                .Where(m => (m.Name == "AddItem" || m.Name == "CanAddItem") && m.ReturnType == typeof(bool));
            private static bool Prefix(Inventory __instance, object[] __args, ref bool __result)
            {
                if (Active(__instance) == null || Allows(__instance, Resolve(__args))) return true;
                __result = false; return false;
            }
        }
        [HarmonyPatch]
        private static class InsertName
        {
            private static IEnumerable<MethodBase> TargetMethods() => typeof(Inventory).GetMethods(AccessTools.all).Where(m => m.Name == "AddItem" && m.ReturnType == typeof(ItemDrop.ItemData));
            private static bool Prefix(Inventory __instance, object[] __args, ref ItemDrop.ItemData __result)
            {
                if (Active(__instance) == null || Allows(__instance, Resolve(__args))) return true;
                __result = null; return false;
            }
        }
        [HarmonyPatch(typeof(Inventory), "AddItem", typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool))]
        private static class InsertPosition
        {
            private static bool Prefix(Inventory __instance, ItemDrop.ItemData item, ref int amount, int x, int y, ref bool __result)
            {
                var rule = Active(__instance); if (rule == null) return true;
                if (!Allows(__instance, item, new Vector2i(x, y))) { __result = false; return false; }
                if (rule.Store.Adapter.SingleItems)
                {
                    if (__instance.GetItemAt(x, y) != null) { __result = false; return false; }
                    amount = Math.Min(1, amount);
                }
                return true;
            }
        }
        [HarmonyPatch(typeof(Inventory), "AddItem", typeof(ItemDrop.ItemData))]
        private static class InsertSingle
        {
            private static bool Prefix(Inventory __instance, ItemDrop.ItemData item, ref bool __result)
            {
                var rule = Active(__instance); if (rule == null || !rule.Store.Adapter.SingleItems) return true;
                var pos = Destination(__instance, item);
                if (pos.x >= 0) __instance.AddItem(item, 1, pos.x, pos.y, false);
                __result = item.m_stack == 0; return false;
            }
        }
        [HarmonyPatch(typeof(Inventory), "AddItem", typeof(ItemDrop.ItemData), typeof(Vector2i))]
        private static class InsertSinglePosition
        {
            private static bool Prefix(Inventory __instance, ItemDrop.ItemData item, Vector2i pos, ref bool __result)
            {
                var rule = Active(__instance); if (rule == null) return true;
                if (!Allows(__instance, item, pos)) { __result = false; return false; }
                if (!rule.Store.Adapter.SingleItems) return true;
                __instance.AddItem(item, 1, pos.x, pos.y, false); __result = item.m_stack == 0; return false;
            }
        }
        [HarmonyPatch(typeof(InventoryGrid), "DropItem")]
        private static class Drop
        {
            private static bool Prefix(InventoryGrid __instance, Inventory fromInventory, ItemDrop.ItemData item, ref int amount, Vector2i pos, ref bool __result)
            {
                var destination = __instance.GetInventory(); var target = Get(destination); var source = Get(fromInventory);
                if (target != null && !target.Store.Owner || source != null && !source.Store.Owner || !Allows(destination, item, pos)) { __result = false; return false; }
                if (target != null && target.Store.Adapter.SingleItems) amount = Math.Min(1, amount);
                var displaced = destination.GetItemAt(pos.x, pos.y);
                bool swaps = displaced != null && displaced != item && item.m_stack == amount && (!displaced.IsSameType(item) || item.m_shared.m_maxStackSize == 1);
                if (swaps && !Allows(fromInventory, displaced, item.m_gridPos)) { __result = false; return false; }
                return true;
            }
        }
    }
}
