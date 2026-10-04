using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class PlayerPotionGame
    {
        private static PlayerChange[] initial;
        private static readonly HashSet<int> confirmedEffects=new HashSet<int>();
        private static int suppressResources;
        internal static bool Presenting=>suppressResources>0;
        internal static void Initial(IEnumerable<PlayerChange> rows)
        {initial=rows?.Where(r=>r.Table=="effects").ToArray();if(rows==null)confirmedEffects.Clear();}
        internal static bool Supported(StatusEffect effect)
        {
            if (!effect || effect.m_ttl <= 0 || float.IsInfinity(effect.m_ttl) || float.IsNaN(effect.m_ttl)) return false;
            if (effect.GetType() == typeof(StatusEffect)) return true;
            if (effect.GetType() != typeof(SE_Stats) && effect.GetType()!=typeof(SE_Puke)) return false;
            var stats = (SE_Stats)effect;
            return stats.m_healthPerTick>=0 && stats.m_adrenalineUpFront==0 && (!(stats is SE_Puke puke) || puke.m_removeInterval>0 && !float.IsInfinity(puke.m_removeInterval)) &&
                new[]{stats.m_healthUpFront,stats.m_healthOverTime,stats.m_healthPerTick,stats.m_healthOverTimeInterval,stats.m_healthOverTimeDuration,stats.m_tickInterval,
                    stats.m_staminaUpFront,stats.m_staminaOverTime,stats.m_staminaOverTimeDuration,stats.m_staminaDrainPerSec,stats.m_eitrUpFront,stats.m_eitrOverTime,stats.m_eitrOverTimeDuration,stats.m_healthPerTickMinHealthPercentage}
                    .All(v=>!float.IsNaN(v)&&!float.IsInfinity(v));
        }
        private static StatusEffect Definition(string prefab)
        {
            var go = ObjectDB.instance.GetItemPrefab(prefab); var drop = go ? go.GetComponent<ItemDrop>() : null;
            var effect = drop ? drop.m_itemData.m_shared.m_consumeStatusEffect : null;
            if (!Supported(effect)) throw new InvalidOperationException("Potion effect requires another authority handler");
            return effect;
        }
        internal static IEnumerable<StatusEffect> Active(PlayerSnapshot snapshot) => snapshot.Rows.Where(r => r.Table == "effects")
            .Select(r => r.Values).Where(v => Convert.ToDouble(v[3]) < Convert.ToDouble(v[4])).Select(v => Definition((string)v[1]));
        internal static PlayerActionPlan Prepare(InventoryMoveRequest request,PlayerSnapshot snapshot,PlayerActionInventory inventory,ItemDrop.ItemData item)
        {
            if (request.Action.Amount != 1 || item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable ||
                item.m_shared.m_food > 0 || item.m_worldLevel < Game.m_worldLevel) throw new InvalidOperationException("Invalid potion serving");
            var effect = Definition(item.m_dropPrefab.name); int id = effect.NameHash(); string category = effect.m_category ?? "";
            var active = snapshot.Rows.Where(r => r.Table == "effects").Select(r => r.Values).Where(v => Convert.ToDouble(v[3]) < Convert.ToDouble(v[4]));
            if (active.Any(v => Convert.ToInt32(v[0]) == id || category.Length != 0 && (string)v[2] == category))
                throw new InvalidOperationException("Potion effect or category is still active");
            inventory.Remove(request.Action.FromY * 256 + request.Action.FromX,1);
            var changes = inventory.Delta(request.Action.Operation,snapshot.Revision).Changes.ToList();
            if(effect is SE_Stats stats)changes.AddRange(PlayerResources.Restore(snapshot,Math.Max(0,stats.m_healthUpFront),Math.Max(0,stats.m_staminaUpFront),Math.Max(0,stats.m_eitrUpFront)));
            changes.Add(new PlayerChange("effects",false,id,item.m_dropPrefab.name,category,0d,effect.m_ttl));
            changes.Add(PlayerEffectClock.Anchor(PlayerEffectClock.Read(snapshot.Rows)));
            return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(request.Action.Operation,snapshot.Revision,changes),new Dictionary<long,ObjectRecord>()),() => { });
        }
        internal static Action Presentation(IEnumerable<PlayerChange> source,Player player)
        {
            var work = new List<Action>();
            foreach (var row in source)
            {
                if (!player) throw new InvalidDataException("Potion view is unavailable");
                var values = row.Values; int id = Convert.ToInt32(values[0]);
                if (row.Delete) { work.Add(() => {confirmedEffects.Remove(id);player.GetSEMan().RemoveStatusEffect(id,true);}); continue; }
                var effect = Definition((string)values[1]); float age = Convert.ToSingle(values[3]), duration = Convert.ToSingle(values[4]);
                if (effect.NameHash() != id || (effect.m_category ?? "") != (string)values[2] || age < 0 || age >= duration || duration != effect.m_ttl)
                    throw new InvalidDataException("Invalid confirmed potion effect");
                work.Add(() =>
                {
                    var manager = player.GetSEMan(); var live = manager.GetStatusEffect(id);
                    confirmedEffects.Add(id);
                    if (!live) live = manager.AddStatusEffect(effect,false,0,0,-1);
                    if (!live) throw new InvalidDataException("Confirmed potion effect could not be applied");
                    live.m_time = age;
                });
            }
            return () => { suppressResources++;try{foreach (var apply in work) apply();}finally{suppressResources--;} };
        }
        internal static IEnumerable<PlayerChange> Periodic(PlayerSnapshot before,PlayerSnapshot current,double seconds)
        {
            var result=new List<PlayerChange>();
            double Value(string key)=>result.LastOrDefault(r=>r.Table=="state" && (string)r.Values[0]==key) is PlayerChange row?Convert.ToDouble(row.Values[2]):PlayerResources.Read(current,key);
            void Set(string key,double value){result.RemoveAll(r=>r.Table=="state" && (string)r.Values[0]==key);result.Add(PlayerResources.Row(key,value));}
            void Add(string key,double amount)
            {if(amount==0)return;double value=Value(key);if(key=="health" && value==0)return;Set(key,Math.Max(0,Math.Min(PlayerResources.Read(current,"max_"+key),value+amount)));}
            var foods=current.Rows.Where(r=>r.Table=="food").Select(r=>Convert.ToInt32(r.Values[0])).ToList();
            foreach(var row in before.Rows.Where(r=>r.Table=="effects"))
            {
                var v=row.Values;var stats=Definition((string)v[1]) as SE_Stats;if(!stats)continue;
                double from=Convert.ToDouble(v[3]),to=Math.Min(Convert.ToDouble(v[4]),from+seconds);if(to<=from)continue;
                double Ticks(double interval,double duration)=>interval<=0?0:Math.Max(0,Math.Floor(Math.Min(to,duration)/interval)-Math.Floor(Math.Min(from,duration)/interval));
                double Active(double duration)=>Math.Max(0,Math.Min(to,duration)-Math.Min(from,duration));
                if(stats.m_healthOverTime>0 && stats.m_healthOverTimeInterval>0)
                {
                    double duration=stats.m_healthOverTimeDuration>0?stats.m_healthOverTimeDuration:stats.m_ttl;
                    Add("health",Ticks(stats.m_healthOverTimeInterval,duration)*stats.m_healthOverTime/(duration/stats.m_healthOverTimeInterval));
                }
                if(stats.m_healthPerTick>0 && Value("health")/PlayerResources.Read(current,"max_health")>=stats.m_healthPerTickMinHealthPercentage)
                    Add("health",Ticks(stats.m_tickInterval,stats.m_ttl)*stats.m_healthPerTick);
                double staminaDuration=stats.m_staminaOverTimeDuration>0?stats.m_staminaOverTimeDuration:stats.m_ttl;
                double eitrDuration=stats.m_eitrOverTimeDuration>0?stats.m_eitrOverTimeDuration:stats.m_ttl;
                double staminaAmount=stats.m_staminaOverTime*Active(staminaDuration)/staminaDuration;
                if(staminaAmount!=0 && stats.m_staminaOverTimeIsFraction)staminaAmount*=PlayerResources.Read(current,"max_stamina");
                Add("stamina",staminaAmount);Add("eitr",stats.m_eitrOverTime*Active(eitrDuration)/eitrDuration);
                if(stats.m_staminaDrainPerSec>0)
                {
                    Add("stamina",-stats.m_staminaDrainPerSec*(to-from)*Game.m_staminaRate);
                    var definition=Game.instance.m_playerPrefab.GetComponent<Player>();Set(PlayerResources.StaminaDelay,definition.m_staminaRegenDelay);
                }
                if(stats is SE_Puke puke)
                {
                    int count=(int)Math.Min(foods.Count,Ticks(puke.m_removeInterval,puke.m_ttl));
                    while(count-->0){int index=UnityEngine.Random.Range(0,foods.Count);result.Add(new PlayerChange("food",true,foods[index]));foods.RemoveAt(index);}
                }
            }
            return result;
        }
        [HarmonyPatch]
        private static class ResourceScope
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {yield return AccessTools.Method(typeof(SE_Stats),nameof(SE_Stats.UpdateStatusEffect));yield return AccessTools.Method(typeof(SE_Puke),nameof(SE_Puke.UpdateStatusEffect));}
            private static void Prefix(SE_Stats __instance,out int __state)
            {__state=suppressResources;if(PlayerSessionGame.Managed && __instance.m_character==Player.m_localPlayer && confirmedEffects.Contains(__instance.NameHash()))suppressResources++;}
            private static Exception Finalizer(int __state,Exception __exception){suppressResources=__state;return __exception;}
        }
        [HarmonyPatch]
        private static class ResourcePrediction
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(Character),nameof(Character.Heal));
                yield return AccessTools.Method(typeof(Player),nameof(Player.AddStamina));
                yield return AccessTools.Method(typeof(Player),nameof(Player.AddEitr));
                yield return AccessTools.Method(typeof(Player),nameof(Player.UseStamina));
            }
            [HarmonyPriority(Priority.First+300)]
            private static bool Prefix()=>suppressResources==0;
        }
        [HarmonyPatch(typeof(Player),nameof(Player.RemoveOneFood))]
        private static class PukePrediction
        {private static bool Prefix(ref bool __result){if(suppressResources==0)return true;__result=false;return false;}}
        internal static ItemDrop.ItemData Consumed(PlayerBatch batch)
        {
            var row = batch.Changes.FirstOrDefault(r => r.Table == "effects" && !r.Delete);
            if (row == null) return null;
            var prefab = ObjectDB.instance.GetItemPrefab((string)row.Values[1]);
            return prefab ? prefab.GetComponent<ItemDrop>().m_itemData : null;
        }
        [HarmonyPatch(typeof(Player),nameof(Player.Load))]
        private static class Restore
        {
            private static void Postfix(Player __instance)
            {
                if (!PlayerSessionGame.Managed || initial == null) return;
                var rows = initial; initial = null;
                Presentation(rows,__instance)();
            }
        }
    }
}
