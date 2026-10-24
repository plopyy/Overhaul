using System.Collections.Generic;
using HarmonyLib;
using Overhaul.Utility;

namespace Overhaul.Building
{
    // Snapping is switched off and on with a key press (X) instead of held with Shift, which turns the ghost finely now
    // (BuildRotation). The game reads its "AltPlace" button for this while placing: there it reads the switch instead.
    // Its other keyboard use while building, Shift + right click to copy (CopyPieceShortcut), reads as never held. The
    // gamepad keeps the game's own buttons. The switch stays as left between pieces and tools.
    internal static class SnapToggle
    {
        internal static bool Off { get; private set; }

        private static bool Placing(string name) => name == "AltPlace" ? Off : ZInput.GetButton(name);
        private static bool Never(string name) => name == "AltPlace" ? false : ZInput.GetButton(name);

        private static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions, string method) =>
            instructions.MethodReplacer(AccessTools.Method(typeof(ZInput), nameof(ZInput.GetButton)), AccessTools.Method(typeof(SnapToggle), method));

        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        private static class Ghost { private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> i) => Replace(i, nameof(Placing)); }

        [HarmonyPatch(typeof(Player), "UpdatePlacement")]
        private static class Placement
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> i) => Replace(i, nameof(Never));

            private static void Prefix(Player __instance, bool takeInput)
            {
                if (__instance != Player.m_localPlayer || !takeInput || !__instance.InPlaceMode() || Hud.IsPieceSelectionVisible() ||
                    !ZInput.GetKeyDown(OverhaulConfig.BuildSnapToggleKey.Value)) return;
                Off = !Off;
                __instance.Message(MessageHud.MessageType.Center, Off ? "$overhaul_build_snap_off" : "$overhaul_build_snap_on");
            }
        }
    }
}
