using System.Collections.Generic;
using HarmonyLib;
using Overhaul.Rarity;
using UnityEngine;

namespace Overhaul.Magic
{
    // Magic power and spell slots of the Overhaul staffs (StaffModels). Magic power is the staff's base stat in place of
    // its blunt, fire... damage: it rises with the staff's tier and upgrade level, and the rarity bonus raises it like
    // the other gear's damage. Until the grimoire spells are cast, the base staff's attack deals the magic power split
    // like the base staff's damage. The 4 spell slots hold the ids of the grimoire spells installed in the staff, in
    // the item's own data, so each staff keeps its spells.
    internal static class StaffMagic
    {
        internal enum Slot { NormalSpell1, NormalSpell2, SpecialSpell, ShieldSpell }
        internal const int SlotCount = 4;
        static string Key(Slot slot) => "overhaul_" + slot;

        // Base staff's damage at quality 1, by Overhaul staff prefab: the share of each damage type.
        internal static readonly Dictionary<string, HitData.DamageTypes> Shares = new Dictionary<string, HitData.DamageTypes>();

        internal static StaffModels.Staff Of(ItemDrop.ItemData item) =>
            item?.m_dropPrefab != null && StaffModels.ByPrefab.TryGetValue(item.m_dropPrefab.name, out var staff) ? staff : null;
        internal static bool IsStaff(ItemDrop.ItemData item) => Of(item) != null;

        static float BasePower(StaffModels.Staff staff, int quality) => staff.Power + Mathf.Max(0, quality - 1) * staff.PowerPerLevel;
        // Magic power shown and used, rarity bonus included; null for any other item (tooltip hook).
        internal static float? Power(ItemDrop.ItemData item, int quality)
        {
            var staff = Of(item);
            if (staff == null) return null;
            if (quality < 0) quality = item.m_quality;
            return Mathf.Round(BasePower(staff, quality) * ItemRarity.Multiplier(item) * 10f) / 10f;
        }

        internal static string Spell(ItemDrop.ItemData item, Slot slot) =>
            item?.m_customData != null && item.m_customData.TryGetValue(Key(slot), out var id) && id.Length > 0 ? id : null;
        internal static void SetSpell(ItemDrop.ItemData item, Slot slot, string id)
        {
            if (item?.m_customData == null) return;
            if (string.IsNullOrEmpty(id)) item.m_customData.Remove(Key(slot));
            else item.m_customData[Key(slot)] = id;
        }

        // The staff's damage is its magic power (before the rarity bonus, which ItemRarity applies after), split like the
        // base staff's damage; enchantments still add to it after.
        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetDamage), typeof(int), typeof(float))]
        static class Damage
        {
            [HarmonyPriority(Priority.First)]
            static void Postfix(ItemDrop.ItemData __instance, int quality, ref HitData.DamageTypes __result)
            {
                var staff = Of(__instance);
                if (staff == null || !Shares.TryGetValue(staff.Prefab, out var share)) return;
                float total = share.GetTotalDamage();
                if (total <= 0) return;
                var damage = share; damage.Modify(BasePower(staff, quality) / total);
                __result.Add(damage);
            }
        }
    }
}
