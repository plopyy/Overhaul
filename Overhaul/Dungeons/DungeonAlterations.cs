using System;
using System.Collections.Generic;
using HarmonyLib;
using Overhaul.Utility;
using UnityEngine;

namespace Overhaul.Dungeons
{
    // Dvergr towers start their reset delay when altered, not when visited: a chest's content changed
    // or one of the tower's own pieces hit by a player. The machine owning the object tells the server.
    internal static partial class DungeonRuntime
    {
        private const string AlteredRpc = "Overhaul_LocationAltered";
        private static readonly HashSet<ZDOID> reportedAlterations = new HashSet<ZDOID>();

        private static void ReportAlteration(ZNetView view)
        {
            if (!view || !view.IsValid() || !reportedAlterations.Add(view.GetZDO().m_uid) || ZRoutedRpc.instance == null) return;
            Vector3 position = view.transform.position;
            if (ZNet.instance && ZNet.instance.IsServer()) Altered(position);
            else ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), AlteredRpc, position);
        }

        // On the server: the tower around this point, if any, starts its delay.
        private static void Altered(Vector3 position)
        {
            if (!ZNet.instance || !ZNet.instance.IsServer() || session == null || !ZoneSystem.instance) return;
            Vector2s zone = ZoneSystem.GetZone(position);
            for (int x = -4; x <= 4; x++) for (int y = -4; y <= 4; y++)
            {
                HashSet<ZDOID> candidates;
                if (!visitSites.TryGetValue(new Vector2s(zone.x + x, zone.y + y), out candidates)) continue;
                foreach (ZDOID id in candidates)
                {
                    ZDO proxy = session.GetZDO(id);
                    if (proxy == null || proxy.GetLong(VisitStartedKey, 0) != 0) continue;
                    var location = ZoneSystem.instance.GetLocation(proxy.GetInt(ZDOVars.s_location, 0));
                    if (location == null || !DungeonPolicy.Towers.Contains(location.m_prefab.Name)) continue;
                    byte[] journal = proxy.GetByteArray(JournalKey, null);
                    if (journal != null && journal.Length != 0) continue;
                    try
                    {
                        List<Bounds> volumes = GetVisitBounds(proxy);
                        if (volumes != null && StartFirstVisit(proxy, position, volumes, DateTime.UtcNow.Ticks))
                            Log.LogInfo("Dungeon " + location.m_prefab.Name + " " + id + " : tour alteree, delai de reset demarre");
                    }
                    catch (Exception e) { Log.LogInfo("Dungeon " + location.m_prefab.Name + " " + id + " : alteration non prise en compte (" + e.Message + ")"); }
                }
            }
        }

        [HarmonyPatch(typeof(ZRoutedRpc), MethodType.Constructor, typeof(bool))]
        private static class RegisterAlteration
        {
            private static void Postfix(ZRoutedRpc __instance) =>
                __instance.Register<Vector3>(AlteredRpc, (sender, position) => Altered(position));
        }

        [HarmonyPatch(typeof(ZNet), "Awake")]
        private static class ClearAlterations { private static void Postfix() => reportedAlterations.Clear(); }

        // A chest's content changed after its default items were added (on its owner).
        [HarmonyPatch(typeof(Container), nameof(Container.OnContainerChanged))]
        private static class ChestChanged
        {
            private static void Prefix(Container __instance)
            {
                var view = __instance.m_nview;
                if (__instance.m_loading || !view || !view.IsValid() || !view.IsOwner() || !view.GetZDO().GetBool(ZDOVars.s_addedDefaultItems)) return;
                if (__instance.m_piece && __instance.m_piece.GetCreator() != 0) return;
                ReportAlteration(view);
            }
        }

        // One of the location's own pieces (no builder) damaged by a player, on its owner.
        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.RPC_Damage))]
        private static class PieceHit
        {
            private static void Prefix(WearNTear __instance, HitData hit)
            {
                var view = __instance.m_nview;
                if (hit == null || !view || !view.IsValid() || !view.IsOwner() || hit.GetTotalDamage() <= 0 || !(hit.GetAttacker() is Player)) return;
                if (__instance.m_piece && __instance.m_piece.GetCreator() != 0) return;
                ReportAlteration(view);
            }
        }
    }
}
