using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Overhaul.Persistence
{
    // A detached server inventory. An unsuccessful action discards this instance.
    internal sealed class PlayerActionInventory
    {
        private readonly PlayerChange[] original;
        private readonly InventoryMoveLayout layout;
        private readonly Dictionary<int, object[]> items = new Dictionary<int, object[]>();
        private readonly Dictionary<int, Dictionary<string,string>> metadata = new Dictionary<int, Dictionary<string,string>>();
        internal PlayerActionInventory(IEnumerable<PlayerChange> rows, InventoryMoveLayout layout)
        {
            this.layout = layout;
            original = rows.Where(r => r.Table == "inventory" || r.Table == "item_data").ToArray();
            foreach (var row in original.Where(r => r.Table == "inventory"))
            {
                var v = row.Values; int key = Key(v);
                if (row.Delete || !layout.Contains(key) || Convert.ToInt32(v[4]) < 1 || Convert.ToInt32(v[4]) > layout.Maximum(Convert.ToInt32(v[3])))
                    throw new InvalidDataException("Invalid server action inventory");
                items.Add(key,v); metadata.Add(key,new Dictionary<string,string>());
            }
            foreach (var row in original.Where(r => r.Table == "item_data"))
            {
                var v = row.Values;
                if (row.Delete || !metadata.TryGetValue(Key(v),out var data)) throw new InvalidDataException("Orphan action item metadata");
                data.Add((string)v[3],(string)v[4]);
            }
        }
        private static int Key(object[] v)
        {
            int x = Convert.ToInt32(v[1]), y = Convert.ToInt32(v[2]);
            if ((string)v[0] != "main" || x < 0 || x > 255 || y < 0 || y > 255) throw new InvalidDataException("Invalid action inventory cell");
            return y * 256 + x;
        }
        internal object[] Item(int key) => items.TryGetValue(key,out var row) ? (object[])row.Clone() : throw new InvalidOperationException("Source slot is empty");
        internal Dictionary<string,string> Data(int key) => new Dictionary<string,string>(metadata[key]);
        internal void Remove(int key, int amount, bool allowQuest = false)
        {
            var row = Item(key);
            if (!layout.Available(key) || amount < 1 || amount > Convert.ToInt32(row[4]) || !allowQuest && layout.IsQuest(Convert.ToInt32(row[3])))
                throw new InvalidOperationException("Item cannot be consumed by this action");
            int remaining = Convert.ToInt32(row[4]) - amount;
            if (remaining == 0) { items.Remove(key); metadata.Remove(key); }
            else { row[4] = remaining; items[key] = row; }
        }
        // Costs are resolved from the server recipe, never supplied as resulting SQL rows by a client.
        internal void Consume(IDictionary<int,int> costs)
        {
            foreach (var cost in costs)
                if (cost.Value < 1 || layout.IsQuest(cost.Key) || items.Where(p => layout.Available(p.Key) && Convert.ToInt32(p.Value[3]) == cost.Key && !Convert.ToBoolean(p.Value[7]))
                    .Sum(p => (long)Convert.ToInt32(p.Value[4])) < cost.Value) throw new InvalidOperationException("Missing crafting resources");
            foreach (var cost in costs)
            {
                int left = cost.Value;
                foreach (int key in items.Where(p => layout.Available(p.Key) && Convert.ToInt32(p.Value[3]) == cost.Key && !Convert.ToBoolean(p.Value[7])).Select(p => p.Key).OrderBy(k => k).ToArray())
                {
                    int count = Math.Min(left,Convert.ToInt32(items[key][4])); Remove(key,count); left -= count; if (left == 0) break;
                }
            }
        }
        private static bool Equal(object a,object b) => a == null || b == null || a is string || b is string ? Equals(a,b) : Convert.ToDecimal(a) == Convert.ToDecimal(b);
        internal void Add(object[] source, IDictionary<string,string> data)
        {
            var row = (object[])source.Clone(); int prefab = Convert.ToInt32(row[3]), left = Convert.ToInt32(row[4]);
            if (left < 1 || left > ushort.MaxValue || Convert.ToBoolean(row[7])) throw new InvalidOperationException("Invalid granted stack");
            int maximum = layout.Maximum(prefab);
            bool Merge(int key) => Enumerable.Range(3,row.Length-3).Where(i => i != 4 && i != 7).All(i => Equal(items[key][i],row[i])) &&
                metadata[key].Count == data.Count && metadata[key].All(p => data.TryGetValue(p.Key,out var value) && value == p.Value);
            var candidates = layout.Slots.Where(k => layout.Accepts(k,prefab) && (items.ContainsKey(k) ? maximum > 1 && Merge(k) : layout.Ordinary(k) || layout.Ammo(k)))
                .OrderBy(k => items.ContainsKey(k) ? 0 : 1).ThenBy(k => k).ToArray();
            if (candidates.Sum(k => (long)maximum - (items.ContainsKey(k) ? Convert.ToInt32(items[k][4]) : 0)) < left)
                throw new InvalidOperationException("Inventory is full");
            foreach (int key in candidates)
            {
                bool exists = items.TryGetValue(key,out var target); int count = exists ? Convert.ToInt32(target[4]) : 0;
                int added = Math.Min(left,maximum-count); if (added == 0) continue;
                if (!exists)
                {
                    target = (object[])row.Clone(); target[1] = key % 256; target[2] = key / 256;
                    items.Add(key,target); metadata.Add(key,new Dictionary<string,string>(data));
                    metadata[key].Remove("eaqs_parked");
                }
                target[4] = count + added; left -= added; if (left == 0) break;
            }
        }
        internal void Set(int key, object[] row)
        {
            if (!items.ContainsKey(key) || Key(row) != key || !layout.Accepts(key,Convert.ToInt32(row[3])) ||
                Convert.ToInt32(row[4]) < 1 || Convert.ToInt32(row[4]) > layout.Maximum(Convert.ToInt32(row[3])))
                throw new InvalidOperationException("Invalid modified item");
            // Validate the complete row before replacing its detached copy.
            items[key] = new PlayerChange("inventory",false,row).Values;
        }
        internal PlayerBatch Delta(string operation,long revision)
        {
            var result = new List<PlayerChange>();
            var oldItems = original.Where(r => r.Table == "inventory").ToDictionary(r => Key(r.Values));
            foreach (int key in oldItems.Keys.Union(items.Keys).OrderBy(k => k))
            {
                bool exists = items.TryGetValue(key,out var row);
                var before = original.Where(r => Key(r.Values) == key).ToArray();
                if (exists && oldItems.TryGetValue(key,out var previous) && previous.Values.Zip(row,Equal).All(v => v) &&
                    before.Length == metadata[key].Count + 1 && before.Where(r => r.Table == "item_data").All(r => metadata[key].TryGetValue((string)r.Values[3],out var value) && value == (string)r.Values[4])) continue;
                if (oldItems.ContainsKey(key)) result.Add(new PlayerChange("inventory",true,"main",key%256,key/256));
                if (!exists) continue;
                result.Add(new PlayerChange("inventory",false,row));
                foreach (var pair in metadata[key].OrderBy(p => p.Key,StringComparer.Ordinal))
                    result.Add(new PlayerChange("item_data",false,"main",key%256,key/256,pair.Key,pair.Value));
            }
            return new PlayerBatch(operation,revision,result);
        }
    }
}
