using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Storage
{
    internal static class StationStorage
    {
        internal static int Reading;
        internal static bool Executing;
        internal static bool SuppressReading;
        internal static bool Busy => pending != null;
        private static Pending pending;
        internal static List<Container> Transaction;
        private sealed class Pending
        {
            internal Player Player; internal InventoryGui Gui; internal Recipe Recipe;
            internal ItemDrop.ItemData Upgrade; internal CraftingStation Station;
            internal bool Multi; internal int Amount, Variant, Index;
            internal string Token = Guid.NewGuid().ToString("N");
            internal float Started = Time.realtimeSinceStartup, LastRequest = -100;
            internal List<Container> Chests;
        }
        internal static bool HasStation(Player player) => player && player == Player.m_localPlayer && player.GetCurrentCraftingStation();
        internal static bool Context(Inventory inventory) => !SuppressReading && ((Reading > 0 && HasStation(Player.m_localPlayer)) || (BuildStorage.Reading > 0 && BuildStorage.Enabled(Player.m_localPlayer))) && inventory == Player.m_localPlayer.GetInventory();
        internal static List<Container> Nearby(Player player)
        {
            if (!HasStation(player)) return new List<Container>();
            var station = player.GetCurrentCraftingStation(); long id = player.GetPlayerID();
            ChestAccess.Loaded.RemoveWhere(c => !c);
            return ChestAccess.Loaded.Where(c => ChestAccess.Eligible(c) && c.gameObject.activeInHierarchy &&
                Vector3.Distance(c.transform.position, station.transform.position) <= ChestAccess.ResourceRange &&
                ChestAccess.Allows(c, id) && c.CheckAccess(id) && ChestAccess.WardAccess(c, id) &&
                !MoveReservation.Busy(c) && !c.m_inUse && ChestAccess.Data(c).GetInt(ZDOVars.s_inUse, 0) == 0 &&
                (!ChestAccess.Leased(c) || (pending != null && ChestAccess.OwnLease(c, pending.Token))))
                .OrderBy(c => ChestAccess.Data(c).m_uid.ToString(), StringComparer.Ordinal).ToList();
        }
        internal static IEnumerable<Container> Sources() => Transaction ?? (BuildStorage.Reading > 0 ? BuildStorage.Nearby(Player.m_localPlayer) : Nearby(Player.m_localPlayer));
        internal static int ExtraCount(string name, int quality, bool worldLevel)
        {
            int sum = 0;
            foreach (var c in Sources()) { c.Load(); sum = checked(sum + c.GetInventory().CountItems(name, quality, worldLevel)); }
            return sum;
        }
        internal static bool Begin(InventoryGui gui, Player player)
        {
            if (Executing || !HasStation(player) || !gui.m_craftRecipe || player.NoCostCheat() || ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoCraftCost)) return true;
            if (pending != null || BuildStorage.Busy) return false;
            int quality = gui.m_craftUpgradeItem == null ? 1 : gui.m_craftUpgradeItem.m_quality + 1;
            int count = gui.m_multiCrafting ? gui.m_multiCraftAmount : 1;
            // No ownership request if the carried inventory already suffices.
            bool suppress = SuppressReading; SuppressReading = true;
            bool carried;
            try { carried = player.HaveRequirements(gui.m_craftRecipe, false, quality, count); }
            finally { SuppressReading = suppress; }
            if (carried) return true;
            if (!player.HaveRequirements(gui.m_craftRecipe, false, quality, count)) return false;
            var names = new HashSet<string>(gui.m_craftRecipe.m_resources.Where(r => r.m_resItem).Select(r => r.m_resItem.m_itemData.m_shared.m_name));
            var chests = Nearby(player).Where(c => { c.Load(); return c.GetInventory().GetAllItems().Any(i => names.Contains(i.m_shared.m_name)); }).ToList();
            if (chests.Count == 0) return true;
            pending = new Pending { Player = player, Gui = gui, Recipe = gui.m_craftRecipe, Upgrade = gui.m_craftUpgradeItem,
                Station = player.GetCurrentCraftingStation(), Multi = gui.m_multiCrafting, Amount = gui.m_multiCraftAmount,
                Variant = gui.m_craftVariant, Chests = chests };
            return false;
        }
        internal static void Tick()
        {
            if (pending == null) return;
            var p = pending;
            if (!p.Player || !p.Gui || !p.Station || p.Player.IsDead() || !InventoryGui.IsVisible() || p.Player.GetCurrentCraftingStation() != p.Station ||
                Vector3.Distance(p.Player.transform.position, p.Station.transform.position) > p.Station.m_useDistance + 2f ||
                p.Gui.m_craftRecipe != p.Recipe || p.Gui.m_craftUpgradeItem != p.Upgrade ||
                Time.realtimeSinceStartup - p.Started > 8f)
            { Cancel(); return; }
            if (p.Index < p.Chests.Count)
            {
                var c = p.Chests[p.Index];
                if (!ChestAccess.Eligible(c) || !ChestAccess.Allows(c, p.Player.GetPlayerID()) ||
                    Vector3.Distance(c.transform.position, p.Station.transform.position) > ChestAccess.ResourceRange)
                { Cancel(); return; }
                if (c.m_nview.IsOwner() && ChestAccess.OwnLease(c, p.Token))
                { c.Load(); p.Index++; p.LastRequest = -100; }
                else if (Time.realtimeSinceStartup - p.LastRequest >= 1f)
                {
                    p.LastRequest = Time.realtimeSinceStartup;
                    var stationView = p.Station.GetComponent<ZNetView>();
                    if (!stationView || !stationView.IsValid()) { Cancel(); return; }
                    if (c.m_nview.IsOwner()) ChestAccess.RequestLease(c, ZNet.GetUID(), p.Player.GetZDOID(), stationView.GetZDO().m_uid, p.Token);
                    else c.m_nview.InvokeRPC(ChestAccess.LeaseRpc, p.Player.GetZDOID(), stationView.GetZDO().m_uid, p.Token);
                }
                return;
            }
            if (p.Chests.Any(c => !ChestAccess.Eligible(c) || !c.m_nview.IsOwner() || !ChestAccess.OwnLease(c, p.Token) ||
                Vector3.Distance(c.transform.position, p.Station.transform.position) > ChestAccess.ResourceRange ||
                !ChestAccess.Allows(c, p.Player.GetPlayerID()) || !ChestAccess.WardAccess(c, p.Player.GetPlayerID())))
            { Cancel(); return; }
            Transaction = p.Chests; Executing = true;
            try
            {
                // Native crafting still checks station, recipe, output space, quality and DLC.
                p.Gui.m_multiCrafting = p.Multi; p.Gui.m_multiCraftAmount = p.Amount; p.Gui.m_craftVariant = p.Variant;
                int quality = p.Upgrade == null ? 1 : p.Upgrade.m_quality + 1;
                if (!p.Player.HaveRequirements(p.Recipe, false, quality, p.Multi ? p.Amount : 1)) return;
                p.Gui.DoCrafting(p.Player);
            }
            catch (Exception e) { Utility.Log.LogError("Craft from chests: " + e); }
            finally { Executing = false; Transaction = null; Cancel(); }
        }
        internal static void Cancel()
        {
            if (pending == null) return;
            foreach (var c in pending.Chests) ChestAccess.Release(c, pending.Token);
            pending = null;
        }
        internal static void Remove(Inventory inventory, string name, int amount, int quality, bool worldLevel)
        {
            int carried = inventory.GetAllItems().Where(i => Match(i, name, quality, worldLevel)).Sum(i => i.m_stack);
            int own = Math.Min(amount, carried);
            // The prefix only intercepts the player inventory; chest removals use native code.
            bool executing = Executing; Executing = false;
            try { if (own > 0) inventory.RemoveItem(name, own, quality, worldLevel); }
            finally { Executing = executing; }
            int remaining = amount - own;
            foreach (var c in Transaction)
            {
                int take = Math.Min(remaining, c.GetInventory().CountItems(name, quality, worldLevel));
                if (take > 0) { c.GetInventory().RemoveItem(name, take, quality, worldLevel); remaining -= take; }
                if (remaining == 0) break;
            }
            if (remaining != 0) throw new InvalidOperationException("Locked crafting inventory changed during consumption");
        }
        internal static bool Match(ItemDrop.ItemData item, string name, int quality, bool worldLevel) =>
            (name == null || item.m_shared.m_name == name) && (quality < 0 || item.m_quality == quality) && (!worldLevel || item.m_worldLevel >= Game.m_worldLevel);
    }

    [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
    internal static class StationCraftPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(InventoryGui __instance, Player player, out bool __state)
        {
            __state = StationStorage.SuppressReading;
            bool run = StationStorage.Begin(__instance, player);
            if (run && !StationStorage.Executing) StationStorage.SuppressReading = true;
            return run;
        }
        private static void Finalizer(bool __state) => StationStorage.SuppressReading = __state;
    }
    [HarmonyPatch(typeof(Game), "Update")]
    internal static class StationTickPatch { private static void Postfix() { StationStorage.Tick(); BuildStorage.Tick(); } }
    [HarmonyPatch(typeof(InventoryGui), "UpdateRecipeList")]
    internal static class StationRecipeListPatch
    {
        internal struct ReadState
        {
            internal bool SuppressReading;
            internal List<Container> Transaction;
        }
        private static void Prefix(out ReadState __state)
        {
            __state = new ReadState { SuppressReading = StationStorage.SuppressReading, Transaction = StationStorage.Transaction };
            // DoCrafting rebuilds this list before its consumption scope ends.
            // Availability must include all accessible chests, not just those
            // reserved for the recipe being crafted (or only the carried bag).
            StationStorage.SuppressReading = false;
            StationStorage.Transaction = null;
        }
        private static void Finalizer(ReadState __state)
        {
            StationStorage.SuppressReading = __state.SuppressReading;
            StationStorage.Transaction = __state.Transaction;
        }
    }
    [HarmonyPatch]
    internal static class StationReadPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Player), "HaveRequirementItems");
            yield return AccessTools.Method(typeof(Player), "GetFirstRequiredItem");
            yield return AccessTools.Method(typeof(InventoryGui), "SetupRequirement");
            yield return AccessTools.Method(typeof(AugaUnity.AugaCraftingPanel), "HaveRequirementsHelper");
        }
        private static void Prefix(MethodBase __originalMethod, out int[] __state)
        {
            __state = new[] { StationStorage.Reading, BuildStorage.Reading };
            if (StationStorage.HasStation(Player.m_localPlayer)) StationStorage.Reading++;
            else if (__originalMethod.DeclaringType == typeof(InventoryGui) && BuildStorage.Enabled(Player.m_localPlayer)) BuildStorage.Reading++;
        }
        private static void Finalizer(int[] __state) { StationStorage.Reading = __state[0]; BuildStorage.Reading = __state[1]; }
    }
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.CountItems))]
    internal static class StationCountPatch
    {
        private static void Postfix(Inventory __instance, string name, int quality, bool matchWorldLevel, ref int __result)
        { if (StationStorage.Context(__instance)) __result = checked(__result + StationStorage.ExtraCount(name, quality, matchWorldLevel)); }
    }
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.GetItem), new[] { typeof(string), typeof(int), typeof(bool) })]
    internal static class StationItemPatch
    {
        private static void Postfix(Inventory __instance, string name, int quality, bool isPrefabName, ref ItemDrop.ItemData __result)
        {
            if (__result != null || isPrefabName || !StationStorage.Context(__instance)) return;
            foreach (var c in StationStorage.Sources())
            { __result = c.GetInventory().GetItem(name, quality, false); if (__result != null) return; }
        }
    }
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), new[] { typeof(string), typeof(int), typeof(int), typeof(bool) })]
    internal static class StationConsumePatch
    {
        private static bool Prefix(Inventory __instance, string name, int amount, int itemQuality, bool worldLevelBased)
        {
            if (!StationStorage.Executing || StationStorage.Transaction == null || !Player.m_localPlayer || __instance != Player.m_localPlayer.GetInventory()) return true;
            StationStorage.Remove(__instance, name, amount, itemQuality, worldLevelBased); return false;
        }
    }
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.ItemCheated), new[] { typeof(Piece.Requirement[]), typeof(int), typeof(bool) })]
    internal static class StationCheatedPatch
    {
        private static void Postfix(Inventory __instance, Piece.Requirement[] resources, int quality, bool matchWorldLevel, ref bool __result)
        {
            if (!__result && StationStorage.Executing && StationStorage.Transaction != null && Player.m_localPlayer && __instance == Player.m_localPlayer.GetInventory())
                __result = StationStorage.Transaction.Any(c => c.GetInventory().ItemCheated(resources, quality, matchWorldLevel));
        }
    }
}

