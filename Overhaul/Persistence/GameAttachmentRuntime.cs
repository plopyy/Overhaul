using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameAttachmentRuntime
    {
        private sealed class Link
        {internal Player Player;internal ZNetView Target;internal Transform Point;internal int Kind,Index;internal double Next;}
        private static readonly Dictionary<ZDOID,Link> links=new Dictionary<ZDOID,Link>();
        private static readonly Dictionary<ZDOID,double> cooldowns=new Dictionary<ZDOID,double>();
        private static bool applying;
        internal static bool Bed(ZDOID actor)=>links.TryGetValue(actor,out var link)&&link.Kind==1;
        internal static bool Cooling(ZDOID actor)=>cooldowns.TryGetValue(actor,out double next)&&Time.timeAsDouble<next;
        internal static bool Occupied(Transform point)=>links.Values.Any(link=>link.Player&&link.Point==point);
        internal static void Forget(ZDOID actor)
        {if(links.TryGetValue(actor,out var link)&&link.Player){link.Player.m_sleeping=false;Detach(link.Player);}links.Remove(actor);cooldowns.Remove(actor);}
        internal static void Attach(Player player,ZNetView target,int kind,int index)
        {
            if(!player||!target||!target.IsValid())return;
            Transform point;GameObject colliders=null;bool hide=false,ship=false;string animation;Vector3 offset;
            if(kind==1)
            {var bed=target.GetComponent<Bed>();if(!bed)return;point=bed.m_spawnPoint;colliders=bed.gameObject;hide=true;animation="attach_bed";offset=new Vector3(0,.5f,0);}
            else
            {var chairs=target.GetComponentsInChildren<Chair>(true);if(kind!=2||index<0||index>=chairs.Length)return;var chair=chairs[index];point=chair.m_attachPoint;ship=chair.m_inShip;animation=chair.m_attachAnimation;offset=chair.m_detachOffset;}
            if(!point)return;
            if(links.TryGetValue(player.GetZDOID(),out var old)&&old.Point==point)return;
            bool previous=applying;applying=true;
            try{player.AttachStop();player.AttachStart(point,colliders,hide,kind==1,ship,animation,offset);}
            finally{applying=previous;}
            var link=new Link{Player=player,Target=target,Point=point,Kind=kind,Index=index};links[player.GetZDOID()]=link;if(GameCreatureAuthority.Enabled)cooldowns[player.GetZDOID()]=Time.timeAsDouble+2;
            Animation(player,animation,true);Publish(player,link);
        }
        internal static void Detach(Player player)
        {
            if(!player||player.IsSleeping())return;
            string animation=player.m_attachAnimation;bool previous=applying;applying=true;
            try{player.AttachStop();}finally{applying=previous;}
            if(links.Remove(player.GetZDOID())){Animation(player,animation,false);Publish(player,null);}
        }
        internal static void Sleeping(Player player,bool sleeping)
        {if(!player)return;player.m_sleeping=sleeping;if(!sleeping){player.m_wakeupTime=ZNet.instance.GetTimeSeconds();Detach(player);}else if(links.TryGetValue(player.GetZDOID(),out var link))Publish(player,link);}
        private static void Animation(Player player,string name,bool value)
        {if(!GameCreatureAuthority.Enabled||string.IsNullOrEmpty(name))return;var data=player.m_nview.GetZDO();int hash=438569+ZSyncAnimation.GetHash(name);data.AddSessionHash(hash);data.Set(hash,value?1:0);}
        private static void Publish(Player player,Link link)
        {
            if(!GameCreatureAuthority.Enabled||!player.m_nview||!player.m_nview.IsValid())return;
            player.m_nview.InvokeRPC(ZNetView.Everybody,"Overhaul_Furniture",link?.Target?.GetZDO()?.m_uid??ZDOID.None,link?.Kind??0,link?.Index??0,player.m_sleeping);
            if(link!=null)link.Next=Time.timeAsDouble+1;
        }
        internal static void Tick(Player player)
        {
            if(!links.TryGetValue(player.GetZDOID(),out var link))return;
            if(!link.Point||!link.Target||!link.Target.IsValid()||player.IsDead()||player.IsTeleporting())
            {player.m_sleeping=false;Detach(player);return;}
            player.UpdateAttach();
            if(player.m_body){player.m_body.position=player.transform.position;player.m_body.rotation=player.transform.rotation;}
            if(Time.timeAsDouble>=link.Next)Publish(player,link);
        }
        [HarmonyPatch(typeof(Player),"Awake")]
        private static class Register
        {
            private static void Postfix(Player __instance)
            {
                var view=__instance.m_nview;if(!view||!view.IsValid())return;
                view.Register<ZDOID,int,int,bool>("Overhaul_Furniture",(sender,target,kind,index,sleeping)=>
                {
                    if(!PlayerSessionGame.Managed||__instance!=Player.m_localPlayer||GameCreatureAuthority.Enabled||sender!=ZNet.instance.GetServerPeer()?.m_uid)return;
                    if(kind==0){__instance.m_sleeping=false;Detach(__instance);return;}
                    var instance=ZNetScene.instance.FindInstance(target);var furniture=instance?instance.GetComponent<ZNetView>():null;
                    if(furniture){Attach(__instance,furniture,kind,index);__instance.m_sleeping=sleeping;}
                });
            }
        }
        [HarmonyPatch(typeof(Player),nameof(Player.AttachStop))]
        private static class LeaveIntent
        {
            private static bool Prefix(Player __instance)
            {
                if(applying||!PlayerSessionGame.Managed||__instance!=Player.m_localPlayer||!links.ContainsKey(__instance.GetZDOID()))return true;
                if(!__instance.IsSleeping())InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand{Kind=PlayerActionKind.UseOn,Definition=PlayerFurnitureGame.Leave});return false;
            }
            private static void Postfix(Player __instance)
            {if(!applying&&!__instance.m_attached&&links.Remove(__instance.GetZDOID()))Publish(__instance,null);}
        }
        [HarmonyPatch(typeof(Player),"OnDestroy")]
        private static class Cleanup
        {
            private static void Prefix(Player __instance)
            {foreach(var id in links.Where(pair=>ReferenceEquals(pair.Value.Player,__instance)).Select(pair=>pair.Key).ToArray())Forget(id);}
        }
    }
}
