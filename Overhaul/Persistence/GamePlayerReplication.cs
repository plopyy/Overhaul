using System;
using System.Linq;
using HarmonyLib;

namespace Overhaul.Persistence
{
    internal static class GamePlayerReplication
    {
        [ThreadStatic] private static ZRpc incoming;
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
            if(existing==null)return id.UserID==peer.m_uid;
            return wasPlayer && existing.GetOwner()==peer.m_uid && (peer.m_characterID.IsNone() || peer.m_characterID==id);
        }
        [HarmonyPatch(typeof(ZDOMan),"RPC_ZDOData")]
        private static class Incoming
        {
            private static void Prefix(ZRpc rpc,out ZRpc __state)
            {__state=incoming;incoming=GameCreatureAuthority.Enabled?rpc:null;}
            private static void Finalizer(ZRpc __state)=>incoming=__state;
        }
        [HarmonyPatch(typeof(ZDO),nameof(ZDO.Deserialize))]
        private static class CanonicalFields
        {
            private static void Postfix(ZDO __instance)
            {
                if(incoming==null || !PlayerPrefab(__instance.GetPrefab()))return;
                var session=PlayerSessionGame.Session(incoming);
                if(session==null || session.State!=PlayerAdmission.Phase.Ready)return;
                var state=InventoryMoveGame.State(__instance.m_uid)??session.Snapshot;
                __instance.Set(ZDOVars.s_playerID,PlayerSessionGame.CharacterId(session));
                var name=state.Rows.SingleOrDefault(r=>r.Table=="state" && (string)r.Values[0]=="player_name");
                if(name!=null)__instance.Set(ZDOVars.s_playerName,(string)name.Values[3]);
                float health=(float)PlayerResources.Read(state,"health"),maximum=(float)PlayerResources.Read(state,"max_health");
                __instance.Set(ZDOVars.s_health,health);__instance.Set(ZDOVars.s_maxHealth,maximum);__instance.Set(ZDOVars.s_dead,health<=0);
            }
        }
    }
}
