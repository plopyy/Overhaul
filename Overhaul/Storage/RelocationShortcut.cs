using System;
using System.Linq;
using EquipmentAndQuickSlots;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Overhaul.Storage
{
    internal static class RelocationShortcut
    {
        // Match the printed key in the active OS layout, not the QWERTY position.
        internal static KeyControl Resolve(Keyboard keyboard)
        {
            if(keyboard==null)return null;
            foreach(var key in keyboard.allKeys)
                if(string.Equals(key.displayName,"H",StringComparison.OrdinalIgnoreCase))return key;
            // Non-Latin layouts may have no H. Function keys are layout independent.
            return keyboard.f6Key;
        }
        internal static bool Down=>PreventSimilarHotkeys.IsShortcutDown(ValConfig.EffectiveMoveObjectKey);
        internal static string Label {
            get {
                var shortcut=ValConfig.EffectiveMoveObjectKey;
                return string.Join(" + ",new[]{shortcut.MainKey}.Concat(shortcut.Modifiers).Select(AugaUnity.AugaModsSettings.KeyLabel));
            }
        }
        internal static string ControlsText()=>Localization.instance.Localize("$overhaul_move_controls").Replace("{0}",Label);
    }
}
