using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AugaUnity
{
    // Lives on the existing Auga confirmation dialog, so its clock also runs when
    // Apply was pressed from a different settings tab. Uses unscaled time in menus.
    public class AugaResolutionConfirmation : MonoBehaviour
    {
        public AugaGraphicsSettings Settings;
        public TMP_Text Countdown;
        public Button Accept;
        private float _deadline;
        private void OnEnable() { _deadline = Time.unscaledTime + 5f; }
        private void Update()
        {
            float remaining = _deadline - Time.unscaledTime;
            Countdown.text = Mathf.Max(0, Mathf.CeilToInt(remaining)).ToString();
            if (remaining <= 0f || ZInput.GetButtonDown("JoyBack") || ZInput.GetButtonDown("JoyButtonB")
                || ZInput.GetKeyDown(KeyCode.Escape, true)) Settings.RejectResolution();
        }
    }
}
