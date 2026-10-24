using System.Collections.Generic;
using HarmonyLib;
using UnityEngine.InputSystem;

namespace Overhaul.Building
{
    // In building mode Ctrl, Alt and Shift belong to building (turning the ghost about X/Z, finely), and so do the
    // snapping switch (X) and rotation reset (<) keys: every game action bound to one of them on the keyboard (crouch,
    // run/dash, sit, auto pickup toggle, the game's own held snapping…) is switched off at its source, its input
    // action, while the hammer, hoe or cultivator is out, so no key press reaches it whoever reads it; they are switched
    // back on when leaving building mode. Building reads these keys directly. Gamepad-only actions are left alone.
    internal static class BuildModifierKeys
    {
        private static readonly string[] Modifiers = { "/leftCtrl", "/rightCtrl", "/ctrl", "/leftAlt", "/rightAlt", "/alt", "/leftShift", "/rightShift", "/shift" };
        private static readonly List<ZInput.ButtonDef> off = new List<ZInput.ButtonDef>();

        private static bool OnModifier(InputAction action)
        {
            if (action == null) return false;
            foreach (var binding in action.bindings)
            {
                string path = binding.effectivePath;
                if (path == null || !path.StartsWith("<Keyboard>")) continue;
                foreach (var key in Modifiers) if (path.EndsWith(key, System.StringComparison.OrdinalIgnoreCase)) return true;
                // The snapping switch and rotation reset keys by their control names ("<Keyboard>/x" → "/x").
                foreach (var reserved in new[] { Utility.OverhaulConfig.BuildSnapToggleKey.Value, Utility.OverhaulConfig.BuildResetKey.Value })
                {
                    string control = reserved == UnityEngine.KeyCode.Less ? "/" + (BuildRotation.LessKey()?.name ?? "") : ZInput.KeyCodeToPath(reserved, false) ?? "";
                    if (control == "/") continue;
                    if (control.Length > 0 && path.EndsWith("/" + control.Substring(control.LastIndexOf('/') + 1), System.StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        private static void Switch(bool building)
        {
            if (building == off.Count > 0 || ZInput.instance == null) return;
            if (!building)
            {
                foreach (var def in off) def?.ButtonAction?.Enable();
                off.Clear();
                return;
            }
            foreach (var pair in ZInput.instance.m_buttons)
            {
                var def = pair.Value;
                if (def?.ButtonAction == null || !def.ButtonAction.enabled || !OnModifier(def.ButtonAction)) continue;
                def.ButtonAction.Disable(); def.ResetState();
                off.Add(def);
            }
            // Nothing on a modifier: an empty marker keeps the state "building" until leaving.
            if (off.Count == 0) off.Add(null);
        }

        [HarmonyPatch(typeof(Player), "Update")]
        private static class Mode
        {
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer) return;
                Switch(__instance.InPlaceMode());
            }
        }

        // Leaving the world while building.
        [HarmonyPatch(typeof(Player), "OnDestroy")]
        private static class Gone
        {
            private static void Prefix(Player __instance) { if (__instance == Player.m_localPlayer) Switch(false); }
        }
    }
}
