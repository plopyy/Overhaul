using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Overhaul.Persistence
{
    // An intent, never item data supplied by the client. Identity comes from the admitted connection.
    internal sealed class PlayerInventoryMove
    {
        internal readonly string Operation;
        internal readonly long Revision;
        internal readonly int FromX, FromY, ToX, ToY, Amount;
        internal PlayerInventoryMove(string operation, long revision, int fromX, int fromY, int toX, int toY, int amount)
        {
            if (!Guid.TryParseExact(operation, "N", out _) || revision < 0 || amount <= 0 || amount > ushort.MaxValue ||
                new[] { fromX, fromY, toX, toY }.Any(v => v < 0 || v >= 256) || fromX == toX && fromY == toY)
                throw new ArgumentException("Invalid inventory move");
            Operation = operation; Revision = revision; FromX = fromX; FromY = fromY; ToX = toX; ToY = toY; Amount = amount;
        }
        internal string Fingerprint => string.Join(":", new long[] { Revision, FromX, FromY, ToX, ToY, Amount }
            .Select(v => v.ToString(CultureInfo.InvariantCulture)));
    }

    // Constructed from server prefab/layout data before entering the writer. No Unity references.
    internal sealed class PlayerInventoryRules
    {
        private readonly HashSet<int> slots;
        private readonly Dictionary<int, int> stacks;
        internal PlayerInventoryRules(IEnumerable<int> allowedSlots, IDictionary<int, int> maxStacks)
        {
            slots = new HashSet<int>(allowedSlots); stacks = new Dictionary<int, int>(maxStacks);
            if (slots.Count == 0 || slots.Any(s => s < 0 || s >= 65536) ||
                stacks.Any(s => s.Value < 1 || s.Value > ushort.MaxValue)) throw new ArgumentException("Invalid server inventory rules");
        }
        internal bool Allows(int x, int y) => slots.Contains(y * 256 + x);
        internal int MaxStack(int prefab) => stacks.TryGetValue(prefab, out int max) ? max : throw new InvalidDataException("Unknown item prefab");
    }

    internal static class PlayerInventoryAuthority
    {
        internal static PlayerBatch Prepare(PlayerInventoryMove request, PlayerInventoryRules rules, IEnumerable<PlayerChange> rows)
        {
            if (!rules.Allows(request.FromX, request.FromY) || !rules.Allows(request.ToX, request.ToY))
                throw new InvalidOperationException("Inventory slot is not available");
            var inventory = rows.Where(r => r.Table == "inventory" && !r.Delete).Select(r => r.Values)
                .Where(v => (string)v[0] == "main").ToArray();
            Func<object[], int, int, bool> at = (v, x, y) => Convert.ToInt32(v[1]) == x && Convert.ToInt32(v[2]) == y;
            var source = inventory.SingleOrDefault(v => at(v, request.FromX, request.FromY));
            var target = inventory.SingleOrDefault(v => at(v, request.ToX, request.ToY));
            if (source == null || request.Amount > Convert.ToInt32(source[4])) throw new InvalidOperationException("Insufficient items in source slot");
            // Equipped/reserved slots require the separate equipment action, never an implicit unequip.
            if (Convert.ToBoolean(source[7]) || target != null && Convert.ToBoolean(target[7]))
                throw new InvalidOperationException("Equipped items require an equipment action");
            int maximum = rules.MaxStack(Convert.ToInt32(source[3]));
            if (request.Amount > maximum) throw new InvalidOperationException("Stack limit exceeded");
            var custom = rows.Where(r => r.Table == "item_data" && !r.Delete).Select(r => r.Values)
                .Where(v => (string)v[0] == "main").ToArray();
            Func<int, int, Dictionary<string, string>> data = (x, y) => custom.Where(v => at(v, x, y)).ToDictionary(v => (string)v[3], v => (string)v[4]);
            var sourceData = data(request.FromX, request.FromY);
            if (target != null)
            {
                // Merge only identical persisted item metadata. Never discard quality, durability or custom data.
                if (Enumerable.Range(3, source.Length - 3).Where(i => i != 4).Any(i => !Equal(source[i], target[i])) ||
                    !SameData(sourceData, data(request.ToX, request.ToY))) throw new InvalidOperationException("Items cannot be merged");
                if ((long)Convert.ToInt32(target[4]) + request.Amount > maximum) throw new InvalidOperationException("Destination stack is full");
            }
            var changes = new List<PlayerChange>();
            int remaining = Convert.ToInt32(source[4]) - request.Amount;
            if (remaining == 0) changes.Add(new PlayerChange("inventory", true, "main", request.FromX, request.FromY));
            else { source[4] = remaining; changes.Add(new PlayerChange("inventory", false, source)); }
            if (target == null)
            {
                target = (object[])source.Clone(); target[1] = request.ToX; target[2] = request.ToY; target[4] = request.Amount;
                changes.Add(new PlayerChange("inventory", false, target));
                foreach (var entry in sourceData) changes.Add(new PlayerChange("item_data", false, "main", request.ToX, request.ToY, entry.Key, entry.Value));
            }
            else { target[4] = Convert.ToInt32(target[4]) + request.Amount; changes.Add(new PlayerChange("inventory", false, target)); }
            return new PlayerBatch(request.Operation, request.Revision, changes);
        }
        private static bool SameData(Dictionary<string, string> a, Dictionary<string, string> b)
            => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && p.Value == v);
        private static bool Equal(object a, object b)
        {
            if (a is string || b is string || a == null || b == null) return Equals(a, b);
            return Convert.ToDecimal(a) == Convert.ToDecimal(b);
        }
    }
}
