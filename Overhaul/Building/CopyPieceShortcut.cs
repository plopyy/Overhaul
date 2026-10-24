using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Building
{
    // The middle mouse button copies the piece aimed at (in place of the game's Shift + right click, which now does
    // nothing): it is selected, turned as that piece stands, tilt included (BuildRotation). Gamepad copy is unchanged.
    internal static class CopyPieceShortcut
    {
        private static bool NoCopy(Player player) => false;

        [HarmonyPatch(typeof(Player), "UpdatePlacement")]
        private static class Copy
        {
            private static void Prefix(Player __instance, bool takeInput)
            {
                if (__instance != Player.m_localPlayer || !takeInput || !__instance.InPlaceMode() || Storage.RelocationClient.Active ||
                    Hud.IsPieceSelectionVisible() || !ZInput.GetMouseButtonDown(2) || !GameCamera.instance) return;
                var camera = GameCamera.instance.transform;
                Piece aimed = null;
                if (Physics.Raycast(camera.position, camera.forward, out var hit, 50f, __instance.m_removeRayMask) &&
                    Vector3.Distance(hit.point, __instance.m_eye.position) < __instance.m_maxPlaceDistance)
                    aimed = hit.collider.GetComponentInParent<Piece>();
                if (!__instance.CopyPiece() || !aimed) return;
                BuildRotation.Local = Quaternion.Inverse(Quaternion.Euler(0, __instance.m_placeRotationDegrees * __instance.m_placeRotation, 0)) * aimed.transform.rotation;
            }

            // The game's keyboard copy (Shift + right click) is dropped; its gamepad copy is another call, kept.
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var copy = AccessTools.Method(typeof(Player), "CopyPiece");
                bool done = false;
                foreach (var code in instructions)
                {
                    if (!done && code.Calls(copy)) { done = true; yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, AccessTools.Method(typeof(CopyPieceShortcut), nameof(NoCopy))).MoveLabelsFrom(code); continue; }
                    yield return code;
                }
                if (!done) Utility.Log.LogError("Copy piece: the game's copy shortcut not found");
            }
        }
    }
}
