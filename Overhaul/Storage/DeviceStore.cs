using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Storage
{
    // Inventories own the stock. Native machine fields only retain operating state
    // (timers, burning fractions, occupied cooking anchors and visible attachments).
    internal sealed class DeviceStore : MonoBehaviour
    {
        internal DeviceAdapter Adapter;
        internal ZNetView View;
        internal Container Container;
        internal Inventory Items, Fuel, Working;
        internal bool Ready, Dropping, Loading;
        internal int Changing;
        internal ZDO Data => View && View.IsValid() ? View.GetZDO() : null;
        internal bool Owner => Data != null && View.IsOwner();
        internal bool Visible => Adapter != null && Adapter.Window;
        private int itemsKey, fuelKey, workKey, markerKey;
        private byte[] itemsBytes, fuelBytes, workBytes;
        private bool reported;

        internal static DeviceStore Of(Component source)
        {
            if (!source) return null;
            foreach (var store in source.GetComponents<DeviceStore>())
                if (store.Adapter != null && (store.Adapter.Source == source || store.Container == source)) return store;
            return null;
        }
        internal static DeviceStore At(GameObject go)
        {
            if (!go) return null;
            foreach (var store in go.GetComponentsInParent<DeviceStore>())
                if (store.Visible) return store;
            return null;
        }
        internal static void Attach(Component source)
        {
            if (!source || Of(source)) return;
            var adapter = DeviceAdapter.Create(source);
            if (adapter == null) return;
            var view = adapter.Network;
            if (!view || !view.IsValid()) return; // Prefabs and placement ghosts.
            if (adapter.Window && source.GetComponent<Container>()) return;
            var store = source.gameObject.AddComponent<DeviceStore>();
            store.Adapter = adapter; adapter.Store = store; store.View = view;
            string prefix = "overhaul_device_";
            if (view.gameObject != source.gameObject)
            {
                var path = source.transform.name;
                for (var t = source.transform.parent; t && t != view.transform; t = t.parent) path = t.name + "/" + path;
                prefix += path + "_";
            }
            store.markerKey = (prefix + "inventory_v1").GetStableHashCode();
            store.itemsKey = adapter.Window ? ZDOVars.s_items : (prefix + "items").GetStableHashCode();
            store.fuelKey = (prefix + "fuel_items").GetStableHashCode();
            store.workKey = (prefix + "working_items").GetStableHashCode();
            store.Items = new Inventory(adapter.Title, null, adapter.Width, adapter.Height);
            store.Fuel = new Inventory(adapter.FuelLabel, null, 3, 2);
            store.Working = new Inventory("Processing", null, Math.Max(1, adapter.WorkSlots), 1);
            DeviceInventoryRules.Register(store.Items, store, false);
            DeviceInventoryRules.Register(store.Fuel, store, true);
            store.Items.m_onChanged += store.Changed;
            store.Fuel.m_onChanged += store.Changed;
            store.Working.m_onChanged += store.Changed;
            if (adapter.Window) store.Container = source.gameObject.AddComponent<Container>();
            else
            {
                store.InvokeRepeating(nameof(Tick), 1f, 1f);
                var wear = source.GetComponent<WearNTear>();
                if (wear && source is Catapult) wear.m_onDestroyed += store.DropAll;
            }
            DeviceActions.Register(store);
            store.Load();
        }
        private void OnDestroy()
        {
            if (Items != null) { Items.m_onChanged -= Changed; DeviceInventoryRules.Unregister(Items); }
            if (Fuel != null) { Fuel.m_onChanged -= Changed; DeviceInventoryRules.Unregister(Fuel); }
            if (Working != null) Working.m_onChanged -= Changed;
        }
        internal void Change(Action action)
        {
            Changing++;
            try { action(); }
            finally { Changing--; }
            Changed();
        }
        internal void Changed()
        {
            if (Changing != 0 || !Ready || !Owner || Dropping) return;
            Changing++;
            try { Adapter.Publish(); Save(); }
            finally { Changing--; }
        }
        internal void Save()
        {
            if (!Ready || !Owner) return;
            Resize(Items, Adapter.Height); Resize(Fuel, 2);
            Write(Items, itemsKey, ref itemsBytes);
            Write(Fuel, fuelKey, ref fuelBytes);
            Write(Working, workKey, ref workBytes);
            Data.Set(markerKey, true); // Written last, after the complete initial import.
            if (Container) Container.m_lastRevision = Data.DataRevision;
        }
        private void Write(Inventory inventory, int key, ref byte[] previous)
        {
            var package = new ZPackage(); inventory.Save(package); var bytes = package.GetArray();
            if (previous == null || !previous.SequenceEqual(bytes)) { Data.Set(key, bytes); previous = bytes; }
        }
        private void Read(Inventory inventory, int key, ref byte[] previous)
        {
            var bytes = Data.GetByteArray(key, null);
            if (bytes == null || previous != null && previous.SequenceEqual(bytes)) return;
            inventory.Load(new ZPackage(bytes)); previous = bytes;
            Resize(inventory, inventory == Items ? Adapter.Height : inventory == Fuel ? 2 : 1);
        }
        private static void Resize(Inventory inventory, int minimum) => inventory.m_height = Math.Max(minimum, inventory.GetAllItems().Select(i => i.m_gridPos.y + 1).DefaultIfEmpty(0).Max());
        internal bool Load()
        {
            if (Data == null || Adapter == null) return false;
            if (Adapter is CatapultInventory catapult && catapult.PendingOwnership)
            {
                if (Owner) { catapult.PendingOwnership = false; Changed(); }
                return Ready;
            }
            Changing++; Loading = true;
            try
            {
                if (!Data.GetBool(markerKey, false))
                {
                    if (!Owner || !ObjectDB.instance) return false;
                    Items.RemoveAll(); Fuel.RemoveAll(); Working.RemoveAll();
                    Adapter.Import();
                    Ready = true;
                    Adapter.Publish();
                    Save();
                }
                else
                {
                    Read(Items, itemsKey, ref itemsBytes);
                    Read(Fuel, fuelKey, ref fuelBytes);
                    Read(Working, workKey, ref workBytes);
                    Ready = true;
                    if (Owner) Adapter.Publish();
                }
                if (Container) Container.m_lastRevision = Data.DataRevision;
                reported = false;
                return true;
            }
            catch (Exception error)
            {
                Ready = false;
                if (!reported) { Debug.LogError("Overhaul: cannot load device inventory " + name + ": " + error); reported = true; }
                return false;
            }
            finally { Changing--; Loading = false; }
        }
        internal void Tick()
        {
            if (!Load() || !Owner || Dropping) return;
            Adapter.Tick();
        }
        internal bool Open(Humanoid user, bool hold)
        {
            if (hold || !Container || !Load()) return false;
            return Container.Interact(user, false, false);
        }
        internal string Hover() => Localization.instance.Localize(Adapter.Title + "\n[<color=yellow><b>$KEY_Use</b></color>] $piece_container_open") + DeviceActions.Hint(this);
        internal void DropAll()
        {
            if (!Ready || !Owner || Dropping) return;
            Dropping = true;
            try
            {
                Adapter.BeforeDestroyed();
                foreach (var inventory in new[] { Items, Fuel, Working })
                {
                    foreach (var item in inventory.GetAllItems().ToArray())
                        ItemDrop.DropItem(item, item.m_stack, transform.position + Vector3.up + UnityEngine.Random.insideUnitSphere * .3f, Quaternion.identity);
                    inventory.RemoveAll();
                }
                Save();
            }
            finally { Dropping = false; }
        }
        internal static ItemDrop.ItemData Item(string prefab, int amount = 1, bool cheated = false)
        {
            var go = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(prefab) : null;
            if (!go || !go.GetComponent<ItemDrop>()) throw new InvalidOperationException("Missing stored item prefab: " + prefab);
            var item = go.GetComponent<ItemDrop>().m_itemData.Clone();
            item.m_dropPrefab = go; item.m_stack = amount; item.m_cheated |= cheated;
            return item;
        }
        internal static void ImportItem(Inventory target, ItemDrop.ItemData item, int x = -1)
        {
            // Legacy queues may contain more distinct stacks than the new capacity.
            // Keep these rows accessible for withdrawal; normal insertion stays capped.
            if (x >= 0) { item.m_gridPos = new Vector2i(x, 0); target.m_inventory.Add(item); return; }
            while (item.m_stack > 0)
            {
                var copy = item.Clone(); copy.m_stack = Math.Min(item.m_stack, item.m_shared.m_maxStackSize);
                while (!target.CanAddItem(copy, copy.m_stack)) target.m_height++;
                if (!target.AddItem(copy)) throw new InvalidOperationException("Could not import device stock");
                item.m_stack -= Math.Min(item.m_stack, item.m_shared.m_maxStackSize);
            }
        }

        [HarmonyPatch(typeof(Container), "Awake")]
        private static class PrepareContainer
        {
            private static void Prefix(Container __instance)
            {
                var store = __instance.GetComponents<DeviceStore>().FirstOrDefault(s => s.Visible && !s.Container);
                if (!store) return;
                store.Container = __instance;
                __instance.m_name = store.Adapter.Title; __instance.m_width = store.Adapter.Width; __instance.m_height = store.Adapter.Height;
                if (store.View.gameObject != __instance.gameObject) __instance.m_rootObjectOverride = store.View;
            }
            private static void Postfix(Container __instance)
            {
                var store = Of(__instance); if (!store) return;
                __instance.m_inventory.m_onChanged -= __instance.OnContainerChanged;
                __instance.m_inventory = store.Items;
            }
        }
        [HarmonyPatch(typeof(Container), "Save")]
        private static class SaveContainer { private static bool Prefix(Container __instance) { var s = Of(__instance); if (!s) return true; s.Save(); return false; } }
        [HarmonyPatch(typeof(Container), "Load")]
        private static class LoadContainer { private static bool Prefix(Container __instance, ref bool __result) { var s = Of(__instance); if (!s) return true; __result = s.Load(); return false; } }
        [HarmonyPatch(typeof(Container), "CheckForChanges")]
        private static class UpdateContainer { private static void Postfix(Container __instance) { var s = Of(__instance); if (s && s.Ready && s.Owner && !s.Dropping) s.Adapter.Tick(); } }
        [HarmonyPatch(typeof(Container), "OnDestroyed")]
        private static class DestroyContainer { private static bool Prefix(Container __instance) { var s = Of(__instance); if (!s || !s.Ready) return true; s.DropAll(); return false; } }
    }
}
