using System;
using HarmonyLib;

namespace Overhaul.Persistence
{
    internal static class GameRpcAuthority
    {
        // RoutedRPC carries a claimed sender inside the package. Bind that claim to
        // the actual connection before either executing or forwarding the message.
        internal static bool Accept(ZRpc connection,ZPackage package)
        {
            if(package==null || connection==null)return false;
            var peer=ZNet.instance.GetPeer(connection);
            if(peer==null || !peer.IsReady() || peer.m_uid==ZNet.GetUID())return false;
            int position=package.GetPos();
            try
            {
                if(package.Size()-position<44)return false;
                package.ReadLong();long sender=package.ReadLong();
                package.ReadLong();package.ReadZDOID();package.ReadInt();int length=package.ReadInt();
                return sender==peer.m_uid && length>=0 && length==package.Size()-package.GetPos();
            }
            catch(Exception){return false;}
            finally{package.SetPos(position);}
        }
        [HarmonyPatch(typeof(ZRoutedRpc),"RPC_RoutedRPC")]
        private static class Incoming
        {
            [HarmonyPriority(Priority.First+300)]
            private static bool Prefix(ZRpc rpc,ZPackage pkg)
            {
                if(PlayerPersistenceConfig.Enabled?.Value!=true || !ZNet.instance || !ZNet.instance.IsServer())return true;
                return Accept(rpc,pkg);
            }
        }
    }
}
