using System;
using System.Collections.Generic;
using System.Linq;
using GUIFramework;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Valheim.SettingsGui;

namespace AugaUnity
{
    public class AugaGraphicsSettings : MonoBehaviour, ISettingsTab
    {
        // Direct Unity references and enum arrays survive loading from BepInEx's
        // embedded assembly; custom nested serializable binding classes did not.
        public Slider[] Sliders;
        public TMP_Text[] SliderValues;
        public GraphicsSettingInt[] SliderSettings;
        public Toggle[] Toggles;
        public GraphicsSettingBool[] ToggleSettings;
        private float[] _initialSliderValues;
        private bool[] _initialToggleValues;
        public GuiDropdown ResolutionDropdown;
        public Toggle Fullscreen;
        public Button TestResolution;
        public Toggle SSAO;
        public AugaResolutionConfirmation Confirmation;
        private List<Resolution> _resolutions;
        public GuiDropdown RenderResolution, Upscaling, Preset;
        public CanvasGroup[] Categories;
        public Button[] CategoryButtons;
        public GameObject[] CategoryHighlights;
        public Selectable[] DisplayControls, QualityControls, LightingControls, EffectsControls;
        private List<int> _renderValues;
        private List<GraphicsSettingsPreset> _presets;
        private GraphicsModeConfiguration _config;
        private GraphicsSettingsState _stagedState, _initialUiState;
        private int _initialPresetID, _category;
        private bool _ready;
        private Button _back, _apply;
        private static readonly Func<GraphicsSettingInt, int, string> NativeDisplay =
            (Func<GraphicsSettingInt, int, string>)Delegate.CreateDelegate(typeof(Func<GraphicsSettingInt, int, string>),
            typeof(Valheim.SettingsGui.GraphicsSettings).GetMethod("GetDisplayValue", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public));

        private Resolution _previousResolution;
        private FullScreenMode _previousMode;
        private bool _testing;
        private OkActionCompletedHandler _callback;
        public event Action<string, int> SharedSettingChanged { add { } remove { } }

        public void Initialize()
        {
            _ready = false;
            ValidateBindings();
            string phase = "lecture des reglages du jeu";
            try { InitializeValues(ref phase); }
            catch (Exception error)
            {
                throw new InvalidOperationException("AugaSettings/Graphics : echec pendant " + phase + ".", error);
            }
        }

        private void InitializeValues(ref string phase)
        {
            var state = GraphicsSettingsManager.Instance.GetCurrentSettingsWithCurrentPresetApplied(false);
            _stagedState = state;
            phase = "initialisation des sliders";
            _initialSliderValues = new float[Sliders.Length];
            for (int i = 0; i < Sliders.Length; i++)
            {
                int value = state.GetValue(SliderSettings[i]);
                if (SliderSettings[i] == GraphicsSettingInt.FpsLimit && value < 30) value = 361;
                Sliders[i].SetValueWithoutNotify(value);
                _initialSliderValues[i] = Sliders[i].value;
            }
            phase = "initialisation des cases a cocher";
            _initialToggleValues = new bool[Toggles.Length];
            for (int i = 0; i < Toggles.Length; i++)
            {
                _initialToggleValues[i] = state.GetValue(ToggleSettings[i]);
                Toggles[i].SetIsOnWithoutNotify(_initialToggleValues[i]);
            }
            phase = "lecture de la resolution actuelle";
            var current = PresentManager.GetCurrentPresentResolution();
            _resolutions = Screen.resolutions.OrderByDescending(r => r.width).ThenByDescending(r => r.height)
                .ThenByDescending(r => r.refreshRateRatio.value).ToList();
            if (!_resolutions.Any(r => SameResolution(r, current))) _resolutions.Insert(0, current);
            phase = "remplissage du menu des resolutions";
            ResolutionDropdown.ClearOptions();
            ResolutionDropdown.AddOptions(_resolutions.Select(r => r.width + "x" + r.height + " " + r.refreshRateRatio.value.ToString("0.##") + " Hz").ToList());
            ResolutionDropdown.SetValueWithoutNotify(_resolutions.FindIndex(r => SameResolution(r, current)));
            ResolutionDropdown.RefreshShownValue();
            Fullscreen.SetIsOnWithoutNotify(Screen.fullScreen);
            ResolutionDropdown.interactable = Fullscreen.interactable = GraphicsSettingsManager.CanChangePresentSettings();
            phase = "initialisation du dialogue et des libelles";
            Confirmation.gameObject.SetActive(false);
            InitializeNewOptions(state);
            _initialUiState = ReadState();
            _initialPresetID = SelectedPresetID;
            _ready = true;
            SelectCategory(0);
            OnValuesChanged();
        }

        private void ValidateBindings()
        {
            if (!GraphicsSettingsManager.Instance)
                throw new InvalidOperationException("AugaSettings/Graphics : GraphicsSettingsManager.Instance est absent a l'ouverture.");
            Require(ResolutionDropdown, nameof(ResolutionDropdown));
            Require(ResolutionDropdown.captionText, "ResolutionDropdown.captionText");
            Require(ResolutionDropdown.itemText, "ResolutionDropdown.itemText");
            Require(ResolutionDropdown.template, "ResolutionDropdown.template");
            Require(Fullscreen, nameof(Fullscreen));
            Require(TestResolution, nameof(TestResolution));
            Require(RenderResolution, nameof(RenderResolution));
            Require(Upscaling, nameof(Upscaling));
            Require(Preset, nameof(Preset));
            Require(Confirmation, nameof(Confirmation));
            Require(Confirmation.Countdown, "Confirmation.Countdown");
            Require(Confirmation.Accept, "Confirmation.Accept");
            if (Sliders == null || Toggles == null || SliderValues == null || SliderSettings == null || ToggleSettings == null
                || Sliders.Length == 0 || Toggles.Length == 0 || Sliders.Length != SliderValues.Length
                || Sliders.Length != SliderSettings.Length || Toggles.Length != ToggleSettings.Length)
                throw new InvalidOperationException("AugaSettings/Graphics : tableaux de references Unity absents ou de tailles differentes dans le prefab charge.");
            for (int i = 0; i < Sliders.Length; i++)
            {
                Require(Sliders[i], "Sliders[" + i + "]");
                Require(SliderValues[i], "SliderValues[" + i + "]");
            }
            for (int i = 0; i < Toggles.Length; i++)
            {
                Require(Toggles[i], "Toggles[" + i + "]");
            }
        }

        private static void Require(UnityEngine.Object reference, string field)
        {
            if (!reference) throw new InvalidOperationException("AugaSettings/Graphics : reference manquante dans le prefab charge : " + field);
        }
        public void OnValuesChanged()
        {
            for (int i = 0; i < Sliders.Length; i++) SliderValues[i].text = DisplayValue(SliderSettings[i], (int)Sliders[i].value);
            TestResolution.interactable = !_testing && ResolutionChanged();
            if (_ready) SelectCategory(_category);
        }

        public void OnOkAsync(OkActionCompletedHandler callback)
        {
            var state = ReadState();
            if (!state.Equals(_initialUiState) || SelectedPresetID != _initialPresetID)
            {
                var selected = _presets[Preset.value];
                if (selected) GraphicsSettingsManager.Instance.SaveAndApplyGraphicsSettingsWithPreset(ref state, selected.m_type);
                else GraphicsSettingsManager.Instance.SaveAndApplyGraphicsSettingsCustom(ref state);
            }
            if (ResolutionChanged())
            {
                _callback = callback;
                OnTestResolution();
            }
            else callback?.Invoke();
        }

        public void OnTestResolution()
        {
            if (_testing || !ResolutionChanged()) return;
            _previousResolution = PresentManager.GetCurrentPresentResolution();
            _previousMode = Screen.fullScreenMode;
            _testing = true;
            var selected = _resolutions[ResolutionDropdown.value];
            Screen.SetResolution(selected.width, selected.height, Fullscreen.isOn ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed, selected.refreshRateRatio);
            Settings.instance.BlockNavigation(true);
            Confirmation.gameObject.SetActive(true);
            EventSystem.current.SetSelectedGameObject(Confirmation.Accept.gameObject);
        }

        public void AcceptResolution() { FinishResolution(true, true); }
        public void RejectResolution() { FinishResolution(false, true); }
        public void OnBack() { FinishResolution(false, false); }
        public void Terminate() { FinishResolution(false, false); }
        public void OnSharedSettingChanged(string setting, int value) { }
        public void OnTabOpen(Button backButton, Button okButton)
        {
            _back = backButton; _apply = okButton; SelectCategory(_category);
        }

        private void FinishResolution(bool accept, bool complete)
        {
            if (!_testing) return;
            _testing = false;
            if (!accept)
            {
                Screen.SetResolution(_previousResolution.width, _previousResolution.height, _previousMode, _previousResolution.refreshRateRatio);
                ResolutionDropdown.SetValueWithoutNotify(_resolutions.FindIndex(r => SameResolution(r, _previousResolution)));
                Fullscreen.SetIsOnWithoutNotify(_previousMode != FullScreenMode.Windowed);
            }
            Confirmation.gameObject.SetActive(false);
            Settings.instance.BlockNavigation(false);
            var callback = _callback;
            _callback = null;
            OnValuesChanged();
            if (complete) callback?.Invoke();
        }

        private bool ResolutionChanged()
        {
            return _resolutions != null && GraphicsSettingsManager.CanChangePresentSettings()
                && (!SameResolution(_resolutions[ResolutionDropdown.value], PresentManager.GetCurrentPresentResolution()) || Fullscreen.isOn != Screen.fullScreen);
        }

        private static bool SameResolution(Resolution a, Resolution b)
        {
            return a.width == b.width && a.height == b.height && Math.Abs(a.refreshRateRatio.value - b.refreshRateRatio.value) < 0.01;
        }

        private static string DisplayValue(GraphicsSettingInt setting, int value) => NativeDisplay(setting, value);

        private int SelectedPresetID => _presets[Preset.value] ? _presets[Preset.value].m_type.ID : 100;
        private GraphicsSettingsState ReadState()
        {
            var state = _stagedState;
            for (int i = 0; i < Sliders.Length; i++) state.SetValue(SliderSettings[i], (int)Sliders[i].value);
            for (int i = 0; i < Toggles.Length; i++) state.SetValue(ToggleSettings[i], Toggles[i].isOn);
            state.SetValue(GraphicsSettingInt.Target3DResolutionVertical, _renderValues[RenderResolution.value]);
            state.SetValue(GraphicsSettingInt.UpscalingAlgorithm, Upscaling.value);
            return state;
        }
        private void InitializeNewOptions(GraphicsSettingsState state)
        {
            _config = GraphicsSettingsManager.Instance.GetCurrentGraphicsModeConfiguration(false);
            _presets = new List<GraphicsSettingsPreset>();
            if (_config == null || _config.HasCustomPreset || _config.Presets.Count == 0) _presets.Add(null);
            if (_config != null) _presets.AddRange(_config.Presets);
            Preset.ClearOptions();
            Preset.AddOptions(_presets.Select(p => Localization.instance.Localize(p ? p.m_type.NameTextId : "$settings_quality_mode_custom")).ToList());
            int presetIndex = _presets.FindIndex(p => (p ? p.m_type.ID : 100) == GraphicsSettingsManager.Instance.CurrentPresetID);
            Preset.SetValueWithoutNotify(Math.Max(0, presetIndex));
            Upscaling.ClearOptions();
            Upscaling.AddOptions(Enum.GetValues(typeof(UpscalingAlgorithm)).Cast<UpscalingAlgorithm>().Select(a => a.ToDisplayName()).ToList());
            Upscaling.SetValueWithoutNotify(state.GetValue(GraphicsSettingInt.UpscalingAlgorithm));
            _renderValues = new List<int> { int.MaxValue, 2160, 1800, 1600, 1440, 1200, 1080, 900, 800, 720, 600, 480, 360, 240, 192, 160 };
            if (UpscaledFrameBuffer.AutomaticRenderScaleSupported()) _renderValues.Add(0);
            int height = Screen.height;
            for (int i = 2; i <= (height - 1) / 240; i++) if (height % i == 0 && !_renderValues.Contains(height / i)) _renderValues.Add(height / i);
            EnsureRenderValue(state.GetValue(GraphicsSettingInt.Target3DResolutionVertical));
            foreach (var slider in Sliders) slider.onValueChanged.AddListener(OnQualityFloatChanged);
            foreach (var toggle in Toggles) toggle.onValueChanged.AddListener(OnQualityBoolChanged);
            RenderResolution.onValueChanged.AddListener(OnQualityIntChanged);
            Upscaling.onValueChanged.AddListener(OnQualityIntChanged);
            Preset.onValueChanged.AddListener(OnPresetChanged);
            if (_config != null)
            {
                for (int i = 0; i < Sliders.Length; i++) Sliders[i].interactable = SliderSettings[i].IsPresentSetting() ? GraphicsSettingsManager.CanChangePresentSettings() : _config.HasCustomPreset || _config.CanCustomizeGraphicsSetting(SliderSettings[i]);
                for (int i = 0; i < Toggles.Length; i++) Toggles[i].interactable = ToggleSettings[i].IsPresentSetting() ? GraphicsSettingsManager.CanChangePresentSettings() : _config.HasCustomPreset || _config.CanCustomizeGraphicsSetting(ToggleSettings[i]);
                RenderResolution.interactable = _config.HasCustomPreset || _config.CanCustomizeGraphicsSetting(GraphicsSettingInt.Target3DResolutionVertical);
                Upscaling.interactable = _config.HasCustomPreset || _config.CanCustomizeGraphicsSetting(GraphicsSettingInt.UpscalingAlgorithm);
            }
        }
        private void EnsureRenderValue(int value)
        {
            if (!_renderValues.Contains(value)) _renderValues.Add(value);
            _renderValues.Sort((a,b) => (b == 0 ? int.MaxValue - 1 : b).CompareTo(a == 0 ? int.MaxValue - 1 : a));
            RenderResolution.ClearOptions();
            RenderResolution.AddOptions(_renderValues.Select(v => v == int.MaxValue ? Localization.instance.Localize("$settings_native") : v == 0 ? Localization.instance.Localize("$settings_automatic") : v + "p").ToList());
            RenderResolution.SetValueWithoutNotify(_renderValues.IndexOf(value));
        }
        private void OnQualityFloatChanged(float value) => CheckCustomPreset();
        private void OnQualityBoolChanged(bool value) => CheckCustomPreset();
        private void OnQualityIntChanged(int value) => CheckCustomPreset();
        private void CheckCustomPreset()
        {
            if (!_ready) return;
            var preset = _presets[Preset.value];
            if (preset && _config.HasCustomPreset)
            {
                var state = ReadState();
                bool matches = true;
                foreach (GraphicsSettingInt setting in Enum.GetValues(typeof(GraphicsSettingInt)))
                    if (setting != GraphicsSettingInt.None && !setting.IsPresentSetting() && !_config.CanCustomizeGraphicsSetting(setting) && preset.TryGetQualitySetting(setting, out int expected) && state.GetValue(setting) != expected) matches = false;
                foreach (GraphicsSettingBool setting in Enum.GetValues(typeof(GraphicsSettingBool)))
                    if (setting != GraphicsSettingBool.None && !setting.IsPresentSetting() && !_config.CanCustomizeGraphicsSetting(setting) && preset.TryGetQualitySetting(setting, out bool expected) && state.GetValue(setting) != expected) matches = false;
                if (!matches) Preset.SetValueWithoutNotify(_presets.IndexOf(null));
            }
            OnValuesChanged();
        }
        public void OnPresetChanged(int index)
        {
            if (!_ready || index < 0 || index >= _presets.Count) return;
            var state = ReadState();
            var preset = _presets[index];
            if (preset) GraphicsSettingsManager.Instance.SetGraphicsSettingsFromPreset(_config, ref state, preset);
            _stagedState = state;
            for (int i = 0; i < Sliders.Length; i++) { int v = state.GetValue(SliderSettings[i]); Sliders[i].SetValueWithoutNotify(SliderSettings[i] == GraphicsSettingInt.FpsLimit && v < 30 ? 361 : v); }
            for (int i = 0; i < Toggles.Length; i++) Toggles[i].SetIsOnWithoutNotify(state.GetValue(ToggleSettings[i]));
            EnsureRenderValue(state.GetValue(GraphicsSettingInt.Target3DResolutionVertical));
            Upscaling.SetValueWithoutNotify(state.GetValue(GraphicsSettingInt.UpscalingAlgorithm));
            OnValuesChanged();
        }
        public void SelectCategory(int index)
        {
            if (Categories == null || index < 0 || index >= Categories.Length) return;
            _category = index;
            var group = GetComponent<UIGroupHandler>(); if (group) group.m_defaultElement = CategoryButtons[index].gameObject;
            for (int i = 0; i < Categories.Length; i++) { bool active = i == index; Categories[i].alpha = active ? 1 : 0; Categories[i].interactable = active; Categories[i].blocksRaycasts = active; CategoryHighlights[i].SetActive(active); }
            var controls = (index == 0 ? DisplayControls : index == 1 ? QualityControls : index == 2 ? LightingControls : EffectsControls).Where(c => c.IsInteractable()).ToArray();
            if (controls.Length == 0) return;
            for (int i = 0; i < controls.Length; i++) controls[i].navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnUp = i > 0 ? controls[i-1] : CategoryButtons[index], selectOnDown = i + 1 < controls.Length ? controls[i+1] : _back, selectOnLeft = CategoryButtons[index] };
            for (int i = 0; i < CategoryButtons.Length; i++) CategoryButtons[i].navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnUp = i > 0 ? CategoryButtons[i-1] : null, selectOnDown = i + 1 < CategoryButtons.Length ? CategoryButtons[i+1] : _back, selectOnRight = controls[0] };
            foreach (var footer in new[] { _back, _apply }) if (footer) { var n = footer.navigation; n.selectOnUp = controls.Last(); footer.navigation = n; }
        }
    }
}

