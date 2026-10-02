using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Patches
{
    [HarmonyPatch(typeof(Player), "Update")]
    internal static class HoldPickupPatch
    {
        private static Player trackedPlayer;
        private static float pickupAt = -1f;
        private static bool wasPressed;

        [HarmonyPostfix]
        private static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            if (trackedPlayer != __instance) { trackedPlayer = __instance; pickupAt = -1f; wasPressed = false; }
            bool pressed = ZInput.GetButton("Use") || ZInput.GetButton("JoyUse");
            bool newPress = pressed && !wasPressed;
            wasPressed = pressed;
            if (!__instance.TakeInput() || __instance.IsDead() || __instance.IsTeleporting()
                || Hud.InRadial())
            {
                pickupAt = -1f;
                return;
            }
            if (newPress && pickupAt < 0f) pickupAt = Time.time + 0.3f;
            // The normal interaction already picks up the aimed item on press.
            // Only a continuous hold triggers the additional area pickup, once per press.
            if (!pressed) { pickupAt = -1f; return; }
            if (pickupAt < 0f || Time.time < pickupAt) return;
            pickupAt = -1f;
            var seen = new HashSet<ItemDrop>();
            var position = __instance.transform.position;
            foreach (var collider in Physics.OverlapSphere(position, 5f, __instance.m_autoPickupMask))
            {
                var body = collider.attachedRigidbody;
                if (!body) continue;
                var item = body.GetComponent<ItemDrop>();
                if (!item)
                {
                    var floating = body.GetComponent<FloatingTerrainDummy>();
                    if (floating && floating.m_parent) item = floating.m_parent.GetComponent<ItemDrop>();
                }
                if (!item || !seen.Add(item) || item.IsPiece() || item.InTar()
                    || (item.transform.position - position).sqrMagnitude > 25f) continue;
                var view = item.GetComponent<ZNetView>();
                if (!view || !view.IsValid()) continue;
                item.Load();
                if (__instance.HaveUniqueKey(item.m_itemData.m_shared.m_name)
                    || !__instance.GetInventory().CanAddItem(item.m_itemData, -1)) continue;
                // The native manual pickup handles inventory and networking without a weight limit.
                item.Pickup(__instance);
            }
        }
    }
}
