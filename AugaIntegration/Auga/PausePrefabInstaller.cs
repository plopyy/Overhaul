using HarmonyLib;
using UnityEngine;

namespace Auga
{
    [UiModule(UiModule.PauseMenu)]
    internal static class PausePrefabInstaller
    {
        [HarmonyPatch(typeof(Menu), "Start")]
        internal static class Install
        {
            private static bool Prefix(Menu __instance)
            {
                if (__instance.name.StartsWith("AugaMenu")) return true;
                var original = __instance;
                var replacement = Object.Instantiate(Auga.Assets.MenuPrefab, original.transform.parent, false).GetComponent<Menu>();
                replacement.transform.SetSiblingIndex(original.transform.GetSiblingIndex());
                replacement.m_settingsPrefab = UiModules.Enabled(UiModule.Settings) ? Auga.Assets.SettingsPrefab : original.m_settingsPrefab;
                replacement.m_feedbackPrefab = original.m_feedbackPrefab;
                replacement.m_startScene = original.m_startScene;
                var players = AccessTools.Field(typeof(Menu), "CurrentPlayersPrefab");
                players.SetValue(replacement, players.GetValue(original));
                replacement.m_cloudStorageWarning = Preserve(original.m_cloudStorageWarning, original.transform, replacement.m_root);
                replacement.m_cloudStorageWarningNextSave = Preserve(original.m_cloudStorageWarningNextSave, original.transform, replacement.m_root);
                replacement.m_gamepadRoot = Preserve(original.m_gamepadRoot, original.transform, replacement.m_root);
                replacement.m_gamepadMapController = original.m_gamepadMapController;
                foreach (var button in replacement.m_root.GetComponentsInChildren<UnityEngine.UI.Button>(true))
                    for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
                        if (button.onClick.GetPersistentTarget(i) == original)
                        {
                            var method = AccessTools.Method(typeof(Menu), button.onClick.GetPersistentMethodName(i));
                            var action = (UnityEngine.Events.UnityAction)System.Delegate.CreateDelegate(typeof(UnityEngine.Events.UnityAction), replacement, method);
                            button.onClick.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.Off);
                            button.onClick.AddListener(action);
                        }
                CopyCanvas(original.gameObject, replacement.gameObject);
                original.gameObject.SetActive(false);
                Object.Destroy(original.gameObject);
                return false;
            }
        }

        internal static void CopyCanvas(GameObject original, GameObject replacement)
        {
                var canvas = original.GetComponent<Canvas>();
                if (canvas)
                {
                    var target = replacement.GetComponent<Canvas>() ?? replacement.AddComponent<Canvas>();
                    target.renderMode = canvas.renderMode;
                    target.worldCamera = canvas.worldCamera;
                    target.planeDistance = canvas.planeDistance;
                    target.overrideSorting = canvas.overrideSorting;
                    target.sortingLayerID = canvas.sortingLayerID;
                    target.sortingOrder = canvas.sortingOrder;
                    target.referencePixelsPerUnit = canvas.referencePixelsPerUnit;
                    if (!replacement.GetComponent<UnityEngine.UI.GraphicRaycaster>()) replacement.AddComponent<UnityEngine.UI.GraphicRaycaster>();
                    var scaler = original.GetComponent<UnityEngine.UI.CanvasScaler>();
                    if (scaler)
                    {
                        var copy = replacement.GetComponent<UnityEngine.UI.CanvasScaler>() ?? replacement.AddComponent<UnityEngine.UI.CanvasScaler>();
                        copy.uiScaleMode = scaler.uiScaleMode;
                        copy.referenceResolution = scaler.referenceResolution;
                        copy.screenMatchMode = scaler.screenMatchMode;
                        copy.matchWidthOrHeight = scaler.matchWidthOrHeight;
                        copy.scaleFactor = scaler.scaleFactor;
                        copy.referencePixelsPerUnit = scaler.referencePixelsPerUnit;
                    }
                }
        }

        private static GameObject Preserve(GameObject value, Transform original, Transform parent)
        {
            if (value && value.transform.IsChildOf(original)) value.transform.SetParent(parent, false);
            return value;
        }
    }
}
