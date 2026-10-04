using HarmonyLib;

namespace Overhaul.Persistence
{
    internal static class InventoryMoveUI
    {
        private static InventoryMoveRpc Client => InventoryMoveGame.Client;
        private static bool Active => Client?.Controller != null && !Client.Controller.Closed;
        private static int Bag(Inventory inventory)
        {
            if (Player.m_localPlayer && inventory == Player.m_localPlayer.GetInventory()) return 0;
            if (Client.Container && inventory == Client.Container.GetInventory()) return 1;
            return -1;
        }
        private static bool Move(InventoryMoveKind kind, Inventory source, Inventory destination, ItemDrop.ItemData item, Vector2i pos, int amount)
        {
            int from = Bag(source), to = Bag(destination);
            if (from < 0 || to < 0 || Client.Controller.Busy || !Player.m_localPlayer || Player.m_localPlayer.IsTeleporting()) return false;
            bool sent = Client.Controller.Move(kind, from, to, item?.m_gridPos.x ?? 0, item?.m_gridPos.y ?? 0, pos.x, pos.y, amount);
            if (sent) PlayerEquipmentGame.MoveAnimation(Client.Controller.Pending,item,pos);
            return sent;
        }

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnSelectedItem))]
        private static class Select
        {
            [HarmonyPriority(Priority.First + 100)]
            private static bool Prefix(InventoryGui __instance, InventoryGrid grid, ItemDrop.ItemData item, Vector2i pos, InventoryGrid.Modifier mod)
            {
                if (!Active) return true;
                if (Client.Controller.Busy) return false;
                if (__instance.m_dragGo && __instance.m_dragItem != null)
                {
                    if (__instance.m_dragInventory == grid.GetInventory() && __instance.m_dragItem.m_gridPos == pos)
                    { __instance.SetupDragItem(null, null, 1); return false; }
                    Move(InventoryMoveKind.Slot, __instance.m_dragInventory, grid.GetInventory(), __instance.m_dragItem, pos, __instance.m_dragAmount);
                    return false;
                }
                if (item != null && mod == InventoryGrid.Modifier.Move && __instance.m_currentContainer)
                {
                    var source = grid.GetInventory();
                    var destination = source == Player.m_localPlayer.GetInventory() ? __instance.m_currentContainer.GetInventory() : Player.m_localPlayer.GetInventory();
                    Move(InventoryMoveKind.Quick, source, destination, item, default(Vector2i), item.m_stack); return false;
                }
                return true; // Selection and split dialog do not change the inventory.
            }
        }
        [HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.DropItem))]
        private static class Grid
        {
            [HarmonyPriority(Priority.First + 100)]
            private static bool Prefix(InventoryGrid __instance, Inventory fromInventory, ItemDrop.ItemData item, int amount, Vector2i pos, ref bool __result)
            {
                if (!Active) return true;
                __result = false;
                Move(InventoryMoveKind.Slot, fromInventory, __instance.GetInventory(), item, pos, amount); return false;
            }
        }
        [HarmonyPatch(typeof(EquipmentAndQuickSlots.AmmoSlots), nameof(EquipmentAndQuickSlots.AmmoSlots.Insert))]
        private static class Ammo
        {
            [HarmonyPriority(Priority.First + 100)]
            private static bool Prefix(Player player, Inventory source, ItemDrop.ItemData item, ref bool __result)
            {
                if (!Active || player != Player.m_localPlayer) return true;
                if (source == player.GetInventory() && EquipmentAndQuickSlots.Slots.GetItemSlot(item)?.IsAmmoSlot == true) return true;
                __result = item != null && EquipmentAndQuickSlots.AmmoSlots.IsAmmo(item) &&
                    Move(InventoryMoveKind.Ammo, source, player.GetInventory(), item, default(Vector2i), item.m_stack);
                return false;
            }
        }
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnTakeAll))]
        private static class TakeAll
        {
            [HarmonyPriority(Priority.First + 100)]
            private static bool Prefix(InventoryGui __instance)
            {
                if (!Active) return true;
                if (__instance.m_currentContainer) Move(InventoryMoveKind.TakeAll, __instance.m_currentContainer.GetInventory(), Player.m_localPlayer.GetInventory(), null, default(Vector2i), 1);
                return false;
            }
        }
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnStackAll))]
        private static class StackAll
        {
            [HarmonyPriority(Priority.First + 100)]
            private static bool Prefix(InventoryGui __instance)
            {
                if (!Active) return true;
                if (__instance.m_currentContainer) Move(InventoryMoveKind.StackAll, Player.m_localPlayer.GetInventory(), __instance.m_currentContainer.GetInventory(), null, default(Vector2i), 1);
                return false;
            }
        }
        [HarmonyPatch(typeof(Container), nameof(Container.Interact))]
        private static class Open
        {
            [HarmonyPriority(Priority.First + 100)]
            private static bool Prefix(Container __instance, Humanoid character, bool hold, bool alt, ref bool __result)
            {
                if (!Active || character != Player.m_localPlayer) return true;
                __result = !hold && Client.Open(__instance, alt ? InventoryMoveKind.StackAll : (InventoryMoveKind?)null); return false;
            }
        }
        [HarmonyPatch(typeof(Container), nameof(Container.TakeAll))]
        private static class HoverTake
        {
            [HarmonyPriority(Priority.First + 100)]
            private static bool Prefix(Container __instance, ref bool __result)
            { if (!Active) return true; __result = Client.Open(__instance, InventoryMoveKind.TakeAll); return false; }
        }
        [HarmonyPatch(typeof(Container), nameof(Container.StackAll))]
        private static class HoverStack
        {
            [HarmonyPriority(Priority.First + 100)]
            private static bool Prefix(Container __instance)
            { if (!Active) return true; Client.Open(__instance, InventoryMoveKind.StackAll); return false; }
        }
        // The native GUI uses IsOwner only to decide whether to display the open container.
        // ZNetView ownership stays on the server, including Container.Save and SetInUse checks.
        [HarmonyPatch(typeof(Container), nameof(Container.IsOwner))]
        private static class ViewOwner
        {
            private static void Postfix(Container __instance, ref bool __result)
            { if (Active && Client.ManagedView(__instance) && Client.Controller.ContainerId != 0) __result = true; }
        }
        [HarmonyPatch(typeof(Container), nameof(Container.Load))]
        private static class ReadOnlyView
        {
            private static bool Prefix(Container __instance, ref bool __result)
            {
                if (!Active || !Client.ManagedView(__instance) || ZNet.instance.IsServer()) return true;
                __result = false; return false;
            }
        }
        [HarmonyPatch(typeof(Container), nameof(Container.Save))]
        private static class ServerWrites
        {
            private static bool Prefix(Container __instance) => !Active || !Client.ManagedView(__instance) || ZNet.instance.IsServer();
        }
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
        private static class Close
        {
            private static void Postfix() { if (Active) Client.CloseContainer(); }
        }
        // The host's GUI closing must not clear the in-use state of other viewers.
        [HarmonyPatch(typeof(Container), nameof(Container.SetInUse))]
        private static class SharedUse
        {
            private static bool Prefix(Container __instance) => !InventoryMoveGame.SharedView(__instance);
        }
        [HarmonyPatch(typeof(AugaUnity.AugaInventorySorter), nameof(AugaUnity.AugaInventorySorter.Sort))]
        private static class Sort
        {
            [HarmonyPriority(Priority.First + 100)]
            private static bool Prefix(Inventory inventory, ref bool __result)
            {
                if (!Active) return true;
                __result = false; int bag = Bag(inventory);
                if (bag >= 0 && !Client.Controller.Busy) Client.Controller.Move(InventoryMoveKind.Sort, bag, bag, 0, 0, 0, 0, 1);
                return false;
            }
        }
    }
}
