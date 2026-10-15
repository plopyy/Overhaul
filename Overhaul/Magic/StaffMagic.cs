using HarmonyLib;
using Overhaul.Rarity;
using UnityEngine;

namespace Overhaul.Magic
{
    // Magic power and spell slots of the Overhaul staffs (StaffModels). Magic power is the staff's base stat in place of
    // its blunt, fire... damage: it rises with the staff's tier and upgrade level, and the rarity bonus raises it like
    // the other gear's damage. Magic power has no element of its own: the spell cast turns all of it into its
    // element's damage (100 power with a fire spell = 100 fire damage, with an ice spell = 100 frost damage). Until the
    // grimoire spells are cast, the spell is the base staff's (staff of embers: fire, frost staff: ice). The 4 spell slots hold the ids of the grimoire spells installed in the staff, in
    // the item's own data, so each staff keeps its spells.
    internal static class StaffMagic
    {
        internal enum Slot { NormalSpell1, NormalSpell2, SpecialSpell, ShieldSpell }
        internal const int SlotCount = 4;
        static string Key(Slot slot) => "overhaul_" + slot;

        // Element of the spell the staff casts for now: its base staff's.
        static string CastElement(StaffModels.Staff staff) => staff.Base == "StaffIceShards" ? "ice" : "fire";
        // Damage dealt by a spell of the element (grimoire elements: fire, ice, lightning): the whole power in that type.
        internal static HitData.DamageTypes SpellDamage(string element, float power)
        {
            var damage = new HitData.DamageTypes();
            switch (element)
            {
                case "ice": damage.m_frost = power; break;
                case "lightning": damage.m_lightning = power; break;
                default: damage.m_fire = power; break;
            }
            return damage;
        }

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

        // The staff's damage is its magic power (before the rarity bonus, which ItemRarity applies after) in the element of
        // the spell cast; enchantments still add to it after.
        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetDamage), typeof(int), typeof(float))]
        static class Damage
        {
            [HarmonyPriority(Priority.First)]
            static void Postfix(ItemDrop.ItemData __instance, int quality, ref HitData.DamageTypes __result)
            {
                var staff = Of(__instance);
                if (staff == null) return;
                __result.Add(SpellDamage(CastElement(staff), BasePower(staff, quality)));
            }
        }
    }
}
