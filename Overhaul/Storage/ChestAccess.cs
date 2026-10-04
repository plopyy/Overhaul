using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Storage
{
    internal static class ChestAccess
    {
        internal const float ResourceRange = 30f;
        internal static readonly HashSet<Container> Loaded = new HashSet<Container>();
        internal static readonly int PrivateKey = "overhaul_chest_private".GetStableHashCode();
        internal static readonly int LeaseKey = "overhaul_craft_lease".GetStableHashCode();
        internal static readonly int LeasePeerKey = "overhaul_craft_peer".GetStableHashCode();
        internal static readonly int LeaseUntilKey = "overhaul_craft_until".GetStableHashCode();
        internal const string PrivacyRpc = "Overhaul_ChestPrivacy", LeaseRpc = "Overhaul_ChestLease";
        internal static ZDO Data(Container c) => c && c.m_nview && c.m_nview.IsValid() ? c.m_nview.GetZDO() : null;
        internal static long Creator(Container c) => Data(c)?.GetLong(ZDOVars.s_creator, 0) ?? 0;
        internal static bool Private(Container c) => Data(c)?.GetBool(PrivateKey, false) == true;
        internal static bool Eligible(Container c) => Data(c) != null && Creator(c) != 0 && c.GetComponent<Piece>() && !c.GetComponent<TombStone>();
        internal static bool Allows(Container c, long player) => !Private(c) || (player != 0 && Creator(c) == player);
        internal static long Now => ZNet.instance ? ZNet.instance.GetTime().Ticks : DateTime.UtcNow.Ticks;
        internal static bool Leased(Container c) => Data(c)?.GetLong(LeaseUntilKey, 0) > Now;
        internal static bool OwnLease(Container c, string token) => Data(c) != null && Leased(c) &&
            Data(c).GetString(LeaseKey, "") == token && Data(c).GetLong(LeasePeerKey, 0) == ZNet.GetUID();

        internal static ZDO Actor(long sender, ZDOID character)
        {
            var data = ZDOMan.instance?.GetZDO(character);
            var prefab = data == null || !ZNetScene.instance ? null : ZNetScene.instance.GetPrefab(data.GetPrefab());
            return data != null && data.GetOwner() == sender && data.GetLong(ZDOVars.s_playerID, 0) != 0 &&
                prefab && prefab.GetComponent<Player>() ? data : null;
        }
        internal static bool WardAccess(Container c, long player)
        {
            if (!c.m_checkGuardStone) return true;
            return WardAccessAt(c.transform.position,player);
        }
        internal static bool WardAccessAt(Vector3 position,long player)
        {
            bool inside = false;
            foreach (var area in PrivateArea.m_allAreas)
            {
                if (!area || !area.IsEnabled() || !area.IsInside(position, 0)) continue;
                inside = true;
                if (area.m_piece.GetCreator() == player || area.IsPermitted(player)) return true;
            }
            return !inside;
        }
        internal static void SetPrivacy(Container c, long sender, ZDOID actorId, bool value)
        {
            var actor = Actor(sender, actorId);
            if (!Eligible(c) || !c.m_nview.IsOwner() || actor == null || Leased(c) ||
                Creator(c) == 0 || Creator(c) != actor.GetLong(ZDOVars.s_playerID, 0) ||
                Vector3.Distance(actor.GetPosition(), c.transform.position) > 5f) return;
            Data(c).Set(PrivateKey, value);
        }
        internal static void RequestLease(Container c, long sender, ZDOID actorId, ZDOID stationId, string token)
        {
            if (!Eligible(c) || !c.m_nview.IsOwner() || MoveReservation.Busy(c) || string.IsNullOrEmpty(token) || token.Length > 64) return;
            var actor = Actor(sender, actorId); var station = ZDOMan.instance.GetZDO(stationId);
            var stationPrefab = station == null ? null : ZNetScene.instance.GetPrefab(station.GetPrefab());
            var bench = stationPrefab ? stationPrefab.GetComponent<CraftingStation>() : null;
            if (actor == null) return;
            bool building = stationId == ZDOID.None;
            if ((!building && (!bench || Vector3.Distance(actor.GetPosition(), station.GetPosition()) > bench.m_useDistance + 2f)) ||
                Vector3.Distance(building ? actor.GetPosition() : station.GetPosition(), c.transform.position) > ChestAccess.ResourceRange || c.m_inUse || Data(c).GetInt(ZDOVars.s_inUse, 0) != 0 ||
                (Leased(c) && (Data(c).GetString(LeaseKey, "") != token || Data(c).GetLong(LeasePeerKey, 0) != sender))) return;
            long player = actor.GetLong(ZDOVars.s_playerID, 0);
            if (!Allows(c, player) || !c.CheckAccess(player) || !WardAccess(c, player)) return;
            c.Load(); c.Save();
            Data(c).Set(LeaseKey, token); Data(c).Set(LeasePeerKey, sender);
            Data(c).Set(LeaseUntilKey, Now + TimeSpan.FromSeconds(15).Ticks);
            Data(c).SetOwner(sender);
            if (sender != ZNet.GetUID()) ZDOMan.instance.ForceSendZDO(sender, Data(c).m_uid);
        }
        internal static void Release(Container c, string token)
        {
            if (Data(c) == null || !c.m_nview.IsOwner() || Data(c).GetString(LeaseKey, "") != token) return;
            c.Save(); Data(c).Set(LeaseUntilKey, 0L); Data(c).Set(LeaseKey, ""); Data(c).Set(LeasePeerKey, 0L);
        }
    }
    [HarmonyPatch(typeof(Container), "Awake")]
    internal static class ChestRegisterPatch
    {
        private static void Postfix(Container __instance)
        {
            if (ChestAccess.Data(__instance) == null) return;
            ChestAccess.Loaded.Add(__instance);
            if (!__instance.GetComponent<ChestRegistration>())
                __instance.gameObject.AddComponent<ChestRegistration>().Container = __instance;
            var c = __instance;
            c.m_nview.Register<ZDOID, bool>(ChestAccess.PrivacyRpc, (sender, actor, value) => ChestAccess.SetPrivacy(c, sender, actor, value));
            c.m_nview.Register<ZDOID, ZDOID, string>(ChestAccess.LeaseRpc, (sender, actor, station, token) => ChestAccess.RequestLease(c, sender, actor, station, token));
        }
    }
    internal sealed class ChestRegistration : MonoBehaviour
    {
        internal Container Container;
        private void OnDestroy()
        {
            if (!ReferenceEquals(Container, null)) ChestAccess.Loaded.Remove(Container);
        }
    }
    [HarmonyPatch(typeof(Container), "CheckAccess")]
    internal static class ChestPermissionPatch
    {
        private static bool Prefix(Container __instance, long playerID, ref bool __result)
        {
            if (!ChestAccess.Eligible(__instance)) return true;
            // The per-chest setting replaces the prefab's old public/private default.
            __result = ChestAccess.Allows(__instance, playerID); return false;
        }
    }
    [HarmonyPatch]
    internal static class ChestRequestPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() => new[] { "RPC_RequestOpen", "RPC_RequestStack", "RPC_RequestTakeAll" }.Select(n => AccessTools.Method(typeof(Container), n));
        private static bool Prefix(Container __instance, long uid, long playerID, System.Reflection.MethodBase __originalMethod)
        {
            if (!__instance.m_nview.IsOwner()) return true;
            // Match the claimed persistent character ID against a character owned by sender.
            bool allowed = !MoveReservation.Busy(__instance) && !ChestAccess.Leased(__instance) && (!ChestAccess.Private(__instance) ||
                (ChestAccess.Allows(__instance, playerID) && ZDOMan.instance.m_objectsByID.Values.Any(z =>
                    z.GetLong(ZDOVars.s_playerID, 0) == playerID && ChestAccess.Actor(uid, z.m_uid) != null)));
            if (!allowed) __instance.m_nview.InvokeRPC(uid, __originalMethod.Name.Replace("Request", "") + "Response", false);
            return allowed;
        }
    }
}

