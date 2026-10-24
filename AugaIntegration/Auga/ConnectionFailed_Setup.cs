using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Auga
{
    // The main menu's "connection failed / disconnected" window in Auga's look (AugaConnectionFailed, OverhaulLab
    // ConnectionFailedAuthoring) in place of the game's one. The full Auga main menu (MainMenu module, off by default)
    // used to replace it; this installs it on its own, as the character and world selections are.
    [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.Awake))]
    [UiModule(UiModule.WorldSelection)]
    public static class ConnectionFailed_Setup
    {
        public static void Postfix(FejdStartup __instance)
        {
            if (UiModules.Enabled(UiModule.MainMenu) || !Auga.Assets.ConnectionFailedDialog) return;
            var original = __instance.m_connectionFailedPanel;
            if (!original) return;
            var dialog = Object.Instantiate(Auga.Assets.ConnectionFailedDialog, original.transform.parent, false);
            dialog.name = "ConnectionFailed";
            dialog.SetActive(false);
            __instance.m_connectionFailedPanel = dialog;
            __instance.m_connectionFailedError = dialog.transform.Find("Text").GetComponent<TMP_Text>();
            dialog.transform.Find("ButtonYes").GetComponent<Button>().onClick.AddListener(__instance.OnConnectionFailedOk);
            Object.Destroy(original);
        }
    }
}
