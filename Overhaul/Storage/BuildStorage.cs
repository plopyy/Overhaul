using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Storage
{
    internal static class BuildStorage
    {
        internal static int Reading;
        internal static bool Busy => pending != null;
        private static Pending pending;
        private sealed class Pending
        {
            internal Player Player;
            internal Piece Piece;
            internal ItemDrop.ItemData Tool;
            internal Vector3 Position, Origin;
            internal Quaternion Rotation;
            internal List<Container> Chests;
            internal string Token = Guid.NewGuid().ToString("N");
            internal float Started = Time.realtimeSinceStartup, LastRequest = -100;
            internal int Index;
            internal bool Ready;
        }
        internal static bool Enabled(Player p) => p && p == Player.m_localPlayer && p.InPlaceMode() &&
            p.GetBuildTool() && p.GetBuildTool().m_skill == Skills.SkillType.Crafting;
        internal static List<Container> Nearby(Player p)
        {
            if (!Enabled(p)) return new List<Container>();
            long id = p.GetPlayerID();
            ChestAccess.Loaded.RemoveWhere(c => !c);
            return ChestAccess.Loaded.Where(c => ChestAccess.Eligible(c) && c.gameObject.activeInHierarchy &&
                Vector3.Distance(c.transform.position, p.transform.position) <= ChestAccess.ResourceRange &&
                ChestAccess.Allows(c, id) && c.CheckAccess(id) && ChestAccess.WardAccess(c, id) &&
                !MoveReservation.Busy(c) && !c.m_inUse && ChestAccess.Data(c).GetInt(ZDOVars.s_inUse, 0) == 0 &&
                (!ChestAccess.Leased(c) || (pending != null && ChestAccess.OwnLease(c, pending.Token))))
                .OrderBy(c => ChestAccess.Data(c).m_uid.ToString(), StringComparer.Ordinal).ToList();
        }
        internal static bool Begin(Player player, Piece piece)
        {
            if (StationStorage.Executing) return pending != null && Valid(pending);
            if (!Enabled(player) || piece.m_repairPiece || piece.m_removePiece ||
                player.NoCostCheat() || ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey())) return true;
            if (Busy || StationStorage.Busy) return false;
            bool old = StationStorage.SuppressReading; StationStorage.SuppressReading = true;
            bool carried;
            try { carried = player.HaveRequirements(piece, Player.RequirementMode.CanBuild); }
            finally { StationStorage.SuppressReading = old; }
            if (carried) return true;
            if (!player.m_placementGhost || !player.HaveRequirements(piece, Player.RequirementMode.CanBuild)) return false;
            var names = new HashSet<string>(piece.m_resources.Where(r => r.m_resItem).Select(r => r.m_resItem.m_itemData.m_shared.m_name));
            var chests = Nearby(player).Where(c => { c.Load(); return c.GetInventory().GetAllItems().Any(i => names.Contains(i.m_shared.m_name)); }).ToList();
            if (chests.Count == 0) return false;
            pending = new Pending { Player = player, Piece = piece, Tool = player.GetRightItem(),
                Position = player.m_placementGhost.transform.position, Rotation = player.m_placementGhost.transform.rotation,
                Origin = player.transform.position, Chests = chests };
            return false;
        }
        private static bool Valid(Pending p) => Enabled(p.Player) && !p.Player.IsDead() &&
            p.Player.GetSelectedPiece() == p.Piece && p.Player.GetRightItem() == p.Tool && p.Player.m_placementGhost &&
            Vector3.Distance(p.Player.transform.position, p.Origin) < .5f &&
            Vector3.Distance(p.Player.m_placementGhost.transform.position, p.Position) < .1f &&
            Quaternion.Angle(p.Player.m_placementGhost.transform.rotation, p.Rotation) < 1f &&
            Time.realtimeSinceStartup - p.Started < 8f;
        private static bool Allowed(Container c, Pending p) => ChestAccess.Eligible(c) && c.gameObject.activeInHierarchy &&
            Vector3.Distance(c.transform.position, p.Player.transform.position) <= ChestAccess.ResourceRange &&
            ChestAccess.Allows(c, p.Player.GetPlayerID()) && c.CheckAccess(p.Player.GetPlayerID()) &&
            ChestAccess.WardAccess(c, p.Player.GetPlayerID()) && !MoveReservation.Busy(c) && !c.m_inUse && ChestAccess.Data(c).GetInt(ZDOVars.s_inUse, 0) == 0;
        internal static void Tick()
        {
            var p = pending; if (p == null) return;
            if (!Valid(p)) { Cancel(); return; }
            if (p.Index >= p.Chests.Count) { p.Ready = true; return; }
            var c = p.Chests[p.Index];
            if (!Allowed(c, p)) { Cancel(); return; }
            if (c.m_nview.IsOwner() && ChestAccess.OwnLease(c, p.Token)) { c.Load(); p.Index++; p.LastRequest = -100; }
            else if (Time.realtimeSinceStartup - p.LastRequest >= 1f)
            {
                p.LastRequest = Time.realtimeSinceStartup;
                if (c.m_nview.IsOwner()) ChestAccess.RequestLease(c, ZNet.GetUID(), p.Player.GetZDOID(), ZDOID.None, p.Token);
                else c.m_nview.InvokeRPC(ChestAccess.LeaseRpc, p.Player.GetZDOID(), ZDOID.None, p.Token);
            }
        }
        internal static bool Resume(Player player)
        {
            var p = pending;
            if (p == null || !p.Ready || player != p.Player) return false;
            if (Time.time - player.m_lastToolUseTime <= player.m_placeDelay) return false;
            if (!Valid(p) || StationStorage.Busy || p.Chests.Any(c => !Allowed(c, p) || !c.m_nview.IsOwner() || !ChestAccess.OwnLease(c, p.Token)))
            { Cancel(); return false; }
            StationStorage.Transaction = p.Chests;
            StationStorage.Executing = true;
            // Replay only the queued placement input through the original UpdatePlacement flow.
            // Native placement checks, consumption, durability, skills and effects stay together.
            player.m_placePressedTime = Time.time;
            return true;
        }
        internal static void Finish()
        { StationStorage.Executing = false; StationStorage.Transaction = null; Cancel(); }
        internal static void Cancel()
        {
            if (pending == null) return;
            foreach (var c in pending.Chests) ChestAccess.Release(c, pending.Token);
            pending = null;
        }
    }
    [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements), new[] { typeof(Piece), typeof(Player.RequirementMode) })]
    internal static class BuildRequirementsPatch
    {
        private static void Prefix(Player __instance, Player.RequirementMode mode, out int __state)
        { __state = BuildStorage.Reading; if (mode != Player.RequirementMode.IsKnown && BuildStorage.Enabled(__instance)) BuildStorage.Reading++; }
        private static void Finalizer(int __state) => BuildStorage.Reading = __state;
    }
    [HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
    internal static class BuildBeginPatch
    {
        private static bool Prefix(Player __instance, Piece piece, ref bool __result)
        { if (BuildStorage.Begin(__instance, piece)) return true; __result = false; return false; }
    }
    [HarmonyPatch(typeof(Player), "UpdatePlacement")]
    internal static class BuildResumePatch
    {
        private static void Prefix(Player __instance, bool takeInput, out bool __state) => __state = takeInput && !Hud.IsPieceSelectionVisible() && BuildStorage.Resume(__instance);
        private static void Finalizer(bool __state) { if (__state) BuildStorage.Finish(); }
    }
}
namespace Overhaul.Storage
{
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.HaveItem), new[] { typeof(string), typeof(bool) })]
    internal static class BuildHaveItemPatch
    {
        private static void Postfix(Inventory __instance, string name, bool matchWorldLevel, ref bool __result)
        {
            if (!__result && BuildStorage.Reading > 0 && StationStorage.Context(__instance))
                __result = StationStorage.ExtraCount(name, -1, matchWorldLevel) > 0;
        }
    }
}
