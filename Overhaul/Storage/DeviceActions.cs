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
                else if (store.Load() && (pendingEquipment != store || Time.time > pendingUntil))
                {
                    // Take-all's ownership handshake without its two-second cooldown or the window.
                    // One request in flight at a time, for at most half a second.
                    pendingEquipment = store; pendingUntil = Time.time + .5f;
                    store.View.InvokeRPC(SwapRequest, player.GetPlayerID());
                }
            }
            else if (gui && gui.m_currentContainer == store.Container && store.Owner) gui.OnTakeAll();
            else store.Container.TakeAll(player); // Native access/ownership handshake.
            return true;
        }
        internal static void SwapEquipment(DeviceStore store, Player player)
        {
            if (!Access(store, player) || !store.Owner || !(store.Adapter is ArmorInventory)) return;
            if (player.InAttack() || player.InDodge() || player.IsSwimming() && !player.IsOnGround()) return;
            if (!DeviceEquipmentSwap.Exchange(store, player)) player.Message(MessageHud.MessageType.Center, "$overhaul_device_swap_failed");
        }
        private const string SwapRequest = "Overhaul_RequestEquipmentSwap", SwapResponse = "Overhaul_EquipmentSwapResponse";
        internal static void Register(DeviceStore store)
        {
            if (!(store.Adapter is ArmorInventory)) return;
            store.View.Register<long>(SwapRequest, (uid, playerID) => OnSwapRequest(store, uid, playerID));
            store.View.Register<bool>(SwapResponse, (uid, granted) => OnSwapResponse(store, uid, granted));
        }
        // Owner side: the checks of Container.RPC_RequestTakeAll, minus its cooldown.
        private static void OnSwapRequest(DeviceStore store, long uid, long playerID)
        {
            if (!store.View.IsOwner() || !store.Container) return;
            bool granted = !(store.Container.IsInUse() && uid != ZNet.GetUID()) && store.Container.CheckAccess(playerID);
            store.View.InvokeRPC(uid, SwapResponse, granted);
        }
        private static void OnSwapResponse(DeviceStore store, long uid, bool granted)
        {
            if (pendingEquipment != store) return;
            pendingEquipment = null;
            var player = Player.m_localPlayer; if (!player) return;
            if (!granted) { player.Message(MessageHud.MessageType.Center, "$msg_inuse"); return; }
            store.View.ClaimOwnership();
            ZDOMan.instance.ForceSendZDO(uid, store.View.GetZDO().m_uid);
            store.Load();
            SwapEquipment(store, player);
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
