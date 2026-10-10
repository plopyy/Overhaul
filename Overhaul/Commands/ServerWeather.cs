using HarmonyLib;

namespace Overhaul.Commands
{
    // o_weather: an administrator forces the weather for the whole server. The request goes to the server, which checks
    // the sender is an administrator with devcommands, keeps the weather and sends it to every player; a player who
    // joins later asks the server for it. Lasts until o_weather reset or a server restart.
    internal static class ServerWeather
    {
        private const string RequestRpc = "Overhaul_WeatherRequest", ApplyRpc = "Overhaul_Weather", QueryRpc = "Overhaul_WeatherQuery";
        private static string current = "";

        // Client side: "" resets the weather.
        internal static void Request(string environment) =>
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RequestRpc, environment ?? "");

        private static void OnRequest(long sender, string environment)
        {
            if (!ZNet.instance || !ZNet.instance.IsServer()) return;
            bool host = sender == ZRoutedRpc.instance.m_id;
            ZNetPeer peer = host ? null : ZNet.instance.GetPeer(sender);
            if (host ? !AdminCommandAccess.LocalEnabled : peer == null || !AdminCommands.Authorized(peer.m_rpc))
            {
                Utility.Log.LogWarning("o_weather refused for peer " + sender + ": administrator with devcommands required");
                return;
            }
            current = environment ?? "";
            Utility.Log.LogInfo("Weather forced by " + (host ? "the host" : peer.m_playerName) + ": " + (current.Length > 0 ? current : "normal"));
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, ApplyRpc, current);
        }

        private static void OnApply(long sender, string environment)
        {
            if (sender != ZRoutedRpc.instance.GetServerPeerID() && !(ZNet.instance && ZNet.instance.IsServer())) return;
            if (EnvMan.instance) EnvMan.instance.SetForceEnvironment(environment ?? "");
        }

        private static void OnQuery(long sender)
        {
            if (ZNet.instance && ZNet.instance.IsServer() && current.Length > 0) ZRoutedRpc.instance.InvokeRoutedRPC(sender, ApplyRpc, current);
        }

        [HarmonyPatch(typeof(Game), "Start")]
        private static class Register
        {
            private static void Postfix()
            {
                current = "";
                ZRoutedRpc.instance.Register<string>(RequestRpc, OnRequest);
                ZRoutedRpc.instance.Register<string>(ApplyRpc, OnApply);
                ZRoutedRpc.instance.Register(QueryRpc, OnQuery);
            }
        }

        // A joining player gets the forced weather, if any.
        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class Joined
        {
            private static void Postfix(Player __instance)
            {
                if (__instance == Player.m_localPlayer && ZNet.instance && !ZNet.instance.IsServer())
                    ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), QueryRpc);
            }
        }
    }
}
