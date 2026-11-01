using HarmonyLib;

namespace Overhaul.Utility
{
    // Start game opens on the tab last played from: the server list after joining a server, the worlds after a solo game.
    internal static class StartGameTab
    {
        const string Key = "Overhaul.LastPlayOnServer";

        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.OnWorldStart))]
        private static class Solo { private static void Prefix() => PlatformPrefs.SetInt(Key, 0); }
        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.JoinServer))]
        private static class Server { private static void Prefix() => PlatformPrefs.SetInt(Key, 1); }
        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.OnStartGame))]
        private static class Open
        {
            private static void Postfix(FejdStartup __instance)
            {
                if (PlatformPrefs.GetInt(Key, 0) != 1 || !__instance.m_startGamePanel.activeInHierarchy) return;
                var tabs = __instance.m_startGamePanel.GetComponentInChildren<TabHandler>();
                if (tabs) tabs.SetActiveTab(1, false, true);
            }
        }
    }
}
