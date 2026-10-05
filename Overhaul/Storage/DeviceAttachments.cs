using System;
using System.Linq;
using UnityEngine;

namespace Overhaul.Storage
{
    internal sealed class StandInventory : DeviceAdapter
    {
        internal readonly ItemStand Stand;
        internal StandInventory(ItemStand stand) { Stand = stand; }
        internal override ZNetView Network => Stand.m_nview;
        internal override bool Window => Stand.GetComponent<Piece>() && Stand.m_canBeRemoved && !Stand.m_guardianPower && !Stand.m_autoAttach;
        internal override int Width => 1;
        internal override int Height => 1;
        internal override bool SingleItems => true;
        internal override string Title => Stand.m_name;
        internal override string MaterialLabel => Word("item");
        internal override bool Accepts(ItemDrop.ItemData item, bool fuel, Vector2i? pos = null) => !fuel && item?.m_dropPrefab && Stand.CanAttach(item);
        internal override void Import()
        {
            int hash = Store.Data.GetInt(ZDOVars.s_item, 0); if (hash == 0) return;
            var item = FromHash(hash); ItemDrop.LoadFromZDO(item, Store.Data); item.m_stack = 1;
            DeviceStore.ImportItem(Store.Items, item, 0);
        }
        internal void Pull()
        {
            if (!Store.Owner || !Store.Ready || Store.Changing > 0) return;
            Store.Change(() => { Store.Items.RemoveAll(); Import(); });
        }
        internal override void Publish()
        {
            var item = First(Store.Items); int hash = item?.m_dropPrefab ? item.m_dropPrefab.name.GetStableHashCode() : 0;
            bool changed = hash != Store.Data.GetInt(ZDOVars.s_item, 0) || item != null && (item.m_variant != Store.Data.GetInt(ZDOVars.s_variant, 0) || item.m_quality != Store.Data.GetInt(ZDOVars.s_quality, 1));
            Store.Data.Set(ZDOVars.s_item, hash);
            if (item != null) ItemDrop.SaveToZDO(item, Store.Data);
            if (changed)
            {
                Store.View.InvokeRPC(ZNetView.Everybody, "SetVisualItem", hash, item?.m_variant ?? 0, item?.m_quality ?? 1, Stand.GetOrientation());
                Stand.m_effects.Create(Stand.transform.position, Quaternion.identity);
            }
        }
        internal override void BeforeDestroyed() { Store.Data.Set(ZDOVars.s_item, 0); }
    }

    internal sealed class ArmorInventory : DeviceAdapter
    {
        internal static readonly VisSlot[] Order = { VisSlot.Helmet, VisSlot.Chest, VisSlot.Legs, VisSlot.Shoulder, VisSlot.Utility, VisSlot.BackRight, VisSlot.BackLeft };
        internal readonly ArmorStand Stand;
        internal readonly int[] NativeSlots;
        internal ArmorInventory(ArmorStand stand)
        {
            Stand = stand;
            NativeSlots = Order.Select(slot => stand.m_slots.FindIndex(s => s.m_slot == slot)).ToArray();
        }
        internal override ZNetView Network => Stand.m_nview;
        internal override int Width => 7;
        internal override int Height => 1;
        internal override bool SingleItems => true;
        internal override string Title => Stand.m_name;
        internal override string MaterialLabel => Word("equipment");
        internal override bool Accepts(ItemDrop.ItemData item, bool fuel, Vector2i? pos = null)
        {
            if (fuel || item == null || !item.m_dropPrefab) return false;
            if (!pos.HasValue) return Enumerable.Range(0, 7).Any(i => Accepts(item, false, new Vector2i(i, 0)));
            int slot = pos.Value.x; if (slot < 0 || slot >= 7 || pos.Value.y != 0 || NativeSlots[slot] < 0) return false;
            if (!Stand.CanAttach(Stand.m_slots[NativeSlots[slot]], item)) return false;
            return Order[slot] == VisSlot.Chest || Order[slot] == VisSlot.Legs || Stand.GetAttachPrefab(item.m_dropPrefab);
        }
        internal override void Import()
        {
            for (int i = 0; i < NativeSlots.Length; i++)
            {
                int native = NativeSlots[i]; if (native < 0) continue;
                int hash = Stand.GetAttachedItem(native); if (hash == 0) continue;
                var item = FromHash(hash); ItemDrop.LoadFromZDO(item, Store.Data, native); item.m_stack = 1;
                DeviceStore.ImportItem(Store.Items, item, i);
            }
        }
        internal void Pull()
        {
            if (!Store.Owner || !Store.Ready || Store.Changing > 0) return;
            Store.Change(() => { Store.Items.RemoveAll(); Import(); });
        }
        internal override void Publish()
        {
            for (int i = 0; i < NativeSlots.Length; i++)
            {
                int native = NativeSlots[i]; if (native < 0) continue;
                var item = Store.Items.GetItemAt(i, 0); int hash = item?.m_dropPrefab ? item.m_dropPrefab.name.GetStableHashCode() : 0;
                bool changed = hash != Stand.GetAttachedItem(native) || item != null && item.m_variant != Store.Data.GetInt(native + "_variant", 0);
                Store.Data.Set(ArmorStand.s_itemKeyHashes[native], hash);
                if (item != null) ItemDrop.SaveToZDO(item, Store.Data, native);
                if (changed) Store.View.InvokeRPC(ZNetView.Everybody, "RPC_SetVisualItem", native, hash, item?.m_variant ?? 0);
            }
            Stand.UpdateSupports();
        }
        internal override void BeforeDestroyed()
        {
            foreach (int native in NativeSlots) if (native >= 0) Store.Data.Set(ArmorStand.s_itemKeyHashes[native], 0);
        }
    }

    internal sealed class CatapultInventory : DeviceAdapter
    {
        internal readonly Catapult Catapult;
        internal bool PendingOwnership;
        internal CatapultInventory(Catapult catapult) { Catapult = catapult; }
        internal override bool Window => false;
        internal override int Width => 1;
        internal override int Height => 1;
        internal override string Title => Catapult.GetComponent<Piece>()?.m_name ?? Catapult.name;
        internal override string MaterialLabel => Word("projectile");
        internal override bool Accepts(ItemDrop.ItemData item, bool fuel, Vector2i? pos = null) => !fuel && item?.m_dropPrefab && Catapult.CanItemBeLoaded(item);
        internal override void Import()
        {
            if (Catapult.m_loadedItem == null || Catapult.m_loadStack <= 0) return;
            var item = Catapult.m_loadedItem.Clone(); item.m_stack = Catapult.m_loadStack; DeviceStore.ImportItem(Store.Items, item, 0);
        }
        internal void Loaded(ItemDrop.ItemData item)
        {
            Store.Changing++;
            try { Store.Items.RemoveAll(); DeviceStore.ImportItem(Store.Items, item, 0); }
            finally { Store.Changing--; }
            PendingOwnership = !Store.Owner;
            if (Store.Owner) Store.Changed();
        }
        internal override void Publish()
        {
            var item = First(Store.Items);
            Catapult.m_loadedItem = item;
            Catapult.m_loadStack = item?.m_stack ?? 0;
        }
        internal override void Tick()
        {
            if (PendingOwnership) { PendingOwnership = false; Store.Save(); }
            var item = First(Store.Items);
            if (item == null) return;
            if (!Catapult.m_visualItem) Catapult.RPC_SetLoadedVisual(0, item.m_dropPrefab.name);
            Publish();
            if (Catapult.m_armAnimTime == 0 && !Catapult.IsInvoking("Shoot")) Catapult.Invoke("Shoot", Catapult.m_shootAfterLoadDelay);
        }
        internal override void BeforeDestroyed() { Catapult.CancelInvoke("Shoot"); Catapult.m_loadedItem = null; Catapult.m_loadStack = 0; }
    }

    internal sealed class OfferingInventory : DeviceAdapter
    {
        internal readonly OfferingBowl Bowl;
        internal OfferingInventory(OfferingBowl bowl) { Bowl = bowl; }
        internal override bool Window => false;
        internal override int Width => 1;
        internal override int Height => 1;
        internal override string Title => Bowl.m_name;
        internal override string MaterialLabel => Word("offering");
        internal override bool Accepts(ItemDrop.ItemData item, bool fuel, Vector2i? pos = null) => !fuel && Bowl.m_bossItem && item?.m_shared?.m_name == Bowl.m_bossItem.m_itemData.m_shared.m_name;
        internal override void Import() { } // Bowls consume accepted offerings; stand-based altars own their stored items.
    }
}
