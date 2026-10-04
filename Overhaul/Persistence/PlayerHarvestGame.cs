using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class PlayerHarvestGame
    {
        internal const string Harvest="harvest.pick";
        private static bool Enabled=>PlayerPersistenceConfig.Enabled?.Value==true;
        internal static PlayerActionPlan Prepare(ZDO actor,GameObject target,InventoryMoveRequest request,PlayerSnapshot snapshot)
        {
            var plans=new List<PlayerActionPlan>();var targets=new List<ZDOID>();var objects=new Dictionary<long,ObjectRecord>();var updated=new Dictionary<string,PlayerChange>();
            string Key(PlayerChange r)=>r.Table+":"+r.Values[0]+(r.Table=="knowledge"?":"+r.Values[1]:"");
            try
            {
                var first=PrepareOne(actor,target,request,snapshot);plans.Add(first);targets.Add(target.GetComponent<ZNetView>().GetZDO().m_uid);foreach(var pair in first.Change.Objects)objects.Add(pair.Key,pair.Value);foreach(var row in first.Change.Player.Changes)updated[Key(row)]=row;
                var pick=target.GetComponent<Pickable>();
                float radius=Mathf.Clamp(global::Overhaul.Utility.OverhaulConfig.PickupRange?.Value ?? 0,0,20);
                if(pick && radius>0)
                {
                    var candidates=Physics.OverlapSphere(target.transform.position,radius).Select(c=>c.GetComponentInParent<Pickable>()).Where(p=>p && p!=pick && p.m_itemPrefab==pick.m_itemPrefab).Distinct().OrderBy(p=>(p.transform.position-target.transform.position).sqrMagnitude).Take(64);
                    foreach(var other in candidates)
                    {
                        if(objects.Count>=127)break;
                        var view=other.m_nview;if(!view || !view.IsValid() || !view.GetZDO().Persistent || GamePersistence.ActionReserved(view.GetZDO().m_uid))continue;
                        var current=new PlayerSnapshot(snapshot.Revision,snapshot.Rows.Where(r=>!updated.ContainsKey(Key(r))).Concat(updated.Values));
                        PlayerActionPlan next;try{next=PrepareOne(actor,other.gameObject,request,current);}catch(InvalidOperationException){continue;}
                        var additions=next.Change.Objects;
                        if(objects.Count+additions.Count>128){GamePersistence.ReleaseAction(new[]{view.GetZDO().m_uid});break;}
                        plans.Add(next);targets.Add(view.GetZDO().m_uid);foreach(var pair in additions)objects.Add(pair.Key,pair.Value);foreach(var row in next.Change.Player.Changes)updated[Key(row)]=row;
                    }
                }
                return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(request.Action.Operation,snapshot.Revision,updated.Values),objects),()=>{foreach(var plan in plans)plan.Publish();});
            }
            catch
            {
                GamePersistence.ReleaseAction(targets);
                throw;
            }
        }
        private static PlayerActionPlan PrepareOne(ZDO actor,GameObject target,InventoryMoveRequest request,PlayerSnapshot snapshot)
        {
            var view=target.GetComponent<ZNetView>();var data=view.GetZDO();var pick=target.GetComponent<Pickable>();var loose=target.GetComponent<PickableItem>();
            if(request.Action.Amount!=1 || !Storage.ChestAccess.WardAccessAt(target.transform.position,actor.GetLong(ZDOVars.s_playerID,0)))throw new InvalidOperationException("Harvest is unavailable");
            var outputs=new List<ObjectRecord>();var changes=new List<PlayerChange>();
            void Drop(GameObject prefab,int count,Vector3 point,Quaternion rotation)
            {
                var template=prefab?prefab.GetComponent<ItemDrop>():null;
                if(!template || count<1 || count>4096)throw new InvalidOperationException("Invalid harvest output");
                while(count>0)
                {
                    if(outputs.Count>=126)throw new InvalidOperationException("Too many harvest outputs");
                    var item=template.m_itemData.Clone();item.m_dropPrefab=prefab;item.m_stack=Math.Min(count,item.m_shared.m_maxStackSize);item.m_equipped=false;item.m_worldLevel=Game.m_worldLevel;item.m_cheated=false;
                    outputs.Add(PlayerDropGame.Ground(item,point,rotation,0,Vector3.up*4));count-=item.m_stack;
                }
            }
            if(loose)
            {
                if(loose.m_picked || !loose.m_itemPrefab)throw new InvalidOperationException("Harvest item is unavailable");
                Drop(loose.m_itemPrefab.gameObject,loose.GetStackSize(),target.transform.position+Vector3.up*.2f,target.transform.rotation);
                var point=target.transform.position;var looseEffects=loose.m_pickEffector;
                return PlayerActionGame.RemoveWorldObject(view,new PlayerBatch(request.Action.Operation,snapshot.Revision,changes),outputs,()=>looseEffects.Create(point,Quaternion.identity,null,1,-1,default(ZDOID)));
            }
            if(!pick || pick.m_picked || data.GetBool(ZDOVars.s_picked,pick.m_defaultPicked) || pick.m_enabled==0 || !pick.m_itemPrefab)
                throw new InvalidOperationException("Plant is unavailable");
            var floating=pick.GetComponent<Floating>();if(pick.m_tarPreventsPicking && floating && floating.IsInTar())throw new InvalidOperationException("Harvest is stuck in tar");
            if(pick.m_pickRaiseSkill!=Skills.SkillType.None)changes.AddRange(PlayerCraftProgressGame.Raise(snapshot,pick.m_pickRaiseSkill,1));
            var raised=new PlayerSnapshot(snapshot.Revision,snapshot.Rows.Where(r=>r.Table!="skills" || !changes.Any(c=>c.Table=="skills" && Convert.ToInt32(c.Values[0])==Convert.ToInt32(r.Values[0]))).Concat(changes));
            int bonus=pick.m_pickRaiseSkill!=Skills.SkillType.None && UnityEngine.Random.value<PlayerCraftProgressGame.Factor(raised,pick.m_pickRaiseSkill)*pick.m_maxLevelBonusChance?pick.m_bonusYieldAmount:0;
            int count=checked((pick.m_dontScale?pick.m_amount:Mathf.Max(pick.m_minAmountScaled,Game.instance.ScaleDrops(pick.m_itemPrefab,pick.m_amount)))+bonus);
            if(count<0 || count>126)throw new InvalidOperationException("Harvest yield exceeds action limit");
            int offset=0;
            Vector3 Position(){var v=UnityEngine.Random.insideUnitCircle*.2f;return target.transform.position+pick.GetSpawnOffset()+new Vector3(v.x,.5f*offset++,v.y);}
            for(int i=0;i<count;i++)Drop(pick.m_itemPrefab,1,Position(),Quaternion.Euler(0,UnityEngine.Random.Range(0,360),0));
            if(!pick.m_extraDrops.IsEmpty())foreach(var item in pick.m_extraDrops.GetDropListItems())Drop(item.m_dropPrefab,item.m_stack,Position(),Quaternion.Euler(0,UnityEngine.Random.Range(0,360),0));
            changes.Add(PlayerCraftProgressGame.Increment(snapshot,"statistics:0:pickable",pick.m_itemPrefab.name,1));
            if(pick.m_harvestStat!=PlayerStatType.None)changes.Add(PlayerCraftProgressGame.Increment(snapshot,"statistics:0:values",((int)pick.m_harvestStat).ToString(CultureInfo.InvariantCulture),1));
            var crop=CropFarming.Find(target);bool replant=crop!=null && crop.Replant;
            if(replant)
            {
                if(data.GetBool(CropFarming.FinishedKey,false))throw new InvalidOperationException("Crop has already been harvested");
                var seed=GamePersistence.AllocateActionObject(crop.Seedling,target.transform.position,target.transform.rotation);
                seed.Properties.Add(new PropertyRecord{Key=CropFarming.SourceKey,Type="string",Value=crop.Seedling.name});
                seed.Properties.Add(new PropertyRecord{Key=ZDOVars.s_plantTime,Type="long",Value=ZNet.instance.GetTime().Ticks});
                seed.Properties.Add(new PropertyRecord{Key=ZDOVars.s_creator,Type="long",Value=data.GetLong(ZDOVars.s_creator,0)});
                seed.Properties.Add(new PropertyRecord{Key=ZDOVars.s_creatorIndex,Type="int",Value=data.GetInt(ZDOVars.s_creatorIndex,-1)});outputs.Add(seed);
            }
            var position=target.transform.position;var spawn=position+pick.GetSpawnOffset();var pickEffects=pick.m_pickEffector;var bonusEffects=pick.m_bonusEffect;float aggravate=pick.m_aggravateRange;bool atSpawn=pick.m_pickEffectAtSpawnPoint;
            Action effects=()=>{pickEffects.Create(atSpawn?spawn:position,Quaternion.identity,null,1,-1,actor.m_uid);if(bonus>0)bonusEffects.Create(position,Quaternion.identity,null,1,-1,actor.m_uid);if(aggravate>0)BaseAI.AggravateAllInArea(position,aggravate,BaseAI.AggravatedReason.Theif);};
            var batch=new PlayerBatch(request.Action.Operation,snapshot.Revision,changes);
            if(replant || pick.m_respawnTimeMinutes<=0 && !pick.m_hideWhenPicked)return PlayerActionGame.RemoveWorldObject(view,batch,outputs,effects);
            using(var world=new PlayerActionObjectGame(data))
            {
                world.Set(ZDOVars.s_picked,1);if(pick.m_respawnTimeMinutes>0)world.Set(ZDOVars.s_pickedTime,ZNet.instance.GetTime().Ticks);
                return world.FinishWithObjects(batch,outputs,()=>{pick.m_picked=true;pick.m_pickedTime=data.GetLong(ZDOVars.s_pickedTime,0);view.InvokeRPC(ZNetView.Everybody,"RPC_SetPicked",true);effects();});
            }
        }
        [HarmonyPatch]
        private static class Intent
        {
            private static IEnumerable<MethodBase> TargetMethods(){yield return AccessTools.Method(typeof(Pickable),"Interact");yield return AccessTools.Method(typeof(PickableItem),"Interact");}
            [HarmonyPriority(Priority.First+200)]
            private static bool Prefix(Component __instance,Humanoid character,ref bool __result)
            {
                if(character!=Player.m_localPlayer || !PlayerSessionGame.Managed)return true;__result=false;
                var view=__instance.GetComponent<ZNetView>();if(!view || !view.IsValid() || character.IsTeleporting())return false;
                var id=view.GetZDO().m_uid;InventoryMoveGame.Client?.Controller.Act(new PlayerActionCommand{Kind=PlayerActionKind.UseOn,Definition=Harvest,TargetUser=id.UserID,TargetId=id.ID},0,0,1);return false;
            }
        }
        [HarmonyPatch]
        private static class Legacy
        {
            private static IEnumerable<MethodBase> TargetMethods(){yield return AccessTools.Method(typeof(Pickable),"RPC_Pick");yield return AccessTools.Method(typeof(PickableItem),"RPC_Pick");}
            private static bool Prefix(Component __instance){var v=__instance.GetComponent<ZNetView>();return !Enabled && (!v || !v.IsValid() || !GamePersistence.ActionReserved(v.GetZDO().m_uid));}
        }
        [HarmonyPatch(typeof(Pickable),"UpdateRespawn")]
        private static class Respawn
        {private static bool Prefix(Pickable __instance)=>!__instance.m_nview || !__instance.m_nview.IsValid() || !GamePersistence.ActionReserved(__instance.m_nview.GetZDO().m_uid);}
        [HarmonyPatch(typeof(Pickable),"RPC_SetPicked")]
        private static class Picked
        {
            private static bool Prefix(Pickable __instance,long sender)
            {if(__instance.m_nview && __instance.m_nview.IsValid() && GamePersistence.ActionReserved(__instance.m_nview.GetZDO().m_uid))return false;return !Enabled || sender==__instance.m_nview.GetZDO().GetOwner();}
        }
    }
}





