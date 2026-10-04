using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameTeleportAction
    {
        internal const string Portal="movement.portal",Dungeon="movement.dungeon";
        private static readonly HashSet<Teleport> entrances=new HashSet<Teleport>();
        internal static void RegisterEntrances(GameObject root)
        {
            GameWorldInteraction.Register(root);
            if(!root||!GameCreatureAuthority.Enabled)return;
            GameDiscoveryRuntime.Register(root);
            foreach(var entry in root.GetComponentsInChildren<Teleport>(true))
            {
                if(!entrances.Add(entry))continue;
                var lifetime=entry.gameObject.AddComponent<EntranceLifetime>();lifetime.Entry=entry;
            }
        }
        public sealed class EntranceLifetime:MonoBehaviour
        {internal Teleport Entry;private void OnDestroy(){entrances.Remove(Entry);}}
        [ThreadStatic] private static bool updating;
        internal static bool CanStart(Player player)=>player&&player.m_body&&!player.IsDead()&&!player.IsTeleporting()&&!player.InIntro()&&player.m_teleportCooldown>=2;
        internal static bool AllowedItems(PlayerSnapshot state,bool all)
        {
            // Run the native inventory rule over detached canonical items.
            var inventory=new Inventory("Portal validation",null,8,64);
            foreach(var row in state.Rows.Where(row=>row.Table=="inventory"))inventory.m_inventory.Add(PlayerInventoryView.ReadItem(row.Values,null,true));
            return inventory.IsTeleportable(all);
        }
        internal static PlayerActionPlan Prepare(ZDO actor,InventoryMoveRequest request,PlayerSnapshot state)
        {
            var instance=ZNetScene.instance.FindInstance(actor.m_uid);var player=instance?instance.GetComponent<Player>():null;
            if(request.Action.Amount!=1||!CanStart(player)||GameDeathProgress.IsDead(state)||PlayerResources.Read(state,"health")<=0)throw new InvalidOperationException("Character cannot teleport");
            Vector3 point;Quaternion rotation;bool distant=request.Gameplay.Definition==Portal;PlayerStatType statistic;
            if(distant)
            {
                var source=ZNetScene.instance.FindInstance(new ZDOID(request.Gameplay.TargetUser,request.Gameplay.TargetId));var portal=source?source.GetComponent<TeleportWorld>():null;
                if(!portal||!portal.m_nview||!portal.m_nview.IsValid()||(actor.GetPosition()-portal.transform.position).sqrMagnitude>25||GamePersistence.ActionReserved(portal.m_nview.GetZDO().m_uid))throw new InvalidOperationException("Portal is unavailable");
                if(ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoPortals))throw new InvalidOperationException("Portals are disabled");
                if(ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoBossPortals)&&((RandEventSystem.instance&&RandEventSystem.instance.GetBossEvent()!=null)||(ZoneSystem.instance.GetGlobalKey(GlobalKeys.activeBosses,out float bosses)&&bosses>0)))throw new InvalidOperationException("A boss prevents portal travel");
                if(!AllowedItems(state,portal.m_allowAllItems))throw new InvalidOperationException("Inventory prevents portal travel");
                var destination=ZDOMan.instance.GetZDO(portal.m_nview.GetZDO().GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal));
                var prefab=destination==null?null:ZNetScene.instance.GetPrefab(destination.GetPrefab());
                if(destination==null||!prefab||!prefab.GetComponent<TeleportWorld>()||GamePersistence.ActionReserved(destination.m_uid))throw new InvalidOperationException("Portal destination is unavailable");
                rotation=destination.GetRotation();point=destination.GetPosition()+rotation*Vector3.forward*portal.m_exitDistance+Vector3.up;statistic=PlayerStatType.PortalsUsed;
            }
            else
            {
                if(request.Gameplay.Definition!=Dungeon||request.Gameplay.TargetId!=0)throw new InvalidOperationException("Unknown teleport intent");
                var requested=new Vector3(request.Gameplay.Position[0],request.Gameplay.Position[1],request.Gameplay.Position[2]);
                Prune();var entry=entrances.FirstOrDefault(candidate=>candidate&&candidate.gameObject.activeInHierarchy&&(candidate.transform.position-requested).sqrMagnitude<.0625f&&(candidate.transform.position-actor.GetPosition()).sqrMagnitude<=25);
                if(!entry||!entry.m_targetPoint)throw new InvalidOperationException("Dungeon entrance is unavailable");
                if(ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoBossPortals)&&player.InInterior()&&Location.IsInsideActiveBossDungeon(actor.GetPosition()))throw new InvalidOperationException("A boss prevents dungeon exit");
                point=entry.m_targetPoint.GetTeleportPoint();rotation=entry.m_targetPoint.transform.rotation;
                statistic=player.InInterior()?PlayerStatType.PortalDungeonOut:PlayerStatType.PortalDungeonIn;
            }
            var changes=new[]{PlayerCraftProgressGame.Increment(state,"statistics:0:values",((int)statistic).ToString(CultureInfo.InvariantCulture),1)};
            return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(request.Action.Operation,state.Revision,changes),new Dictionary<long,ObjectRecord>()),()=>{if(player&&!player.IsDead())Begin(player,point,rotation,distant);});
        }
        internal static bool Begin(Player player,Vector3 point,Quaternion rotation,bool distant)
        {
            if(!CanStart(player)||float.IsNaN(point.sqrMagnitude)||float.IsInfinity(point.sqrMagnitude))return false;
            player.m_teleporting=true;player.m_distantTeleport=distant;player.m_teleportTimer=0;player.m_teleportCooldown=0;
            player.InvalidateCachedLiquidDepth();player.m_teleportFromPos=player.transform.position;player.m_teleportFromRot=player.transform.rotation;
            player.m_teleportTargetPos=point;player.m_teleportTargetRot=rotation;return true;
        }
        internal static void Tick(Player player,float dt)
        {
            bool previous=updating;updating=true;
            try{player.UpdateTeleport(dt);}finally{updating=previous;}
        }
        [HarmonyPatch(typeof(Player),"UpdateTeleport")]
        private static class Clock
        {private static bool Prefix(Player __instance)=>updating||!GameMovementRuntime.Managed(__instance)&&!(PlayerSessionGame.Managed&&__instance==Player.m_localPlayer);}
        [HarmonyPatch(typeof(Player),nameof(Player.TeleportTo))]
        private static class Direct
        {
            private static bool Prefix(Player __instance,Vector3 pos,Quaternion rot,bool distantTeleport,ref bool __result)
            {
                if(GameMovementRuntime.Managed(__instance)){__result=Begin(__instance,pos,rot,distantTeleport);return false;}
                if(PlayerSessionGame.Managed&&__instance==Player.m_localPlayer){__result=false;return false;}return true;
            }
        }
        [HarmonyPatch(typeof(Character),"RPC_TeleportTo")]
        private static class Legacy
        {private static bool Prefix(Character __instance)=>!(__instance is Player player)||!GameMovementRuntime.Managed(player)&&!(PlayerSessionGame.Managed&&player==Player.m_localPlayer);}
        [HarmonyPatch(typeof(TeleportWorld),nameof(TeleportWorld.Teleport))]
        private static class PortalIntent
        {
            private static bool Prefix(TeleportWorld __instance,Player player)
            {
                if(!PlayerSessionGame.Managed||player!=Player.m_localPlayer)return !GameMovementRuntime.Managed(player);
                if(!player.IsTeleporting()&&__instance.m_nview&&__instance.m_nview.IsValid())
                {var id=__instance.m_nview.GetZDO().m_uid;InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand{Kind=PlayerActionKind.UseOn,Definition=Portal,TargetUser=id.UserID,TargetId=id.ID});}
                return false;
            }
        }
        [HarmonyPatch(typeof(Teleport),nameof(Teleport.Interact))]
        private static class DungeonIntent
        {
            private static bool Prefix(Teleport __instance,Humanoid character,bool hold,ref bool __result)
            {
                if(!PlayerSessionGame.Managed||character!=Player.m_localPlayer)return true;
                __result=false;if(!hold&&!character.IsTeleporting()){var p=__instance.transform.position;InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand{Kind=PlayerActionKind.UseOn,Definition=Dungeon,Position=new[]{p.x,p.y,p.z}});}return false;
            }
        }
        // Teleport has no Awake/OnDestroy of its own. Register from its containing
        // scene object at instantiation, including non-network dungeon children.
        [HarmonyPatch(typeof(ZNetScene),"CreateObject",new[]{typeof(ZDO)})]
        private static class Register
        {private static void Postfix(GameObject __result)=>RegisterEntrances(__result);}
        [HarmonyPatch(typeof(LocationProxy),"SpawnLocation")]
        private static class RegisterLocation
        {private static void Postfix(LocationProxy __instance,bool __result){if(__result)RegisterEntrances(__instance.m_instance);}}
        [HarmonyPatch(typeof(DungeonGenerator),"PlaceRoom",new[]{typeof(DungeonDB.RoomData),typeof(Vector3),typeof(Quaternion),typeof(RoomConnection),typeof(ZoneSystem.SpawnMode)})]
        private static class RegisterRoom
        {private static void Postfix(Room __result){if(__result)RegisterEntrances(__result.gameObject);}}
        internal static void Prune()=>entrances.RemoveWhere(entry=>!entry);
        internal static void Clear(){entrances.Clear();GameDiscoveryRuntime.Clear();}
    }
}



