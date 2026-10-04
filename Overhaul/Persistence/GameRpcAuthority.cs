using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;

namespace Overhaul.Persistence
{
    internal static class GameRpcAuthority
    {
        private static readonly HashSet<int> serverMessages = new HashSet<int>();
        static GameRpcAuthority()
        {
            foreach(var name in new[]{"SetGlobalKey","RemoveGlobalKey","GlobalKeys","LocationIcons","SetEvent","startrandomevent","resetrandomevent",
                "SleepStart","SleepStop","RPC_SetConnection","RPC_RegisterKill","RPC_DiscoverLocationResponse","RPC_TeleportPlayer"})serverMessages.Add(name.GetStableHashCode());
        }
        internal static bool CanDelete(long sender,ZDOID id)
        {
            if(id.IsNone()||id.ID==0)return false;
            var data=ZDOMan.instance?.GetZDO(id);
            // A short-lived effect may be destroyed before its first ZDO packet.
            // Such an announcement can only concern the sender's own ID space.
            if(data==null)return id.UserID==sender;
            return data.GetOwner()==sender&&!data.Persistent&&!PlayerFishingCastGame.ServerOwned(id)&&
                !GameCreatureAuthority.Owns(data)&&!GameWorldAuthority.Owns(data);
        }
        internal static ZPackage FilterDeletion(long sender,ZPackage input)
        {
            int count=input.ReadInt();
            if(count<0||count!=(input.Size()-input.GetPos())/12||(input.Size()-input.GetPos())%12!=0)throw new InvalidDataException("Invalid deleted object list");
            var keep=new List<ZDOID>();
            for(int i=0;i<count;i++){var id=input.ReadZDOID();if(CanDelete(sender,id))keep.Add(id);}
            var output=new ZPackage();output.Write(keep.Count);foreach(var id in keep)output.Write(id);return output;
        }
        private static bool FilterRoutedDeletion(ref ZPackage package)
        {
            var original=package;int position=original.GetPos();
            try
            {
                long messageId=original.ReadLong(),sender=original.ReadLong(),target=original.ReadLong();var objectId=original.ReadZDOID();int method=original.ReadInt();
                if(!objectId.IsNone()||method!="DestroyZDO".GetStableHashCode())return true;
                var message=new ZRoutedRpc.RoutedRPCData{m_msgID=messageId,m_senderPeerID=sender,m_targetPeerID=target,m_targetZDO=objectId,m_methodHash=method,m_parameters=original.ReadPackage()};
                var contents=message.m_parameters.ReadPackage();
                if(message.m_parameters.GetPos()!=message.m_parameters.Size())return false;
                var filtered=FilterDeletion(message.m_senderPeerID,contents);
                message.m_parameters=new ZPackage();message.m_parameters.Write(filtered);
                var rewritten=new ZPackage();message.Serialize(rewritten);rewritten.SetPos(0);package=rewritten;return true;
            }
            catch(Exception){return false;}
            finally{original.SetPos(position);}
        }
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
                long targetPeer=package.ReadLong();var target=package.ReadZDOID();int method=package.ReadInt();int length=package.ReadInt();
                if(sender!=peer.m_uid || length<0 || length!=package.Size()-package.GetPos())return false;
                if(target.IsNone()&&serverMessages.Contains(method))return false;
                // A denied mutation must not still be forwarded to other clients as
                // a fake visual/state confirmation for a server-controlled object.
                if(targetPeer!=ZNet.GetUID() && (GameCreatureAuthority.Owns(target) || GameWorldAuthority.Owns(target) || PlayerFishingCastGame.ServerOwned(target)))return false;
                var data=ZDOMan.instance?.GetZDO(target);
                var prefab=data!=null && ZNetScene.instance?ZNetScene.instance.GetPrefab(data.GetPrefab()):null;
                if(prefab && prefab.GetComponent<Player>() && (target!=GameAvatarBinding.Current(connection) || data.GetOwner()!=peer.m_uid))return false;
                if(method=="SetTrigger".GetStableHashCode() && GameAttackRuntime.Active(target))return false;
                return true;
            }
            catch(Exception){return false;}
            finally{package.SetPos(position);}
        }
        [HarmonyPatch(typeof(ZRoutedRpc),"RPC_RoutedRPC")]
        private static class Incoming
        {
            [HarmonyPriority(Priority.First+300)]
            private static bool Prefix(ZRpc rpc,ref ZPackage pkg)
            {
                if(PlayerPersistenceConfig.Enabled?.Value!=true || !ZNet.instance || !ZNet.instance.IsServer())return true;
                return Accept(rpc,pkg)&&FilterRoutedDeletion(ref pkg);
            }
        }
    }
}
