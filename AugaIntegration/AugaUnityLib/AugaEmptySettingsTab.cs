using System;
using UnityEngine;
using UnityEngine.UI;
using Valheim.SettingsGui;

namespace AugaUnity
{
    // Empty prefab page reserved for options that will be migrated later.
    public class AugaEmptySettingsTab : MonoBehaviour, ISettingsTab
    {
        public event Action<string, int> SharedSettingChanged { add { } remove { } }
        public void Initialize() { }
        public void OnBack() { }
        public void Terminate() { }
        public void OnSharedSettingChanged(string setting, int value) { }
        public void OnOkAsync(OkActionCompletedHandler callback) { callback?.Invoke(); }
        public void OnTabOpen(Button backButton, Button okButton)
        {
            // Do not retain navigation to a control on the page we just hid.
            GuiUtils.SetNavigationUp(backButton, okButton);
            GuiUtils.SetNavigationUp(okButton, backButton);
        }
    }
}
