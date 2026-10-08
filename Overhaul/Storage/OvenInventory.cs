using System;
using System.Linq;
using UnityEngine;

namespace Overhaul.Storage
{
    internal sealed class OvenInventory : CookingInventory
    {
        private static readonly int LayoutKey = "overhaul_oven_slots_v2".GetStableHashCode();
        internal readonly int[] Slots;
        internal OvenInventory(CookingStation source) : base(source)
        {
            // View from the oven opening: far row first, then left to right.
            var center = source.m_slots.Aggregate(Vector3.zero, (v, t) => v + t.position) / source.m_slots.Length;
            var front = Vector3.ProjectOnPlane(source.m_addFoodSwitch.transform.position - center, Vector3.up).normalized;
            var right = Vector3.Cross(Vector3.up, -front);
            var depth = Enumerable.Range(0, source.m_slots.Length).OrderBy(i => Vector3.Dot(source.m_slots[i].position, front)).ToArray();
            Slots = depth.Take(2).OrderBy(i => Vector3.Dot(source.m_slots[i].position, right))
                .Concat(depth.Skip(2).OrderBy(i => Vector3.Dot(source.m_slots[i].position, right))).ToArray();
        }
        // Each cell holds a stack of one raw dish; the first one cooks in its physical slot.
        private ItemDrop.ItemData Cell(int cell) => Store.Items.GetItemAt(cell % 2, cell / 2);
        private bool Raw(ItemDrop.ItemData item) => item?.m_dropPrefab && Cooker.m_conversion.Any(c => c.m_from && c.m_from.name == item.m_dropPrefab.name);
        internal override void Publish()
        {
            base.Publish();
            if (!Store.Data.GetBool(LayoutKey, false)) Migrate();
            for (int cell = 0; cell < Slots.Length; cell++)
            {
                var item = Cell(cell);
                Cooker.GetSlot(Slots[cell], out var name, out _, out var status, out _);
                if (status != CookingStation.Status.NotDone) continue; // finished: Advance ejects it
                string wanted = Raw(item) ? item.m_dropPrefab.name : "";
                // Same dish already cooking: keep its progress (more of it added, reload, owner change).
                if (name == wanted) continue;
                Set(cell, wanted, item?.m_cheated ?? false);
                if (wanted.Length > 0) Cooker.m_addEffect.Create(Cooker.m_slots[Slots[cell]].position, Quaternion.identity);
            }
        }
        private void Set(int cell, string name, bool cheated)
        {
            Cooker.SetSlot(Slots[cell], name, 0, CookingStation.Status.NotDone, cheated);
            Store.View.InvokeRPC(ZNetView.Everybody, "RPC_SetSlotVisual", Slots[cell], name);
        }
        private void Migrate()
        {
            var reserve = Store.Items.GetAllItems().ToArray();
            Store.Items.RemoveAll();
            for (int cell = 0; cell < Slots.Length; cell++)
            {
                var item = Store.Working.GetItemAt(Slots[cell], 0);
                if (item == null) continue;
                item.m_gridPos = new Vector2i(cell % 2, cell / 2);
                Store.Items.m_inventory.Add(item);
            }
            Store.Working.RemoveAll();
            // Keep old waiting stock accessible; never discard an old stack merely
            // because the oven now has four physical slots instead of a queue.
            foreach (var item in reserve)
            {
                for (int cell = 0; cell < Slots.Length && item.m_stack > 0; cell++)
                {
                    if (Store.Items.GetItemAt(cell % 2, cell / 2) != null) continue;
                    var unit = item.Clone(); unit.m_stack = 1; unit.m_gridPos = new Vector2i(cell % 2, cell / 2);
                    Store.Items.m_inventory.Add(unit); item.m_stack--; Set(cell, unit.m_dropPrefab.name, unit.m_cheated);
                }
                if (item.m_stack <= 0) continue;
                int index = 4;
                while (Store.Items.GetItemAt(index % 2, index / 2) != null) index++;
                item.m_gridPos = new Vector2i(index % 2, index / 2); Store.Items.m_inventory.Add(item);
            }
            Store.Data.Set(LayoutKey, true); Store.Save();
        }
        internal override void Advance()
        {
            if (!Store.Ready || !Store.Owner || Store.Dropping) return;
            Store.Change(() =>
            {
                var front = Cooker.transform.position + Cooker.transform.forward * 2;
                for (int cell = 0; cell < Slots.Length; cell++)
                {
                    var item = Cell(cell);
                    // Cooked dishes kept in a cell by 2.2.0.53 to 2.2.1.6 are ejected too.
                    if (item != null && !Raw(item))
                    {
                        for (int i = 0; i < item.m_stack; i++) Cooker.SpawnItem(item.m_dropPrefab.name, Slots[cell], front, item.m_cheated);
                        Store.Items.m_inventory.Remove(item); item = null;
                    }
                    Cooker.GetSlot(Slots[cell], out var name, out _, out var status, out var cheated);
                    if (name.Length > 0 && status != CookingStation.Status.NotDone)
                    {
                        // Ejected like other production machines: native output, protected on the ground.
                        Cooker.SpawnItem(name, Slots[cell], front, cheated);
                        if (item != null) { Store.Items.RemoveItem(item, 1); item = Cell(cell); }
                        Set(cell, "", false);
                    }
                    // The next dish of the stack starts cooking.
                    Cooker.GetSlot(Slots[cell], out name, out _, out _, out _);
                    if (name.Length == 0 && Raw(item))
                    {
                        Set(cell, item.m_dropPrefab.name, item.m_cheated);
                        Cooker.m_addEffect.Create(Cooker.m_slots[Slots[cell]].position, Quaternion.identity);
                    }
                }
            });
        }
    }
}
