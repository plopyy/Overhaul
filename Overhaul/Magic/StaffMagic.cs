using HarmonyLib;
using Overhaul.Rarity;
using UnityEngine;

namespace Overhaul.Magic
{
    // Magic power and spell slots of the Overhaul staffs (StaffModels). Magic power is the staff's base stat in place of
    // its blunt, fire... damage: it rises with the staff's tier and upgrade level, the rarity bonus raises it like the
    // other gear's damage, then the effects of Effects.cfg add to it: MagicPower (flat, enchantment of the staff) and
    // MagicPowerBonus (percent, the player's spells, buffs, class skills...). Magic power has no element of its own:
    // a spell deals its percent of it as its element's damage (fireball 120 % with 100 power = 120 fire damage). Until
    // the grimoire spells are cast, the spell is the base staff's at 100 % (staff of embers: fire, frost staff: ice). The 4 spell slots hold the ids of the grimoire spells installed in the staff, in
    // the item's own data, so each staff keeps its spells.
    internal static class StaffMagic
    {
        internal enum Slot { NormalSpell1, NormalSpell2, SpecialSpell, ShieldSpell }
        internal const int SlotCount = 4;
        static string Key(Slot slot) => "overhaul_" + slot;

        // Element of the spell the staff casts for now: its base staff's.
        static string CastElement(StaffModels.Staff staff) => staff.Base == "StaffIceShards" ? "ice" : "fire";
        // Damage dealt by a spell of the element (grimoire elements: fire, ice, lightning) worth percent of the power.
        internal static HitData.DamageTypes SpellDamage(string element, float percent, float power)
        {
            var damage = new HitData.DamageTypes(); float value = power * percent / 100f;
            switch (element)
            {
                case "ice": damage.m_frost = value; break;
                case "lightning": damage.m_lightning = value; break;
                default: damage.m_fire = value; break;
            }
            return damage;
        }

        internal static StaffModels.Staff Of(ItemDrop.ItemData item) =>
            item?.m_dropPrefab != null && StaffModels.ByPrefab.TryGetValue(item.m_dropPrefab.name, out var staff) ? staff : null;
        internal static bool IsStaff(ItemDrop.ItemData item) => Of(item) != null;

        // Magic power of the staff for the local player (its wielder): base by tier and upgrade level, rarity bonus, the
        // staff's MagicPower and the player's MagicPowerBonus; null for any other item (tooltip hook).
        internal static float? Power(ItemDrop.ItemData item, int quality)
        {
            var staff = Of(item);
            if (staff == null) return null;
            if (quality < 0) quality = item.m_quality;
            float power = (staff.Power + Mathf.Max(0, quality - 1) * staff.PowerPerLevel) * ItemRarity.Multiplier(item) + Enchantments.ItemValue(item, "MagicPower");
            power *= 1f + Effects.Effects.Get(Player.m_localPlayer, "MagicPowerBonus") / 100f;
            return Mathf.Round(Mathf.Max(0, power) * 10f) / 10f;
        }

        internal static string Spell(ItemDrop.ItemData item, Slot slot) =>
            item?.m_customData != null && item.m_customData.TryGetValue(Key(slot), out var id) && id.Length > 0 ? id : null;
        internal static void SetSpell(ItemDrop.ItemData item, Slot slot, string id)
        {
            if (item?.m_customData == null) return;
            if (string.IsNullOrEmpty(id)) item.m_customData.Remove(Key(slot));
            else item.m_customData[Key(slot)] = id;
        }

        // The staff's damage is the spell's share of its magic power, in the spell's element. The rarity bonus is already
        // in the power, so ItemRarity's multiplier (run after) is taken back out; the staff's other enchantments
        // (FireDmg...) still add to it after.
        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetDamage), typeof(int), typeof(float))]
        static class Damage
        {
            [HarmonyPriority(Priority.First)]
            static void Postfix(ItemDrop.ItemData __instance, int quality, ref HitData.DamageTypes __result)
            {
                var staff = Of(__instance);
                if (staff == null) return;
                __result.Add(SpellDamage(CastElement(staff), 100f, Power(__instance, quality).Value / ItemRarity.Multiplier(__instance)));
            }
        }
    }
}
