using HarmonyLib;
using static EquipmentAndQuickSlots.Slots;

namespace EquipmentAndQuickSlots
{
    internal static class CosmeticEquipment
    {
        internal static bool IsVisualPlayer(Player player) => player &&
            (player == CurrentPlayer || (FejdStartup.instance && FejdStartup.instance.GetPreviewPlayer() == player));
        // Returns true when this click belongs to cosmetics, including a refused
        // removal when the visible bag is full. Never fall through to normal equip.
        internal static bool HandleRightClick(Player player, Inventory inventory, ItemDrop.ItemData item, bool cosmeticTab)
        {
            if (Overhaul.Persistence.PlayerSessionGame.Managed) return Overhaul.Persistence.InventoryMoveUI.Cosmetic(player,inventory,item,cosmeticTab);
            if (player == null || player != CurrentPlayer || inventory != player.GetInventory() || item == null || !inventory.ContainsItem(item)) return false;
            var source = GetItemSlot(item);
            bool removing = source?.IsCosmeticSlot == true;
            Slot target = null;
            if (!removing && cosmeticTab)
                for (int i = 0; i < CosmeticSlotCount; i++)
                    if (slots[CosmeticSlotStartIndex + i].ItemFits(item)) { target = slots[CosmeticSlotStartIndex + i]; break; }
            if (!removing && target == null) return false;
            if (player.IsTeleporting()) return true;

            var resident = target == null ? null : inventory.GetItemAt(target.GridPosition.x, target.GridPosition.y);
            var returnPosition = emptyPosition;
            if (removing || resident != null)
            {
                // A replacement can reuse the clicked item's bag cell, even in a full bag.
                if (!removing && item.m_gridPos.y < VisibleRows) returnPosition = item.m_gridPos;
                else
                    for (int y = 0; y < VisibleRows && returnPosition == emptyPosition; y++)
                        for (int x = 0; x < inventory.GetWidth(); x++)
                            if (inventory.GetItemAt(x, y) == null) { returnPosition = new Vector2i(x, y); break; }
                if (returnPosition == emptyPosition)
                {
                    player.Message(MessageHud.MessageType.Center, "$msg_inventoryfull");
                    return true;
                }
            }
            player.RemoveEquipAction(item);
            if (player.IsItemEquiped(item)) player.UnequipItem(item, false);
            item.m_equipped = false;
            item.m_customData.Remove(customKeyParked);
            PruneLastEquippedSlotFromItem(item);
            if (resident != null)
            {
                player.RemoveEquipAction(resident);
                resident.m_gridPos = returnPosition;
                PruneLastEquippedSlotFromItem(resident);
            }
            item.m_gridPos = removing ? returnPosition : target.GridPosition;
            ClearCachedItems();
            inventory.Changed();
            return true;
        }

        [HarmonyPatch(typeof(InventoryGui), "OnRightClickItem")]
        private static class RightClick
        {
            private static bool Prefix(InventoryGui __instance, InventoryGrid grid, ItemDrop.ItemData item)
            {
                if (__instance.m_dragItem != null) return true;
                return !HandleRightClick(Player.m_localPlayer, grid.GetInventory(), item, Auga.EquipmentPanelBridge.IsCosmeticTab);
            }
        }

        internal static bool IsCosmetic(Player player, ItemDrop.ItemData item)
        {
            if (player == null || player != CurrentPlayer || item == null || !player.GetInventory().ContainsItem(item)) return false;
            return GetItemSlot(item)?.IsCosmeticSlot == true;
        }

        internal static ItemDrop.ItemData Get(Player player, int index)
        {
            if (!IsVisualPlayer(player)) return null;
            var slot = slots[CosmeticSlotStartIndex + index];
            if (slot == null || !slot.IsActive) return null;
            var pos = slot.GridPosition;
            var item = player.GetInventory().GetItemAt(pos.x, pos.y);
            return slot.ItemFits(item) ? item : null;
        }

        internal static void Apply(Player player, VisEquipment visual)
        {
            if (!visual || !IsVisualPlayer(player)) return;
            // Only the owner publishes native visual hashes. Peers render the same
            // replicated armor, without receiving or equipping the cosmetic items.
            if (player.m_nview && player.m_nview.IsValid() && !player.m_nview.IsOwner()) return;
            var head = Get(player, 0);
            var chest = Get(player, 1);
            var legs = Get(player, 2);
            var cape = Get(player, 3);
            if (head?.m_dropPrefab) visual.SetHelmetItem(head.m_dropPrefab.name.GetStableHashCode());
            if (chest?.m_dropPrefab) visual.SetChestItem(chest.m_dropPrefab.name.GetStableHashCode());
            if (legs?.m_dropPrefab) visual.SetLegItem(legs.m_dropPrefab.name.GetStableHashCode());
            if (cape?.m_dropPrefab) visual.SetShoulderItem(cape.m_dropPrefab.name.GetStableHashCode(), cape.m_variant, cape.m_quality);
        }

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem))]
        private static class PreventEquip
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(Humanoid __instance, ItemDrop.ItemData item, ref bool __result)
            {
                if (!(__instance is Player player) || !IsCosmetic(player, item)) return true;
                if (player.IsItemEquiped(item)) player.UnequipItem(item, false);
                item.m_equipped = false;
                __result = false;
                return false;
            }
        }

        [HarmonyPatch(typeof(FejdStartup), "SetupCharacterPreview")]
        private static class RefreshMenuPreview
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(FejdStartup __instance)
            {
                // LoadPlayerData finishes inventory row migration before the menu
                // assigns its preview player. Refresh after both steps, including
                // characters with cosmetics but no normally equipped armor.
                var player = __instance.GetPreviewPlayer();
                if (player && player.m_visEquipment)
                    player.SetupVisEquipment(player.m_visEquipment, false);
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.QueueEquipAction))]
        private static class PreventEquipQueue
        {
            private static bool Prefix(Player __instance, ItemDrop.ItemData item) => !IsCosmetic(__instance, item);
        }

        [HarmonyPatch(typeof(Humanoid), "SetupVisEquipment")]
        private static class VisualOverride
        {
            private static void Postfix(Humanoid __instance, VisEquipment visEq) { if (__instance is Player player) Apply(player, visEq); }
        }

        [HarmonyPatch(typeof(Player), "OnInventoryChanged")]
        private static class RefreshVisuals
        {
            private static void Postfix(Player __instance)
            {
                if (__instance != CurrentPlayer || __instance.m_isLoading || !__instance.m_visEquipment) return;
                if (__instance.m_nview && __instance.m_nview.IsValid() && !__instance.m_nview.IsOwner()) return;
                __instance.SetupVisEquipment(__instance.m_visEquipment, false);
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class RefreshAfterSpawn
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Player __instance)
            {
                // A character wearing only cosmetics has no gameplay equipment
                // to trigger SetupEquipment when its inventory is restored.
                if (__instance == CurrentPlayer && __instance.m_visEquipment &&
                    (!__instance.m_nview || !__instance.m_nview.IsValid() || __instance.m_nview.IsOwner()))
                    __instance.SetupVisEquipment(__instance.m_visEquipment, false);
            }
        }
    }
}
