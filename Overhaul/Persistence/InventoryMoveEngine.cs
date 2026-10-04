using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Overhaul.Persistence
{
    internal enum InventoryMoveKind { Slot, Quick, TakeAll, StackAll, Sort, Ammo, Gameplay, Cosmetic }

    // Inventory 0 is the admitted player's bag; inventory 1 is the server-resolved container.
    // No item identity, metadata or claimed player ID is accepted from a client.
    internal sealed class InventoryMoveAction
    {
        internal readonly string Operation;
        internal readonly long PlayerRevision, ContainerRevision;
        internal readonly InventoryMoveKind Kind;
        internal readonly int From, To, FromX, FromY, ToX, ToY, Amount;
        private readonly Dictionary<int, long> slotVersions;
        internal IEnumerable<KeyValuePair<int,long>> SlotVersions => slotVersions;
        internal long SlotVersion(int key) => slotVersions.TryGetValue(key, out long value) ? value : ContainerRevision;
        internal InventoryMoveAction(string operation, long playerRevision, long containerRevision,
            InventoryMoveKind kind, int from, int to, int fromX, int fromY, int toX, int toY, int amount, IDictionary<int,long> slotVersions = null)
        {
            this.slotVersions = slotVersions == null ? new Dictionary<int,long>() : new Dictionary<int,long>(slotVersions);
            if (!Guid.TryParseExact(operation, "N", out _) || playerRevision < 0 || containerRevision < 0 ||
                !Enum.IsDefined(typeof(InventoryMoveKind), kind) || from < 0 || from > 1 || to < 0 || to > 1 ||
                new[] { fromX, fromY, toX, toY }.Any(v => v < 0 || v > 255) || amount < 1 || amount > ushort.MaxValue ||
                (kind == InventoryMoveKind.Slot && from == to && fromX == toX && fromY == toY) ||
                    (kind != InventoryMoveKind.Slot && kind != InventoryMoveKind.Sort && kind != InventoryMoveKind.Ammo && kind != InventoryMoveKind.Gameplay && kind != InventoryMoveKind.Cosmetic && from == to) ||
                (kind == InventoryMoveKind.Gameplay && (from != 0 || to != 0)) ||
                    (kind == InventoryMoveKind.Cosmetic && (from != 0 || to != 0 || fromX == toX && fromY == toY)) ||
                (kind == InventoryMoveKind.Ammo && to != 0) || (kind == InventoryMoveKind.Sort && from != to) || this.slotVersions.Count > 4096 ||
                this.slotVersions.Any(p => p.Key < 0 || p.Key > 65535 || p.Value < 0)) throw new ArgumentException("Invalid inventory movement intent");
            Operation = operation; PlayerRevision = playerRevision; ContainerRevision = containerRevision;
            Kind = kind; From = from; To = to; FromX = fromX; FromY = fromY; ToX = toX; ToY = toY; Amount = amount;
        }
        internal bool UsesContainer => From == 1 || To == 1;
    }

    // A copied server-side layout: null filter means an ordinary cell, a set means a restricted cell.
    internal sealed class InventoryMoveLayout
    {
        private readonly SortedDictionary<int, HashSet<int>> cells;
        private readonly Dictionary<int, int> maximum;
        private readonly HashSet<int> quest;
        private readonly Dictionary<int, string> equipment;
        private readonly HashSet<int> blocked = new HashSet<int>();
        private readonly Dictionary<int,string> order;
        private readonly HashSet<int> ammo;
        private readonly HashSet<int> cosmetics = new HashSet<int>();
        internal bool Ammo(int key) => ammo.Contains(key);
        internal bool Cosmetic(int key) => cosmetics.Contains(key);
        internal InventoryMoveLayout WithCosmetics(IEnumerable<int> keys)
        {
            foreach (int key in keys) { if (!cells.ContainsKey(key)) throw new ArgumentException("Unknown cosmetic cell"); cosmetics.Add(key); }
            return this;
        }
        internal InventoryMoveLayout(IDictionary<int, IEnumerable<int>> slots, IDictionary<int, int> maxStacks, IEnumerable<int> questItems,
            IDictionary<int, string> equipmentCells = null, IDictionary<int,string> sortOrder = null, IEnumerable<int> ammoCells = null)
        {
            ammo = new HashSet<int>(ammoCells ?? Enumerable.Empty<int>());
            order = sortOrder == null ? new Dictionary<int,string>() : new Dictionary<int,string>(sortOrder);
            equipment = equipmentCells == null ? new Dictionary<int, string>() : new Dictionary<int, string>(equipmentCells);
            cells = new SortedDictionary<int, HashSet<int>>(slots.ToDictionary(p => p.Key,
                p => p.Value == null ? null : new HashSet<int>(p.Value)));
            maximum = new Dictionary<int, int>(maxStacks); quest = new HashSet<int>(questItems);
            if (cells.Count == 0 || cells.Count > 4096 || cells.Keys.Any(k => k < 0 || k > 65535) ||
                maximum.Any(p => p.Value < 1 || p.Value > ushort.MaxValue)) throw new ArgumentException("Invalid inventory layout");
        }
        internal IEnumerable<int> Slots => cells.Keys;
        internal bool Contains(int key) => cells.ContainsKey(key);
        internal bool Available(int key) => cells.ContainsKey(key) && !blocked.Contains(key);
        internal bool Accepts(int key, int prefab) => Available(key) && cells.TryGetValue(key, out var filter) && maximum.ContainsKey(prefab) && (filter == null || filter.Contains(prefab));
        internal InventoryMoveLayout Excluding(IEnumerable<int> keys)
        {
            var copy = new InventoryMoveLayout(cells.ToDictionary(p => p.Key, p => (IEnumerable<int>)p.Value), maximum, quest, equipment, order, ammo);
            copy.WithCosmetics(cosmetics); foreach (int key in keys) copy.blocked.Add(key); return copy;
        }
        internal int Maximum(int prefab) => maximum.TryGetValue(prefab, out int value) ? value : throw new InvalidDataException("Unknown server item prefab");
        internal bool IsQuest(int prefab) => quest.Contains(prefab);
        internal string Equipment(int key) => equipment.TryGetValue(key, out var value) ? value : null;
        internal bool Ordinary(int key) => cells.TryGetValue(key, out var filter) && filter == null;
        internal string Order(int prefab) => order.TryGetValue(prefab, out var value) ? value : prefab.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal sealed class InventoryMoveResult
    {
        // Present only while planning a paperdoll move; never sent over the network.
        internal PlayerChange[] EquipmentBefore,EquipmentAfter;
        internal readonly PlayerBatch Player, Container;
        internal readonly int Moved;
        internal InventoryMoveResult(InventoryMoveAction request, IEnumerable<PlayerChange> player, IEnumerable<PlayerChange> container, int moved)
        {
            Player = new PlayerBatch(request.Operation, request.PlayerRevision, player);
            Container = new PlayerBatch(request.Operation, request.ContainerRevision, container); Moved = moved;
        }
        internal InventoryMoveResult(PlayerBatch player, PlayerBatch container, int moved) { Player = player; Container = container; Moved = moved; }
    }

    internal static class InventoryMoveEngine
    {
        private sealed class Item
        {
            internal object[] Values;
            internal readonly Dictionary<string, string> Data = new Dictionary<string, string>();
            internal int Prefab => Convert.ToInt32(Values[3]);
            internal int Count { get => Convert.ToInt32(Values[4]); set => Values[4] = value; }
            internal Item Clone() { var copy = new Item { Values = (object[])Values.Clone() }; foreach (var p in Data) copy.Data.Add(p.Key, p.Value); return copy; }
        }
        private static Dictionary<int, Item> Read(IEnumerable<PlayerChange> source, InventoryMoveLayout layout)
        {
            var rows = source.ToArray(); var items = new Dictionary<int, Item>();
            foreach (var row in rows.Where(r => r.Table == "inventory"))
            {
                var v = row.Values; int key = Key(v);
                if (row.Delete || !layout.Contains(key) || Convert.ToInt32(v[4]) < 1 ||
                    Convert.ToInt32(v[4]) > layout.Maximum(Convert.ToInt32(v[3]))) throw new InvalidDataException("Invalid authoritative inventory slot");
                items.Add(key, new Item { Values = v });
            }
            foreach (var row in rows.Where(r => r.Table == "item_data"))
            {
                var v = row.Values;
                if (row.Delete || !items.TryGetValue(Key(v), out var item)) throw new InvalidDataException("Orphan inventory metadata");
                item.Data.Add((string)v[3], (string)v[4]);
            }
            if (rows.Any(r => r.Table != "inventory" && r.Table != "item_data")) throw new InvalidDataException("Unexpected inventory table");
            return items;
        }
        private static int Key(object[] values)
        {
            int x = Convert.ToInt32(values[1]), y = Convert.ToInt32(values[2]);
            if ((string)values[0] != "main" || x < 0 || x > 255 || y < 0 || y > 255) throw new InvalidDataException("Invalid inventory cell");
            return y * 256 + x;
        }
        private static bool Equal(object a, object b) => a is string || b is string || a == null || b == null
            ? Equals(a, b) : Convert.ToDecimal(a) == Convert.ToDecimal(b);
        private static bool Mergeable(Item a, Item b) => Enumerable.Range(3, a.Values.Length - 3)
            .Where(i => i != 4 && i != 7).All(i => Equal(a.Values[i], b.Values[i])) &&
            a.Data.Count == b.Data.Count && a.Data.All(p => b.Data.TryGetValue(p.Key, out var v) && v == p.Value);
        private static void Position(Item item, int key, int bag, InventoryMoveLayout oldLayout, InventoryMoveLayout newLayout)
        {
            int previous = Key(item.Values);
            if (bag == 1 || newLayout.Cosmetic(key) || oldLayout.Equipment(previous) != null && oldLayout.Equipment(previous) != newLayout.Equipment(key)) item.Values[7] = false;
            item.Values[1] = key % 256; item.Values[2] = key / 256;
            item.Data.Remove("eaqs_parked");
            if (newLayout.Cosmetic(key) || oldLayout.Cosmetic(previous)) { item.Data.Remove("eaqs_player"); item.Data.Remove("eaqs_slot"); }
            string slot = newLayout.Equipment(key);
            if (bag == 0 && slot != null && !Convert.ToBoolean(item.Values[7])) item.Data["eaqs_parked"] = slot;
        }

        internal static InventoryMoveResult Prepare(InventoryMoveAction action, IEnumerable<PlayerChange> player,
            InventoryMoveLayout playerLayout, IEnumerable<PlayerChange> container = null, InventoryMoveLayout containerLayout = null)
        {
            if (action.Kind == InventoryMoveKind.Gameplay) throw new InvalidOperationException("Gameplay action requires its server handler");
            if (action.UsesContainer && (container == null || containerLayout == null)) throw new InvalidOperationException("No authorized container");
            var layouts = new[] { playerLayout, containerLayout };
            var bags = new[] { Read(player, playerLayout), action.UsesContainer ? Read(container, containerLayout) : new Dictionary<int, Item>() };
            var changed = new[] { new HashSet<int>(), new HashSet<int>() };
            var from = bags[action.From]; var to = bags[action.To]; int moved = 0;
            Func<int, int, int, bool, int> move = (sourceKey, targetKey, amount, swap) =>
            {
                if (!from.TryGetValue(sourceKey, out var source)) throw new InvalidOperationException("Source slot is empty");
                if (!layouts[action.From].Available(sourceKey)) throw new InvalidOperationException("Source slot is reserved");
                if (amount > source.Count) throw new InvalidOperationException("Insufficient source quantity");
                if (!layouts[action.To].Accepts(targetKey, source.Prefab)) throw new InvalidOperationException("Item does not fit destination");
                bool cross = action.From != action.To;
                if (cross && layouts[action.From].IsQuest(source.Prefab)) throw new InvalidOperationException("Quest item cannot leave inventory");
                to.TryGetValue(targetKey, out var target);
                if (target != null && (!Mergeable(source, target) || layouts[action.To].Maximum(source.Prefab) == 1))
                {
                    if (!swap || amount != source.Count || !layouts[action.From].Accepts(sourceKey, target.Prefab) ||
                        cross && layouts[action.To].IsQuest(target.Prefab)) throw new InvalidOperationException("Items cannot be exchanged");
                    from[sourceKey] = target; to[targetKey] = source;
                    Position(target, sourceKey, action.From, layouts[action.To], layouts[action.From]);
                    Position(source, targetKey, action.To, layouts[action.From], layouts[action.To]);
                }
                else
                {
                    int capacity = layouts[action.To].Maximum(source.Prefab) - (target?.Count ?? 0);
                    amount = Math.Min(amount, capacity);
                    if (amount == 0) return 0;
                    if (target == null) { target = source.Clone(); target.Count = 0; Position(target, targetKey, action.To, layouts[action.From], layouts[action.To]); to.Add(targetKey, target); }
                    target.Count += amount; source.Count -= amount;
                    if (source.Count == 0) from.Remove(sourceKey);
                }
                changed[action.From].Add(sourceKey); changed[action.To].Add(targetKey); return amount;
            };
            if (action.Kind == InventoryMoveKind.Sort)
            {
                var before = from.ToDictionary(p => p.Key, p => p.Value.Clone());
                var sortLayout = layouts[action.From];
                int[] cells = sortLayout.Slots.Where(key => sortLayout.Available(key) &&
                    (action.From == 1 || key / 256 > 0 && sortLayout.Ordinary(key)) &&
                    (!from.TryGetValue(key, out var value) || !Convert.ToBoolean(value.Values[7]))).ToArray();
                var moving = cells.Where(from.ContainsKey).Select(key => from[key]).ToList();
                foreach (int key in cells) from.Remove(key);
                for (int i = 0; i < moving.Count; i++)
                    for (int j = i + 1; j < moving.Count && moving[i].Count < sortLayout.Maximum(moving[i].Prefab); j++)
                    {
                        if (!Mergeable(moving[i], moving[j])) continue;
                        int count = Math.Min(moving[j].Count, sortLayout.Maximum(moving[i].Prefab) - moving[i].Count);
                        moving[i].Count += count; moving[j].Count -= count;
                    }
                var ordered = moving.Where(i => i.Count > 0).OrderBy(i => sortLayout.Order(i.Prefab), StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(i => Convert.ToInt32(i.Values[5])).ThenBy(i => Convert.ToInt32(i.Values[8]))
                    .ThenByDescending(i => i.Count).ThenBy(i => Key(i.Values)).ToArray();
                for (int i = 0; i < ordered.Length; i++)
                { ordered[i].Values[1] = cells[i] % 256; ordered[i].Values[2] = cells[i] / 256; from.Add(cells[i], ordered[i]); }
                foreach (int key in cells)
                {
                    before.TryGetValue(key, out var a); from.TryGetValue(key, out var b);
                    if (a == null && b == null || a != null && b != null && a.Count == b.Count && Mergeable(a,b)) continue;
                    changed[action.From].Add(key); moved++;
                }
            }
            else if (action.Kind == InventoryMoveKind.Cosmetic)
            {
                int source = action.FromY*256+action.FromX,target = action.ToY*256+action.ToX;
                if (!from.TryGetValue(source,out var sourceItem) || action.Amount != sourceItem.Count ||
                    (!playerLayout.Cosmetic(source) && !playerLayout.Cosmetic(target)) ||
                    !playerLayout.Cosmetic(target) && !playerLayout.Ordinary(target)) throw new InvalidOperationException("Invalid cosmetic movement");
                if (playerLayout.Cosmetic(target) && to.TryGetValue(target,out var resident) && !playerLayout.Ordinary(source))
                {
                    int? free = playerLayout.Slots.Where(k => playerLayout.Ordinary(k) && playerLayout.Accepts(k,resident.Prefab) && !to.ContainsKey(k)).Select(k => (int?)k).FirstOrDefault();
                    if (!free.HasValue) throw new InvalidOperationException("No room for the previous cosmetic item");
                    move(target,free.Value,resident.Count,false);
                }
                moved = move(source,target,action.Amount,true);
            }
            else if (action.Kind == InventoryMoveKind.Slot)
            {
                moved = move(action.FromY * 256 + action.FromX, action.ToY * 256 + action.ToX, action.Amount, true);
            }
            else
            {
                bool single = action.Kind == InventoryMoveKind.Quick || action.Kind == InventoryMoveKind.Ammo;
                int[] sources = single ? new[] { action.FromY * 256 + action.FromX } : from.Keys.OrderBy(k => k).ToArray();
                foreach (int key in sources)
                {
                    if (!from.TryGetValue(key, out var item)) { if (single) throw new InvalidOperationException("Source slot is empty"); continue; }
                    if (!layouts[action.From].Available(key)) continue;
                    if (layouts[action.From].IsQuest(item.Prefab)) continue;
                    if (action.Kind == InventoryMoveKind.StackAll && Convert.ToBoolean(item.Values[7])) continue;
                    if (single && action.Amount > item.Count) throw new InvalidOperationException("Insufficient source quantity");
                    int remaining = single ? Math.Min(action.Amount, item.Count) : item.Count;
                    bool matched = to.Values.Any(v => v.Prefab == item.Prefab);
                    if (action.Kind == InventoryMoveKind.StackAll && !matched) continue;
                    foreach (int dest in layouts[action.To].Slots.OrderBy(k => to.ContainsKey(k) ? 0 : 1))
                    {
                        if (action.From == action.To && dest == key || action.Kind == InventoryMoveKind.Ammo && !layouts[action.To].Ammo(dest)) continue;
                        if (!layouts[action.To].Accepts(dest, item.Prefab) || to.TryGetValue(dest, out var target) &&
                            (!Mergeable(item, target) || layouts[action.To].Maximum(item.Prefab) == 1)) continue;
                        int count = move(key, dest, remaining, false); moved += count; remaining -= count;
                        if (remaining == 0) break;
                    }
                }
            }
            if (moved == 0) throw new InvalidOperationException("No items can be moved");
            Func<int, IEnumerable<PlayerChange>> effects = bag => changed[bag].OrderBy(k => k).SelectMany(key =>
            {
                // Full replacement removes metadata belonging to the displaced item before inserting the new one.
                var rows = new List<PlayerChange> { new PlayerChange("inventory", true, "main", key % 256, key / 256) };
                if (bags[bag].TryGetValue(key, out var item))
                {
                    rows.Add(new PlayerChange("inventory", false, item.Values));
                    rows.AddRange(item.Data.Select(p => new PlayerChange("item_data", false, "main", key % 256, key / 256, p.Key, p.Value)));
                }
                return rows;
            });
            var result = new InventoryMoveResult(action, effects(0), effects(1), moved);
            if ((action.Kind == InventoryMoveKind.Slot || action.Kind == InventoryMoveKind.Cosmetic) && action.From == 0 && action.To == 0 &&
                (playerLayout.Equipment(action.FromY*256+action.FromX) != null || playerLayout.Equipment(action.ToY*256+action.ToX) != null ||
                playerLayout.Cosmetic(action.FromY*256+action.FromX) || playerLayout.Cosmetic(action.ToY*256+action.ToX)))
            {
                result.EquipmentBefore = player.ToArray();
                result.EquipmentAfter = bags[0].Values.SelectMany(item => new[] { new PlayerChange("inventory",false,item.Values) }
                    .Concat(item.Data.Select(p => new PlayerChange("item_data",false,"main",item.Values[1],item.Values[2],p.Key,p.Value)))).ToArray();
            }
            return result;
        }
    }
}
