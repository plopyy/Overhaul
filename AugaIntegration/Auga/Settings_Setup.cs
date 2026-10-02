using System.Linq;
using System.Collections.Generic;
using Valheim.SettingsGui;
using AugaUnity;
using HarmonyLib;
using TMPro;
using UnityEngine.UI;

namespace Auga
{
    [HarmonyPatch]
    [UiModule(UiModule.Settings)]
    public static class Settings_Setup
    {
        // 1.0.14 lists these commands in Settings but creates them as non-rebindable.
        // Correct the capability itself so capture, Save and Load use the same native path.
        [HarmonyPatch(typeof(ZInput), "Reset")]
        [HarmonyPostfix]
        public static void CameraZoomRebindable_Postfix(ZInput __instance)
        {
            foreach (var name in new[] { "CamZoomIn", "CamZoomOut" })
            {
                var button = __instance.GetButtonDef(name);
                if (button != null) button.Rebindable = true;
            }
        }

        // Also covers an input instance created before Auga installed its patches.
        [HarmonyPatch(typeof(ZInput), "StartBindKey")]
        [HarmonyPrefix]
        public static void CameraZoomCapture_Prefix(ZInput __instance, string __0)
        {
            if (__0 == "CamZoomIn" || __0 == "CamZoomOut")
            {
                var button = __instance.GetButtonDef(__0);
                if (button != null) button.Rebindable = true;
            }
        }

        // Settings controls and their native tab controllers are serialized in AugaSettings.
        [HarmonyPatch(typeof(Valheim.SettingsGui.KeyboardMouseSettings), "UpdateBindings")]
        [HarmonyPrefix]
        public static bool UpdateBindings_Prefix(List<KeySetting> ___m_keys)
        {
            if (!___m_keys.Any(key => key.m_keyTransform.GetComponent<AugaBindingDisplay>() != null))
                return true;
            foreach (var key in ___m_keys)
            {
                var bindingDisplay = key.m_keyTransform.GetComponent<AugaBindingDisplay>();
                if (!bindingDisplay)
                {
                    Auga.LogWarning($"Could not set binding for {key.m_keyTransform.name}");
                    continue;
                }
                bindingDisplay.SetBinding(key.m_keyName);

                var keyButton = key.m_keyTransform.GetComponentInChildren<Button>();
                if (keyButton != null)
                {
                    var textComponent = keyButton.GetComponentInChildren<TMP_Text>();
                    if (textComponent != null)
                    {
                        textComponent.text = Localization.instance.GetBoundKeyString(key.m_keyName, true);
                    }
                }
            }
            
            // Current Valheim owns the gamepad diagram through GamepadMapController.
            return false;
        }
    }
}
