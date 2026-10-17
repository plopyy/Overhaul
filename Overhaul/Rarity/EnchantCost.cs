using System;
using System.Collections.Generic;
using System.Linq;
using Overhaul.Storage;
using UnityEngine;

namespace Overhaul.Rarity
{
    // Line change cost (RerollCost of RaritySystem.cfg), paid like a craft at a workbench: what the player carries counts
    // with the chests around the enchanting table (same rules as crafting: range, access, wards, chests in use or reserved
    // left out). The player's own items are taken first; when chests are needed they are leased through the crafting
    // lease, with the table as the station, and released once the items are taken.
    internal static class EnchantCost
    {
        private sealed class Pending
        {
            internal Player Player; internal CraftingStation Station;
            internal List<Container> Chests; internal Action<bool> Done;
            internal string Token = Guid.NewGuid().ToString("N");
            internal float Started = Time.realtimeSinceStartup, LastRequest = -100;
            internal int Index;
        }
        private static Pending pending;
        internal static bool Busy => pending != null;

        internal static (string Name, int Amount, Sprite Icon)[] Items()
        {
            var list = new List<(string, int, Sprite)>();
            foreach (var (prefab, amount) in RarityConfig.Current.RerollCost)
            {
                var drop = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(prefab)?.GetComponent<ItemDrop>() : null;
                if (drop) list.Add((drop.m_itemData.m_shared.m_name, amount, drop.m_itemData.GetIcon()));
            }
            return list.ToArray();
        }

        private static IEnumerable<Container> Nearby(Player player, CraftingStation station)
        {
            long id = player.GetPlayerID();
            ChestAccess.Loaded.RemoveWhere(c => !c);
            return ChestAccess.Loaded.Where(c => ChestAccess.Eligible(c) && c.gameObject.activeInHierarchy &&
                Vector3.Distance(c.transform.position, station.transform.position) <= ChestAccess.ResourceRange &&
                ChestAccess.Allows(c, id) && c.CheckAccess(id) && ChestAccess.WardAccess(c, id) &&
                !MoveReservation.Busy(c) && !c.m_inUse && ChestAccess.Data(c).GetInt(ZDOVars.s_inUse, 0) == 0 &&
                (!ChestAccess.Leased(c) || (pending != null && ChestAccess.OwnLease(c, pending.Token))))
                .OrderBy(c => ChestAccess.Data(c).m_uid.ToString(), StringComparer.Ordinal);
        }

        internal static int Count(Player player, CraftingStation station, string name)
        {
            if (!player) return 0;
            int count = player.GetInventory().CountItems(name);
            if (station) foreach (var c in Nearby(player, station)) count += c.GetInventory().CountItems(name);
            return count;
        }

        internal static bool Affordable(Player player, CraftingStation station) =>
            player && (player.NoCostCheat() || Items().All(c => Count(player, station, c.Name) >= c.Amount));

        // Takes the cost, then calls done(true); done(false) when it could not be taken.
        internal static void Pay(Player player, CraftingStation station, Action<bool> done)
        {
            if (pending != null || !Affordable(player, station)) { done(false); return; }
            if (player.NoCostCheat()) { done(true); return; }
            var cost = Items(); var inventory = player.GetInventory();
            if (cost.All(c => inventory.CountItems(c.Name) >= c.Amount))
            {
                foreach (var c in cost) inventory.RemoveItem(c.Name, c.Amount);
                done(true); return;
            }
            var names = new HashSet<string>(cost.Select(c => c.Name));
            var chests = Nearby(player, station).Where(c => c.GetInventory().GetAllItems().Any(i => names.Contains(i.m_shared.m_name))).ToList();
            pending = new Pending { Player = player, Station = station, Chests = chests, Done = done };
        }

        // Called every frame while the window is open: leases the chests one by one, then takes the cost.
        internal static void Tick()
        {
            var p = pending; if (p == null) return;
            if (!p.Player || p.Player.IsDead() || !p.Station || Time.realtimeSinceStartup - p.Started > 8f) { Finish(false); return; }
            if (p.Index < p.Chests.Count)
            {
                var c = p.Chests[p.Index];
                if (!c || !ChestAccess.Eligible(c)) { Finish(false); return; }
                if (c.m_nview.IsOwner() && ChestAccess.OwnLease(c, p.Token)) { c.Load(); p.Index++; p.LastRequest = -100; }
                else if (Time.realtimeSinceStartup - p.LastRequest >= 1f)
                {
                    p.LastRequest = Time.realtimeSinceStartup;
                    var stationId = p.Station.GetComponent<ZNetView>().GetZDO().m_uid;
                    if (c.m_nview.IsOwner()) ChestAccess.RequestLease(c, ZNet.GetUID(), p.Player.GetZDOID(), stationId, p.Token);
                    else c.m_nview.InvokeRPC(ChestAccess.LeaseRpc, p.Player.GetZDOID(), stationId, p.Token);
                }
                return;
            }
            var inventories = new List<Inventory> { p.Player.GetInventory() };
            inventories.AddRange(p.Chests.Select(c => c.GetInventory()));
            var cost = Items();
            if (cost.Any(c => inventories.Sum(i => i.CountItems(c.Name)) < c.Amount)) { Finish(false); return; }
            foreach (var (name, amount, _) in cost)
            {
                int left = amount;
                foreach (var inventory in inventories)
                {
                    if (left <= 0) break;
                    int take = Math.Min(left, inventory.CountItems(name));
                    if (take > 0) { inventory.RemoveItem(name, take); left -= take; }
                }
            }
            Finish(true);
        }

        private static void Finish(bool paid)
        {
            var p = pending; pending = null;
            if (p == null) return;
            foreach (var c in p.Chests) if (c) ChestAccess.Release(c, p.Token);
            p.Done(paid);
        }
    }
}
