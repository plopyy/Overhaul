using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Overhaul.Dungeons
{
    internal static class BossInteriorReservation
    {
        internal static readonly int HeightKey = "overhaul_interior_height_v1".GetStableHashCode();
        internal static readonly int HistoryKey = "overhaul_interior_history_v1".GetStableHashCode();
        internal const int Lanes = 256;

        internal static Bounds Bounds(ZDO proxy)
        {
            var bounds = BossDungeonLayout.BoundsFor(proxy.GetPosition());
            var center = bounds.center;
            center.y = proxy.GetFloat(HeightKey, center.y);
            bounds.center = center;
            return bounds;
        }

        internal static List<Bounds> History(ZDO proxy)
        {
            var result = new List<Bounds>();
            var bytes = proxy.GetByteArray(HistoryKey, null);
            if (bytes == null || bytes.Length == 0) return result;
            var package = new ZPackage(bytes);
            int count = package.ReadInt();
            if (count < 0 || count > 4096) throw new InvalidOperationException("Invalid interior history");
            for (int i = 0; i < count; i++) result.Add(new Bounds(package.ReadVector3(), package.ReadVector3()));
            return result;
        }

        internal static void Remember(ZDO proxy, IEnumerable<Bounds> volumes)
        {
            var all = History(proxy);
            // Retain old positions for characters whose private logout save is still offline.
            foreach (var group in volumes.Where(b => b.center.y > 3000).GroupBy(b => Mathf.RoundToInt(b.center.y / 128f)))
            {
                var bounds = group.First();
                foreach (var part in group.Skip(1)) bounds.Encapsulate(part);
                if (!all.Any(b => b.Contains(bounds.min) && b.Contains(bounds.max))) all.Add(bounds);
            }
            var package = new ZPackage(); package.Write(all.Count);
            foreach (var bounds in all) { package.Write(bounds.center); package.Write(bounds.size); }
            proxy.Set(HistoryKey, package.GetArray());
        }

        internal static float ChooseHeight(ZDO proxy)
        {
            var preferred = Bounds(proxy);
            string owner = proxy.GetString(DungeonRuntime.IdentityKey, "");
            var obstacles = new List<Bounds>();
            foreach (var other in ZDOMan.instance.m_objectsByID.Values)
            {
                if (other == proxy) continue;
                if (other.GetInt(BossDungeonLayout.LayoutKey, 0) == 1 && other.GetInt(ZDOVars.s_location, 0) != 0)
                    obstacles.Add(Bounds(other));
                obstacles.AddRange(History(other));
                if (other.GetPosition().y < 11000 || (owner.Length != 0 && other.GetString(DungeonRuntime.OwnerKey, "") == owner)) continue;
                // Include pre-modification interiors and world objects, not only marked proxies.
                obstacles.Add(new Bounds(other.GetPosition(), Vector3.one * 24f));
            }
            int first = Mathf.RoundToInt((preferred.center.y - 12000f) / 128f);
            for (int attempt = 0; attempt < Lanes; attempt++)
            {
                var candidate = preferred;
                var center = candidate.center; center.y = 12000f + ((first + attempt) % Lanes) * 128f;
                candidate.center = center;
                if (!obstacles.Any(b => b.Intersects(candidate))) return center.y;
            }
            throw new InvalidOperationException("Aucun emplacement interieur libre dans les 256 bandes reservees");
        }
    }
}
