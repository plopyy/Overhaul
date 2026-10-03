using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Overhaul.Persistence
{
    // Main-thread collector. A capture stays pending until its durable acknowledgement arrives.
    // It reads only changed blocks; bulk native resets are spread across bounded captures.
    internal sealed class PlayerMapTracker
    {
        internal sealed class Capture
        {
            internal readonly PlayerChange[] Rows;
            internal readonly Dictionary<int, long> Versions;
            internal readonly PlayerMapTracker Owner;
            internal Capture(PlayerMapTracker owner, PlayerChange[] rows, Dictionary<int, long> versions)
            { Owner = owner; Rows = rows; Versions = versions; }
        }

        private readonly int size, cells, blocks;
        private readonly Dictionary<int, long> dirty = new Dictionary<int, long>();
        private long version;
        internal int PendingBlocks => dirty.Count;

        internal PlayerMapTracker(int size)
        {
            if (size < 1 || size > 4096) throw new ArgumentOutOfRangeException(nameof(size));
            this.size = size; cells = checked(size * size);
            blocks = (cells + PlayerMapFormat.CellsPerBlock - 1) / PlayerMapFormat.CellsPerBlock;
        }

        internal void Changed(bool shared, int x, int y)
        {
            if (x < 0 || y < 0 || x >= size || y >= size) throw new ArgumentOutOfRangeException();
            Mark((shared ? blocks : 0) + (y * size + x) / PlayerMapFormat.CellsPerBlock);
        }

        internal void Reset(bool sharedOnly)
        {
            for (int key = sharedOnly ? blocks : 0; key < blocks * 2; key++) Mark(key);
        }

        private void Mark(int key) { dirty[key] = checked(++version); }

        internal Capture Read(BitArray self, BitArray others, int maxBlocks = 32)
        {
            if (self == null || others == null || self.Length != cells || others.Length != cells)
                throw new ArgumentException("Map dimensions changed during player session");
            if (maxBlocks < 1 || maxBlocks > 256) throw new ArgumentOutOfRangeException(nameof(maxBlocks));
            var selected = dirty.OrderBy(p => p.Value).Take(maxBlocks).ToArray();
            var rows = new List<PlayerChange>(selected.Length);
            foreach (var entry in selected)
            {
                bool shared = entry.Key >= blocks;
                int block = entry.Key % blocks, start = block * PlayerMapFormat.CellsPerBlock;
                var source = shared ? others : self;
                var bits = new byte[PlayerMapFormat.BytesPerBlock];
                bool any = false;
                for (int i = 0; i < Math.Min(PlayerMapFormat.CellsPerBlock, cells - start); i++)
                    if (source[start + i]) { bits[i / 8] |= (byte)(1 << (i % 8)); any = true; }
                rows.Add(any ? new PlayerChange("map", false, shared ? "others" : "self", block, bits)
                    : new PlayerChange("map", true, shared ? "others" : "self", block));
            }
            return new Capture(this, rows.ToArray(), selected.ToDictionary(p => p.Key, p => p.Value));
        }

        internal void Acknowledge(Capture capture)
        {
            if (capture == null || !ReferenceEquals(capture.Owner, this))
                throw new ArgumentException("Map acknowledgement belongs to another session");
            foreach (var entry in capture.Versions)
                if (dirty.TryGetValue(entry.Key, out var current) && current == entry.Value) dirty.Remove(entry.Key);
        }
    }
}
