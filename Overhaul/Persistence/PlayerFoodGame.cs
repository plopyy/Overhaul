using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;
using Overhaul.Leveling;

namespace Overhaul.Persistence
{
    internal static class PlayerFoodGame
    {
        internal static float Multiplier(IEnumerable<PlayerChange> rows)
        {
            var row = rows.FirstOrDefault(r => r.Table == "custom_data" && (string)r.Values[0] == NutritionDuration.SaveKey);
            if (row == null) return 1;
            if (!float.TryParse((string)row.Values[1],NumberStyles.Float,CultureInfo.InvariantCulture,out float value) ||
                value < 1 || float.IsInfinity(value)) throw new InvalidDataException("Invalid saved nutrition duration");
            return value;
        }
        private static ItemDrop.ItemData Meal(string prefab,float multiplier)
        {
            var go = ObjectDB.instance.GetItemPrefab(prefab); var drop = go ? go.GetComponent<ItemDrop>() : null;
            if (!drop || drop.m_itemData.m_shared.m_food <= 0) throw new InvalidOperationException("Food definition is unavailable");
            var item = NutritionDuration.CopyMeal(drop.m_itemData,multiplier); item.m_dropPrefab = go; return item;
        }
        internal static PlayerActionPlan Prepare(InventoryMoveRequest request,PlayerSnapshot snapshot,PlayerActionInventory inventory)
        {
            if (request.Action.Amount != 1) throw new InvalidOperationException("Invalid meal quantity");
            int slot = request.Action.FromY * 256 + request.Action.FromX;
            var item = PlayerInventoryView.ReadItem(inventory.Item(slot),null,true);
            if (item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable || item.m_shared.m_food <= 0 ||
                item.m_shared.m_consumeStatusEffect || item.m_worldLevel < Game.m_worldLevel)
                throw new InvalidOperationException("This consumable requires another action handler");
            float multiplier = Multiplier(snapshot.Rows);
            item = Meal(item.m_dropPrefab.name,multiplier);
            var foods = snapshot.Rows.Where(r => r.Table == "food").Select(r => r.Values).Where(v => Convert.ToDouble(v[2]) > 0)
                .ToDictionary(v => Convert.ToInt32(v[0]));
            if (foods.Count > 3 || foods.Keys.Any(k => k < 0 || k >= 3)) throw new InvalidDataException("Invalid active food slots");
            var same = foods.FirstOrDefault(p => Meal((string)p.Value[1],multiplier).m_shared.m_name == item.m_shared.m_name);
            bool depleted(KeyValuePair<int,object[]> p) => Convert.ToDouble(p.Value[2]) < Meal((string)p.Value[1],multiplier).m_shared.m_foodBurnTime / 2f;
            int target;
            if (same.Value != null)
            {
                if (!depleted(same)) throw new InvalidOperationException("This food is still active");
                target = same.Key;
            }
            else if (foods.Count < 3) target = Enumerable.Range(0,3).First(k => !foods.ContainsKey(k));
            else
            {
                var replace = foods.Where(depleted).OrderBy(p => Convert.ToDouble(p.Value[2])).ThenBy(p => p.Key).FirstOrDefault();
                if (replace.Value == null) throw new InvalidOperationException("All food slots are full");
                target = replace.Key;
            }
            if (item.m_shared.m_foodBurnTime <= 0 || float.IsInfinity(item.m_shared.m_foodBurnTime)) throw new InvalidDataException("Invalid meal duration");
            inventory.Remove(slot,1);
            var changes = inventory.Delta(request.Action.Operation,snapshot.Revision).Changes.ToList();
            // Include all three slots so the confirmation also corrects expired local foods.
            for (int i = 0; i < 3; i++)
                changes.Add(i == target ? new PlayerChange("food",false,i,item.m_dropPrefab.name,item.m_shared.m_foodBurnTime) :
                    foods.TryGetValue(i,out var v) ? new PlayerChange("food",false,v) : new PlayerChange("food",true,i));
            changes.Add(PlayerFoodClock.Anchor(PlayerFoodClock.Read(snapshot.Rows)));
            changes.Add(PlayerCraftProgressGame.Increment(snapshot,"statistics:0:values",((int)PlayerStatType.FoodEaten).ToString(CultureInfo.InvariantCulture),1));
            changes.Add(PlayerCraftProgressGame.Increment(snapshot,"statistics:0:food",item.m_shared.m_name,1));
            return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(request.Action.Operation,snapshot.Revision,changes),new Dictionary<long,ObjectRecord>()),() => { });
        }
        internal static PlayerActionPlan FromContainer(ZRpc rpc,InventoryMoveRequest request,PlayerSnapshot snapshot)
        {
            var lease = InventoryMoveGame.Source(rpc,request,out var chest);
            try
            {
                var package = new ZPackage(); chest.GetInventory().Save(package); var before = PlayerNativeFormat.DecodeInventory(package.GetArray());
                var bag = new PlayerActionInventory(before,lease.Layout);
                var planned = Prepare(request,snapshot,bag).Change.Player;
                var delta = bag.Delta(request.Action.Operation,0);
                if (!lease.Reserve(ContainerVersions.Slots(delta))) throw new InvalidOperationException("Food source slot is busy");
                var player = new PlayerBatch(planned.Operation,planned.ExpectedRevision,planned.Changes.Where(r => r.Table != "inventory" && r.Table != "item_data"));
                var action = new PlayerWorldAction(player,new Dictionary<long,ObjectRecord>(),
                    new Dictionary<long,PlayerContainerAction> { [lease.ObjectId] = new PlayerContainerAction(before,delta) });
                return new PlayerActionPlan(action,() => { lease.Publish(action.CommittedContainers[lease.ObjectId]); lease.Dispose(); });
            }
            catch { lease.Dispose(); throw; }
        }
        internal static void Feedback(Player player,PlayerBatch confirmed)
        {
            try
            {
                var stat = confirmed.Changes.FirstOrDefault(r => r.Table == "knowledge" && !r.Delete && (string)r.Values[0] == "statistics:0:food");
                if (stat == null) return;
                var item = confirmed.Changes.Where(r => r.Table == "food" && !r.Delete).Select(r => Meal((string)r.Values[1],1))
                    .FirstOrDefault(i => i.m_shared.m_name == (string)stat.Values[1]);
                if (item == null) return;
                player.m_consumeItemEffects.Create(player.transform.position,Quaternion.identity,null,1f,-1,player.GetZDOID());
                if (player.m_zanim) player.m_zanim.SetTrigger("eat");
                player.SetUseHandVisual(item.m_dropPrefab,item.m_shared.m_foodEatAnimTime);
            }
            catch (Exception error) { ZLog.LogWarning("[Overhaul food visual] " + error.Message); }
        }
        internal static Action Presentation(IEnumerable<PlayerChange> source,Player player)
        {
            var rows = source.ToArray(); if (rows.Length == 0) return () => { };
            if (!player) throw new InvalidDataException("Food view is unavailable");
            var foods = new List<Player.Food>();
            foreach (var row in rows.Where(r => !r.Delete).OrderBy(r => Convert.ToInt32(r.Values[0])))
            {
                var v = row.Values;
                if (Convert.ToInt32(v[0]) < 0 || Convert.ToInt32(v[0]) >= 3 || Convert.ToSingle(v[2]) <= 0) throw new InvalidDataException("Invalid confirmed food");
                var item = Meal((string)v[1],NutritionDuration.Multiplier(player));
                foods.Add(new Player.Food { m_name = (string)v[1],m_item = item,m_time = Convert.ToSingle(v[2]),
                    m_health = item.m_shared.m_food,m_stamina = item.m_shared.m_foodStamina,m_eitr = item.m_shared.m_foodEitr });
            }
            if (rows.Length != 3 || rows.Select(r => Convert.ToInt32(r.Values[0])).Distinct().Count() != 3) throw new InvalidDataException("Incomplete food confirmation");
            return () =>
            {
                player.m_foods.Clear(); player.m_foods.AddRange(foods);
                player.GetTotalFoodValue(out float health,out float stamina,out float eitr);
                player.SetMaxHealth(health,true); player.SetMaxStamina(stamina,true); player.SetMaxEitr(eitr,true);
            };
        }
        [HarmonyPatch(typeof(Player),nameof(Player.ConsumeItem))]
        private static class ConsumeIntent
        {
            [HarmonyPriority(Priority.First + 200)]
            private static bool Prefix(Player __instance,Inventory inventory,ItemDrop.ItemData item,ref bool __result)
            {
                if (__instance != Player.m_localPlayer || !PlayerSessionGame.Managed) return true;
                __result = false;
                inventory = inventory ?? __instance.GetInventory();
                if (item == null || !inventory.ContainsItem(item)) return false;
                var endpoint = InventoryMoveGame.Client; var id = ZDOID.None;
                if (inventory != __instance.GetInventory())
                {
                    var chest = endpoint?.Container;
                    if (!chest || !endpoint.ManagedView(chest) || inventory != chest.GetInventory() || !chest.m_nview || !chest.m_nview.IsValid()) return false;
                    id = chest.m_nview.GetZDO().m_uid;
                }
                endpoint?.Controller.Act(new PlayerActionCommand { Kind = PlayerActionKind.Consume,TargetUser = id.UserID,TargetId = id.ID },item.m_gridPos.x,item.m_gridPos.y,1);
                return false;
            }
        }
    }
    // At most one timer write in flight per connected player. Disconnection flushes the
    // fractional remainder; no timer runs between sessions and no native profile is captured.
    internal sealed class PlayerFoodClockGame
    {
        private readonly PlayerIdentity identity;
        private readonly PlayerDatabaseWriter writer;
        private Task<bool> pending;
        private double elapsed;
        private int frame = -1;
        internal PlayerFoodClockGame(PlayerIdentity identity,PlayerDatabaseWriter writer) { this.identity = identity; this.writer = writer; }
        internal void Tick(bool active)
        {
            if (pending?.IsCompleted == true) { pending.GetAwaiter().GetResult(); pending = null; }
            if (frame != Time.frameCount)
            {
                frame = Time.frameCount;
                if (active) elapsed += Time.deltaTime * Game.m_foodRate;
            }
            if (elapsed >= 1 && pending == null) Flush();
        }
        private void Flush()
        { if (elapsed <= 0) return; pending = writer.AdvanceFood(identity,elapsed); elapsed = 0; }
        internal void Close()
        {
            var earlier = pending; Flush();
            foreach (var task in new[] { earlier,pending }.Where(t => t != null).Distinct())
                task.ContinueWith(t => ZLog.LogError("[Overhaul character food clock] " + t.Exception.GetBaseException()),TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
