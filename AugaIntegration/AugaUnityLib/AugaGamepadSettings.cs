using UnityEngine;
using UnityEngine.UI;
using System.Reflection;
using System.Linq;
using TMPro;
using GUIFramework;
using Valheim.SettingsGui;

namespace AugaUnity
{
    // Native gamepad settings with prefab-authored Auga categories.
    public class AugaGamepadSettings : GamepadSettings, ISettingsTab
    {
        public CanvasGroup[] Categories;
        public Button[] CategoryButtons;
        public GameObject[] CategoryHighlights;
        public Selectable[] GeneralControls, CameraControls, CommandControls;
        private int _category;
        private Button _back, _apply;
        public void SelectCategory(int index)
        {
            if (Categories == null || index < 0 || index >= Categories.Length) return;
            _category = index;
            var group = GetComponent<UIGroupHandler>();
            if (group) group.m_defaultElement = CategoryButtons[index].gameObject;
            for (int i = 0; i < Categories.Length; i++)
            {
                bool active = i == index;
                Categories[i].alpha = active ? 1 : 0;
                Categories[i].interactable = active;
                Categories[i].blocksRaycasts = active;
                CategoryHighlights[i].SetActive(active);
            }
            var controls = index == 0 ? GeneralControls : index == 1 ? CameraControls : CommandControls;
            for (int i = 0; i < controls.Length; i++)
            {
                var n = new Navigation { mode = Navigation.Mode.Explicit };
                n.selectOnUp = i > 0 ? controls[i - 1] : CategoryButtons[index];
                n.selectOnDown = i + 1 < controls.Length ? controls[i + 1] : _back;
                n.selectOnLeft = CategoryButtons[index];
                if (index == 2) { n.selectOnRight = i == 0 ? controls[1] : null; n.selectOnLeft = i == 1 ? controls[0] : CategoryButtons[index]; n.selectOnDown = i == 0 ? _back : _apply; }
                controls[i].navigation = n;
            }
            for (int i = 0; i < CategoryButtons.Length; i++)
                CategoryButtons[i].navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnUp = i > 0 ? CategoryButtons[i - 1] : null, selectOnDown = i + 1 < CategoryButtons.Length ? CategoryButtons[i + 1] : _back, selectOnRight = controls[0] };
            if (_back) { var n = _back.navigation; n.selectOnUp = controls[index == 2 ? 0 : controls.Length - 1]; _back.navigation = n; }
            if (_apply) { var n = _apply.navigation; n.selectOnUp = controls[controls.Length - 1]; _apply.navigation = n; }
        }
        public Toggle ToggleRun;
        public GuiDropdown LayoutDropdown;
        public TMP_Text LayoutLabel;
        public TMP_Text GamepadTypeLabel;
        private static readonly FieldInfo CurrentLayout = typeof(GamepadSettings).GetField("m_currentControllerLayout", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        public new void Initialize()
        {
            base.Initialize();
            GamepadTypeLabel.text = Localization.instance.Localize("$auga_controller_type");
            LayoutLabel.text = Localization.instance.Localize("$auga_controller_layout");
            LayoutDropdown.ClearOptions();
            LayoutDropdown.AddOptions(Enumerable.Range(0, (int)InputLayout.Count).Select(i => Localization.instance.Localize(GamepadMapController.GetLayoutStringId((InputLayout)i))).ToList());
            LayoutDropdown.SetValueWithoutNotify((int)(InputLayout)CurrentLayout.GetValue(this));
            LayoutDropdown.onValueChanged.RemoveListener(OnLayoutChosen);
            LayoutDropdown.onValueChanged.AddListener(OnLayoutChosen);
            SelectCategory(0);
        }

        public new void OnOkAsync(OkActionCompletedHandler callback)
        {
            base.OnOkAsync(callback);
        }

        public void OnLayoutChosen(int value)
        {
            if (value < 0 || value >= (int)InputLayout.Count) return;
            CurrentLayout.SetValue(this, (InputLayout)value);
            OnLayoutChanged();
            LayoutDropdown.RefreshShownValue();
        }

        public new void OnBack()
        {
            base.OnBack();
            LayoutDropdown.SetValueWithoutNotify((int)(InputLayout)CurrentLayout.GetValue(this));
        }

        public new void OnTabOpen(Button backButton, Button okButton)
        {
            _back = backButton; _apply = okButton;
            SelectCategory(_category);
        }

        public void Terminate() { }
    }
}


