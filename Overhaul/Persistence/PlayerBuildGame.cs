using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class PlayerBuildGame
    {
        internal const string Repair="build.repair", Remove="build.remove";
        private static readonly int Placed="overhaul_build_placed".GetStableHashCode();
        internal static PlayerActionPlan Prepare(ZDO actor,InventoryMoveRequest request,PlayerSnapshot snapshot,PlayerActionInventory inventory)
        {
            var command=request.Gameplay;int slot=request.Action.FromY*256+request.Action.FromX;
            var tool=PlayerInventoryView.ReadItem(inventory.Item(slot),null,true);var table=tool.m_shared.m_buildPieces;
            if(!tool.m_equipped || !table || tool.m_shared.m_useDurability && tool.m_durability<=0 || request.Action.Amount!=1 || actor.GetBool(ZDOVars.s_dead,false))
                throw new InvalidOperationException("Construction tool is unavailable");
            if(command.Definition==Repair || command.Definition==Remove)return Existing(actor,request,snapshot,inventory,tool);
            var prefab=table.m_pieces.FirstOrDefault(p=>p && p.name==command.Definition);var piece=prefab?prefab.GetComponent<Piece>():null;
            if(!piece || !piece.m_enabled || piece.m_repairPiece || piece.m_removePiece || piece.m_harvest || prefab.GetComponent<TerrainModifier>() || prefab.GetComponent<TerrainOp>())
                throw new InvalidOperationException("Construction definition is unavailable or requires a terrain action");
            var position=new Vector3(command.Position[0],command.Position[1],command.Position[2]);
            var rotation=new Quaternion(command.Rotation[0],command.Rotation[1],command.Rotation[2],command.Rotation[3]);
            if(Mathf.Abs(Quaternion.Dot(rotation,rotation)-1)>0.01f)throw new InvalidOperationException("Invalid construction rotation");
            long creator=actor.GetLong(ZDOVars.s_playerID,0);
            Validate(actor,piece,position,rotation,creator);
            var station=piece.m_craftingStation?CraftingStation.HaveBuildStationInRange(piece.m_craftingStation.m_name,position):null;
            bool free=ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey());
            if(piece.m_craftingStation && !station && !free)throw new InvalidOperationException("Construction station is out of range");
            if(!string.IsNullOrEmpty(piece.m_dlc) && (!DLCMan.instance || !DLCMan.instance.IsDLCInstalled(piece.m_dlc)))throw new InvalidOperationException("Construction DLC is unavailable");
            using(var resources=new PlayerCraftResourcesGame(inventory,station,creator))
            {
                if(!free)foreach(var requirement in piece.m_resources.Where(r=>r.m_resItem && r.m_amount>0))resources.Consume(requirement.m_resItem,requirement.m_amount);
                if(tool.m_shared.m_useDurability)
                {
                    float factor=tool.m_shared.m_placementDurabilitySkill==Skills.SkillType.None?0:PlayerCraftProgressGame.Factor(snapshot,tool.m_shared.m_placementDurabilitySkill);
                    tool.m_durability=Mathf.Max(0,tool.m_durability-tool.m_shared.m_useDurabilityDrain*(1-tool.m_shared.m_placementDurabilityMax*factor)*Game.m_durabilityRate);
                    inventory.Set(slot,PlayerActionGame.Row(tool,slot%256,slot/256).Values);
                }
                var output=GamePersistence.AllocateActionObject(prefab,position,rotation);
                void Add(int key,string type,object value)=>output.Properties.Add(new PropertyRecord{Key=key,Type=type,Value=value});
                Add(ZDOVars.s_creator,"long",creator);Add(ZDOVars.s_creatorIndex,"int",actor.GetInt(ZDOVars.s_creatorIndex,-1));Add(Placed,"int",1);
                if(prefab.GetComponent<Plant>())Add(ZDOVars.s_plantTime,"long",ZNet.instance.GetTime().Ticks);
                if(prefab.GetComponent<ItemDrop>())Add(ZDOVars.s_piece,"int",1);
                if(prefab.GetComponent<PrivateArea>())Add(ZDOVars.s_creatorName,"string",(string)snapshot.Rows.First(r=>r.Table=="state" && (string)r.Values[0]=="player_name").Values[3]);
                if(resources.Cheated && !PlayerProfile.s_bypassCheatChecks)Add(ZDOVars.s_cheated,"int",1);
                var changes=inventory.Delta(request.Action.Operation,snapshot.Revision).Changes.ToList();
                changes.Add(PlayerCraftProgressGame.Increment(snapshot,"statistics:0:values",((int)PlayerStatType.Builds).ToString(),1));
                changes.Add(PlayerCraftProgressGame.Increment(snapshot,"statistics:0:pieces",piece.m_name,1));
                return resources.Finish(new PlayerBatch(request.Action.Operation,snapshot.Revision,changes),()=>piece.m_placeEffect?.Create(position,rotation),new[]{output});
            }
        }
        private static PlayerActionPlan Existing(ZDO actor,InventoryMoveRequest request,PlayerSnapshot snapshot,PlayerActionInventory inventory,ItemDrop.ItemData tool)
        {
            var command=request.Gameplay;var target=ZNetScene.instance.FindInstance(new ZDOID(command.TargetUser,command.TargetId));
            var piece=target?target.GetComponent<Piece>():null;var view=piece?piece.GetComponent<ZNetView>():null;var data=view && view.IsValid()?view.GetZDO():null;
            long creator=actor.GetLong(ZDOVars.s_playerID,0);
            if(data==null || !data.Persistent || GamePersistence.ActionReserved(data.m_uid) || Vector3.Distance(actor.GetPosition(),target.transform.position)>Game.instance.m_playerPrefab.GetComponent<Player>().m_maxPlaceDistance ||
                !Storage.ChestAccess.WardAccessAt(target.transform.position,creator) || Location.IsInsideNoBuildLocation(target.transform.position))throw new InvalidOperationException("Construction target is unavailable");
            if(piece.m_craftingStation && !ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoWorkbench) && !CraftingStation.HaveBuildStationInRange(piece.m_craftingStation.m_name,actor.GetPosition()))throw new InvalidOperationException("Required construction station is unavailable");
            if(command.Definition==Repair)
            {
                var wear=target.GetComponent<WearNTear>();
                if(!tool.m_shared.m_buildPieces.m_pieces.Any(p=>p && p.GetComponent<Piece>()?.m_repairPiece==true) || !wear || data.GetFloat(ZDOVars.s_health,wear.m_health)>=wear.m_health || Time.time-wear.m_lastRepair<1)throw new InvalidOperationException("Piece cannot be repaired");
                int slot=request.Action.FromY*256+request.Action.FromX;
                if(tool.m_shared.m_useDurability){tool.m_durability=Mathf.Max(0,tool.m_durability-tool.m_shared.m_useDurabilityDrain*Game.m_durabilityRate);inventory.Set(slot,PlayerActionGame.Row(tool,slot%256,slot/256).Values);}
                using(var world=new PlayerActionObjectGame(data))
                {world.Set(ZDOVars.s_health,wear.m_health);return world.Finish(inventory.Delta(request.Action.Operation,snapshot.Revision),()=>{wear.m_lastRepair=Time.time;wear.m_nview.InvokeRPC(ZNetView.Everybody,"RPC_HealthChanged",wear.m_health);});}
            }
            bool feast=target.GetComponent<Feast>() || target.GetComponent<ItemDrop>();
            if(!piece.m_canBeRemoved || !piece.CanBeRemoved() || (feast?!tool.m_shared.m_buildPieces.m_canRemoveFeasts:!tool.m_shared.m_buildPieces.m_canRemovePieces))throw new InvalidOperationException("Piece cannot be dismantled");
            // These components have their own stored payloads; never delete one until its
            // complete refund has been incorporated into the same transaction.
            if(target.GetComponent<IRemoved>()!=null || target.GetComponent<Smelter>() || target.GetComponent<CookingStation>() || target.GetComponent<Fermenter>() || target.GetComponent<ItemStand>() || target.GetComponent<ArmorStand>() || target.GetComponent<Turret>() || target.GetComponent<Catapult>() || piece.m_destroyedLootPrefab)
                throw new InvalidOperationException("Dismantling this machine requires its stored contents handler");
            var outputs=new List<ObjectRecord>();var chest=target.GetComponentInChildren<Container>();
            if(chest)
            {
                if(!chest.CheckAccess(creator) || Storage.MoveReservation.Busy(chest) || Storage.ChestAccess.Leased(chest) || GamePersistence.ReservedSlots(data.m_uid).Any())throw new InvalidOperationException("Container is in use");
                chest.Load();foreach(var item in chest.GetInventory().GetAllItems())outputs.Add(PlayerDropGame.Ground(item.Clone(),target.transform.position+Vector3.up,Quaternion.identity));
            }
            if(!ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey()))foreach(var requirement in piece.m_resources.Where(r=>r.m_resItem && r.m_recover && r.m_amount>0))
            {
                int count=requirement.m_amount;var food=target.GetComponent<Feast>();if(food)count=Mathf.FloorToInt(count*food.GetStackPercentige());
                if(data.GetLong(ZDOVars.s_creator,0)==0 && count>0)count=Mathf.Max(1,count/3);
                while(count>0)
                {var item=requirement.m_resItem.m_itemData.Clone();item.m_dropPrefab=requirement.m_resItem.gameObject;item.m_stack=Math.Min(count,item.m_shared.m_maxStackSize);item.m_equipped=false;item.m_cheated|=data.GetBool(ZDOVars.s_cheated,false)&&!PlayerProfile.s_bypassCheatChecks;count-=item.m_stack;outputs.Add(PlayerDropGame.Ground(item,target.transform.position+Vector3.up*piece.m_returnResourceHeightOffset,Quaternion.identity));}
            }
            if(outputs.Count>127)throw new InvalidOperationException("Dismantling output exceeds one transaction");
            return PlayerActionGame.RemoveWorldObject(view,inventory.Delta(request.Action.Operation,snapshot.Revision),outputs);
        }
        internal static void Validate(ZDO actor,Piece piece,Vector3 point,Quaternion rotation,long creator)
        {
            var playerPrefab=Game.instance.m_playerPrefab.GetComponent<Player>();
            if(Vector3.Distance(actor.GetPosition(),point)>playerPrefab.m_maxPlaceDistance+piece.m_extraPlacementDistance ||
                Location.IsInsideNoBuildLocation(point) || !Storage.ChestAccess.WardAccessAt(point,creator))throw new InvalidOperationException("Construction position is inaccessible");
            if(!piece.m_allowedInDungeons && Character.InInterior(point) && !EnvMan.instance.CheckInteriorBuildingOverride() && !ZoneSystem.instance.GetGlobalKey(GlobalKeys.DungeonBuild))throw new InvalidOperationException("Construction is forbidden in this dungeon");
            var biome=Heightmap.FindBiome(point);var ground=Heightmap.FindHeightmap(point);
            if(piece.m_onlyInBiome!=Heightmap.Biome.None && (biome&piece.m_onlyInBiome)==0)throw new InvalidOperationException("Construction biome is invalid");
            if(piece.m_onlyInTeleportArea && !EffectArea.IsPointInsideArea(point,EffectArea.Type.Teleport,0))throw new InvalidOperationException("Construction requires a teleport area");
            if(piece.m_requireDeepSnow && (biome!=Heightmap.Biome.DeepNorth || !ground || ground.GetCultivationMask(point)<=0) ||
                !piece.m_allowedInDeepSnow && biome==Heightmap.Biome.DeepNorth && ground && ground.GetCultivationMask(point)>playerPrefab.m_deepSnowBuildHeight)throw new InvalidOperationException("Construction snow conditions are invalid");
            if(piece.m_cultivatedGroundOnly && (!ground || !ground.IsCultivated(point)))throw new InvalidOperationException("Construction requires cultivated ground");
            if(piece.m_vegetationGroundOnly && (!ground || (biome==Heightmap.Biome.Swamp?ground.GetVegetationMask(point)>.1f:ground.GetVegetationMask(point)<.25f)))throw new InvalidOperationException("Construction requires dirt");
            float water=Floating.GetLiquidLevel(point,1,LiquidType.All);
            if(piece.m_noInWater && water>point.y || piece.m_waterPiece && water<point.y-.5f)throw new InvalidOperationException("Construction water conditions are invalid");
            var extension=piece.GetComponent<StationExtension>();
            if(extension && !extension.FindClosestStationInRange(point))throw new InvalidOperationException("Station extension is out of range");
            int mask=LayerMask.GetMask("Default","static_solid","Default_small","piece","piece_nonsolid","terrain","vehicle");
            var nearby=Physics.OverlapSphere(point,Mathf.Max(10,piece.m_blockRadius,piece.m_connectRadius),mask,QueryTriggerInteraction.Ignore);
            if(piece.m_mustConnectTo && !nearby.Any(c=>{var view=c.GetComponentInParent<ZNetView>();return view && view.IsValid() && view.GetZDO().GetPrefab()==piece.m_mustConnectTo.name.GetStableHashCode() && Vector3.Distance(view.transform.position,point)<=piece.m_connectRadius;}))throw new InvalidOperationException("Required construction connection is absent");
            if(piece.m_blockRadius>0 && nearby.Any(c=>{var other=c.GetComponentInParent<Piece>();return other && Vector3.Distance(other.transform.position,point)<piece.m_blockRadius && piece.m_blockingPieces.Any(p=>p && p.m_name==other.m_name);}))throw new InvalidOperationException("Construction needs more space");
            if(nearby.Any(c=>{var other=c.GetComponentInParent<Piece>();return other && other.m_name==piece.m_name && Vector3.Distance(other.transform.position,point)<.05f && (!piece.m_allowRotatedOverlap || Quaternion.Angle(other.transform.rotation,rotation)<.1f);}))throw new InvalidOperationException("Construction overlaps an existing piece");
            if(piece.m_groundOnly || piece.m_groundPiece || piece.m_cultivatedGroundOnly)
            {if(!Physics.Raycast(point+Vector3.up*.3f,Vector3.down,out var hit,.8f,mask,QueryTriggerInteraction.Ignore) || !hit.collider.GetComponent<Heightmap>())throw new InvalidOperationException("Construction requires ground contact");}
            if(piece.m_mustBeAboveConnected && piece.m_mustConnectTo)
            {if(!Physics.Raycast(point,Vector3.down,out var hit,piece.m_connectRadius,mask,QueryTriggerInteraction.Ignore) || hit.collider.GetComponentInParent<ZNetView>()?.GetZDO()?.GetPrefab()!=piece.m_mustConnectTo.name.GetStableHashCode())throw new InvalidOperationException("Construction must be above its connection");}
            var characters=new List<Character>();Character.GetCharactersInRange(point,30,characters);
            foreach(var collider in piece.GetComponentsInChildren<Collider>())
            {
                if(collider.isTrigger || !collider.enabled)continue;
                var position=point+rotation*piece.transform.InverseTransformPoint(collider.transform.position);
                var orientation=rotation*Quaternion.Inverse(piece.transform.rotation)*collider.transform.rotation;
                if(piece.m_noClipping)
                    foreach(var other in nearby)
                        if(Physics.ComputePenetration(collider,position,orientation,other,other.transform.position,other.transform.rotation,out _,out var depth) && depth>.2f)throw new InvalidOperationException("Construction clips the environment");
                if(!(collider is MeshCollider mesh) || mesh.convex)
                    foreach(var character in characters)
                    {var body=character.GetCollider();if(body && Physics.ComputePenetration(collider,position,orientation,body,body.transform.position,body.transform.rotation,out _,out _))throw new InvalidOperationException("Construction overlaps a character");}
            }
        }
        [HarmonyPatch(typeof(Player),nameof(Player.TryPlacePiece))]
        private static class Intent
        {
            [HarmonyPriority(Priority.First+300)]
            private static bool Prefix(Player __instance,Piece piece,ref bool __result)
            {
                if(__instance!=Player.m_localPlayer || !PlayerSessionGame.Managed)return true;__result=false;
                __instance.UpdatePlacementGhost(true);if(__instance.m_placementStatus!=Player.PlacementStatus.Valid || !__instance.m_placementGhost)return false;
                var tool=__instance.GetRightItem();if(tool==null)return false;var p=__instance.m_placementGhost.transform.position;var q=__instance.m_placementGhost.transform.rotation;
                if(InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand{Kind=PlayerActionKind.Build,Definition=piece.gameObject.name,Position=new[]{p.x,p.y,p.z},Rotation=new[]{q.x,q.y,q.z,q.w}},tool.m_gridPos.x,tool.m_gridPos.y,1)==true)__instance.m_lastToolUseTime=Time.time;
                // Native caller must not consume the materials or create another piece.
                return false;
            }
        }
        [HarmonyPatch(typeof(ZNetScene),"CreateObject")]
        private static class PlacedVisual
        {
            private static void Postfix(ZDO zdo,GameObject __result)
            {if(!__result || zdo.GetInt(Placed,0)==0)return;var wear=__result.GetComponent<WearNTear>();if(wear)wear.OnPlaced();if(zdo.IsOwner())zdo.Set(Placed,0);}
        }
        [HarmonyPatch(typeof(Player),"Repair")]
        private static class RepairIntent
        {
            [HarmonyPriority(Priority.First+300)]
            private static bool Prefix(Player __instance,ItemDrop.ItemData toolItem)
            {if(__instance!=Player.m_localPlayer || !PlayerSessionGame.Managed)return true;Send(__instance,__instance.GetHoveringPiece(),toolItem,Repair);return false;}
        }
        [HarmonyPatch(typeof(Player),"RemovePiece")]
        private static class RemoveIntent
        {
            [HarmonyPriority(Priority.First+300)]
            private static bool Prefix(Player __instance,ref bool __result)
            {if(__instance!=Player.m_localPlayer || !PlayerSessionGame.Managed)return true;__result=false;Send(__instance,__instance.GetHoveringPiece(),__instance.GetRightItem(),Remove);return false;}
        }
        private static void Send(Player player,Piece piece,ItemDrop.ItemData tool,string action)
        {var view=piece?piece.GetComponent<ZNetView>():null;if(tool==null || !view || !view.IsValid())return;var id=view.GetZDO().m_uid;if(InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand{Kind=PlayerActionKind.Build,Definition=action,TargetUser=id.UserID,TargetId=id.ID},tool.m_gridPos.x,tool.m_gridPos.y,1)==true)player.m_lastToolUseTime=Time.time;}
        [HarmonyPatch(typeof(WearNTear),"RPC_Repair")]
        private static class LegacyRepair {private static bool Prefix()=>PlayerPersistenceConfig.Enabled?.Value!=true;}
        [HarmonyPatch(typeof(WearNTear),"RPC_Remove")]
        private static class LegacyRemove {private static bool Prefix()=>PlayerPersistenceConfig.Enabled?.Value!=true;}
    }
}
