using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AugaUnity
{
    // Split stack dialog: typed amount over the slider, flanked by minimum / minus and plus / maximum
    // buttons. They all drive the native slider, which the game reads when the split is accepted
    // (it opens at half the stack).
    public class SplitAmountControls : MonoBehaviour
    {
        public Slider Slider;
        public TMP_InputField Amount;
        public TMP_Text Total;
        public Button Minus;
        public Button Plus;
        public Button Min;
        public Button Max;
        public Button Ok;

        private static SplitAmountControls _typing;
        private float _min = -1, _max = -1, _value = -1;

        // While the amount is typed in, the game's keys (closing the inventory...) must not act.
        public static bool Typing => _typing && _typing.isActiveAndEnabled && _typing.Amount.isFocused;

        public void Awake()
        {
            Minus.onClick.AddListener(() => Set(Slider.value - 1));
            Plus.onClick.AddListener(() => Set(Slider.value + 1));
            Min.onClick.AddListener(() => Set(Slider.minValue));
            Max.onClick.AddListener(() => Set(Slider.maxValue));
            Amount.contentType = TMP_InputField.ContentType.IntegerNumber;
            Amount.onSelect.AddListener(_ => _typing = this);
            Amount.onValueChanged.AddListener(OnTyped);
            Amount.onEndEdit.AddListener(_ => Refresh());
            // Enter accepts the split with the typed amount.
            Amount.onSubmit.AddListener(text => { OnTyped(text); Ok.onClick.Invoke(); });
        }

        public void OnDisable()
        {
            if (_typing == this) _typing = null;
        }

        private void OnTyped(string text)
        {
            if (!int.TryParse(text, out var amount)) return;
            Slider.SetValueWithoutNotify(Mathf.Clamp(amount, Slider.minValue, Slider.maxValue));
            Slider.onValueChanged.Invoke(Slider.value);
            UpdateButtons();
        }

        private void Set(float value)
        {
            Slider.value = Mathf.Clamp(Mathf.Round(value), Slider.minValue, Slider.maxValue);
            Refresh();
        }

        // The game sets the slider's limits and value itself (UpdateLimits) without notifying.
        public void LateUpdate()
        {
            if (Slider.minValue == _min && Slider.maxValue == _max && Slider.value == _value) return;
            Refresh();
        }

        private void Refresh()
        {
            _min = Slider.minValue;
            _max = Slider.maxValue;
            _value = Slider.value;
            if (!Amount.isFocused) Amount.SetTextWithoutNotify(((int)_value).ToString());
            Total.text = "/ " + (int)_max;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            Min.interactable = Minus.interactable = Slider.value > Slider.minValue;
            Max.interactable = Plus.interactable = Slider.value < Slider.maxValue;
        }
    }
}
