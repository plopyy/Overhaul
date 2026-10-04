using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameWagonRuntime
    {
        internal const string Use="wagon.use";
        private const string Puller="overhaul_wagon_puller";
        private static readonly Dictionary<ZDOID,Vagon> wagons=new Dictionary<ZDOID,Vagon>();
        private static GameObject attaching;
        internal static void Forget(ZDOID actor)
        {
            if(!wagons.TryGetValue(actor,out var wagon))return;
            wagons.Remove(actor);if(wagon)wagon.Detach();
        }
        internal static PlayerActionPlan Prepare(ZDO actor,GameObject target,InventoryMoveRequest request,PlayerSnapshot state)
        {
            var wagon=target.GetComponent<Vagon>();
            var player=ZNetScene.instance.FindInstance(actor.m_uid)?.GetComponent<Player>();
            if(!wagon||!player||!player.m_body||request.Action.Amount!=1||player.IsDead()||player.InIntro()||player.IsTeleporting()||player.IsAttached()||
                !Storage.ChestAccess.WardAccessAt(target.transform.position,player.GetPlayerID()))throw new InvalidOperationException("Cart is unavailable");
            bool detach=wagon.IsAttached(player);
            if(!detach&&(wagon.IsAttached()||!wagon.CanAttach(player.gameObject)))throw new InvalidOperationException("Cart cannot be attached here");
            using(var world=new PlayerActionObjectGame(wagon.m_nview.GetZDO()))
                return world.Finish(new PlayerBatch(request.Action.Operation,state.Revision,Array.Empty<PlayerChange>()),()=>
                {
                    if(!wagon||!player)return;
                    if(detach){if(wagon.IsAttached(player))wagon.Detach();return;}
                    if(!wagon.IsAttached()&&wagon.CanAttach(player.gameObject))wagon.AttachTo(player.gameObject);
                });
        }
        [HarmonyPatch(typeof(Vagon),nameof(Vagon.Interact))]
        private static class Intent
        {
            private static bool Prefix(Vagon __instance,Humanoid character,bool hold,ref bool __result)
            {
                if(!PlayerSessionGame.Managed||character!=Player.m_localPlayer)return true;
                __result=false;if(hold||!__instance.m_nview||!__instance.m_nview.IsValid())return false;
                var id=__instance.m_nview.GetZDO().m_uid;
                InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand{Kind=PlayerActionKind.UseOn,Definition=Use,TargetUser=id.UserID,TargetId=id.ID});return false;
            }
        }
        [HarmonyPatch(typeof(Vagon),nameof(Vagon.RPC_RequestOwn))]
        private static class Ownership {private static bool Prefix()=>!GameCreatureAuthority.Enabled;}
        [HarmonyPatch(typeof(Vagon),"AttachTo")]
        private static class Attach
        {
            private static void Prefix(GameObject go,out GameObject __state){__state=attaching;if(GameCreatureAuthority.Enabled)attaching=go;}
            private static void Postfix(Vagon __instance,GameObject go)
            {
                if(!GameCreatureAuthority.Enabled)return;
                var player=go.GetComponent<Player>();if(!player)return;
                wagons[player.GetZDOID()]=__instance;
                __instance.m_nview.GetZDO().Set(Puller,player.GetZDOID());
            }
            private static void Finalizer(GameObject __state){attaching=__state;}
        }
        [HarmonyPatch(typeof(Vagon),"DetachAll")]
        private static class DetachOther
        {
            private static bool Prefix()
            {
                if(!GameCreatureAuthority.Enabled||!attaching)return true;
                var player=attaching.GetComponent<Player>();if(player)Forget(player.GetZDOID());
                return false;
            }
        }
        [HarmonyPatch(typeof(Vagon),"Detach")]
        private static class Detached
        {
            private static bool Prefix(Vagon __instance)
            {
                if(!GameCreatureAuthority.Enabled)return !PlayerSessionGame.Managed||__instance.m_attachJoin||__instance.m_attachedObject;
                var player=__instance.m_attachedObject?__instance.m_attachedObject.GetComponent<Player>():null;
                if(player&&wagons.TryGetValue(player.GetZDOID(),out var current)&&current==__instance)wagons.Remove(player.GetZDOID());
                if(__instance.m_nview&&__instance.m_nview.IsValid())__instance.m_nview.GetZDO().Set(Puller,ZDOID.None);
                return true;
            }
        }
        [HarmonyPatch(typeof(Vagon),"OnDestroy")]
        private static class Destroyed
        {private static void Prefix(Vagon __instance){if(GameCreatureAuthority.Enabled&&__instance.m_attachedObject)__instance.Detach();}}
        [HarmonyPatch(typeof(Vagon),"Update")]
        private static class Update
        {
            private static void Prefix(Vagon __instance)
            {
                if(!GameCreatureAuthority.Enabled)return;
                __instance.m_useRequester=null;
                var player=__instance.m_attachedObject?__instance.m_attachedObject.GetComponent<Player>():null;
                if(player&&(player.IsDead()||!GameMovementRuntime.Managed(player)))__instance.Detach();
            }
        }
        [HarmonyPatch(typeof(Vagon),"LateUpdate")]
        private static class Rope
        {
            private static bool Prefix(Vagon __instance)
            {
                if(!PlayerSessionGame.Managed||GameCreatureAuthority.Enabled||!__instance.m_nview||!__instance.m_nview.IsValid())return true;
                var id=__instance.m_nview.GetZDO().GetZDOID(Puller);
                var player=id.IsNone()?null:ZNetScene.instance.FindInstance(id);
                __instance.m_lineRenderer.enabled=player;
                if(player)
                {
                    __instance.m_lineRenderer.SetPosition(0,__instance.m_lineAttachPoints0.position);
                    __instance.m_lineRenderer.SetPosition(1,player.transform.position+__instance.m_lineAttachOffset);
                    __instance.m_lineRenderer.SetPosition(2,__instance.m_lineAttachPoints1.position);
                }
                return false;
            }
        }
    }
}
