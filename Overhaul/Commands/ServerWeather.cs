using HarmonyLib;

namespace Overhaul.Commands
{
    // o_weather: an administrator sets the weather for the whole server, which then goes on as usual. The request goes
    // to the server, which checks the sender is an administrator with devcommands and sends the weather to every
    // player. It holds until the game's next weather change (the end of the current m_environmentDuration period), when
    // every client lets the normal weather take over at the same moment (all follow the server's clock). A player who
    // joins meanwhile asks the server for it. "reset" ends it at once.
    internal static class ServerWeather
    {
        private const string RequestRpc = "Overhaul_WeatherRequest", ApplyRpc = "Overhaul_Weather", QueryRpc = "Overhaul_WeatherQuery";
        private static string current = "";
        private static double until;

        // Client side: "" resets the weather.
        internal static void Request(string environment) =>
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RequestRpc, environment ?? "");

        // Server side: the sender is the host with devcommands, or a connected administrator with devcommands.
        internal static bool Allowed(long sender, out string who)
        {
            bool host = sender == ZRoutedRpc.instance.m_id;
            ZNetPeer peer = host ? null : ZNet.instance.GetPeer(sender);
            who = host ? "the host" : peer?.m_playerName ?? sender.ToString();
            return host ? AdminCommandAccess.LocalEnabled : peer != null && AdminCommands.Authorized(peer.m_rpc);
        }

        private static void OnRequest(long sender, string environment)
        {
            if (!ZNet.instance || !ZNet.instance.IsServer() || !EnvMan.instance) return;
            if (!Allowed(sender, out string who)) { Utility.Log.LogWarning("o_weather refused for peer " + sender + ": administrator with devcommands required"); return; }
            current = environment ?? "";
            double duration = System.Math.Max(1, EnvMan.instance.m_environmentDuration), now = ZNet.instance.GetTimeSeconds();
            until = current.Length > 0 ? (System.Math.Floor(now / duration) + 1) * duration : 0;
            Utility.Log.LogInfo("Weather set by " + who + ": " + (current.Length > 0 ? current + " until the next weather change (" + (until - now).ToString("0") + " s)" : "normal"));
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, ApplyRpc, current, until);
        }

        private static void OnApply(long sender, string environment, double end)
        {
            if (sender != ZRoutedRpc.instance.GetServerPeerID() && !(ZNet.instance && ZNet.instance.IsServer())) return;
            current = environment ?? ""; until = end;
            if (EnvMan.instance) EnvMan.instance.SetForceEnvironment(current);
        }

        private static void OnQuery(long sender)
        {
            if (ZNet.instance && ZNet.instance.IsServer() && current.Length > 0) ZRoutedRpc.instance.InvokeRoutedRPC(sender, ApplyRpc, current, until);
        }

        // At the next weather change, the normal weather takes over.
        [HarmonyPatch(typeof(EnvMan), "FixedUpdate")]
        private static class Expire
        {
            private static void Postfix(EnvMan __instance)
            {
                if (current.Length == 0 || !ZNet.instance || ZNet.instance.GetTimeSeconds() < until) return;
                current = ""; until = 0;
                __instance.SetForceEnvironment("");
            }
        }

        [HarmonyPatch(typeof(Game), "Start")]
        private static class Register
        {
            private static void Postfix()
            {
                current = ""; until = 0;
                ZRoutedRpc.instance.Register<string>(RequestRpc, OnRequest);
                ZRoutedRpc.instance.Register<string, double>(ApplyRpc, OnApply);
                ZRoutedRpc.instance.Register(QueryRpc, OnQuery);
            }
        }

        // A joining player gets the weather set meanwhile, if any.
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
