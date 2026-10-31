using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Overhaul.Utility
{
    // TEMPORARY, for capturing the main menu scene: F9 on the main menu hides or shows every canvas. To remove.
    [HarmonyPatch(typeof(FejdStartup), "Update")]
    internal static class MenuHideUi
    {
        static bool hidden;
        private static void Postfix()
        {
            if (Keyboard.current == null || !Keyboard.current.f9Key.wasPressedThisFrame) return;
            hidden = !hidden;
            foreach (var canvas in Object.FindObjectsOfType<Canvas>(true)) if (canvas.isRootCanvas) canvas.enabled = !hidden;
        }
    }
}
