using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Building
{
    // Repair in an area: the hammer's repair mends the piece aimed at and every piece linked to it up to Hops pieces away
    // (pieces touching it, the pieces touching those...), all in one go. Each piece follows the game's own rules (ward,
    // station in range, damaged, the game's one second between two repairs of a piece); the stamina, eitr and durability of
    // one repair are paid once for the whole area. Holding the button repeats it (QuickBuild.RepairHeld).
    [HarmonyPatch(typeof(Player), nameof(Player.Repair))]
    internal static class QuickRepair
    {
        private const int Hops = 3;
        // Two pieces touch when their boxes, grown by this margin, overlap.
        private const float Touch = .15f;

        private static bool Prefix(Player __instance, ItemDrop.ItemData toolItem)
        {
            if (__instance != Player.m_localPlayer || !__instance.InPlaceMode()) return true;
            var aimed = __instance.GetHoveringPiece();
            if (!aimed) return true;
            int repaired = 0;
            foreach (var piece in Area(aimed))
            {
                if (!Repairable(__instance, piece)) continue;
                if (!piece.GetComponent<WearNTear>().Repair()) continue;
                repaired++;
                piece.m_placeEffect.Create(piece.transform.position, piece.transform.rotation, null, 1f, -1, __instance.GetZDOID());
            }
            if (repaired == 0)
            {
                __instance.Message(MessageHud.MessageType.TopLeft, aimed.m_name + " $msg_doesnotneedrepair");
                return false;
            }
            __instance.FaceLookDirection();
            __instance.m_zanim.SetTrigger(toolItem.m_shared.m_attack.m_attackAnimation);
            __instance.Message(MessageHud.MessageType.TopLeft, repaired == 1
                ? Localization.instance.Localize("$msg_repaired", aimed.m_name)
                : Localization.instance.Localize("$overhaul_repaired_area", repaired.ToString()));
            __instance.UseStamina(__instance.GetBuildStamina());
            __instance.UseEitr(toolItem.m_shared.m_attack.m_attackEitr);
            if (toolItem.m_shared.m_useDurability) toolItem.m_durability -= toolItem.m_shared.m_useDurabilityDrain * Game.m_durabilityRate;
            return false;
        }

        // Whether holding the button should repair now: some piece of the area aimed at can be repaired.
        internal static bool Ready(Player player)
        {
            var aimed = player.GetHoveringPiece();
            if (!aimed) return false;
            foreach (var piece in Area(aimed))
            {
                var wear = piece.GetComponent<WearNTear>();
                if (Repairable(player, piece) && Time.time - wear.m_lastRepair >= 1f) return true;
            }
            return false;
        }

        // Damaged, and the player may repair it (the game's checks for the piece aimed at).
        private static bool Repairable(Player player, Piece piece)
        {
            var wear = piece.GetComponent<WearNTear>();
            return wear && wear.m_nview && wear.m_nview.IsValid() && wear.GetHealthPercentage() < 1f &&
                player.CheckCanRemovePiece(piece) && PrivateArea.CheckAccess(piece.transform.position, 0, false, false);
        }

        // The piece and the pieces linked to it, up to Hops pieces away.
        private static List<Piece> Area(Piece start)
        {
            var found = new List<Piece> { start };
            var seen = new HashSet<Piece> { start };
            var frontier = new List<Piece> { start };
            for (int hop = 0; hop < Hops && frontier.Count > 0; hop++)
            {
                var next = new List<Piece>();
                foreach (var piece in frontier)
                {
                    if (!Bounds(piece, out var box)) continue;
                    box.Expand(Touch * 2);
                    foreach (var collider in Physics.OverlapBox(box.center, box.extents, Quaternion.identity, Player.m_localPlayer.m_removeRayMask))
                    {
                        var other = collider.GetComponentInParent<Piece>();
                        if (!other || !seen.Add(other) || !other.GetComponent<WearNTear>()) continue;
                        found.Add(other); next.Add(other);
                    }
                }
                frontier = next;
            }
            return found;
        }

        private static bool Bounds(Piece piece, out Bounds box)
        {
            box = default;
            bool any = false;
            foreach (var collider in piece.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled || collider.isTrigger) continue;
                if (!any) { box = collider.bounds; any = true; } else box.Encapsulate(collider.bounds);
            }
            return any;
        }
    }
}
