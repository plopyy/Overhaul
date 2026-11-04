using System;
using System.Text;
using HarmonyLib;

namespace Overhaul.Utility
{
    // The client remembers the last password that worked for each server and answers the prompt by itself; a refused password
    // is forgotten, so the prompt comes back. Kept in the game's local preferences, only encoded: not a secure store.
    internal static class ServerPasswords
    {
        static string server;
        static string Key => "Overhaul.ServerPassword." + server.GetStableHashCode();

        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.JoinServer))]
        private static class Join { private static void Prefix(FejdStartup __instance) => server = __instance.m_joinServer.ToString(); }

        [HarmonyPatch(typeof(ZNet), "RPC_ClientHandshake")]
        private static class Answer
        {
            private static void Postfix(ZNet __instance, bool needPassword)
            {
                if (!needPassword || server == null || __instance.m_tempPasswordRPC == null) return;
                string saved = PlatformPrefs.GetString(Key, "");
                if (saved.Length > 0) __instance.OnPasswordEntered(Encoding.UTF8.GetString(Convert.FromBase64String(saved)));
            }
        }
        [HarmonyPatch(typeof(ZNet), "OnPasswordEntered")]
        private static class Entered
        {
            private static void Prefix(string pwd)
            {
                if (server != null && !string.IsNullOrEmpty(pwd)) PlatformPrefs.SetString(Key, Convert.ToBase64String(Encoding.UTF8.GetBytes(pwd)));
            }
        }
        [HarmonyPatch(typeof(ZNet), "RPC_Error")]
        private static class Refused
        {
            private static void Prefix(int error)
            {
                if (server != null && (ZNet.ConnectionStatus)error == ZNet.ConnectionStatus.ErrorPassword) PlatformPrefs.SetString(Key, "");
            }
        }
    }
}