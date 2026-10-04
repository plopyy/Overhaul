using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GamePlayerReplication
    {
        [ThreadStatic] private static ZRpc incoming;
        private struct IncomingState {internal ZRpc Previous;internal bool Entered;}
        private static bool PlayerPrefab(int hash)
        {var prefab=ZNetScene.instance?ZNetScene.instance.GetPrefab(hash):null;return prefab && prefab.GetComponent<Player>();}
        internal static bool Accept(ZNetPeer peer,ZDOID id,long owner,ZPackage body)
        {
            if(!GameCreatureAuthority.Enabled)return true;
            int position=body.GetPos();int prefab;
            try{body.ReadUShort();prefab=body.ReadInt();}finally{body.SetPos(position);}
            var existing=ZDOMan.instance.GetZDO(id);
            bool player=PlayerPrefab(prefab),wasPlayer=existing!=null && PlayerPrefab(existing.GetPrefab());
            if(!player && !wasPlayer)return true;
            if(!player || peer==null || owner!=peer.m_uid)return false;
            var session=PlayerSessionGame.Session(peer.m_rpc);
            if(session==null || session.State!=PlayerAdmission.Phase.Ready)return false;
            return (existing==null || wasPlayer && existing.GetOwner()==peer.m_uid) && GameAvatarBinding.Accept(peer,id);
        }
        internal static bool Position(ZNetPeer peer,ZDOID id,ZPackage body,ref Vector3 position)
        {
            if(!GameCreatureAuthority.Enabled)return true;
            int offset=body.GetPos();int prefab;try{body.ReadUShort();prefab=body.ReadInt();}finally{body.SetPos(offset);}
            if(!PlayerPrefab(prefab))return true;
            if(GameMovementRuntime.Position(id,out var simulated)){position=simulated;return true;}
            var existing=ZDOMan.instance.GetZDO(id);
            if(existing!=null){position=existing.GetPosition();return true;}
            var session=PlayerSessionGame.Session(peer.m_rpc);var state=InventoryMoveGame.SessionState(peer.m_rpc)??session?.Snapshot;
            return GameSpawnPoint.Resolve(peer.m_rpc,state,out position);
        }
        [HarmonyPatch(typeof(ZDOMan),"RPC_ZDOData")]
        private static class Incoming
        {
            private static void Prefix(ZRpc rpc,out IncomingState __state)
            {__state=new IncomingState{Previous=incoming,Entered=true};incoming=GameCreatureAuthority.Enabled?rpc:null;}
            private static void Finalizer(IncomingState __state){if(__state.Entered)incoming=__state.Previous;}
        }
        [HarmonyPatch(typeof(ZDO),nameof(ZDO.Deserialize))]
        private static class CanonicalFields
        {
            private static void Postfix(ZDO __instance)
            {
                if(incoming==null || !PlayerPrefab(__instance.GetPrefab()))return;
                var session=PlayerSessionGame.Session(incoming);
                if(session==null || session.State!=PlayerAdmission.Phase.Ready)return;
                // Read by authenticated connection: the incoming packet may itself
                // have replaced the avatar's id, which must not select stale login data.
                var state=InventoryMoveGame.SessionState(incoming)??session.Snapshot;
                __instance.Set(ZDOVars.s_playerID,PlayerSessionGame.CharacterId(session));
                var name=state.Rows.SingleOrDefault(r=>r.Table=="state" && (string)r.Values[0]=="player_name");
                if(name!=null)__instance.Set(ZDOVars.s_playerName,(string)name.Values[3]);
                float health=(float)PlayerResources.Read(state,"health"),maximum=(float)PlayerResources.Read(state,"max_health");
                __instance.Set(ZDOVars.s_health,health);__instance.Set(ZDOVars.s_maxHealth,maximum);__instance.Set(ZDOVars.s_dead,health<=0);
                __instance.Set(ZDOVars.s_stamina,(float)PlayerResources.Read(state,"stamina"));
                __instance.Set(ZDOVars.s_eitr,(float)PlayerResources.Read(state,"eitr"));
                __instance.Set(ZDOVars.s_adrenaline,(float)GameAdrenaline.Read(state,PlayerResources.Adrenaline));
                GameMovementRuntime.Protect(__instance);
                var avatar=ZNetScene.instance.FindInstance(__instance.m_uid)?.GetComponent<Player>();
                if(avatar){__instance.Set(ZDOVars.s_dodgeinv,GameDodgeAction.Active(avatar)&&avatar.m_dodgeInvincibleCached);__instance.Set(ZDOVars.s_baseValue,avatar.m_baseValue);}
            }
        }
    }
}
