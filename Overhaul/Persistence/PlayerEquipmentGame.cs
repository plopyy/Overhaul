using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using EquipmentAndQuickSlots;

namespace Overhaul.Persistence
{
    internal static class PlayerEquipmentGame
    {
        private static int applying;
        internal static void Present(Action action) { applying++; try { action(); } finally { applying--; } }
        private static bool Managed(Humanoid player) => player && applying == 0 && player == Player.m_localPlayer &&
            PlayerSessionGame.Managed && !Player.m_localPlayer.m_isLoading;
        internal static PlayerGearDefinition Definition(object[] row)
        {
            var item = PlayerInventoryView.ReadItem(row,null,true); var type = item.m_shared.m_itemType;
            PlayerGearKind kind;
            switch (type)
            {
                case ItemDrop.ItemData.ItemType.OneHandedWeapon: kind = PlayerGearKind.OneHand; break;
                case ItemDrop.ItemData.ItemType.Shield: kind = PlayerGearKind.Shield; break;
                case ItemDrop.ItemData.ItemType.Torch: kind = PlayerGearKind.Torch; break;
                case ItemDrop.ItemData.ItemType.Tool: case ItemDrop.ItemData.ItemType.Bow:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon: case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft: kind = PlayerGearKind.BothHands; break;
                case ItemDrop.ItemData.ItemType.Helmet: kind = PlayerGearKind.Helmet; break;
                case ItemDrop.ItemData.ItemType.Chest: kind = PlayerGearKind.Chest; break;
                case ItemDrop.ItemData.ItemType.Legs: kind = PlayerGearKind.Legs; break;
                case ItemDrop.ItemData.ItemType.Shoulder: kind = PlayerGearKind.Cape; break;
                case ItemDrop.ItemData.ItemType.Utility: kind = PlayerGearKind.Utility; break;
                case ItemDrop.ItemData.ItemType.Trinket: kind = PlayerGearKind.Trinket; break;
                case ItemDrop.ItemData.ItemType.Ammo: case ItemDrop.ItemData.ItemType.AmmoNonEquipable: kind = PlayerGearKind.Ammo; break;
                default: kind = PlayerGearKind.None; break;
            }
            bool usable = (!item.m_shared.m_useDurability || item.m_durability > 0) &&
                (string.IsNullOrEmpty(item.m_shared.m_dlc) || DLCMan.instance && DLCMan.instance.IsDLCInstalled(item.m_shared.m_dlc)) &&
                (kind != PlayerGearKind.Utility && kind != PlayerGearKind.Trinket || item.m_worldLevel >= Game.m_worldLevel);
            return new PlayerGearDefinition { Kind = kind,Name = item.m_shared.m_name,Usable = usable };
        }
        internal static PlayerActionPlan Prepare(ZDO actor,InventoryMoveRequest request,PlayerSnapshot snapshot,PlayerActionInventory inventory)
        {
            if (request.Action.Amount != 1 || request.Gameplay.TargetId != 0) throw new InvalidOperationException("Invalid equipment request");
            int slot = request.Action.FromY*256+request.Action.FromX;
            int visible = InventoryMoveGame.PlayerRows(snapshot.Rows)+Slots.ExtraRows;
            int index = (request.Action.FromY-visible)*Slots.VanillaInventoryWidth+request.Action.FromX;
            if (request.Action.FromY >= visible && Slots.slots.Any(s => s != null && s.Index == index && s.IsCosmeticSlot))
                throw new InvalidOperationException("Cosmetic items do not grant equipment effects");
            var item = PlayerInventoryView.ReadItem(inventory.Item(slot),null,true);
            float duration = Math.Max(0,item.m_shared.m_equipDuration);
            if (float.IsNaN(duration) || float.IsInfinity(duration) || duration > 20) throw new InvalidOperationException("Invalid equipment duration");
            PlayerEquipment.Apply(inventory,slot,request.Gameplay.Kind == PlayerActionKind.Equip,Definition,Slots.WearableUtilityItems);
            float elapsed = 0,last = Time.time;
            return new PlayerActionPlan(new PlayerWorldAction(inventory.Delta(request.Action.Operation,snapshot.Revision),new Dictionary<long,ObjectRecord>()),() => { })
            {
                Ready = () =>
                {
                    var instance = ZNetScene.instance.FindInstance(actor.m_uid); var player = instance ? instance.GetComponent<Player>() : null;
                    if (!player || player.IsDead() || player.IsTeleporting() || player.InDodge() || player.IsSwimming() && !player.IsOnGround())
                        throw new InvalidOperationException("Player cannot complete equipment action");
                    float now = Time.time,dt = Math.Max(0,now-last); last = now;
                    if (player.InAttack()) return false;
                    elapsed += dt; return elapsed >= duration;
                }
            };
        }
        private static bool EquipmentRequest(InventoryMoveRequest request) => request?.Gameplay?.Kind == PlayerActionKind.Equip || request?.Gameplay?.Kind == PlayerActionKind.Unequip;
        private static void Cancel(Player player)
        {
            var client = InventoryMoveGame.Client;
            if (!EquipmentRequest(client?.Controller.Pending)) return;
            client.CancelEquipment();
            Present(() => { player.m_actionQueue.RemoveAll(a => a.m_type == Player.MinorActionData.ActionType.Equip || a.m_type == Player.MinorActionData.ActionType.Unequip); });
        }
        private static void Begin(Player player,ItemDrop.ItemData item,bool equip,bool toggle)
        {
            if (item == null || !player.GetInventory().ContainsItem(item) || player.IsTeleporting() || player.IsDead()) return;
            var client = InventoryMoveGame.Client; var pending = client?.Controller.Pending;
            if (pending != null)
            {
                if (toggle && EquipmentRequest(pending) && pending.Action.FromX == item.m_gridPos.x && pending.Action.FromY == item.m_gridPos.y) Cancel(player);
                return;
            }
            if (client?.Controller.Act(new PlayerActionCommand { Kind = equip ? PlayerActionKind.Equip : PlayerActionKind.Unequip },item.m_gridPos.x,item.m_gridPos.y) != true) return;
            player.CancelReloadAction();
            if (item.m_shared.m_equipDuration > 0)
                player.m_actionQueue.Add(new Player.MinorActionData { m_item = item,
                    m_type = equip ? Player.MinorActionData.ActionType.Equip : Player.MinorActionData.ActionType.Unequip,
                    m_duration = item.m_shared.m_equipDuration,m_progressText = (equip ? "$hud_equipping " : "$hud_unequipping ")+item.m_shared.m_name,
                    m_animation = "equipping",m_startEffect = equip && item.m_shared.m_equipDuration >= 1 ? player.m_equipStartEffects : null });
        }
        internal static void Confirm(Player player,InventoryMoveRequest request)
        {
            if (!EquipmentRequest(request)) return;
            Present(() => player.m_actionQueue.RemoveAll(a => a.m_type == Player.MinorActionData.ActionType.Equip || a.m_type == Player.MinorActionData.ActionType.Unequip));
            if (player.m_actionAnimation == "equipping") { if (player.m_zanim) player.m_zanim.SetBool("equipping",false); player.m_actionAnimation = null; }
        }
        [HarmonyPatch(typeof(Player),"ToggleEquipped")]
        private static class Toggle
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Player __instance,ItemDrop.ItemData item,ref bool __result)
            {
                if (!Managed(__instance)) return true;
                __result = item != null && item.IsEquipable();
                if (__result) Begin(__instance,item,!__instance.IsItemEquiped(item),true); return false;
            }
        }
        [HarmonyPatch(typeof(Player),nameof(Player.QueueEquipAction))]
        private static class QueueEquip
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Player __instance,ItemDrop.ItemData item) { if (!Managed(__instance)) return true; Begin(__instance,item,true,true); return false; }
        }
        [HarmonyPatch(typeof(Player),nameof(Player.QueueUnequipAction))]
        private static class QueueUnequip
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Player __instance,ItemDrop.ItemData item) { if (!Managed(__instance)) return true; Begin(__instance,item,false,true); return false; }
        }
        [HarmonyPatch(typeof(Humanoid),nameof(Humanoid.EquipItem))]
        private static class Equip
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Humanoid __instance,ItemDrop.ItemData item,ref bool __result)
            { if (!Managed(__instance)) return true; __result = false; Begin((Player)__instance,item,true,false); return false; }
        }
        [HarmonyPatch(typeof(Humanoid),nameof(Humanoid.UnequipItem))]
        private static class Unequip
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Humanoid __instance,ItemDrop.ItemData item)
            { if (!Managed(__instance)) return true; Begin((Player)__instance,item,false,false); return false; }
        }
        [HarmonyPatch(typeof(Player),nameof(Player.ClearActionQueue))]
        private static class Interrupt
        {
            [HarmonyPriority(Priority.First+200)]
            private static void Prefix(Player __instance) { if (Managed(__instance)) Cancel(__instance); }
        }
        [HarmonyPatch(typeof(Player),nameof(Player.InMinorAction))]
        private static class PendingAnimation
        {
            private static void Postfix(Player __instance,ref bool __result)
            { if (Managed(__instance) && EquipmentRequest(InventoryMoveGame.Client?.Controller.Pending)) __result = true; }
        }
    }
}
