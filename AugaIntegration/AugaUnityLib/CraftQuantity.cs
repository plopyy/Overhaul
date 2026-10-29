using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AugaUnity
{
    // Minus / amount / plus row above the craft button. Amount stays within 1..Max,
    // where Max is how many crafts the player's resources (bag and nearby chests) allow.
    public class CraftQuantity : MonoBehaviour
    {
        public Button MinusButton;
        public Button PlusButton;
        public TMP_Text AmountText;

        public int Amount { get; private set; } = 1;
        public int Max { get; private set; } = 1;

        public void Awake()
        {
            MinusButton.onClick.AddListener(() => Set(Amount - 1));
            PlusButton.onClick.AddListener(() => Set(Amount + 1));
            Refresh();
        }

        public void SetMax(int max)
        {
            Max = Mathf.Max(1, max);
            Set(Amount);
        }

        public void ResetAmount()
        {
            Set(1);
        }

        private void Set(int amount)
        {
            Amount = Mathf.Clamp(amount, 1, Max);
            Refresh();
        }

        private void Refresh()
        {
            AmountText.text = Amount.ToString();
            MinusButton.interactable = Amount > 1;
            PlusButton.interactable = Amount < Max;
        }
    }
}
