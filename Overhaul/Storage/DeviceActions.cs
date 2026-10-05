using HarmonyLib;
using UnityEngine;

namespace Overhaul.Storage
{
    internal static class DeviceActions
    {
        private static DeviceStore pendingEquipment;
        private static float pendingUntil;
        internal static string Hint(DeviceStore store)
        {
            string text = "\n[<color=yellow><b>" + ChestInteractions.KeyLabel + "</b></color>] $overhaul_chest_take_all";
            if (store.Adapter is ArmorInventory) text += "\n[<color=yellow><b>$overhaul_key_shift + " + ChestInteractions.KeyLabel + "</b></color>] $overhaul_device_place_equipment";
            return Localization.instance.Localize(text);
        }
        internal static bool Access(DeviceStore s, Player p) => s && s.Ready && s.Visible && s.Container && p &&
            !p.IsDead() && !p.IsTeleporting() && Vector3.Distance(p.transform.position, s.transform.position) <= 5f &&
            s.Container.CheckAccess(p.GetPlayerID()) && ChestAccess.WardAccess(s.Container, p.GetPlayerID()) && !MoveReservation.Busy(s.Container);
        internal static bool TryKey(Player player, bool equipment)
        {
            DeviceStore store;
            var gui = InventoryGui.instance;
            if (InventoryGui.IsVisible()) store = DeviceStore.Of(gui.m_currentContainer);
            else
            {
                if (!player.TakeInput() || !GameCamera.instance) return false;
                player.FindHoverObject(out var hover, out _); store = DeviceStore.At(hover);
            }
            if (!store) return false;
            if (!Access(store, player)) { player.Message(MessageHud.MessageType.Center, "$msg_cantopen"); return true; }
            if (equipment)
            {
                if (!(store.Adapter is ArmorInventory)) return true;
                if (gui && gui.m_currentContainer == store.Container && store.Owner) SwapEquipment(store, player);
                else
                {
                    pendingEquipment = store; pendingUntil = Time.time + 5;
                    if (!store.Open(player, false)) pendingEquipment = null;
                }
            }
            else if (gui && gui.m_currentContainer == store.Container && store.Owner) gui.OnTakeAll();
            else store.Container.TakeAll(player); // Native access/ownership handshake.
            return true;
        }
        internal static void OnShown(DeviceStore store)
        {
            if (pendingEquipment != store) return;
            pendingEquipment = null;
            if (Time.time <= pendingUntil) SwapEquipment(store, Player.m_localPlayer);
        }
        internal static void SwapEquipment(DeviceStore store, Player player)
        {
            if (!Access(store, player) || !store.Owner || !(store.Adapter is ArmorInventory)) return;
            if (player.InAttack() || player.InDodge() || player.IsSwimming() && !player.IsOnGround()) return;
            if (!DeviceEquipmentSwap.Exchange(store, player)) player.Message(MessageHud.MessageType.Center, "$overhaul_device_swap_failed");
        }
        [HarmonyPatch(typeof(Container), "RPC_TakeAllResponse")]
        private static class TakeClosed
        {
            private static void Prefix(Container __instance, bool granted)
            {
                var store = DeviceStore.Of(__instance);
                if (granted && store && store.Ready) store.Load();
            }
            private static void Postfix(Container __instance, bool granted)
            {
                var store = DeviceStore.Of(__instance);
                if (granted && store && store.Ready && store.Owner && Player.m_localPlayer && store.Adapter.Dual)
                    Player.m_localPlayer.GetInventory().MoveAll(store.Fuel);
            }
        }
    }
}
