using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class PlayerFishingGame
    {
        internal const string Catch="fish.catch", Pickup="fish.pickup";
        private static bool Enabled=>PlayerPersistenceConfig.Enabled?.Value==true;
        private static FishingFloat waitingFloat;
        private static Fish waitingFish;
        private static bool sent;
        internal static void Clear(){waitingFloat=null;waitingFish=null;sent=false;}
        internal static PlayerActionPlan Prepare(ZDO actor,InventoryMoveRequest request,PlayerSnapshot snapshot,PlayerActionInventory inventory)
        {
            var target=ZNetScene.instance.FindInstance(new ZDOID(request.Gameplay.TargetUser,request.Gameplay.TargetId));var fish=target?target.GetComponent<Fish>():null;
            var view=fish?fish.m_nview:null;var data=view && view.IsValid()?view.GetZDO():null;
            if(data==null || !data.Persistent || GamePersistence.ActionReserved(data.m_uid) || Vector3.Distance(actor.GetPosition(),data.GetPosition())>5 || request.Action.Amount!=1)
                throw new InvalidOperationException("Fish is unavailable");
            bool caught=request.Gameplay.Definition==Catch;FishingFloat line=null;
            if(caught)
            {
                line=FishingFloat.FindFloat(fish);var owner=line?line.GetOwner():null;
                if(!line || !owner || owner.GetZDOID()!=actor.m_uid || Vector3.Distance(actor.GetPosition(),line.transform.position)>5)
                    throw new InvalidOperationException("Fish is not caught by this player");
            }
            else if(request.Gameplay.Definition!=Pickup || !fish.IsOutOfWater())throw new InvalidOperationException("Fish must be out of water for manual pickup");
            var drop=target.GetComponent<ItemDrop>();var prefab=drop?target:fish.m_pickupItem;var template=prefab?prefab.GetComponent<ItemDrop>():null;
            if(!template)throw new InvalidOperationException("Fish item definition is unavailable");
            var item=template.m_itemData.Clone();
            if(drop)ItemDrop.LoadFromZDO(item,data);else{item.m_stack=fish.m_pickupItemStackSize;item.m_dropPrefab=prefab;}
            if(!item.m_dropPrefab)item.m_dropPrefab=ObjectDB.instance.GetItemPrefab(data.GetPrefab());
            item.m_equipped=false;item.m_pickedUp=true;inventory.Add(PlayerActionGame.Row(item).Values,item.m_customData);
            var outputs=new List<ObjectRecord>();var knowledge=new Dictionary<string,PlayerChange>();
            void Known(ItemDrop.ItemData value)=>knowledge[value.m_shared.m_name]=new PlayerChange("knowledge",false,"materials",value.m_shared.m_name,"");
            Known(item);
            if(caught && !fish.m_extraDrops.IsEmpty())foreach(var bonus in fish.m_extraDrops.GetDropListItems())
            {
                bonus.m_equipped=false;bonus.m_pickedUp=true;
                try{inventory.Add(PlayerActionGame.Row(bonus).Values,bonus.m_customData);Known(bonus);}
                catch(PlayerInventoryFullException)
                {
                    int count=bonus.m_stack;while(count>0){if(outputs.Count>=127)throw new InvalidOperationException("Fish bonus output exceeds action limit");var part=bonus.Clone();part.m_stack=Math.Min(count,part.m_shared.m_maxStackSize);outputs.Add(PlayerDropGame.Ground(part,fish.transform.position,Quaternion.identity));count-=part.m_stack;}
                }
            }
            var changes=inventory.Delta(request.Action.Operation,snapshot.Revision).Changes.Concat(knowledge.Values).ToList();
            if(caught)
            {
                changes.Add(PlayerCraftProgressGame.Increment(snapshot,"statistics:0:pickable",fish.GetHoverName(),1));
                changes.Add(PlayerCraftProgressGame.Increment(snapshot,"statistics:0:values",((int)PlayerStatType.FishCaught).ToString(CultureInfo.InvariantCulture),1));
                if(drop && item.m_quality<=6)changes.Add(PlayerCraftProgressGame.Increment(snapshot,"statistics:0:values",((int)PlayerStatType.FishCaughtTier0+item.m_quality).ToString(CultureInfo.InvariantCulture),1));
            }
            var batch=new PlayerBatch(request.Action.Operation,snapshot.Revision,changes);
            var fishPlan=PlayerActionGame.RemoveWorldObject(view,batch,outputs);
            if(!caught || !PlayerFishingCastGame.Managed(line))return fishPlan;
            try
            {
                var linePlan=PlayerActionGame.RemoveWorldObject(line.m_nview,batch,Array.Empty<ObjectRecord>());
                return new PlayerActionPlan(new PlayerWorldAction(batch,fishPlan.Change.Objects.Concat(linePlan.Change.Objects).ToDictionary(p=>p.Key,p=>p.Value)),()=>{fishPlan.Publish();linePlan.Publish();});
            }
            catch{GamePersistence.ReleaseAction(new[]{data.m_uid});throw;}
        }
        private static void SendCatch()
        {
            if(sent || !waitingFish || !waitingFish.m_nview || !waitingFish.m_nview.IsValid())return;
            var id=waitingFish.m_nview.GetZDO().m_uid;
            sent=InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand{Kind=PlayerActionKind.UseOn,Definition=Catch,TargetUser=id.UserID,TargetId=id.ID},0,0,1)==true;
        }
        internal static void RequestCatch(FishingFloat line,Fish fish)
        {if(waitingFloat!=line){waitingFloat=line;waitingFish=fish;sent=false;}SendCatch();}
        internal static void Confirm(InventoryMoveRequest request)
        {
            if(request?.Gameplay?.Definition!=Catch)return;
            var line=waitingFloat;var fish=waitingFish;Clear();
            if(PlayerFishingCastGame.Managed(line))return;
            try
            {
                if(fish)fish.OnHooked(null);
                if(line && line.m_nview && line.m_nview.IsValid()){line.SetCatch(null);line.m_nview.Destroy();}
            }
            catch(Exception error){ZLog.LogWarning("[Overhaul fishing presentation] "+error.Message);}
        }
        [HarmonyPatch(typeof(FishingFloat),nameof(FishingFloat.Catch))]
        private static class CatchIntent
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Fish fish,Character owner,ref string __result)
            {
                var line=fish?FishingFloat.FindFloat(fish):null;
                if(PlayerFishingCastGame.Managed(line))
                {if(line.m_nview.IsOwner())line.m_nview.GetZDO().Set(PlayerFishingCastGame.Ready,1);__result="";return false;}
                if(owner!=Player.m_localPlayer || !PlayerSessionGame.Managed)return true;
                __result="";if(!fish)return false;
                waitingFloat=FishingFloat.FindFloat(fish);waitingFish=fish;sent=false;SendCatch();return false;
            }
        }
        [HarmonyPatch(typeof(FishingFloat),"FixedUpdate")]
        private static class Wait
        {
            private static bool Prefix(FishingFloat __instance)
            {if(__instance!=waitingFloat || !PlayerSessionGame.Managed)return true;SendCatch();return false;}
        }
        [HarmonyPatch(typeof(FishingFloat),"SetCatch")]
        private static class KeepCatch
        {private static bool Prefix(FishingFloat __instance,Fish fish)=>fish || (!PlayerSessionGame.Managed || __instance!=waitingFloat) && (!PlayerFishingCastGame.Managed(__instance) || __instance.m_nview.GetZDO().GetInt(PlayerFishingCastGame.Ready,0)!=1);}
        [HarmonyPatch(typeof(Fish),nameof(Fish.OnHooked))]
        private static class KeepHook
        {private static bool Prefix(Fish __instance,FishingFloat ff){var line=FishingFloat.FindFloat(__instance);return ff || (!PlayerSessionGame.Managed || __instance!=waitingFish) && (!PlayerFishingCastGame.Managed(line) || line.m_nview.GetZDO().GetInt(PlayerFishingCastGame.Ready,0)!=1);}}
        [HarmonyPatch(typeof(ZNetView),nameof(ZNetView.Destroy))]
        private static class KeepFloat
        {private static bool Prefix(ZNetView __instance)=>!PlayerSessionGame.Managed || !waitingFloat || __instance!=waitingFloat.m_nview;}
        [HarmonyPatch(typeof(Fish),nameof(Fish.Pickup))]
        private static class PickupIntent
        {
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Fish __instance,Humanoid character,ref bool __result)
            {
                if(character!=Player.m_localPlayer || !PlayerSessionGame.Managed)return true;__result=false;
                if(!__instance.m_nview || !__instance.m_nview.IsValid() || character.IsTeleporting())return false;
                var id=__instance.m_nview.GetZDO().m_uid;InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand{Kind=PlayerActionKind.UseOn,Definition=Pickup,TargetUser=id.UserID,TargetId=id.ID},0,0,1);return false;
            }
        }
        [HarmonyPatch]
        private static class Legacy
        {
            private static IEnumerable<MethodBase> TargetMethods(){yield return AccessTools.Method(typeof(Fish),"RPC_RequestPickup");yield return AccessTools.Method(typeof(Fish),"RPC_Pickup");}
            private static bool Prefix(Fish __instance)=>!Enabled && (!__instance.m_nview || !__instance.m_nview.IsValid() || !GamePersistence.ActionReserved(__instance.m_nview.GetZDO().m_uid));
        }
        [HarmonyPatch(typeof(Fish),nameof(Fish.CustomFixedUpdate))]
        private static class Simulation
        {private static bool Prefix(Fish __instance)=>!__instance.m_nview || !__instance.m_nview.IsValid() || !GamePersistence.ActionReserved(__instance.m_nview.GetZDO().m_uid);}
    }
}
