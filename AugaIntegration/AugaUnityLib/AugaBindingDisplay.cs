using UnityEngine;
using UnityEngine.UI;
using System.Linq;

namespace AugaUnity
{
    public class AugaBindingDisplay : MonoBehaviour
    {
        public string AutomaticKeyName;

        public Text KeybindText;
        public GameObject KeybindBox;
        public Text LongKeybindText;
        public GameObject LongKeybindBox;
        public GameObject Mouse1;
        public GameObject Mouse2;
        public GameObject Mouse3;
        public GameObject MouseX;
        public Text MouseXText;

        public static bool IsAssignedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var normalized = path.TrimEnd('/');
            return !string.Equals(normalized, "<Keyboard>/None", System.StringComparison.OrdinalIgnoreCase)
                && !string.Equals(normalized, "/Keyboard/None", System.StringComparison.OrdinalIgnoreCase)
                && !string.Equals(normalized, "<Keyboard>", System.StringComparison.OrdinalIgnoreCase)
                && !string.Equals(normalized, "/Keyboard", System.StringComparison.OrdinalIgnoreCase);
        }

        public void Update()
        {
            if (!string.IsNullOrEmpty(AutomaticKeyName))
            {
                SetBinding(AutomaticKeyName);
            }
        }

        public void SetBinding(string keyName)
        {
            var button = ZInput.instance.GetButtonDef(keyName);
            if (button == null)
            {
                Debug.LogError($"[AugaBindingDisplay.SetBinding] Couldn't find key: {keyName}");
                return;
            }

            var action = button.ButtonAction;
            if (action == null || !action.bindings.Any(binding => IsAssignedPath(binding.effectivePath)))
            {
                SetText(Localization.instance.Localize("$auga_binding_none"), -1);
                return;
            }
            var localizedKeyString = Localization.instance.GetBoundKeyString(keyName);
            if (string.IsNullOrWhiteSpace(localizedKeyString))
                localizedKeyString = Localization.instance.Localize("$auga_binding_none");

            var showMouse = -1;
            var keycode = KeyCode.None;
            if (action != null)
            {
                foreach (var binding in action.bindings)
                {
                    var path = binding.effectivePath?.ToLowerInvariant();
                    if (!string.IsNullOrEmpty(path) && path.Contains("keyboard"))
                    {
                        var key = path.Substring(path.LastIndexOf('/') + 1).Replace("numpad", "keypad");
                        System.Enum.TryParse(key, true, out keycode);
                    }
                    if (string.IsNullOrEmpty(path) || !path.Contains("mouse")) continue;
                    if (path.EndsWith("/leftbutton")) showMouse = 0;
                    else if (path.EndsWith("/rightbutton")) showMouse = 1;
                    else if (path.EndsWith("/middlebutton")) showMouse = 2;
                    else if (path.EndsWith("/forwardbutton")) showMouse = 3;
                    else if (path.EndsWith("/backbutton")) showMouse = 4;
                    if (showMouse >= 0) break;
                }
            }

            switch (localizedKeyString)
            {
                case "Equals": localizedKeyString = "="; break;
                case "BackQuote": localizedKeyString = "`"; break;
            }

            if (localizedKeyString.StartsWith("Keypad"))
            {
                localizedKeyString = localizedKeyString.Replace("Keypad", "Num");
            }
            else if (localizedKeyString.StartsWith("Alpha"))
            {
                localizedKeyString = localizedKeyString.Replace("Alpha", "");
            }

            switch (keycode)
            {
                case KeyCode.KeypadDivide: localizedKeyString = localizedKeyString.Replace("Divide", "/"); break;
                case KeyCode.KeypadMinus: localizedKeyString = localizedKeyString.Replace("Minus", "-"); break;
                case KeyCode.KeypadMultiply: localizedKeyString = localizedKeyString.Replace("Multiply", "*"); break;
                case KeyCode.KeypadEquals: localizedKeyString = localizedKeyString.Replace("Equals", "="); break;
                case KeyCode.KeypadPeriod: localizedKeyString = localizedKeyString.Replace("Period", "."); break;
                case KeyCode.KeypadPlus: localizedKeyString = localizedKeyString.Replace("Plus", "+"); break;

                case KeyCode.LeftArrow: localizedKeyString = "←"; break;
                case KeyCode.RightArrow: localizedKeyString = "→"; break;
                case KeyCode.UpArrow: localizedKeyString = "↑"; break;
                case KeyCode.DownArrow: localizedKeyString = "↓"; break;
            }

            if (char.IsPunctuation((char)keycode))
            {
                localizedKeyString = ((char)keycode).ToString();
            }

            SetText(localizedKeyString, showMouse);
        }

        public void SetText(string localizedKeyString, int showMouse = -1)
        {
            if(string.IsNullOrWhiteSpace(localizedKeyString))
                localizedKeyString=Localization.instance.Localize("$auga_binding_none");
            var isOneCharLong = localizedKeyString.Length == 1;
            (isOneCharLong ? KeybindText : LongKeybindText).text = localizedKeyString;
            KeybindBox.SetActive(showMouse < 0 && isOneCharLong);
            LongKeybindBox.SetActive(showMouse < 0 && !isOneCharLong);

            Mouse1.SetActive(showMouse == 0);
            Mouse2.SetActive(showMouse == 1);
            Mouse3.SetActive(showMouse == 2);
            MouseX.SetActive(showMouse > 2);
            MouseXText.text = (showMouse + 1).ToString();
        }
    }
}
