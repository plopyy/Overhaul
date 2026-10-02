using System;
using AugaUnity;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using GUIFramework;

namespace Auga
{
    [UiModule(UiModule.CharacterSelection)]
    internal static class CharacterSelection_Setup
    {
        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.Awake))]
        private static class Install
        {
            private static void Prefix(FejdStartup __instance)
            {
                if (UiModules.Enabled(UiModule.MainMenu)) return; // Full-menu installation already owns this panel.
                Transform prefab = Auga.Assets.MainMenuPrefab.transform.Find("CharacterSelection/SelectCharacter");
                if (!prefab) throw new InvalidOperationException("Auga SelectCharacter prefab missing");
                var contract = prefab.GetComponentInChildren<AugaCharacterSelect>(true);
                Validate(contract);
                GameObject old = __instance.m_selectCharacterPanel;
                bool active = old.activeSelf;
                // Instantiate inactive: bindings must exist before OnEnable refreshes profiles.
                var host = new GameObject("Auga character selection install");
                host.SetActive(false);
                GameObject replacement = UnityEngine.Object.Instantiate(prefab.gameObject, host.transform, false);
                replacement.SetActive(false);
                replacement.name = old.name;
                replacement.transform.SetParent(old.transform.parent, false);
                replacement.transform.SetSiblingIndex(old.transform.GetSiblingIndex());
                var ui = replacement.GetComponentInChildren<AugaCharacterSelect>(true);
                __instance.m_selectCharacterPanel = replacement;
                __instance.m_removeCharacterDialog = ui.RemoveDialog;
                __instance.m_removeCharacterName = ui.RemoveName;
                __instance.m_csRemoveButton = ui.RemoveButton;
                __instance.m_csStartButton = ui.StartButton;
                __instance.m_csNewButton = ui.NewButton;
                __instance.m_csNewBigButton = ui.NewBigButton;
                __instance.m_csLeftButton = ui.LeftButton;
                __instance.m_csRightButton = ui.RightButton;
                __instance.m_csName = ui.NativeName;
                __instance.m_csFileSource = ui.NativeFileSource;
                __instance.m_csSourceInfo = ui.NativeSourceInfo;
                Bind(ui.BackButton, __instance.OnSelelectCharacterBack);
                Bind(ui.StartButton, __instance.OnCharacterStart);
                Bind(ui.RemoveButton, __instance.OnCharacterRemove);
                Bind(ui.NewButton, __instance.OnCharacterNew);
                Bind(ui.NewBigButton, __instance.OnCharacterNew);
                Bind(ui.ManageSavesButton, () => __instance.OnManageSaves(1));
                Bind(ui.RemoveYesButton, __instance.OnButtonRemoveCharacterYes);
                Bind(ui.RemoveNoButton, __instance.OnButtonRemoveCharacterNo);
                old.SetActive(false);
                DestroyInstance(old);
                DestroyInstance(host);
                replacement.SetActive(active);
                var booth = Auga.Assets.MainMenuPrefab.GetComponentInChildren<AugaCharacterSelectPhotoBooth>(true);
                if (booth) UnityEngine.Object.Instantiate(booth, __instance.transform);
                if (Localization.instance != null) Localization.instance.Localize(replacement.transform);
                InstallCreation(__instance);
            }
        }

        private static void InstallCreation(FejdStartup startup)
        {
            var original = startup.m_newCharacterPanel;
            var oldCustomization = original.GetComponent<PlayerCustomizaton>();
            var source = Auga.Assets.MainMenuPrefab.transform.Find("CharacterSelection/NewCharacterPanel");
            var host = new GameObject("Auga creation install"); host.SetActive(false);
            var panel = UnityEngine.Object.Instantiate(source.gameObject, host.transform, false);
            panel.SetActive(false);
            var custom = panel.GetComponent<PlayerCustomizaton>();
            custom.m_noHair = oldCustomization.m_noHair;
            custom.m_noBeard = oldCustomization.m_noBeard;
            custom.m_hairToolTier = oldCustomization.m_hairToolTier;
            custom.m_maleToggle.onValueChanged = new Toggle.ToggleEvent();
            custom.m_maleToggle.onValueChanged.AddListener(on => { if (on) custom.SetPlayerModel(0); custom.m_maleToggle.transform.GetChild(1).gameObject.SetActive(on); });
            custom.m_femaleToggle.onValueChanged = new Toggle.ToggleEvent();
            custom.m_femaleToggle.onValueChanged.AddListener(on => { if (on) custom.SetPlayerModel(1); custom.m_femaleToggle.transform.GetChild(1).gameObject.SetActive(on); });
            if (!custom.m_selectedHair || !custom.m_selectedBeard || !custom.m_beardPanel)
                throw new InvalidOperationException("Auga creation prefab bindings are incomplete");
            startup.m_newCharacterPanel = panel;
            startup.m_csNewCharacterDone = panel.transform.Find("Panel/Done").GetComponent<Button>();
            startup.m_csNewCharacterName = panel.transform.Find("Panel/Content/CharacterName").GetComponent<GuiInputField>();
            startup.m_newCharacterError = panel.transform.Find("Panel/Content/NameExistsWarning").gameObject;
            Bind(startup.m_csNewCharacterDone, () => startup.OnNewCharacterDone(false));
            Bind(panel.transform.Find("Panel/Cancel").GetComponent<Button>(), startup.OnNewCharacterCancel);
            original.SetActive(false);
            panel.transform.SetParent(original.transform.parent, false);
            panel.transform.SetSiblingIndex(original.transform.GetSiblingIndex());
            panel.name = original.name;
            DestroyInstance(original); DestroyInstance(host);
            if (Localization.instance != null) Localization.instance.Localize(panel.transform);
        }

        private static void Validate(AugaCharacterSelect ui)
        {
            if (!ui || !ui.CharacterPortraitPrefab || !ui.CharacterList || !ui.ScrollBar
                || !ui.BackButton || !ui.StartButton || !ui.NewButton || !ui.NewBigButton
                || !ui.RemoveButton || !ui.ManageSavesButton || !ui.RemoveYesButton || !ui.RemoveNoButton
                || !ui.RemoveDialog || !ui.RemoveName || !ui.LeftButton || !ui.RightButton
                || !ui.NativeName || !ui.NativeSourceInfo || !ui.NativeFileSource)
                throw new InvalidOperationException("Auga SelectCharacter prefab has incomplete bindings");
        }
        private static void Bind(Button button, UnityAction action)
        {
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(action);
        }
        private static void DestroyInstance(GameObject instance)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(instance);
            else UnityEngine.Object.DestroyImmediate(instance);
        }
        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.UpdateCharacterList))]
        private static class Refresh
        {
            private static void Postfix(FejdStartup __instance)
            {
                if (UiModules.Enabled(UiModule.MainMenu)) return;
                var ui = __instance.m_selectCharacterPanel.GetComponentInChildren<AugaCharacterSelect>(true);
                if (ui) ui.UpdateCharacterList();
            }
        }
    }
}
