using System;
using System.Linq;
using UnityEngine;

namespace Overhaul.Storage
{
    internal abstract class DeviceAdapter
    {
        internal Component Source;
        internal DeviceStore Store;
        internal virtual ZNetView Network => Source.GetComponent<ZNetView>() ?? Source.GetComponentInParent<ZNetView>();
        internal virtual bool Window => true;
        internal virtual bool Dual => false;
        internal virtual int Width => 3;
        internal virtual int Height => 2;
        internal virtual int WorkSlots => 1;
        internal virtual bool SingleItems => false;
        internal abstract string Title { get; }
        internal abstract string MaterialLabel { get; }
        internal virtual string FuelLabel => "";
        internal abstract bool Accepts(ItemDrop.ItemData item, bool fuel, Vector2i? pos = null);
        internal abstract void Import();
        internal virtual void Publish() { }
        internal virtual void Tick() { }
        internal virtual void BeforeDestroyed() { }
        internal static string Word(string key) => Leveling.LevelingText.Get("overhaul_device_" + key);
        internal static string Name(ItemDrop item) => item ? item.m_itemData.m_shared.m_name : "";
        internal static DeviceAdapter Create(Component c)
        {
            DeviceAdapter a = null;
            if (c is Smelter smelter && (smelter.m_maxOre > 0 || smelter.m_maxFuel > 0)) a = new SmelterInventory(smelter);
            else if (c is CookingStation cooker) a = new CookingInventory(cooker);
            else if (c is Fermenter fermenter) a = new FermenterInventory(fermenter);
            else if (c is Fireplace fire && !fire.m_infiniteFuel && fire.m_fuelItem) a = new FireInventory(fire);
            else if (c is ShieldGenerator shield && shield.m_fuelItems.Count > 0) a = new ShieldInventory(shield);
            else if (c is ItemStand stand) a = new StandInventory(stand);
            else if (c is ArmorStand armor) a = new ArmorInventory(armor);
            else if (c is Catapult catapult) a = new CatapultInventory(catapult);
            else if (c is OfferingBowl bowl) a = new OfferingInventory(bowl);
            if (a != null) a.Source = c;
            return a;
        }
        internal static ItemDrop.ItemData FromHash(int hash)
        {
            var prefab = ObjectDB.instance.GetItemPrefab(hash);
            if (!prefab) throw new InvalidOperationException("Unknown stored item hash " + hash);
            return DeviceStore.Item(prefab.name);
        }
        internal static ItemDrop.ItemData First(Inventory inventory) => inventory.GetAllItems().OrderBy(i => i.m_gridPos.y).ThenBy(i => i.m_gridPos.x).FirstOrDefault();
        internal static void Consume(Inventory inventory, int count)
        {
            foreach (var item in inventory.GetAllItems().ToArray())
            {
                int take = Math.Min(count, item.m_stack);
                if (take > 0) inventory.RemoveItem(item, take);
                count -= take; if (count <= 0) break;
            }
        }
    }

    internal abstract class FuelInventoryAdapter : DeviceAdapter
    {
        internal static readonly int RemainderKey = "overhaul_device_burning_fraction".GetStableHashCode();
        internal abstract ItemDrop DefaultFuel { get; }
        internal abstract bool HasMaterials { get; }
        internal virtual float InitialFuel => 0;
        internal Inventory FuelStock => HasMaterials ? Store.Fuel : Store.Items;
        internal override bool Dual => HasMaterials && DefaultFuel;
        internal override string FuelLabel => Name(DefaultFuel);
        internal virtual bool IsFuel(ItemDrop.ItemData item) => DefaultFuel && item?.m_dropPrefab && item.m_dropPrefab.name == DefaultFuel.name;
        internal void ImportFuel()
        {
            if (!DefaultFuel) return;
            float amount = Math.Max(0, Store.Data.GetFloat(ZDOVars.s_fuel, InitialFuel));
            int whole = Mathf.FloorToInt(amount);
            if (whole > 0) DeviceStore.ImportItem(FuelStock, DeviceStore.Item(DefaultFuel.name, whole));
            Store.Data.Set(RemainderKey, amount - whole);
        }
        internal void PullFuel()
        {
            if (!Store.Ready || !Store.Owner || Store.Changing != 0 || !DefaultFuel) return;
            float amount = Math.Max(0, Store.Data.GetFloat(ZDOVars.s_fuel, InitialFuel));
            int whole = Mathf.FloorToInt(amount), count = FuelStock.CountItems(null, -1, false);
            Store.Change(() =>
            {
                if (whole < count) Consume(FuelStock, count - whole);
                else if (whole > count) DeviceStore.ImportItem(FuelStock, DeviceStore.Item(DefaultFuel.name, whole - count));
                Store.Data.Set(RemainderKey, amount - whole);
            });
        }
        internal override void Publish()
        {
            if (DefaultFuel) Store.Data.Set(ZDOVars.s_fuel, FuelStock.CountItems(null, -1, false) + Store.Data.GetFloat(RemainderKey, 0));
        }
        internal override void BeforeDestroyed()
        {
            if (DefaultFuel) { Store.Data.Set(RemainderKey, 0f); Store.Data.Set(ZDOVars.s_fuel, 0f); }
        }
    }

    internal sealed class SmelterInventory : FuelInventoryAdapter
    {
        internal readonly Smelter Machine;
        internal SmelterInventory(Smelter machine) { Machine = machine; }
        internal override ItemDrop DefaultFuel => Machine.m_maxFuel > 0 ? Machine.m_fuelItem : null;
        internal override bool HasMaterials => Machine.m_maxOre > 0 && !Machine.m_noSourceConversion;
        internal override string Title => Machine.m_name;
        internal override string MaterialLabel
        {
            get
            {
                if (!HasMaterials) return FuelLabel;
                var inputs = Machine.m_conversion.Where(c => c.m_from).Select(c => c.m_from).Distinct().ToArray();
                if (inputs.Length == 1) return Name(inputs[0]);
                if (Utils.GetPrefabName(Machine.gameObject) == "charcoal_kiln") return Word("wood");
                if (Utils.GetPrefabName(Machine.gameObject) == "smelter" || Utils.GetPrefabName(Machine.gameObject) == "blastfurnace") return Word("ore");
                return Word("materials");
            }
        }
        internal override bool Accepts(ItemDrop.ItemData item, bool fuel, Vector2i? pos = null) =>
            item?.m_dropPrefab && (fuel || !HasMaterials ? IsFuel(item) : Machine.IsItemAllowed(item));
        internal override void Import()
        {
            ImportFuel();
            if (!HasMaterials) return;
            int count = Store.Data.GetInt(ZDOVars.s_queued, 0);
            bool cheated = Store.Data.GetBool(ZDOVars.s_cheatedQueued, false);
            for (int i = 0; i < count; i++) DeviceStore.ImportItem(Store.Items, DeviceStore.Item(Store.Data.GetString("item" + i, ""), 1, cheated));
        }
        internal ItemDrop.ItemData Next()
        {
            if (Utils.GetPrefabName(Machine.gameObject) == "charcoal_kiln")
            {
                var wood = Store.Items.GetAllItems().FirstOrDefault(i => i.m_dropPrefab && i.m_dropPrefab.name == "Wood");
                if (wood != null) return wood;
            }
            return First(Store.Items);
        }
        internal override void Publish()
        {
            base.Publish();
            if (DefaultFuel) Machine.m_maxFuel = 6 * DefaultFuel.m_itemData.m_shared.m_maxStackSize;
            if (HasMaterials) Machine.m_maxOre = 6 * Machine.m_conversion.Where(c => c.m_from).Select(c => c.m_from.m_itemData.m_shared.m_maxStackSize).DefaultIfEmpty(1).Max();
        }
        internal override void BeforeDestroyed() { Machine.SpawnProcessed(); base.BeforeDestroyed(); Store.Data.Set(ZDOVars.s_queued, 0); }
    }

    internal sealed class FireInventory : FuelInventoryAdapter
    {
        private readonly Fireplace fire;
        internal FireInventory(Fireplace source) { fire = source; }
        internal override string Title => fire.m_name;
        internal override ItemDrop DefaultFuel => fire.m_fuelItem;
        internal override bool HasMaterials => false;
        internal override float InitialFuel => fire.m_startFuel;
        internal override string MaterialLabel => FuelLabel;
        internal override bool Accepts(ItemDrop.ItemData item, bool fuel, Vector2i? pos = null) => IsFuel(item);
        internal override void Import() => ImportFuel();
        internal override void Publish() { base.Publish(); fire.m_maxFuel = 6 * DefaultFuel.m_itemData.m_shared.m_maxStackSize; }
    }
    internal sealed class ShieldInventory : FuelInventoryAdapter
    {
        private readonly ShieldGenerator shield;
        internal ShieldInventory(ShieldGenerator source) { shield = source; }
        internal override string Title => shield.GetComponent<Piece>()?.m_name ?? shield.name;
        internal override ItemDrop DefaultFuel => shield.m_fuelItems.FirstOrDefault();
        internal override bool HasMaterials => false;
        internal override float InitialFuel => shield.m_defaultFuel;
        internal override string MaterialLabel => Word("bones");
        internal override bool IsFuel(ItemDrop.ItemData item) => item?.m_dropPrefab && shield.m_fuelItems.Any(f => f && f.name == item.m_dropPrefab.name);
        internal override bool Accepts(ItemDrop.ItemData item, bool fuel, Vector2i? pos = null) => IsFuel(item);
        internal override void Import() => ImportFuel();
        internal override void Publish() { base.Publish(); shield.m_maxFuel = 6 * shield.m_fuelItems.Max(f => f.m_itemData.m_shared.m_maxStackSize); }
    }

    internal sealed class CookingInventory : FuelInventoryAdapter
    {
        internal readonly CookingStation Cooker;
        internal CookingInventory(CookingStation source) { Cooker = source; }
        internal override string Title => Cooker.m_name;
        internal override ItemDrop DefaultFuel => Cooker.m_useFuel ? Cooker.m_fuelItem : null;
        internal override bool HasMaterials => true;
        internal override int Width => Utils.GetPrefabName(Cooker.gameObject) == "piece_oven" ? 2 : 3;
        internal override int WorkSlots => Cooker.m_slots.Length;
        internal override string MaterialLabel => Word(Utils.GetPrefabName(Cooker.gameObject) == "piece_FrostFoundry" ? "moulds" : "food");
        internal override bool Accepts(ItemDrop.ItemData item, bool fuel, Vector2i? pos = null) => item?.m_dropPrefab &&
            (fuel ? IsFuel(item) : Cooker.m_conversion.Any(c => c.m_from && c.m_from.name == item.m_dropPrefab.name));
        internal override void Import()
        {
            ImportFuel();
            for (int i = 0; i < WorkSlots; i++)
            {
                Cooker.GetSlot(i, out var name, out var time, out var status, out var cheated);
                if (name.Length > 0) DeviceStore.ImportItem(Store.Working, DeviceStore.Item(name, 1, cheated), i);
            }
        }
        internal override void Publish() { base.Publish(); if (DefaultFuel) Cooker.m_maxFuel = 6 * DefaultFuel.m_itemData.m_shared.m_maxStackSize; }
        internal void Advance()
        {
            if (!Store.Ready || !Store.Owner || Store.Dropping) return;
            Store.Change(() =>
            {
                for (int i = 0; i < WorkSlots; i++)
                {
                    Cooker.GetSlot(i, out var name, out var time, out var status, out var cheated);
                    var active = Store.Working.GetItemAt(i, 0);
                    if (name.Length > 0 && status != CookingStation.Status.NotDone)
                    {
                        Cooker.SpawnItem(name, i, Cooker.transform.position + Cooker.transform.forward * 2, cheated);
                        Cooker.SetSlot(i, "", 0, CookingStation.Status.NotDone, false);
                        if (active != null) Store.Working.RemoveItem(active);
                        name = "";
                    }
                    if (name.Length > 0) continue;
                    if (active != null && Store.Working.ContainsItem(active)) Store.Working.RemoveItem(active);
                    var next = First(Store.Items);
                    if (next == null) continue;
                    var unit = next.Clone(); unit.m_stack = 1; unit.m_gridPos = new Vector2i(i, 0);
                    Store.Items.RemoveItem(next, 1); Store.Working.m_inventory.Add(unit);
                    Cooker.SetSlot(i, unit.m_dropPrefab.name, 0, CookingStation.Status.NotDone, unit.m_cheated);
                    Cooker.m_addEffect.Create(Cooker.m_slots[i].position, Quaternion.identity);
                }
            });
        }
        internal override void BeforeDestroyed()
        {
            for (int i = 0; i < WorkSlots; i++)
            {
                Cooker.GetSlot(i, out var name, out var time, out var status, out var cheated);
                var active = Store.Working.GetItemAt(i, 0);
                if (active != null && name.Length > 0 && status != CookingStation.Status.NotDone)
                {
                    Cooker.SpawnItem(name, i, Cooker.transform.position + Cooker.transform.forward * 2, cheated);
                    Store.Working.RemoveItem(active);
                }
                Cooker.SetSlot(i, "", 0, CookingStation.Status.NotDone, false);
            }
            base.BeforeDestroyed();
        }
    }

    internal sealed class FermenterInventory : DeviceAdapter
    {
        internal readonly Fermenter Fermenter;
        internal FermenterInventory(Fermenter source) { Fermenter = source; }
        internal override string Title => Fermenter.m_name;
        internal override string MaterialLabel => Word("bases");
        internal override bool Accepts(ItemDrop.ItemData item, bool fuel, Vector2i? pos = null) => !fuel && item?.m_dropPrefab && Fermenter.IsItemAllowed(item);
        internal override void Import()
        {
            int content = Fermenter.GetContent();
            if (content != 0) DeviceStore.ImportItem(Store.Working, FromHash(content), 0);
        }
        internal override void Tick()
        {
            if (Fermenter.IsInvoking("DelayedTap")) return;
            var status = Fermenter.GetStatus();
            if (status == Fermenter.Status.Ready)
            {
                Fermenter.RPC_Tap(0); // Original tap animation, effects and delayed output.
                Store.Save();
                return;
            }
            if (status != Fermenter.Status.Empty) return;
            var pending = First(Store.Working);
            if (pending != null)
            {
                // A reload between native tap and delayed output resumes the pending batch.
                Fermenter.m_delayedTapItem = pending.m_dropPrefab.name.GetStableHashCode();
                Fermenter.m_delayedTapItemCheated = pending.m_cheated;
                Fermenter.DelayedTap();
                return;
            }
            var next = First(Store.Items);
            if (next == null) return;
            Store.Change(() =>
            {
                Fermenter.RPC_AddItem(0, next.m_dropPrefab.name.GetStableHashCode(), next.m_cheated);
                if (Fermenter.GetContent() == 0) return;
                var unit = next.Clone(); unit.m_stack = 1;
                Store.Working.AddItem(unit); Store.Items.RemoveItem(next, 1);
            });
        }
        internal override void BeforeDestroyed()
        {
            Fermenter.CancelInvoke("DelayedTap");
            if (Fermenter.GetStatus() == Fermenter.Status.Ready || Fermenter.GetContent() == 0 && Store.Working.NrOfItems() > 0)
            {
                var item = First(Store.Working);
                if (item != null) { Fermenter.m_delayedTapItem = item.m_dropPrefab.name.GetStableHashCode(); Fermenter.m_delayedTapItemCheated = item.m_cheated; Fermenter.DelayedTap(); }
                Store.Working.RemoveAll();
            }
            Store.Data.Set(ZDOVars.s_content, 0); Store.Data.Set(ZDOVars.s_startTime, 0L);
        }
    }
}
