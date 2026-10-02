using System;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace Overhaul
{
    // Jotunn's version exchange omits System.Version.Revision. Keep its checks
    // and require our complete four-part version before native peer admission.
    internal static class NetworkVersion
    {
        internal const string RpcName = "Overhaul_FullVersion";
        private sealed class State
        {
            internal string Version;
            internal bool Rejected;
        }
        private static readonly ConditionalWeakTable<ZRpc, State> Peers = new ConditionalWeakTable<ZRpc, State>();

        internal static void Register(ZRpc rpc)
        {
            Peers.Remove(rpc);
            Peers.Add(rpc, new State());
            rpc.Register<string>(RpcName, (sender, version) =>
            {
                if (Peers.TryGetValue(sender, out var state) && !state.Rejected)
                    state.Version = version;
            });
        }
        internal static void Send(ZRpc rpc) => rpc.Invoke(RpcName, BuildVersion.Value);

        internal static bool Validate(ZNet net, ZRpc rpc)
        {
            if (net.GetPeer(rpc) == null) return false;
            var state = Peers.GetValue(rpc, _ => new State());
            if (!state.Rejected && string.Equals(state.Version, BuildVersion.Value, StringComparison.Ordinal)) return true;
            if (!state.Rejected)
                ZLog.LogWarning($"Overhaul: connection refused. Required version: {BuildVersion.Value}; remote version: {state.Version ?? "missing (old or absent mod)"}.");
            state.Rejected = true;
            if (net.IsServer())
            {
                // Native version errors leave the peer unadmitted; the client
                // closes the connection through the normal connection UI.
                rpc.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorVersion);
            }
            else ZNet.m_connectionStatus = ZNet.ConnectionStatus.ErrorVersion;
            return false;
        }

        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        private static class RegisterVersion
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(ZNetPeer peer) => Register(peer.m_rpc);
        }
        [HarmonyPatch(typeof(ZNet), "SendPeerInfo")]
        private static class SendVersion
        {
            // Uses the same reliable, ordered RPC transport as PeerInfo.
            [HarmonyPriority(Priority.First)]
            private static void Prefix(ZRpc rpc) => Send(rpc);
        }
        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        private static class CheckVersion
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(ZNet __instance, ZRpc rpc) => Validate(__instance, rpc);
        }
    }
}
