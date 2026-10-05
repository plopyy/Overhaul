using System;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Storage
{
    // Use the existing staggered ten-second ItemDrop update, not a world scan.
    internal static class GroundItemExpiry
    {
        internal static readonly int StartedKey = "overhaul_ground_item_utc_v1".GetStableHashCode();
        internal const double LifetimeSeconds = 30 * 60;

        [HarmonyPatch(typeof(ItemDrop), "TimedDestruction")]
        private static class ReplaceNativeExpiry
        {
            private static bool Prefix() => false;
        }

        internal static bool Expired(ZDO data, long now)
        {
            if (data.GetBool(ProductionDrops.ProtectedKey, false)) return false;
            long started = data.GetLong(StartedKey, 0);
            if (started <= 0 || started > now)
            {
                data.Set(StartedKey, now);
                return false;
            }
            return (now - started) / (double)TimeSpan.TicksPerSecond >= LifetimeSeconds;
        }

        [HarmonyPatch(typeof(ItemDrop), "SlowUpdate")]
        internal static class Update
        {
            private static bool Prefix(ItemDrop __instance)
            {
                var view = __instance.m_nview;
                if (!view || !view.IsValid() || !view.IsOwner() || __instance.IsPiece()) return true;
                if (!Expired(view.GetZDO(), DateTime.UtcNow.Ticks)) return true;
                view.Destroy();
                return false;
            }
        }

        [HarmonyPatch(typeof(ItemDrop), "Awake")]
        internal static class Created
        {
            private static void Postfix(ItemDrop __instance)
            {
                var view = __instance.m_nview;
                if (view && view.IsValid() && view.IsOwner() && !__instance.IsPiece())
                {
                    // Existing drops get a full grace period on first adoption.
                    if (view.GetZDO().GetLong(StartedKey, 0) <= 0)
                        view.GetZDO().Set(StartedKey, DateTime.UtcNow.Ticks);
                }
            }
        }
    }
}
