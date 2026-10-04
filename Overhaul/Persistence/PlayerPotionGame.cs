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
        internal static void Initial(IEnumerable<PlayerChange> rows) => initial = rows?.Where(r => r.Table == "effects").ToArray();
        // Resource-changing effects need the authoritative health/stamina/eitr simulation.
        // Do not consume those potions before that simulation is connected.
        internal static bool Supported(StatusEffect effect)
        {
            if (!effect || effect.m_ttl <= 0 || float.IsInfinity(effect.m_ttl) || float.IsNaN(effect.m_ttl)) return false;
            if (effect.GetType() == typeof(StatusEffect)) return true;
            if (effect.GetType() != typeof(SE_Stats)) return false;
            var stats = (SE_Stats)effect;
            return stats.m_healthUpFront == 0 && stats.m_healthOverTime == 0 && stats.m_healthPerTick == 0 &&
                stats.m_staminaUpFront == 0 && stats.m_staminaOverTime == 0 && stats.m_staminaDrainPerSec == 0 &&
                stats.m_eitrUpFront == 0 && stats.m_eitrOverTime == 0 && stats.m_adrenalineUpFront == 0;
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
                if (row.Delete) { work.Add(() => player.GetSEMan().RemoveStatusEffect(id,true)); continue; }
                var effect = Definition((string)values[1]); float age = Convert.ToSingle(values[3]), duration = Convert.ToSingle(values[4]);
                if (effect.NameHash() != id || (effect.m_category ?? "") != (string)values[2] || age < 0 || age >= duration || duration != effect.m_ttl)
                    throw new InvalidDataException("Invalid confirmed potion effect");
                work.Add(() =>
                {
                    var manager = player.GetSEMan(); var live = manager.GetStatusEffect(id);
                    if (!live) live = manager.AddStatusEffect(effect,false,0,0,-1);
                    if (!live) throw new InvalidDataException("Confirmed potion effect could not be applied");
                    live.m_time = age;
                });
            }
            return () => { foreach (var apply in work) apply(); };
        }
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
