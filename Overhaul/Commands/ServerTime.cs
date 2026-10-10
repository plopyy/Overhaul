using HarmonyLib;

namespace Overhaul.Commands
{
    // o_time hh:mm: an administrator moves the server's clock forward to the next hh:mm, so the time of day changes for
    // the server and every player (clients follow the server's network time). Never backward, like sleeping or
    // skiptime, so cooking, smelting and growing timers stay right.
    internal static class ServerTime
    {
        private const string RequestRpc = "Overhaul_TimeRequest";

        internal static void Request(int minutes) => ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RequestRpc, minutes);

        // Clock time (0 to 1 over 24 h, as the sun shows it) to the game's raw day fraction: the game spreads 6:00 to
        // 18:00 over 70 % of its day (EnvMan.RescaleDayFraction), this is the inverse.
        internal static double RawFraction(int minutes)
        {
            double shown = minutes / 1440.0;
            if (shown < .25) return shown / .25 * .15;
            if (shown <= .75) return .15 + (shown - .25) / .5 * .7;
            return .85 + (shown - .75) / .25 * .15;
        }

        private static void OnRequest(long sender, int minutes)
        {
            if (!ZNet.instance || !ZNet.instance.IsServer() || !EnvMan.instance || minutes < 0 || minutes >= 1440) return;
            if (!ServerWeather.Allowed(sender, out string who)) { Utility.Log.LogWarning("o_time refused for peer " + sender + ": administrator with devcommands required"); return; }
            double length = EnvMan.instance.m_dayLengthSec, now = ZNet.instance.GetTimeSeconds();
            double target = System.Math.Floor(now / length) * length + RawFraction(minutes) * length;
            if (target <= now) target += length;
            ZNet.instance.SetNetTime(target);
            Utility.Log.LogInfo("Time of day set to " + (minutes / 60).ToString("00") + ":" + (minutes % 60).ToString("00") + " by " + who + " (+" + (target - now).ToString("0") + " s)");
        }

        [HarmonyPatch(typeof(Game), "Start")]
        private static class Register { private static void Postfix() => ZRoutedRpc.instance.Register<int>(RequestRpc, OnRequest); }
    }
}
