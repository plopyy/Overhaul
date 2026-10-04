using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameWorldInteraction
    {
        private const string SignRpc="Overhaul_SignText";
        private static ZDO Actor(long sender)
        {
            if(sender==ZNet.GetUID())return GameMovementRuntime.Managed(Player.m_localPlayer)?Player.m_localPlayer.m_nview.GetZDO():null;
            var peer=ZNet.instance.GetPeer(sender);return peer==null?null:PlayerSessionGame.Actor(peer.m_rpc);
        }
        internal static ZDO Nearby(Component target,long sender,bool ward=true)
        {
            var view=target?target.GetComponent<ZNetView>():null;var actor=Actor(sender);
            if(!view||!view.IsValid()||!view.IsOwner()||actor==null||actor.GetBool(ZDOVars.s_dead,false)||
                GamePersistence.ActionReserved(view.GetZDO().m_uid)||(target.transform.position-actor.GetPosition()).sqrMagnitude>25)return null;
            return !ward||Storage.ChestAccess.WardAccessAt(target.transform.position,actor.GetLong(ZDOVars.s_playerID,0))?actor:null;
        }
        private static string Author(ZDO actor)
        {
            foreach(var info in ZNet.instance.GetPlayerList())if(info.m_characterID==actor.m_uid)return info.m_userInfo.m_id.ToString();
            return "host";
        }
        private static void SignText(Sign sign,long sender,string text)
        {
            if(!GameCreatureAuthority.Enabled||text==null||text.Length>Mathf.Clamp(sign.m_characterLimit,0,4096))return;
            var actor=Nearby(sign,sender);if(actor==null)return;
            var data=sign.m_nview.GetZDO();data.Set(ZDOVars.s_text,text);data.Set(ZDOVars.s_author,Author(actor));
            var player=ZNetScene.instance.FindInstance(actor.m_uid)?.GetComponent<Player>();
            if(player)data.Set(ZDOVars.s_authorDisplayName,player.GetPlayerName());
            // Native UpdateText refreshes each visible sign. The server needs no UI.
        }
        [HarmonyPatch(typeof(Sign),"Awake")]
        private static class SignRegistration
        {private static void Postfix(Sign __instance){if(__instance.m_nview&&__instance.m_nview.IsValid())__instance.m_nview.Register<string>(SignRpc,(sender,text)=>SignText(__instance,sender,text));}}
        [HarmonyPatch(typeof(Sign),nameof(Sign.SetText))]
        private static class SignIntent
        {
            private static bool Prefix(Sign __instance,string text)
            {
                if(!PlayerSessionGame.Managed)return true;
                if(__instance.m_nview&&__instance.m_nview.IsValid())__instance.m_nview.InvokeRPC(SignRpc,text??"");return false;
            }
        }
        [HarmonyPatch(typeof(TeleportWorld),"RPC_SetTag")]
        private static class PortalTag
        {
            private static bool Prefix(TeleportWorld __instance,long sender,string tag,ref string authorId)
            {
                if(!GameCreatureAuthority.Enabled)return true;
                if(tag==null||tag.Length>128)return false;
                var actor=Nearby(__instance,sender);if(actor==null)return false;authorId=Author(actor);return true;
            }
        }
        [HarmonyPatch(typeof(Tameable),"RPC_SetName")]
        private static class AnimalName
        {
            private static bool Prefix(Tameable __instance,long sender,string name,ref string authorId)
            {
                if(!GameCreatureAuthority.Enabled||sender==ZNet.GetUID())return true;
                if(name==null||name.Length>128)return false;
                var actor=Nearby(__instance,sender);if(actor==null)return false;authorId=Author(actor);return true;
            }
        }
        [HarmonyPatch(typeof(Fireplace),"RPC_ToggleOn")]
        private static class FireToggle
        {private static bool Prefix(Fireplace __instance,long sender)=>!GameCreatureAuthority.Enabled||sender==ZNet.GetUID()||__instance.m_canTurnOff&&Nearby(__instance,sender)!=null;}
        [HarmonyPatch]
        private static class Ward
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {yield return AccessTools.Method(typeof(PrivateArea),"RPC_ToggleEnabled");yield return AccessTools.Method(typeof(PrivateArea),"RPC_TogglePermitted");}
            private static bool Prefix(PrivateArea __instance,long uid,long playerID,object[] __args)
            {
                if(!GameCreatureAuthority.Enabled)return true;
                var actor=Nearby(__instance,uid,false);if(actor==null||actor.GetLong(ZDOVars.s_playerID,0)!=playerID)return false;
                if(__args.Length==3)
                {var player=ZNetScene.instance.FindInstance(actor.m_uid)?.GetComponent<Player>();if(!player)return false;__args[2]=player.GetPlayerName();}
                return true; // Native code still checks creator, enabled state and permissions.
            }
        }
    }
}
