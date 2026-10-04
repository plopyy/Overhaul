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
                long targetPeer=package.ReadLong();var target=package.ReadZDOID();package.ReadInt();int length=package.ReadInt();
                if(sender!=peer.m_uid || length<0 || length!=package.Size()-package.GetPos())return false;
                // A denied mutation must not still be forwarded to other clients as
                // a fake visual/state confirmation for a server-controlled object.
                if(targetPeer!=ZNet.GetUID() && (GameCreatureAuthority.Owns(target) || GameWorldAuthority.Owns(target) || PlayerFishingCastGame.ServerOwned(target)))return false;
                var data=ZDOMan.instance?.GetZDO(target);
                var prefab=data!=null && ZNetScene.instance?ZNetScene.instance.GetPrefab(data.GetPrefab()):null;
                if(prefab && prefab.GetComponent<Player>() && (target!=peer.m_characterID || data.GetOwner()!=peer.m_uid))return false;
                return true;
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
