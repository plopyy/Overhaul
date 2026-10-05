using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Storage
{
    internal static class DeviceHooks
    {
        [HarmonyPatch]
        private static class Attach
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var type in new[] { typeof(Smelter), typeof(CookingStation), typeof(Fermenter), typeof(Fireplace), typeof(ItemStand), typeof(ArmorStand) }) yield return AccessTools.Method(type, "Awake");
                foreach (var type in new[] { typeof(ShieldGenerator), typeof(Catapult), typeof(OfferingBowl) }) yield return AccessTools.Method(type, "Start");
            }
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Component __instance) => DeviceStore.Attach(__instance);
        }
        [HarmonyPatch(typeof(Player), "Interact")]
        private static class Open
        {
            private static bool Prefix(Player __instance, GameObject go, bool hold)
            {
                var store = DeviceStore.At(go);
                if (!store || !store.Ready) return true;
                if (store.Open(__instance, hold)) __instance.DoInteractAnimation(store.gameObject);
                return false;
            }
        }
        [HarmonyPatch]
        private static class Hover
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var type in new[] { typeof(Container), typeof(Switch), typeof(CookingStation), typeof(Fermenter), typeof(Fireplace), typeof(ItemStand) }) yield return AccessTools.Method(type, "GetHoverText");
            }
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(Component __instance, ref string __result)
            {
                var store = DeviceStore.At(__instance.gameObject); if (!store || !store.Ready) return true;
                __result = store.Hover(); return false;
            }
        }
        [HarmonyPatch]
        private static class Use
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var type in new[] { typeof(Switch), typeof(CookingStation), typeof(Fermenter), typeof(Fireplace), typeof(ItemStand) }) yield return AccessTools.Method(type, "UseItem");
            }
            private static bool Prefix(Component __instance, Humanoid user, ref bool __result)
            {
                var store = DeviceStore.At(__instance.gameObject); if (!store || !store.Ready) return true;
                __result = store.Open(user, false); return false;
            }
        }
        [HarmonyPatch]
        private static class Destroy
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var type in new[] { typeof(Smelter), typeof(CookingStation), typeof(Fermenter), typeof(ItemStand), typeof(ArmorStand) }) yield return AccessTools.Method(type, "OnDestroyed");
            }
            private static bool Prefix(Component __instance)
            {
                var store = DeviceStore.Of(__instance); if (!store || !store.Ready) return true;
                store.DropAll(); return false;
            }
        }
        [HarmonyPatch]
        private static class FuelChanged
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(Smelter), "SetFuel");
                yield return AccessTools.Method(typeof(CookingStation), "SetFuel");
                yield return AccessTools.Method(typeof(ShieldGenerator), "RPC_SetFuel");
                foreach (var name in new[] { "UpdateFireplace", "RPC_AddFuel", "RPC_AddFuelAmount", "RPC_SetFuelAmount" }) yield return AccessTools.Method(typeof(Fireplace), name);
            }
            private static void Postfix(Component __instance) { var store = DeviceStore.Of(__instance); (store?.Adapter as FuelInventoryAdapter)?.PullFuel(); }
        }
        [HarmonyPatch(typeof(Smelter), "GetQueueSize")]
        private static class QueueSize
        {
            private static bool Prefix(Smelter __instance, ref int __result)
            {
                var s = DeviceStore.Of(__instance); if (!s || !s.Ready || !(s.Adapter is SmelterInventory adapter) || !adapter.HasMaterials) return true;
                __result = s.Items.CountItems(null, -1, false); return false;
            }
        }
        [HarmonyPatch(typeof(Smelter), "GetQueuedOre")]
        private static class QueuedOre
        {
            private static bool Prefix(Smelter __instance, ref string __result)
            {
                var s = DeviceStore.Of(__instance); if (!s || !s.Ready || !(s.Adapter is SmelterInventory adapter) || !adapter.HasMaterials) return true;
                __result = adapter.Next()?.m_dropPrefab?.name ?? ""; return false;
            }
        }
        [HarmonyPatch(typeof(Smelter), "RemoveOneOre")]
        private static class ConsumeOre
        {
            private static bool Prefix(Smelter __instance)
            {
                var s = DeviceStore.Of(__instance); if (!s || !s.Ready || !(s.Adapter is SmelterInventory adapter) || !adapter.HasMaterials) return true;
                var item = adapter.Next();
                if (s.Owner && item != null) s.Change(() => { s.Data.Set(ZDOVars.s_cheatedQueued, item.m_cheated); s.Items.RemoveItem(item, 1); });
                return false;
            }
        }
        [HarmonyPatch(typeof(Smelter), "QueueOre")]
        private static class AddOre
        {
            private static bool Prefix(Smelter __instance, string name, bool cheated)
            {
                var s = DeviceStore.Of(__instance); if (!s || !s.Ready || !(s.Adapter is SmelterInventory adapter) || !adapter.HasMaterials) return true;
                if (s.Owner) s.Items.AddItem(DeviceStore.Item(name, 1, cheated));
                return false;
            }
        }
        [HarmonyPatch(typeof(CookingStation), "UpdateCooking")]
        private static class Cook
        {
            private static void Postfix(CookingStation __instance)
            {
                var s = DeviceStore.Of(__instance); if (s && s.Ready && s.Owner) ((CookingInventory)s.Adapter).Advance();
            }
        }
        [HarmonyPatch(typeof(Fermenter), "DelayedTap")]
        private static class Tap
        {
            private static bool Prefix(Fermenter __instance)
            {
                var s = DeviceStore.Of(__instance); return !s || !s.Ready || s.Owner && s.Working.NrOfItems() > 0;
            }
            private static void Postfix(Fermenter __instance)
            {
                var s = DeviceStore.Of(__instance); if (s && s.Ready && s.Owner) s.Change(() => s.Working.RemoveAll());
            }
        }
        [HarmonyPatch]
        private static class AttachmentChanged
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var type in new[] { typeof(ItemStand), typeof(ArmorStand) })
                    foreach (var name in new[] { "UpdateAttach", "DropItem", "RPC_DestroyAttachment" }) yield return AccessTools.Method(type, name);
            }
            private static void Postfix(Component __instance)
            {
                var s = DeviceStore.Of(__instance);
                if (s?.Adapter is StandInventory stand) stand.Pull();
                if (s?.Adapter is ArmorInventory armor) armor.Pull();
            }
        }
        [HarmonyPatch(typeof(Catapult), "OnLoadPointUse")]
        private static class LoadCatapult
        {
            private static void Prefix(Catapult __instance, ItemDrop.ItemData item, out ItemDrop.ItemData __state)
            { __state = item?.Clone(); if (__state != null) __state.m_stack = Math.Min(__state.m_stack, __instance.m_maxLoadStack); }
            private static void Postfix(Catapult __instance, bool __result, ItemDrop.ItemData __state)
            {
                if (!__result || __state == null) return;
                var s = DeviceStore.Of(__instance); if (s && s.Ready) ((CatapultInventory)s.Adapter).Loaded(__state);
            }
        }
        [HarmonyPatch(typeof(Catapult), "Shoot")]
        private static class ShootCatapult
        {
            private static bool Prefix(Catapult __instance)
            {
                var s = DeviceStore.Of(__instance); if (!s || !s.Ready || !((CatapultInventory)s.Adapter).PendingOwnership) return true;
                if (!s.Owner) { __instance.Invoke("Shoot", .1f); return false; }
                ((CatapultInventory)s.Adapter).PendingOwnership = false; s.Changed(); return true;
            }
        }
        [HarmonyPatch(typeof(Catapult), "Release")]
        private static class ReleaseCatapult
        {
            private static void Postfix(Catapult __instance)
            {
                var s = DeviceStore.Of(__instance); if (s && s.Ready && s.Owner) s.Change(() => s.Items.RemoveAll());
            }
        }

        // Offerings have no waiting queue: move the accepted resources through
        // their internal inventory at the original, successful consumption point.
        internal static void ConsumeOffering(Inventory source, string name, int amount, int quality, bool worldLevel, OfferingBowl bowl)
        {
            var s = DeviceStore.Of(bowl);
            if (!s || !s.Ready) { source.RemoveItem(name, amount, quality, worldLevel); return; }
            s.Change(() =>
            {
                while (amount > 0)
                {
                    var item = source.GetItem(name, quality, worldLevel); if (item == null) break;
                    int take = Math.Min(item.m_stack, amount);
                    var unit = item.Clone(); unit.m_stack = take;
                    DeviceStore.ImportItem(s.Items, unit);
                    source.RemoveItem(item, take);
                    s.Items.RemoveItem(name, take, quality, false);
                    amount -= take;
                }
            });
        }
        [HarmonyPatch]
        private static class OfferingConsumption
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(OfferingBowl), "UseItem");
                yield return AccessTools.Method(typeof(OfferingBowl), "RPC_RemoveBossSpawnInventoryItems");
            }
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                var remove = AccessTools.Method(typeof(Inventory), "RemoveItem", new[] { typeof(string), typeof(int), typeof(int), typeof(bool) });
                foreach (var instruction in code)
                {
                    if (instruction.Calls(remove))
                    {
                        var load = new CodeInstruction(OpCodes.Ldarg_0); load.labels.AddRange(instruction.labels); load.blocks.AddRange(instruction.blocks);
                        yield return load;
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DeviceHooks), nameof(ConsumeOffering)));
                    }
                    else yield return instruction;
                }
            }
        }
    }
}
