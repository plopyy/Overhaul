using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Auga
{
    // XPortal owns portal data, input blocking, permissions and networking. Only its view is replaced.
    internal static class XPortalCompatibility
    {
        internal static GameObject Prefab;
        private static readonly Type panelType = typeof(XPortal.UI.PortalConfigurationPanel);
        internal static void Install(Harmony owner)
        {
            owner.Patch(AccessTools.DeclaredMethod(panelType, "InitialiseUI"), prefix: new HarmonyMethod(typeof(XPortalCompatibility), nameof(Initialise)));
        }
        internal static void StopWatching() { }
        private static bool Initialise(object __instance)
        {
            if (AccessTools.Field(panelType, "mainPanel").GetValue(__instance) is GameObject existing && existing) return false;
            var parent = GameObject.Find("_GameMain/LoadingGUI/CustomGUIFront");
            if (!Prefab || !parent) return true;
            GameObject view = null;
            try
            {
                view = UnityEngine.Object.Instantiate(Prefab, parent.transform, false);
                view.name = "XPortal_MainPanel";
                var input = view.GetComponentInChildren<InputField>(true);
                var dropdown = view.GetComponentInChildren<Dropdown>(true);
                var toggle = view.GetComponentsInChildren<Toggle>(true).Single(t => t.name == "XPortal_DefaultPortalCheckbox");
                var buttons = view.GetComponentsInChildren<Button>(true);
                var ping = buttons.Single(b => b.name == "XPortal_PingMapButton");
                var ok = buttons.Single(b => b.name == "XPortal_OkayButton");
                var cancel = buttons.Single(b => b.name == "XPortal_CancelButton");
                var hint = view.GetComponentsInChildren<Transform>(true).Single(t => t.name == "XPortal_DestinationGamepadHint").gameObject;
                // Reuse XPortal's own Jotunn input icon; its controller manages visibility.
                hint.GetComponent<Image>().sprite = InputIcon();
                if (!input || !dropdown || !toggle) throw new InvalidOperationException("Incomplete XPortal prefab bindings");
                Bind(__instance, ok, "OnOkayButtonClicked"); Bind(__instance, cancel, "OnCancelButtonClicked"); Bind(__instance, ping, "OnPingMapButtonClicked");
                Hint(__instance, dropdown.gameObject, "JoyButtonX", KeyCode.None);
                Hint(__instance, ping.gameObject, "JoyButtonY", KeyCode.None);
                Hint(__instance, toggle.gameObject, "JoyLStick", KeyCode.None);
                Hint(__instance, ok.gameObject, "JoyButtonA", KeyCode.Return);
                Hint(__instance, cancel.gameObject, "JoyButtonB", KeyCode.Escape);
                Set(__instance, "portalNameInputField", input);
                Set(__instance, "targetPortalDropdown", dropdown);
                Set(__instance, "targetPortalDropdownObject", dropdown.gameObject);
                Set(__instance, "pingMapButtonObject", ping.gameObject);
                Set(__instance, "defaultPortalToggle", toggle);
                Set(__instance, "targetPortalDropdownUpDownKeyhint", hint);
                Localization.instance.Localize(view.transform);
                view.SetActive(false);
                Set(__instance, "mainPanel", view);
                return false;
            }
            catch (Exception error)
            {
                if (view) { view.SetActive(false); UnityEngine.Object.Destroy(view); }
                Debug.LogWarning("[Auga] Unable to bind XPortal prefab: " + error);
                return true;
            }
        }
        private static void Set(object owner, string name, object value) => AccessTools.Field(panelType, name).SetValue(owner, value);
        private static Sprite InputIcon()
        {
            var managerType = AccessTools.TypeByName("Jotunn.Managers.GUIManager");
            var manager = AccessTools.Property(managerType, "Instance").GetValue(null, null);
            return (Sprite)AccessTools.Method(managerType, "GetSprite", new[] { typeof(string) }).Invoke(manager, new object[] { "dpad_updown" });
        }
        private static void Bind(object owner, Button button, string method)
        {
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener((UnityAction)Delegate.CreateDelegate(typeof(UnityAction), owner, AccessTools.DeclaredMethod(panelType, method)));
        }
        private static void Hint(object owner, GameObject control, string key, KeyCode code) =>
            AccessTools.DeclaredMethod(panelType, "AddGamepadHint").Invoke(owner, new object[] { control, key, code });
    }
}
