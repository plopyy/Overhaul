using Valheim.SettingsGui;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace AugaUnity
{
    public class AugaControlsSettings : KeyboardMouseSettings, ISettingsTab
    {
        public UnityEngine.UI.Slider ScrollSpeed;
        public TMPro.TMP_Text ScrollSpeedValue;
        public UnityEngine.UI.ScrollRect[] BindingPanels;
        public CanvasGroup[] Categories;
        public Button[] CategoryButtons;
        public GameObject[] CategoryHighlights;
        public Button ResetButton;
        public UIGroupHandler CategoryGroup;
        public GameObject BindingDialog;
        private int _category;
        private Button _back, _apply;


        // Keep the native binding rows active: Valheim searches them even when their category is hidden.
        // CanvasGroup hides a page and prevents interaction without breaking those references.
        public void SelectCategory(int index)
        {
            if (Categories == null || index < 0 || index >= Categories.Length) return;
            _category = index;
            for (int i = 0; i < Categories.Length; i++)
            {
                bool visible = i == index;
                Categories[i].alpha = visible ? 1 : 0;
                Categories[i].interactable = visible;
                Categories[i].blocksRaycasts = visible;
                CategoryHighlights[i].SetActive(visible);
            }
            RefreshCategoryNavigation();
        }

        private void RefreshCategoryNavigation()
        {
            if (Categories == null || Categories.Length == 0) return;
            var controls = Categories[_category].GetComponentsInChildren<Selectable>(true)
                .Where(s => s.transform.lossyScale.x > .001f).ToArray();
            for (int i = 0; i < controls.Length; i++)
            {
                var n = new Navigation { mode = Navigation.Mode.Explicit };
                n.selectOnUp = i > 0 ? controls[i - 1] : CategoryButtons[_category];
                n.selectOnDown = i + 1 < controls.Length ? controls[i + 1] : ResetButton;
                n.selectOnLeft = CategoryButtons[_category];
                controls[i].navigation = n;
            }
            for (int i = 0; i < CategoryButtons.Length; i++)
            {
                var n = new Navigation { mode = Navigation.Mode.Explicit };
                n.selectOnUp = i > 0 ? CategoryButtons[i - 1] : null;
                n.selectOnDown = i + 1 < CategoryButtons.Length ? CategoryButtons[i + 1] : ResetButton;
                n.selectOnRight = controls.FirstOrDefault();
                CategoryButtons[i].navigation = n;
            }
            var resetNav = ResetButton.navigation;
            resetNav.mode = Navigation.Mode.Explicit;
            resetNav.selectOnUp = controls.LastOrDefault();
            resetNav.selectOnLeft = CategoryButtons[_category];
            resetNav.selectOnDown = _back;
            ResetButton.navigation = resetNav;
            foreach (var footer in new[] { _back, _apply })
                if (footer) { var n = footer.navigation; n.selectOnUp = ResetButton; footer.navigation = n; }
        }

        public new void OnTabOpen(Button backButton, Button okButton)
        {
            _back = backButton; _apply = okButton;
            SelectCategory(_category);
        }

        private void LateUpdate()
        {
            // Native rebinding resets this to the mouse slider, which may be in a hidden sub-page.
            if (CategoryGroup && (!BindingDialog || !BindingDialog.activeSelf))
                CategoryGroup.m_defaultElement = CategoryButtons[_category].gameObject;
        }
        public void OnScrollSpeedChanged() { ScrollSpeedValue.text = ScrollSpeed.value.ToString("0") + "%"; }
        public void OnScrollSpeedValueChanged(float value) { OnScrollSpeedChanged(); }
        public void Terminate() { }

        public new void Initialize()
        {
            base.Initialize();
            ScrollSpeed.SetValueWithoutNotify(PlatformPrefs.GetFloat("AugaMouseScrollSpeed", 100f));
            OnScrollSpeedChanged();
            SelectCategory(0);
        }

        public new void OnOkAsync(OkActionCompletedHandler callback)
        {
            PlatformPrefs.SetFloat("AugaMouseScrollSpeed", ScrollSpeed.value);
            base.OnOkAsync(callback);
        }
    }
}



