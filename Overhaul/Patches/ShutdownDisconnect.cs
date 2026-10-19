using System.Diagnostics;
using System.Threading;
using HarmonyLib;

namespace Overhaul.Patches
{
    // A server shutting down saved the world first (several seconds) and only then told the players, closing their connections
    // right after: the message was often lost, and the players stayed in game with the connection problem icon while the server
    // was already restarting. The players are now told first, before the save: the game's own "Disconnect" message, sent out
    // (flushed) to every connected player, who goes back to the main menu with the disconnected message.
    [HarmonyPatch(typeof(Game), "Shutdown")]
    internal static class ShutdownDisconnect
    {
        private static void Prefix(Game __instance)
        {
            var net = ZNet.instance;
            if (__instance.m_shuttingDown || !net || !net.IsServer() || net.m_peers.Count == 0) return;
            Utility.Log.LogInfo("Server shutting down: disconnecting " + net.m_peers.Count + " player(s) before the save");
            foreach (var peer in net.m_peers)
                if (peer.m_rpc != null && peer.m_socket != null && peer.m_socket.IsConnected()) peer.m_rpc.Invoke("Disconnect");
            // Sent out before going on: the save would otherwise hold the messages back.
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 1500)
            {
                bool pending = false;
                foreach (var peer in net.m_peers)
                    if (peer.m_socket != null && peer.m_socket.IsConnected()) { peer.m_socket.Flush(); pending |= peer.m_socket.GetSendQueueSize() > 0; }
                if (!pending && clock.ElapsedMilliseconds >= 300) break;
                Thread.Sleep(50);
            }
        }
    }
}
