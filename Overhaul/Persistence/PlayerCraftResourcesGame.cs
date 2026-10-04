using System;
using System.Collections.Generic;
using System.Linq;
using Overhaul.Storage;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal sealed class PlayerCraftResourcesGame : IDisposable
    {
        private sealed class Source
        {
            internal Container Chest;
            internal PlayerChange[] Before;
            internal PlayerActionInventory Bag;
            internal long Id;
            internal int[] Reserved;
        }
        private readonly List<Source> sources = new List<Source>();
        private bool submitted;
        internal bool Cheated { get; private set; }
        internal PlayerCraftResourcesGame(PlayerActionInventory player, CraftingStation station, long playerId)
        {
            sources.Add(new Source { Bag = player });
            if (!station) return;
            foreach (var chest in ChestAccess.Loaded.Where(c => ChestAccess.Eligible(c) && c.gameObject.activeInHierarchy &&
                Vector3.Distance(c.transform.position,station.transform.position) <= ChestAccess.ResourceRange &&
                ChestAccess.Allows(c,playerId) && c.CheckAccess(playerId) && ChestAccess.WardAccess(c,playerId) &&
                !MoveReservation.Busy(c) && !ChestAccess.Leased(c) && !GamePersistence.ActionReserved(ChestAccess.Data(c).m_uid) &&
                (InventoryMoveGame.SharedView(c) || !c.IsInUse() && ChestAccess.Data(c).GetInt(ZDOVars.s_inUse,0) == 0))
                .OrderBy(c => ChestAccess.Data(c).m_uid.ToString(),StringComparer.Ordinal))
            {
                chest.Load();
                var rows = Rows(chest.GetInventory()).ToArray();
                sources.Add(new Source { Chest = chest,Before = rows,Bag = new PlayerActionInventory(rows,
                    InventoryMoveGame.ContainerLayout(chest).Excluding(GamePersistence.ReservedSlots(ChestAccess.Data(chest).m_uid))) });
            }
        }
        private static IEnumerable<PlayerChange> Rows(Inventory inventory)
        {
            foreach (var item in inventory.GetAllItems())
            {
                yield return PlayerActionGame.Row(item,item.m_gridPos.x,item.m_gridPos.y);
                foreach (var pair in item.m_customData)
                    yield return new PlayerChange("item_data",false,"main",item.m_gridPos.x,item.m_gridPos.y,pair.Key,pair.Value);
            }
        }
        private IEnumerable<Tuple<Source,int,object[]>> Matching(ItemDrop resource,int quality)
        {
            string name = resource.m_itemData.m_shared.m_name;
            foreach (var source in sources)
                foreach (int key in source.Bag.Keys)
                {
                    var row = source.Bag.Item(key); var prefab = ObjectDB.instance.GetItemPrefab(Convert.ToInt32(row[3]));
                    var item = prefab ? prefab.GetComponent<ItemDrop>() : null;
                    if (source.Bag.Available(key) && !Convert.ToBoolean(row[7]) && Convert.ToInt32(row[11]) >= Game.m_worldLevel &&
                        item && !item.m_itemData.m_shared.m_questItem && item.m_itemData.m_shared.m_name == name &&
                        (quality < 0 || Convert.ToInt32(row[5]) == quality)) yield return Tuple.Create(source,key,row);
                }
        }
        internal int Count(ItemDrop resource,int quality) => checked((int)Matching(resource,quality).Sum(p => Convert.ToInt64(p.Item3[4])));
        internal void Consume(ItemDrop resource,int amount,int quality = -1)
        {
            if (amount < 0 || Count(resource,quality) < amount) throw new InvalidOperationException("Missing crafting resources");
            foreach (var match in Matching(resource,quality).ToArray())
            {
                if (amount == 0) break;
                int take = Math.Min(amount,Convert.ToInt32(match.Item3[4]));
                Cheated |= Convert.ToBoolean(match.Item3[13]); match.Item1.Bag.Remove(match.Item2,take); amount -= take;
            }
        }
        internal PlayerActionPlan Finish(PlayerBatch player,Action effects)
        {
            var changes = new Dictionary<long,PlayerContainerAction>();
            foreach (var source in sources.Where(s => s.Chest))
            {
                var delta = source.Bag.Delta(player.Operation,0); if (!delta.Changes.Any()) continue;
                var data = ChestAccess.Data(source.Chest);
                data.SetOwner(ZNet.GetUID()); source.Chest.Save();
                source.Id = GamePersistence.SnapshotInventory(data);
                int[] slots = ContainerVersions.Slots(delta);
                if (!GamePersistence.ReserveSlots(data.m_uid,slots)) throw new InvalidOperationException("Crafting slots are busy");
                source.Reserved = slots; changes.Add(source.Id,new PlayerContainerAction(source.Before,delta));
            }
            var action = new PlayerWorldAction(player,new Dictionary<long,ObjectRecord>(),changes);
            var plan = new PlayerActionPlan(action,() =>
            {
                foreach (var source in sources.Where(s => s.Reserved != null))
                {
                    var delta = action.CommittedContainers[source.Id]; var bag = source.Chest.GetInventory();
                    var items = InventoryMovePresentation.Prepare(bag,delta,false);
                    bag.m_inventory.Clear(); bag.m_inventory.AddRange(items); bag.Changed(); source.Chest.Save();
                    InventoryMoveGame.Broadcast(source.Chest,delta,null);
                }
                Release();
                try { effects?.Invoke(); } catch (Exception error) { ZLog.LogError("[Overhaul craft effects] " + error); }
            });
            submitted = true; return plan;
        }
        private void Release()
        {
            foreach (var source in sources.Where(s => s.Reserved != null))
            { GamePersistence.ReleaseSlots(ChestAccess.Data(source.Chest),source.Reserved); source.Reserved = null; }
        }
        public void Dispose() { if (!submitted) Release(); }
    }
}
