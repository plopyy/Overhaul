using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Magic
{
    // Magic power and spell slots of the Overhaul staffs (StaffModels). Magic power is the staff's damage stat in place
    // of its blunt, fire... damage, held in the item's generic damage (m_damage) so it goes through the usual damage
    // computation: upgrade levels (m_damagesPerLevel), rarity bonus and the MagicPower effect (Effects.cfg, flat: gear
    // enchantments, class skills, buffs...) added with the other damage effects (ItemRarity). Magic power has no element
    // of its own: when a spell (SpellSystem) hits, its percent of it becomes its element's damage (fireball 120 % with 100
    // power = 120 fire damage). The 4 spell slots hold the ids of the grimoire spells installed in the staff, in the
    // item's own data, so each staff keeps its spells.
    internal static class StaffMagic
    {
        internal enum Slot { NormalSpell1, NormalSpell2, SpecialSpell, ShieldSpell }
        internal const int SlotCount = 4;
        static string Key(Slot slot) => "overhaul_" + slot;

        internal static StaffModels.Staff Of(ItemDrop.ItemData item) =>
            item?.m_dropPrefab != null && StaffModels.ByPrefab.TryGetValue(item.m_dropPrefab.name, out var staff) ? staff : null;
        internal static bool IsStaff(ItemDrop.ItemData item) => Of(item) != null;

        // Turns the magic power of a hit into the spell's element (grimoire elements: fire, ice, lightning), worth percent of it.
        internal static void Convert(ref HitData.DamageTypes damage, string element, float percent)
        {
            float value = damage.m_damage * percent / 100f; damage.m_damage = 0;
            switch (element)
            {
                case "ice": damage.m_frost += value; break;
                case "lightning": damage.m_lightning += value; break;
                default: damage.m_fire += value; break;
            }
        }

        internal static string Spell(ItemDrop.ItemData item, Slot slot) =>
            item?.m_customData != null && item.m_customData.TryGetValue(Key(slot), out var id) && id.Length > 0 ? id : null;
        internal static void SetSpell(ItemDrop.ItemData item, Slot slot, string id)
        {
            if (item?.m_customData == null) return;
            if (string.IsNullOrEmpty(id)) item.m_customData.Remove(Key(slot));
            else item.m_customData[Key(slot)] = id;
        }

        // The staff's projectile (and its area) carries the hit's damage, skill factor included: the spell being cast
        // (SpellSystem) sets the projectile's speed and gravity and turns its magic power into the spell's element there (a second Setup from the same hit finds no
        // magic power left).
        [HarmonyPatch]
        static class SpellHit
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(Projectile), nameof(Projectile.Setup));
                yield return AccessTools.Method(typeof(Aoe), nameof(Aoe.Setup));
            }
            static void Prefix(object __instance, Character owner, ref Vector3 velocity, HitData hitData, ItemDrop.ItemData item)
            {
                if (!IsStaff(item)) return;
                SpellSystem.OnHitSetup(owner, hitData);
                if (__instance is Projectile projectile) SpellSystem.OnProjectileSetup(owner, projectile, ref velocity);
            }
        }

        // A projectile's impact objects (spawn on hit, e.g. the Lightning Strike's staff_thunderblood_aoe) are set up by the
        // game without the hit's damage or weapon, and that impact deals fixed damage of its own (a 0.5 m rod of 400, an
        // area of 80). For a spell's projectile or a Zeus's Wrath bolt, each area of the impact takes the projectile's own
        // damage (the spell's share of the magic power, in its element), the spell's status effect and its caster, and
        // spares the caster's allies; the rod deals none.
        static Projectile spawning;
        [HarmonyPatch(typeof(Projectile), "SpawnOnHit")]
        static class ImpactSpawn
        {
            static void Prefix(Projectile __instance) => spawning = __instance;
            static void Finalizer() => spawning = null;
        }
        [HarmonyPatch(typeof(Aoe), "Awake")]
        static class ImpactArea
        {
            static void Postfix(Aoe __instance)
            {
                var source = spawning;
                if (!source || !(IsStaff(source.m_weapon) || Rarity.MythicEffects.RainStrike)) return;
                __instance.m_useAttackSettings = false;
                __instance.m_damage = __instance.m_radius < 1f ? new HitData.DamageTypes() : source.m_damage;
                __instance.m_owner = source.m_owner; __instance.m_hitFriendly = false;
                // A Zeus's Wrath bolt also hits trees and rocks, at tool tier 4.
                if (Rarity.MythicEffects.RainStrike) { __instance.m_hitProps = true; __instance.m_toolTier = Mathf.Max(__instance.m_toolTier, 4); }
                var spell = IsStaff(source.m_weapon) ? SpellSystem.CastBy(source.m_owner) : null;
                __instance.m_statusEffectHash = spell != null && spell.HasEffect ? spell.EffectName.GetStableHashCode() : 0;
            }
        }

        // Magic power only serves spells: what is left of it in a player's hit (a weapon struck or shot without a
        // spell) is dropped, never dealt as raw damage. Every damageable object (creatures, buildings, trees, rocks...).
        [HarmonyPatch]
        static class NoSpellHit
        {
            static IEnumerable<MethodBase> TargetMethods() =>
                typeof(IDestructible).Assembly.GetTypes()
                    .Where(t => typeof(IDestructible).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                    .Select(t => AccessTools.Method(t, "Damage", new[] { typeof(HitData) }))
                    .Where(m => m != null && m.DeclaringType != null && !m.IsAbstract).Distinct();
            static void Prefix(HitData hit)
            {
                if (hit != null && hit.m_damage.m_damage != 0 && hit.GetAttacker() is Player) hit.m_damage.m_damage = 0;
            }
        }
    }
}
