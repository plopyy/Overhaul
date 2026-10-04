using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
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
