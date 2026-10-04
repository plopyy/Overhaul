using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class PlayerResourceGame
    {
        internal static IEnumerable<PlayerChange> Advance(PlayerSnapshot snapshot,ZDO actor,double seconds)
        {
            var definition=Game.instance?Game.instance.m_playerPrefab?.GetComponent<Player>():null;
            var instance=actor==null?null:ZNetScene.instance.FindInstance(actor.m_uid);var player=instance?instance.GetComponent<Player>():null;
            if(!definition || !player || player.IsDead() || player.InIntro() || player.IsTeleporting())return Array.Empty<PlayerChange>();
            var items=snapshot.Rows.Where(r=>r.Table=="inventory").Select(r=>PlayerInventoryView.ReadItem(r.Values,null,true)).ToArray();
            var equipped=items.Where(i=>i.m_equipped).ToArray();
            var effects=PlayerPotionGame.Active(snapshot).ToList();
            foreach(var item in equipped)
            {
                if(item.m_shared.m_equipStatusEffect)effects.Add(item.m_shared.m_equipStatusEffect);
                if(item.m_shared.m_setStatusEffect && equipped.Count(i=>i.m_shared.m_setName==item.m_shared.m_setName)>=item.m_shared.m_setSize)effects.Add(item.m_shared.m_setStatusEffect);
            }
            effects=effects.GroupBy(e=>e.NameHash()).Select(g=>g.First()).ToList();
            float health=definition.m_baseHP,stamina=definition.m_baseStamina,eitr=0,foodHeal=0;
            float duration=PlayerFoodGame.Multiplier(snapshot.Rows);
            foreach(var row in snapshot.Rows.Where(r=>r.Table=="food"))
            {
                var prefab=ObjectDB.instance.GetItemPrefab((string)row.Values[1]);var item=prefab?prefab.GetComponent<ItemDrop>():null;
                if(!item)throw new InvalidOperationException("Active food definition is unavailable");
                var food=item.m_itemData.m_shared;
                float factor=Mathf.Pow(Mathf.Clamp01(Convert.ToSingle(row.Values[2])/(food.m_foodBurnTime*duration)),.3f);
                health+=food.m_food*factor;stamina+=food.m_foodStamina*factor;eitr+=food.m_foodEitr*factor;foodHeal+=food.m_foodRegen;
            }
            float hpMultiplier=1,staminaMultiplier=1,eitrMultiplier=1,carry=definition.m_maxCarryWeight;
            foreach(var effect in effects){effect.ModifyHealthRegen(ref hpMultiplier);effect.ModifyStaminaRegen(ref staminaMultiplier);effect.ModifyEitrRegen(ref eitrMultiplier);effect.ModifyMaxCarryWeight(definition.m_maxCarryWeight,ref carry);}
            carry+=PlayerCraftProgressGame.Bonus(snapshot,"carry");
            bool encumbered=!PlayerCraftProgressGame.Passive(snapshot,"unburdened") && items.Sum(i=>i.GetWeight())>carry;
            bool busy=player.InAttack() || player.InDodge();float block=player.IsBlocking()?.8f:1;
            bool vitality=PlayerCraftProgressGame.Passive(snapshot,"vitality");
            if(vitality)health+=(float)global::Overhaul.Leveling.LevelingConfig.Current.VitalityHealth;
            eitrMultiplier+=equipped.Sum(i=>i.m_shared.m_eitrRegenModifier);
            return PlayerResources.Regenerate(snapshot,seconds,new PlayerResources.Rates
            {
                MaxHealth=health,MaxStamina=stamina,MaxEitr=eitr,FoodHeal=foodHeal*Mathf.Max(0,hpMultiplier)*(1+PlayerCraftProgressGame.Bonus(snapshot,"health_regen")),
                PassiveHeal=vitality?global::Overhaul.Leveling.LevelingConfig.Current.VitalityRegen:0,
                Stamina=busy || encumbered || player.m_wallRunning || player.IsSwimming() && !player.IsOnGround()?0:definition.m_staminaRegen*Mathf.Max(0,staminaMultiplier)*block*Game.m_staminaRegenRate,
                StaminaShape=definition.m_staminaRegenTimeMultiplier,
                Eitr=busy?0:definition.m_eiterRegen*Mathf.Max(0,eitrMultiplier)*block*(1+PlayerCraftProgressGame.Bonus(snapshot,"eitr_regen"))
            });
        }
        internal static PlayerChange[] BuildCost(InventoryMoveRequest request,PlayerSnapshot snapshot,PlayerActionInventory inventory)
        {
            var tool=PlayerInventoryView.ReadItem(inventory.Item(request.Action.FromY*256+request.Action.FromX),null,true);
            if(!tool.m_equipped || !tool.m_shared.m_buildPieces || tool.m_shared.m_attack==null)throw new InvalidOperationException("Construction tool is unavailable");
            float stamina=tool.m_shared.m_attack.m_attackStamina,eitr=request.Gameplay.Definition==PlayerBuildGame.Remove?0:tool.m_shared.m_attack.m_attackEitr;
            if(stamina==0 && eitr==0)return Array.Empty<PlayerChange>();
            float equipment=0;
            foreach(int slot in inventory.Keys)
            {var item=PlayerInventoryView.ReadItem(inventory.Item(slot),null,true);if(item.m_equipped)equipment+=item.m_shared.m_homeItemsStaminaModifier;}
            stamina*=1+equipment;float baseCost=stamina;
            foreach(var effect in PlayerPotionGame.Active(snapshot))effect.ModifyHomeItemStaminaUsage(baseCost,ref stamina);
            if(tool.m_shared.m_buildPieces && tool.m_shared.m_buildPieces.m_skill!=Skills.SkillType.None)stamina*=1-.5f*PlayerCraftProgressGame.Factor(snapshot,tool.m_shared.m_buildPieces.m_skill);
            eitr*=1-Mathf.Clamp01(PlayerCraftProgressGame.Bonus(snapshot,"eitr_cost"));
            var definition=Game.instance?Game.instance.m_playerPrefab?.GetComponent<Player>():null;
            if(!definition)throw new InvalidOperationException("Player resource definitions are unavailable");
            return PlayerResources.Spend(snapshot,Mathf.Max(0,stamina)*Game.m_staminaRate,Mathf.Max(0,eitr)*Game.m_eitrRate,definition.m_staminaRegenDelay,definition.m_eitrRegenDelay);
        }
        internal static Action Presentation(IEnumerable<PlayerChange> source,Player player)
        {
            var rows=source.ToArray();if(rows.Length==0)return ()=>{};
            if(!player)throw new System.IO.InvalidDataException("Resource view is unavailable");
            var values=new Dictionary<string,float>();
            foreach(var row in rows)
            {
                if(row.Table!="state" || row.Delete)throw new System.IO.InvalidDataException("Invalid resource confirmation");
                string key=(string)row.Values[0];double value=Convert.ToDouble(row.Values[2]);
                PlayerResources.Row(key,value);if(value>float.MaxValue)throw new System.IO.InvalidDataException("Resource value overflow");values.Add(key,(float)value);
            }
            return ()=>
            {
                if(values.TryGetValue("max_health",out var hpMax))player.SetMaxHealth(hpMax,true);
                if(values.TryGetValue("max_stamina",out var staminaMax))player.SetMaxStamina(staminaMax,true);
                if(values.TryGetValue("max_eitr",out var eitrMax))player.SetMaxEitr(eitrMax,true);
                if(values.TryGetValue("health",out var health))player.SetHealth(health);
                if(values.TryGetValue("stamina",out var stamina))player.m_stamina=stamina;
                if(values.TryGetValue("eitr",out var eitr))player.m_eitr=eitr;
                if(values.TryGetValue(PlayerResources.StaminaDelay,out var staminaDelay))player.m_staminaRegenTimer=staminaDelay;
                if(values.TryGetValue(PlayerResources.EitrDelay,out var eitrDelay))player.m_eitrRegenTimer=eitrDelay;
            };
        }
    }
    internal static class PlayerDiscoveryGame
    {
        // These facts are derived from the confirmed inventory plan, never supplied by the client.
        // Keeping them in the item transaction preserves discovery even if the item is immediately consumed.
        internal static PlayerBatch Acquired(PlayerBatch batch)
        {
            var rows=batch.Changes.ToList();
            var known=new HashSet<string>(rows.Where(r=>r.Table=="knowledge").Select(r=>(string)r.Values[0]+"\n"+(string)r.Values[1]));
            void Learn(string category,string key){if(known.Add(category+"\n"+key))rows.Add(new PlayerChange("knowledge",false,category,key,""));}
            foreach(var row in batch.Changes.Where(r=>r.Table=="inventory" && !r.Delete))
            {
                var prefab=ObjectDB.instance.GetItemPrefab(Convert.ToInt32(row.Values[3]));var drop=prefab?prefab.GetComponent<ItemDrop>():null;
                if(!drop)throw new InvalidOperationException("Acquired item definition is unavailable");
                Learn("materials",drop.m_itemData.m_shared.m_name);
                if(drop.m_itemData.m_shared.m_itemType==ItemDrop.ItemData.ItemType.Trophy)Learn("trophies",prefab.name);
            }
            return new PlayerBatch(batch.Operation,batch.ExpectedRevision,rows);
        }
        internal static IEnumerable<PlayerChange> Refresh(PlayerSnapshot snapshot,IEnumerable<PlayerChange> stations=null)
        {
            var rows=snapshot.Rows.ToList();var result=new List<PlayerChange>();
            foreach(var station in stations??Array.Empty<PlayerChange>())
            {
                var before=rows.FirstOrDefault(r=>r.Table=="knowledge" && (string)r.Values[0]=="stations" && (string)r.Values[1]==(string)station.Values[1]);
                if(before!=null && Convert.ToInt32(before.Values[2],CultureInfo.InvariantCulture)>=Convert.ToInt32(station.Values[2],CultureInfo.InvariantCulture))continue;
                if(before!=null)rows.Remove(before);rows.Add(station);result.Add(station);
            }
            var materials=new HashSet<string>(rows.Where(r=>r.Table=="knowledge" && (string)r.Values[0]=="materials").Select(r=>(string)r.Values[1]));
            var recipes=new HashSet<string>(rows.Where(r=>r.Table=="knowledge" && (string)r.Values[0]=="recipes").Select(r=>(string)r.Values[1]));
            var knownStations=rows.Where(r=>r.Table=="knowledge" && (string)r.Values[0]=="stations").ToDictionary(r=>(string)r.Values[1],r=>Convert.ToInt32(r.Values[2],CultureInfo.InvariantCulture));
            var tables=new HashSet<PieceTable>();
            foreach(var row in rows.Where(r=>r.Table=="inventory"))
            {
                var prefab=ObjectDB.instance.GetItemPrefab(Convert.ToInt32(row.Values[3]));var item=prefab?prefab.GetComponent<ItemDrop>():null;if(!item)continue;
                if(materials.Add(item.m_itemData.m_shared.m_name))result.Add(new PlayerChange("knowledge",false,"materials",item.m_itemData.m_shared.m_name,""));
                if(item.m_itemData.m_shared.m_buildPieces)tables.Add(item.m_itemData.m_shared.m_buildPieces);
            }
            bool Station(CraftingStation station,int level)=>!station || knownStations.TryGetValue(station.m_name,out var available) && available>=level;
            bool Dlc(string dlc)=>string.IsNullOrEmpty(dlc) || DLCMan.instance && DLCMan.instance.IsDLCInstalled(dlc);
            var player=Game.instance?Game.instance.m_playerPrefab?.GetComponent<Player>():null;
            var season=player?player.m_seasonalItemGroups.FirstOrDefault(s=>s.IsInSeason()):null;
            foreach(var recipe in ObjectDB.instance.m_recipes)
            {
                if(!recipe || !recipe.m_item || !(recipe.m_enabled || season!=null && season.Recipes.Contains(recipe)) || recipes.Contains(recipe.m_item.m_itemData.m_shared.m_name) ||
                    !Station(recipe.m_craftingStation,recipe.m_minStationLevel) || !Dlc(recipe.m_item.m_itemData.m_shared.m_dlc))continue;
                var requirements=recipe.m_resources.Where(r=>r.m_resItem && !r.m_upgraderResource && r.m_amount>0);
                if(!(recipe.m_requireOnlyOneIngredient?requirements.Any(r=>materials.Contains(r.m_resItem.m_itemData.m_shared.m_name)):requirements.All(r=>materials.Contains(r.m_resItem.m_itemData.m_shared.m_name))))continue;
                if(recipes.Add(recipe.m_item.m_itemData.m_shared.m_name))result.Add(new PlayerChange("knowledge",false,"recipes",recipe.m_item.m_itemData.m_shared.m_name,""));
            }
            foreach(var table in tables)foreach(var prefab in table.m_pieces)
            {
                var piece=prefab?prefab.GetComponent<Piece>():null;
                if(!piece || !(piece.m_enabled || season!=null && season.Pieces.Contains(prefab)) || recipes.Contains(piece.m_name) || !Station(piece.m_craftingStation,1) || !Dlc(piece.m_dlc) ||
                    piece.m_resources.Any(r=>r.m_resItem && r.m_amount>0 && !materials.Contains(r.m_resItem.m_itemData.m_shared.m_name)))continue;
                recipes.Add(piece.m_name);result.Add(new PlayerChange("knowledge",false,"recipes",piece.m_name,""));
            }
            return result;
        }
        internal static PlayerChange[] Nearby(ZDO actor,Dictionary<string,int> observed)
        {
            if(actor==null || actor.GetBool(ZDOVars.s_dead,false))return Array.Empty<PlayerChange>();
            var rows=new List<PlayerChange>();
            foreach(var station in CraftingStation.m_allStations)
            {
                if(!station || Vector3.Distance(station.transform.position,actor.GetPosition())>=station.m_discoverRange)continue;
                int level=station.GetLevel(true);
                if(observed.TryGetValue(station.m_name,out var old) && old>=level)continue;
                observed[station.m_name]=level;rows.Add(new PlayerChange("knowledge",false,"stations",station.m_name,level.ToString(CultureInfo.InvariantCulture)));
            }
            return rows.ToArray();
        }
    }
    internal static class PlayerProgressGame
    {
        private static Character actor;
        [HarmonyPatch(typeof(FishingFloat),"FixedUpdate")]
        private static class FishingSimulation
        {
            private static void Prefix(FishingFloat __instance,out Character __state)
            {
                __state=actor;
                if(PlayerFishingCastGame.Enabled && PlayerFishingCastGame.Managed(__instance) && ZNet.instance.IsServer() && __instance.m_nview.IsOwner())actor=__instance.GetOwner();
            }
            private static Exception Finalizer(Character __state,Exception __exception){actor=__state;return __exception;}
        }
        [HarmonyPatch(typeof(Player),nameof(Player.RaiseSkill))]
        private static class Skill
        {
            [HarmonyPriority(Priority.First+300)]
            private static bool Prefix(Player __instance,Skills.SkillType skill,float value)
            {
                if(!actor || actor!=__instance)return true;
                if(skill!=Skills.SkillType.None && value>0 && !float.IsNaN(value) && !float.IsInfinity(value))
                    InventoryMoveGame.Progress(actor.GetZDOID(),state=>PlayerCraftProgressGame.Raise(state,skill,value));
                return false;
            }
        }
        [HarmonyPatch(typeof(Game),nameof(Game.IncrementPlayerStat))]
        private static class Statistics
        {
            [HarmonyPriority(Priority.First+300)]
            private static bool Prefix(PlayerStatType stat,float amount)
            {
                if(!actor)return true;
                if(stat!=PlayerStatType.None && !float.IsNaN(amount) && !float.IsInfinity(amount))
                    InventoryMoveGame.Progress(actor.GetZDOID(),state=>new[]{PlayerCraftProgressGame.Increment(state,"statistics:0:values",((int)stat).ToString(CultureInfo.InvariantCulture),amount)});
                return false;
            }
        }
    }
}
