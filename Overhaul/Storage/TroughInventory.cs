using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Storage
{
    internal static class TroughInventory
    {
        private sealed class Owner { internal TroughContainer Trough; internal int Loading; }
        private static readonly ConditionalWeakTable<Inventory, Owner> Owners = new ConditionalWeakTable<Inventory, Owner>();
        private static readonly HashSet<string> Foods = new HashSet<string>();
        private static bool foodsReady;

        internal static void ClearFoods() { Foods.Clear(); foodsReady = false; }
        internal static void RegisterAnimal(MonsterAI animal)
        {
            if (!animal || !animal.GetComponent<Tameable>() || animal.m_consumeItems == null) return;
            foreach (var food in animal.m_consumeItems)
                if (food && food.m_itemData?.m_shared != null) Foods.Add(food.m_itemData.m_shared.m_name);
        }
        internal static void RefreshFoods()
        {
            if (!ZNetScene.instance) return;
            Foods.Clear();
            foreach (var prefab in ZNetScene.instance.m_prefabs)
                if (prefab) RegisterAnimal(prefab.GetComponent<MonsterAI>());
            foodsReady = true;
            foreach (var trough in FeedingTrough.Loaded) if (trough) trough.RefreshVisual();
        }
        internal static bool IsFood(ItemDrop.ItemData item)
        {
            if (!foodsReady) RefreshFoods();
            return item?.m_shared != null && Foods.Contains(item.m_shared.m_name);
        }
        internal static void Register(Inventory inventory, TroughContainer trough)
        { Owners.Remove(inventory); Owners.Add(inventory, new Owner { Trough = trough }); }
        internal static void Unregister(Inventory inventory) => Owners.Remove(inventory);
        internal static bool Allows(Inventory inventory, ItemDrop.ItemData item) => inventory == null ||
            !Owners.TryGetValue(inventory, out var owner) || owner.Loading > 0 || IsFood(item);

        [HarmonyPatch(typeof(Container), "Awake")]
        internal static class Attach
        {
            private static void Postfix(Container __instance) => __instance.GetComponent<TroughContainer>()?.AttachInventory();
        }

        [HarmonyPatch]
        internal static class LoadScope
        {
            private static IEnumerable<MethodBase> TargetMethods() => typeof(Inventory).GetMethods(AccessTools.all)
                .Where(m => m.Name == "Load" || m.Name == "LoadOld");
            private static void Prefix(Inventory __instance, out Owner __state)
            {
                Owners.TryGetValue(__instance, out __state);
                if (__state != null) __state.Loading++;
            }
            private static void Finalizer(Owner __state)
            {
                if (__state == null) return;
                __state.Loading--;
                if (__state.Trough) __state.Trough.RefreshVisual();
            }
        }

        private static ItemDrop.ItemData Resolve(object[] args)
        {
            var item = args.OfType<ItemDrop.ItemData>().FirstOrDefault();
            if (item != null) return item;
            GameObject prefab = args[0] as GameObject;
            if (!prefab && ObjectDB.instance)
            {
                if (args[0] is string name) prefab = ObjectDB.instance.GetItemPrefab(name);
                else if (args[0] is int hash) prefab = ObjectDB.instance.GetItemPrefab(hash);
            }
            return prefab ? prefab.GetComponent<ItemDrop>()?.m_itemData : null;
        }
        private static bool Restricted(Inventory inventory) => Owners.TryGetValue(inventory, out var owner) && owner.Loading == 0;

        [HarmonyPatch]
        internal static class Insert
        {
            private static IEnumerable<MethodBase> TargetMethods() => typeof(Inventory).GetMethods(AccessTools.all)
                .Where(m => (m.Name == "AddItem" || m.Name == "CanAddItem") && m.ReturnType == typeof(bool));
            private static bool Prefix(Inventory __instance, object[] __args, ref bool __result)
            {
                if (!Restricted(__instance) || IsFood(Resolve(__args))) return true;
                __result = false; return false;
            }
        }
        [HarmonyPatch]
        internal static class InsertByName
        {
            private static IEnumerable<MethodBase> TargetMethods() => typeof(Inventory).GetMethods(AccessTools.all)
                .Where(m => m.Name == "AddItem" && m.ReturnType == typeof(ItemDrop.ItemData));
            private static bool Prefix(Inventory __instance, object[] __args, ref ItemDrop.ItemData __result)
            {
                if (!Restricted(__instance) || IsFood(Resolve(__args))) return true;
                __result = null; return false;
            }
        }

        [HarmonyPatch(typeof(InventoryGrid), "DropItem")]
        internal static class Drop
        {
            private static bool Prefix(InventoryGrid __instance, Inventory fromInventory, ItemDrop.ItemData item,
                int amount, Vector2i pos, ref bool __result)
            {
                var destination = __instance.GetInventory();
                if (!Allows(destination, item)) { __result = false; return false; }
                var displaced = destination.GetItemAt(pos.x, pos.y);
                bool swaps = displaced != null && displaced != item && item.m_stack == amount &&
                    (!displaced.IsSameType(item) || item.m_shared.m_maxStackSize == 1);
                // Native swaps remove the source first: reject both directions before any mutation.
                if (swaps && !Allows(fromInventory, displaced)) { __result = false; return false; }
                return true;
            }
        }
    }
}
