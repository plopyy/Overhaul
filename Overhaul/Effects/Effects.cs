using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Effects
{
    // The mod's effect registry. Effects.Get(character, id) returns the total of an effect, summed from every
    // source (equipment enchantments today; buffs, class skills... later) and kept within its Effects.cfg range.
    // The local player's totals are rebuilt from scratch, never added to or removed from, whenever something
    // changes (equipment, configuration) and at least once a second, so a missed event cannot leave a value behind.
    // They are also published in the character's ZDO, where other machines read them (loot and harvest bonuses
    // are rolled by the owner of the creature or object, not by the player).
    internal static class Effects
    {
        // A source adds its values for the player to the totals.
        internal delegate void Source(Player player, Dictionary<string, float> totals);
        private static readonly List<Source> Sources = new List<Source>();
        internal static void Register(Source source) => Sources.Add(source);

        // Effects that have code. Effects.cfg may list others: they are read but do nothing yet.
        private static readonly HashSet<string> Implemented = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "HealthMax", "HealthRegen", "EitrMax", "EitrRegen", "EitrCost", "FoodDuration",
            "FireDmg", "FrostDmg", "LightningDmg", "PoisonDmg", "SpiritDmg",
            "AttackSpeed", "MoveSpeed", "FallDmg", "FallSpeed", "ProjectileCount",
            "ResistFire", "ResistFrost", "ResistLightning", "ResistPoison", "ResistSpirit", "ResistBlunt", "ResistSlash", "ResistPierce",
            "ParryBonus", "BackstabBonus", "Knockback", "LifeSteal", "DamageReflect", "HarvestBonus", "CarryWeight",
            "CritChance", "LootBonus", "Indestructible", "ExplosiveProjectile", "CastSpeed",
            "PhysicDmg", "Armor", "MiningDmg", "ChoppingDmg",
        };
        internal static bool Known(string id) => Implemented.Contains(id);

        // Effects of the item carrying them: damage and armour raise the item's own stats (shown in its tooltip),
        // Indestructible and ExplosiveProjectile act on that item only. Equipment does not add them to the player's totals.
        internal static readonly HashSet<string> ItemEffects = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "FireDmg", "FrostDmg", "LightningDmg", "PoisonDmg", "SpiritDmg", "PhysicDmg", "Armor", "MiningDmg", "ChoppingDmg",
            "Indestructible", "ExplosiveProjectile",
        };

        private const string ZdoPrefix = "overhaul_fx_";
        private const float RefreshSeconds = 1f;
        private static readonly Dictionary<string, float> Totals = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> Published = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static Player owner;
        private static bool dirty = true;
        private static float nextRefresh;

        internal static void Invalidate() => dirty = true;

        internal static float Get(Character character, string id)
        {
            if (!(character is Player player)) return 0;
            if (player == Player.m_localPlayer)
            {
                Refresh(player);
                return Totals.TryGetValue(id, out float value) ? value : 0;
            }
            ZDO zdo = player.m_nview ? player.m_nview.GetZDO() : null;
            return zdo != null ? zdo.GetFloat(ZdoPrefix + id, 0) : 0;
        }

        private static void Refresh(Player player)
        {
            if (!dirty && owner == player && Time.time < nextRefresh) return;
            dirty = false; nextRefresh = Time.time + RefreshSeconds;
            if (owner != player) { owner = player; Published.Clear(); }
            var raw = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in Sources)
            {
                try { source(player, raw); }
                catch (Exception e) { Utility.Log.LogWarning("Effect source failed: " + e.Message); }
            }
            Totals.Clear();
            foreach (var pair in raw)
                if (EffectConfig.Current.Effects.TryGetValue(pair.Key, out var effect))
                    Totals[effect.Id] = Mathf.Clamp(pair.Value, effect.Min, effect.Max);
            Publish(player);
        }

        private static void Publish(Player player)
        {
            ZDO zdo = player.m_nview && player.m_nview.IsValid() && player.m_nview.IsOwner() ? player.m_nview.GetZDO() : null;
            if (zdo == null) return;
            foreach (var pair in Totals)
                if (!Published.TryGetValue(pair.Key, out float sent) || sent != pair.Value) { zdo.Set(ZdoPrefix + pair.Key, pair.Value); Published[pair.Key] = pair.Value; }
            foreach (string id in new List<string>(Published.Keys))
                if (!Totals.ContainsKey(id)) { zdo.Set(ZdoPrefix + id, 0f); Published.Remove(id); }
        }

        internal static void Add(Dictionary<string, float> totals, string id, float value)
        {
            if (string.IsNullOrEmpty(id) || value == 0) return;
            totals[id] = (totals.TryGetValue(id, out float current) ? current : 0) + value;
        }

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem))]
        private static class Equip { private static void Postfix(Humanoid __instance) { if (__instance == Player.m_localPlayer) Invalidate(); } }

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UnequipItem))]
        private static class Unequip { private static void Postfix(Humanoid __instance) { if (__instance == Player.m_localPlayer) Invalidate(); } }

        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class Spawned { private static void Postfix() => Invalidate(); }
    }
}
