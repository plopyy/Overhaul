using System;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Storage
{
    internal static class MoveReservation
    {
        internal const string Rpc="Overhaul_ReserveMove",ReplyRpc="Overhaul_ReserveMoveReply";
        internal static readonly int TokenKey="overhaul_move_token".GetStableHashCode(),PeerKey="overhaul_move_peer".GetStableHashCode(),UntilKey="overhaul_move_until".GetStableHashCode();
        internal static bool Busy(Piece p)=>p&&p.m_nview&&p.m_nview.IsValid()&&p.m_nview.GetZDO().GetLong(UntilKey,0)>ChestAccess.Now;
        internal static bool Busy(Container c)=>c&&Busy(c.GetComponentInParent<Piece>());
        internal static bool Own(Piece p,long peer,string token)=>Busy(p)&&!string.IsNullOrEmpty(token)&&p.m_nview.GetZDO().GetLong(PeerKey,0)==peer&&p.m_nview.GetZDO().GetString(TokenKey,"")==token;
        internal static bool Change(Piece p,long peer,ZDOID actorId,string token,bool release)
        {
            if(!PieceRelocation.Eligible(p)||!p.m_nview.IsOwner()||string.IsNullOrEmpty(token)||token.Length>64)return false;
            var data=p.m_nview.GetZDO();bool matches=data.GetLong(PeerKey,0)==peer&&data.GetString(TokenKey,"")==token;
            if(release)
            {
                if(!matches)return false;
                data.Set(UntilKey,0L);data.Set(TokenKey,"");data.Set(PeerKey,0L);return true;
            }
            var actor=ChestAccess.Actor(peer,actorId);
            if (Persistence.GamePersistence.InventoryReserved(data.m_uid)) return false;
            if(actor==null||actor.GetFloat(ZDOVars.s_health,0)<=0||!PieceRelocation.Near(p,actor.GetPosition())||
                (Busy(p)&&!matches)||!PieceRelocation.Access(p,actor.GetLong(ZDOVars.s_playerID,0),peer,token))return false;
            data.Set(TokenKey,token);data.Set(PeerKey,peer);data.Set(UntilKey,ChestAccess.Now+TimeSpan.FromSeconds(15).Ticks);return true;
        }
        internal static void Register(Piece p)
        {
            p.m_nview.Register<ZPackage>(Rpc,(peer,packet)=>
            {
                if(!p.m_nview.IsOwner())return;
                try
                {
                    var actor=packet.ReadZDOID();string token=packet.ReadString();bool release=packet.ReadBool();
                    bool ok=Change(p,peer,actor,token,release);
                    if(!release)p.m_nview.InvokeRPC(peer,ReplyRpc,token,ok);
                }
                catch(Exception error){Debug.LogWarning("[Overhaul] Move reservation rejected: "+error.Message);}
            });
            p.m_nview.Register<string,bool>(ReplyRpc,(peer,token,ok)=>
            {if(peer==p.m_nview.GetZDO().GetOwner())RelocationClient.Reserved(p,token,ok);});
        }
        [HarmonyPatch(typeof(Container),nameof(Container.Interact))]
        private static class Open
        {
            private static bool Prefix(Container __instance,Humanoid character,ref bool __result)
            {if(!Busy(__instance))return true;character.Message(MessageHud.MessageType.Center,"$msg_inuse");__result=true;return false;}
        }
    }
}
