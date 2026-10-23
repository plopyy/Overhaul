using System.Collections.Generic;
using HarmonyLib;

namespace Overhaul.Building
{
    // In building mode Ctrl, Alt and Shift belong to building (turning the ghost about X/Z, placing without snapping):
    // any other game action bound to one of them on the keyboard (crouch, run/dash, auto pickup toggle…) does nothing
    // while the hammer, hoe or cultivator is out. Gamepad bindings are left alone.
    internal static class BuildModifierKeys
    {
        // The building actions themselves.
        private static readonly HashSet<string> Building = new HashSet<string> { "AltPlace" };
        private static readonly string[] Modifiers = { "/leftCtrl", "/rightCtrl", "/ctrl", "/leftAlt", "/rightAlt", "/alt", "/leftShift", "/rightShift", "/shift" };
        private static readonly Dictionary<string, bool> onModifier = new Dictionary<string, bool>();
        private static int frame = -1;

        private static bool Blocked(string name)
        {
            var player = Player.m_localPlayer;
            if (!player || !player.InPlaceMode() || name == null || Building.Contains(name)) return false;
            // Bindings can change in the settings: looked up again each frame.
            if (frame != UnityEngine.Time.frameCount) { frame = UnityEngine.Time.frameCount; onModifier.Clear(); }
            if (onModifier.TryGetValue(name, out bool blocked)) return blocked;
            blocked = false;
            var def = ZInput.instance?.GetButtonDef(name);
            string path = def?.ButtonAction?.bindings.Count > 0 ? def.ButtonAction.bindings[0].effectivePath : null;
            if (path != null && path.StartsWith("<Keyboard>"))
                foreach (var key in Modifiers) if (path.EndsWith(key, System.StringComparison.OrdinalIgnoreCase)) { blocked = true; break; }
            onModifier[name] = blocked;
            return blocked;
        }

        // Blocked: the action reads as not pressed and the game's check is skipped.
        private static bool Skip(string name, ref bool result)
        {
            if (!Blocked(name)) return true;
            result = false; return false;
        }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButton))]
        private static class Held { private static bool Prefix(string name, ref bool __result) => Skip(name, ref __result); }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButtonDown))]
        private static class Down { private static bool Prefix(string name, ref bool __result) => Skip(name, ref __result); }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButtonUp))]
        private static class Up { private static bool Prefix(string name, ref bool __result) => Skip(name, ref __result); }
    }
}
