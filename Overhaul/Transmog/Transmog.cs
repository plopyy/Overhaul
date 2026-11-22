using System;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Transmog
{
    // Transmogrification: a weapon, tool or shield can wear the look of another item of exactly the same type (one-handed
    // axe on one-handed axe, claws on claws, two-handed sword on two-handed sword...). The look is saved on the item itself
    // (custom data "overhaul_transmog" = "<prefab>:<variant>") and only changes what is drawn: the model in hand or on the
    // back and the icon. Stats, name, quality, enchantments stay the item's own.
    // The look reaches every player through the game's own visual equipment: the hash the owner writes in its ZDO is the
    // look's prefab instead of the item's, so other clients show it with no extra data.
    internal static class Transmog
    {
        internal const string DataKey = "overhaul_transmog";
        internal const int Cost = 5;   // mystic dyes per look applied; removing a look is free

        // ---------------------------------------------------------------- type
        // The exact type an item belongs to, or null when it cannot be transmogrified (only items held in the hands).
        // Same item type, skill and hand animation; tools also by what they build (hammer, hoe, cultivator...) and shields
        // split in tower shields (no parry) and parrying shields (round, buckler).
        internal static string Kind(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null || !item.m_dropPrefab) return null;
            var s = item.m_shared;
            switch (s.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Bow:
                case ItemDrop.ItemData.ItemType.Torch:
                    return s.m_itemType + "|" + s.m_skillType + "|" + s.m_animationState;
                case ItemDrop.ItemData.ItemType.Tool:
                    return s.m_itemType + "|" + s.m_skillType + "|" + s.m_animationState + "|" + (s.m_buildPieces ? s.m_buildPieces.name : "");
                case ItemDrop.ItemData.ItemType.Shield:
                    return s.m_itemType + "|" + (s.m_timedBlockBonus <= 1f ? "tower" : "parry");
                default: return null;
            }
        }
        internal static bool Compatible(ItemDrop.ItemData target, ItemDrop.ItemData source)
        {
            if (target == null || source == null || target == source) return false;
            var kind = Kind(target);
            if (kind == null || kind != Kind(source)) return false;
            // A look identical to what the target already shows changes nothing.
            return Shown(source) != Shown(target);
        }

        // ---------------------------------------------------------------- data on the item
        internal readonly struct Look : IEquatable<Look>
        {
            public readonly string Prefab; public readonly int Variant;
            public Look(string prefab, int variant) { Prefab = prefab; Variant = variant; }
            public bool Equals(Look o) => Prefab == o.Prefab && Variant == o.Variant;
            public override bool Equals(object o) => o is Look l && Equals(l);
            public override int GetHashCode() => (Prefab ?? "").GetHashCode() * 31 + Variant;
            public static bool operator ==(Look a, Look b) => a.Equals(b);
            public static bool operator !=(Look a, Look b) => !a.Equals(b);
        }

        // The look set on the item, if any (and its prefab still exists).
        internal static bool TryGet(ItemDrop.ItemData item, out Look look)
        {
            look = default;
            if (item?.m_customData == null || !item.m_customData.TryGetValue(DataKey, out var value) || string.IsNullOrEmpty(value)) return false;
            int colon = value.LastIndexOf(':');
            string prefab = colon > 0 ? value.Substring(0, colon) : value;
            int.TryParse(colon > 0 ? value.Substring(colon + 1) : "0", out int variant);
            if (!Prefab(prefab)) return false;
            look = new Look(prefab, variant);
            return true;
        }
        // What the item shows: its look, else itself.
        internal static Look Shown(ItemDrop.ItemData item) => TryGet(item, out var look) ? look : new Look(item.m_dropPrefab ? item.m_dropPrefab.name : "", item.m_variant);
        internal static void Set(ItemDrop.ItemData item, Look look)
        {
            if (look.Prefab == (item.m_dropPrefab ? item.m_dropPrefab.name : null) && look.Variant == item.m_variant) item.m_customData.Remove(DataKey);
            else item.m_customData[DataKey] = look.Prefab + ":" + look.Variant;
        }
        internal static void Clear(ItemDrop.ItemData item) => item?.m_customData?.Remove(DataKey);

        internal static GameObject Prefab(string name) => ObjectDB.instance && !string.IsNullOrEmpty(name) ? ObjectDB.instance.GetItemPrefab(name) : null;
        internal static ItemDrop.ItemData PrefabItem(string name) { var p = Prefab(name); var d = p ? p.GetComponent<ItemDrop>() : null; return d ? d.m_itemData : null; }
        internal static string LookName(ItemDrop.ItemData item)
        {
            if (!TryGet(item, out var look)) return null;
            var data = PrefabItem(look.Prefab); return data != null ? data.m_shared.m_name : look.Prefab;
        }

        // ---------------------------------------------------------------- visuals
        // While a humanoid writes its visual equipment, the hand and back items are swapped for their look.
        [ThreadStatic] static Humanoid current;
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.SetupVisEquipment))]
        static class Scope
        {
            static void Prefix(Humanoid __instance) => current = __instance;
            static void Finalizer() => current = null;
        }
        static void Swap(ItemDrop.ItemData item, ref int itemHash, ref int variant)
        {
            if (item == null || !TryGet(item, out var look)) return;
            itemHash = look.Prefab.GetStableHashCode(); variant = look.Variant;
        }
        [HarmonyPatch(typeof(VisEquipment), nameof(VisEquipment.SetLeftItem), typeof(int), typeof(int), typeof(int))]
        static class Left { static void Prefix(ref int itemHash, ref int variant) { if (current) Swap(current.m_leftItem, ref itemHash, ref variant); } }
        [HarmonyPatch(typeof(VisEquipment), nameof(VisEquipment.SetLeftBackItem), typeof(int), typeof(int), typeof(int))]
        static class LeftBack { static void Prefix(ref int itemHash, ref int variant) { if (current) Swap(current.m_hiddenLeftItem, ref itemHash, ref variant); } }
        [HarmonyPatch(typeof(VisEquipment), nameof(VisEquipment.SetRightItem), typeof(int), typeof(int))]
        static class Right { static void Prefix(ref int itemHash) { int v = 0; if (current) Swap(current.m_rightItem, ref itemHash, ref v); } }
        [HarmonyPatch(typeof(VisEquipment), nameof(VisEquipment.SetRightBackItem), typeof(int), typeof(int))]
        static class RightBack { static void Prefix(ref int itemHash) { int v = 0; if (current) Swap(current.m_hiddenRightItem, ref itemHash, ref v); } }

        // The icon follows the look (inventory, hotbar, tooltips), the name stays the item's.
        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetIcon))]
        static class Icon
        {
            static void Postfix(ItemDrop.ItemData __instance, ref Sprite __result)
            {
                if (__instance.m_customData == null || __instance.m_customData.Count == 0 || !TryGet(__instance, out var look)) return;
                var data = PrefabItem(look.Prefab); var icons = data?.m_shared.m_icons;
                if (icons == null || icons.Length == 0) return;
                __result = icons[Mathf.Clamp(look.Variant, 0, icons.Length - 1)];
            }
        }
        // Tooltip: one line under the description naming the look worn.
        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool))]
        static class Tooltip
        {
            static void Postfix(ItemDrop.ItemData item, bool appending, ref string __result)
            {
                if (appending) return;
                var name = LookName(item); if (name == null) return;
                __result = "<color=#7CC0FF>$overhaul_transmog_look_line " + name + "</color>\n" + __result;
            }
        }
    }
}
