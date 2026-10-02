using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;

namespace Auga
{
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Awake))]
    [UiModule(UiModule.WorldSelection)]
    public static class ZNet_Awake_Patch
    {
        [UsedImplicitly]
        public static void Postfix(ZNet __instance)
        {
            var original = __instance.m_passwordDialog;
            var parent = original.parent;
            var newPasswordDialog = Object.Instantiate(Auga.Assets.PasswordDialog, parent, false);
            newPasswordDialog.gameObject.SetActive(false);
            __instance.m_passwordDialog = newPasswordDialog.GetComponent<RectTransform>();
            Object.Destroy(original.gameObject);
            Debug.Log("[Auga] Password dialog installed; awaiting server handshake.");

            var originalConnecting = __instance.m_connectingDialog;
            var newConnectingDialog = Object.Instantiate(Auga.Assets.ConnectingDialog, originalConnecting.parent, false);
            newConnectingDialog.gameObject.SetActive(false);
            __instance.m_connectingDialog = newConnectingDialog.GetComponent<RectTransform>();
            Object.Destroy(originalConnecting.gameObject);
        }
    }

    [UiModule(UiModule.WorldSelection)]
    [HarmonyPatch(typeof(ZNet), "RPC_ClientHandshake")]
    internal static class PasswordHandshakeDiagnostic
    {
        private static void Prefix(bool __1)
        {
            Debug.Log("[Auga] Server handshake received; password required=" + __1);
        }

        private static void Postfix(ZNet __instance)
        {
            var dialog = __instance.m_passwordDialog;
            var input = dialog.GetComponentInChildren<GUIFramework.GuiInputField>(true);
            var canvas = dialog.GetComponentInParent<Canvas>();
            Debug.Log("[Auga] Password dialog after handshake: activeSelf=" + dialog.gameObject.activeSelf +
                ", activeInHierarchy=" + dialog.gameObject.activeInHierarchy +
                ", inputBound=" + (input && input.textComponent) +
                ", canvasEnabled=" + (canvas && canvas.isActiveAndEnabled));
        }
    }
}
