using System;
using System.Collections.Generic;
using System.Linq;
using GUIFramework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Valheim.SettingsGui;

namespace AugaUnity
{
    // Serialized on the original Misc page; owns values, never builds UI.
    public class AugaMiscSettings : MonoBehaviour, ISettingsTab
    {
        public GuiDropdown Language;
        public Slider GuiScale;
        public TMP_Text GuiScaleValue;
        public Slider AutoBackups;
        public TMP_Text AutoBackupsValue;
        public Toggle CameraShake;
        public Toggle ImmersiveShipCamera;
        public Toggle ReduceBackgroundUsage;
        public Toggle ReduceFlashingLights;
        public Toggle ShowKeyHints;
        public Toggle Tutorials;
        public Toggle ToggleRun;
        public Toggle ToggleBlock;
        public Toggle SkipIntroCinematic;
        public Button ResetTutorials;
        public TMP_Text[] CompactLabels;
        public string[] LabelKeys;
        public string[] FrenchLabels;

        private List<string> _languages;
        private float _originalScale;
        private bool _hasScaleSnapshot;
        private bool _scalePreviewed;
        // No setting on this page is shared with another original Auga tab.
        public event Action<string, int> SharedSettingChanged { add { } remove { } }

        public void Initialize()
        {
            ApplyCompactLabels();
            _languages = Localization.instance.GetLanguages().ToList();
            Language.ClearOptions();
            Language.AddOptions(_languages.Select(language => Localization.instance.Localize("$language_" + language.ToLowerInvariant())).ToList());
            Language.SetValueWithoutNotify(Math.Max(0, _languages.IndexOf(Localization.instance.GetSelectedLanguage())));
            Language.RefreshShownValue();
            _originalScale = PlatformPrefs.GetFloat("GuiScale", GuiScaler.PlatformDefaultScaling);
            _hasScaleSnapshot = true;
            _scalePreviewed = false;
            GuiScale.SetValueWithoutNotify(_originalScale * 100f);
            AutoBackups.SetValueWithoutNotify(PlatformPrefs.GetInt("AutoBackups", 4));
            CameraShake.SetIsOnWithoutNotify(PlatformPrefs.GetInt("CameraShake", 1) == 1);
            ImmersiveShipCamera.SetIsOnWithoutNotify(PlatformPrefs.GetInt("ShipCameraTilt", 1) == 1);
            ReduceBackgroundUsage.SetIsOnWithoutNotify(PlatformPrefs.GetInt("ReduceBackgroundUsage", 0) == 1);
            ReduceFlashingLights.SetIsOnWithoutNotify(PlatformPrefs.GetInt("ReduceFlashingLights", 0) == 1);
            ShowKeyHints.SetIsOnWithoutNotify(PlatformPrefs.GetInt("KeyHints", 1) == 1);
            Tutorials.SetIsOnWithoutNotify(PlatformPrefs.GetInt("TutorialsEnabled", 1) == 1);
            ToggleRun.SetIsOnWithoutNotify(PlatformPrefs.GetInt("ToggleRun", ZInput.IsGamepadActive() ? 1 : 0) == 1);
            ToggleBlock.SetIsOnWithoutNotify(PlatformPrefs.GetInt("ToggleBlock", 0) == 1);
            SkipIntroCinematic.SetIsOnWithoutNotify(PlatformPrefs.GetBool("SkipIntroCinematic", false));
            GuiScaleValue.text = GuiScale.value.ToString("0") + "%";
            OnAutoBackupsChanged();
        }

        public void OnOkAsync(OkActionCompletedHandler callback)
        {
            PlatformPrefs.SetFloat("GuiScale", GuiScale.value / 100f);
            PlatformPrefs.SetInt("AutoBackups", (int)AutoBackups.value);
            PlatformPrefs.SetInt("CameraShake", CameraShake.isOn ? 1 : 0);
            PlatformPrefs.SetInt("ShipCameraTilt", ImmersiveShipCamera.isOn ? 1 : 0);
            PlatformPrefs.SetInt("ReduceBackgroundUsage", ReduceBackgroundUsage.isOn ? 1 : 0);
            PlatformPrefs.SetInt("ReduceFlashingLights", ReduceFlashingLights.isOn ? 1 : 0);
            PlatformPrefs.SetInt("KeyHints", ShowKeyHints.isOn ? 1 : 0);
            PlatformPrefs.SetInt("TutorialsEnabled", Tutorials.isOn ? 1 : 0);
            PlatformPrefs.SetInt("ToggleRun", ToggleRun.isOn ? 1 : 0);
            ZInput.ToggleRun = ToggleRun.isOn;
            PlatformPrefs.SetInt("ToggleBlock", ToggleBlock.isOn ? 1 : 0);
            PlatformPrefs.SetBool("SkipIntroCinematic", SkipIntroCinematic.isOn);
            if (Player.m_localPlayer) Player.m_localPlayer.ToggleBlock = ToggleBlock.isOn;
            Settings.ReduceBackgroundUsage = ReduceBackgroundUsage.isOn;
            Settings.ReduceFlashingLights = ReduceFlashingLights.isOn;
            Raven.m_tutorialsEnabled = Tutorials.isOn;
            GuiScaler.SetScale(GuiScale.value / 100f);
            _scalePreviewed = false;
            Localization.instance.SetLanguage(_languages[Language.value]);
            callback?.Invoke();
        }

        public void OnBack()
        {
            // Settings calls OnBack on every page, even if an earlier page failed
            // to initialize. Restore only a preview that this page actually made.
            if (!_scalePreviewed) return;
            GuiScaler.SetScale(_originalScale);
            _scalePreviewed = false;
        }
        public void Terminate() { OnBack(); }
        public void OnSharedSettingChanged(string setting, int value) { }
        public void OnTabOpen(Button backButton, Button okButton)
        {
            GuiUtils.SetNavigationDown(SkipIntroCinematic, backButton);
            GuiUtils.SetNavigationUp(backButton, SkipIntroCinematic);
            GuiUtils.SetNavigationDown(ToggleBlock, okButton);
            GuiUtils.SetNavigationUp(okButton, ToggleBlock);
        }

        public void OnGuiScaleChanged()
        {
            if (!_hasScaleSnapshot)
            {
                _originalScale = PlatformPrefs.GetFloat("GuiScale", GuiScaler.PlatformDefaultScaling);
                _hasScaleSnapshot = true;
            }
            GuiScaleValue.text = GuiScale.value.ToString("0") + "%";
            _scalePreviewed = true;
            GuiScaler.SetScale(GuiScale.value / 100f);
        }

        public void OnAutoBackupsChanged()
        {
            AutoBackupsValue.text = AutoBackups.value == 1f ? "0" : AutoBackups.value.ToString("0");
        }

        public void ApplyCompactLabels()
        {
            for (int i = 0; i < CompactLabels.Length; ++i)
                CompactLabels[i].text = Localization.instance.Localize(LabelKeys[i]);
        }

        public void OnResetTutorials() { Player.ResetSeenTutorials(); }
    }
}
