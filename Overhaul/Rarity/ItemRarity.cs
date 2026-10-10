using System.Collections.Generic;
using System.Linq;
using AugaUnity;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Rarity
{
    // An item's rarity, stored in its custom data (saved with the item and synchronised in multiplayer),
    // or given by the catalyst list for catalyst items. Rarity raises the item's base stats (damage, armour,
    // block, upgrades included) by its BaseStatBonus %, and gives it empty enchantment lines to fill later.
    internal static class ItemRarity
    {
        internal const string RarityKey = "overhaul_rarity", EnchantKey = "overhaul_enchants";
        internal const string EmptyEnchant = "None";

        internal static RarityDef Of(ItemDrop.ItemData item)
        {
            if (item == null) return null;
            var data = RarityConfig.Current;
            if (item.m_customData != null && item.m_customData.TryGetValue(RarityKey, out string id)) return data.Rarity(id);
            string prefab = item.m_dropPrefab ? item.m_dropPrefab.name : null;
            return prefab != null && data.Catalysts.TryGetValue(prefab, out string catalyst) ? data.Rarity(catalyst) : null;
        }

        // Slot colour: the Overhaul rarity, else Epic Loot's when it is installed.
        internal static Color? ColorOf(ItemDrop.ItemData item) => Of(item)?.Color ?? EpicLootVisuals.RarityOf(item);

        // Every colour of a multicolour rarity (drawn as a gradient), or null.
        internal static Color[] GradientOf(ItemDrop.ItemData item) { var rarity = Of(item); return rarity != null && rarity.Colors.Length > 1 ? rarity.Colors : null; }

        internal static string[] Enchants(ItemDrop.ItemData item) =>
            item?.m_customData != null && item.m_customData.TryGetValue(EnchantKey, out string value) && value.Length > 0 ? value.Split(',') : new string[0];

        internal static void Assign(ItemDrop.ItemData item, RarityDef rarity)
        {
            if (item == null || rarity == null) return;
            item.m_customData[RarityKey] = rarity.Id;
            if (rarity.EnchantCount > 0) item.m_customData[EnchantKey] = string.Join(",", Enumerable.Repeat(EmptyEnchant, rarity.EnchantCount));
            else item.m_customData.Remove(EnchantKey);
        }

        // Weighted pick among the rarities, in file order.
        internal static RarityDef Roll(float[] weights)
        {
            var rarities = RarityConfig.Current.Rarities;
            float total = 0;
            for (int i = 0; i < rarities.Count && i < weights.Length; i++) total += Mathf.Max(0, weights[i]);
            if (total <= 0) return rarities.FirstOrDefault();
            float roll = Random.Range(0f, total);
            for (int i = 0; i < rarities.Count && i < weights.Length; i++)
            {
                roll -= Mathf.Max(0, weights[i]);
                if (roll < 0) return rarities[i];
            }
            return rarities[Mathf.Min(weights.Length, rarities.Count) - 1];
        }

        // Spawns an item with the given rarity, as a loot drop.
        internal static ItemDrop Drop(string prefabName, RarityDef rarity, Vector3 position)
        {
            GameObject prefab = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
            if (!prefab) { Utility.Log.LogWarning("Rarity loot: unknown item " + prefabName); return null; }
            GameObject instance = Object.Instantiate(prefab, position + Random.insideUnitSphere * .5f, Quaternion.Euler(0, Random.Range(0, 360), 0));
            ItemDrop drop = instance.GetComponent<ItemDrop>();
            if (drop)
            {
                drop.m_itemData.m_worldLevel = (byte)Game.m_worldLevel;
                Assign(drop.m_itemData, rarity);
                drop.Save();
            }
            Rigidbody body = instance.GetComponent<Rigidbody>();
            if (body)
            {
                Vector3 push = Random.insideUnitSphere;
                if (push.y < 0) push.y = -push.y;
                body.AddForce(push * 5f, ForceMode.VelocityChange);
            }
            return drop;
        }

        private static float Multiplier(ItemDrop.ItemData item)
        {
            var rarity = Of(item);
            return rarity == null || rarity.BaseStatBonus == 0 ? 1f : 1f + rarity.BaseStatBonus / 100f;
        }

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetDamage), typeof(int), typeof(float))]
        private static class Damage
        {
            private static void Postfix(ItemDrop.ItemData __instance, ref HitData.DamageTypes __result)
            {
                float multiplier = Multiplier(__instance);
                if (multiplier != 1f) __result.Modify(multiplier);
            }
        }

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetArmor), typeof(int), typeof(float))]
        private static class Armor { private static void Postfix(ItemDrop.ItemData __instance, ref float __result) => __result *= Multiplier(__instance); }

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetBaseBlockPower), typeof(int))]
        private static class Block { private static void Postfix(ItemDrop.ItemData __instance, ref float __result) => __result *= Multiplier(__instance); }

        // Creature loot from the MobLoot sections: each loot list rolls its chance, then drops its quantity
        // of items picked in its list, each with a rarity rolled from the list's weights.
        internal static void CreatureLoot(string creature, Vector3 center)
        {
            var data = RarityConfig.Current;
            if (creature == null || !data.MobLoots.TryGetValue(creature, out var lists)) return;
            foreach (string name in lists)
            {
                if (!data.LootLists.TryGetValue(name, out var list) || list.Items.Length == 0) continue;
                if (Random.Range(0f, 100f) >= list.Chance) continue;
                for (int i = 0; i < list.Quantity; i++)
                    Drop(list.Items[Random.Range(0, list.Items.Length)], Roll(list.Weights), center);
            }
        }

        private const string RagdollCreature = "overhaul_rarity_creature";

        // Creatures without a corpse drop their loot at death.
        [HarmonyPatch(typeof(CharacterDrop), "OnDeath")]
        private static class DeathLoot
        {
            private static void Postfix(CharacterDrop __instance)
            {
                if (__instance.m_dropsEnabled && __instance.m_character)
                    CreatureLoot(Utils.GetPrefabName(__instance.gameObject), __instance.m_character.GetCenterPoint());
            }
        }

        // Creatures with a corpse (trolls, bears...) drop their loot when the corpse vanishes, like the vanilla loot:
        // the corpse remembers which creature it was.
        [HarmonyPatch(typeof(Ragdoll), nameof(Ragdoll.Setup))]
        private static class CorpseRemember
        {
            private static void Postfix(Ragdoll __instance, CharacterDrop characterDrop)
            {
                if (characterDrop && __instance.m_dropItems && __instance.m_nview && __instance.m_nview.IsValid())
                    __instance.m_nview.GetZDO().Set(RagdollCreature, Utils.GetPrefabName(characterDrop.gameObject));
            }
        }

        [HarmonyPatch(typeof(Ragdoll), "SpawnLoot")]
        private static class CorpseLoot
        {
            private static void Postfix(Ragdoll __instance, Vector3 center)
            {
                if (!__instance.m_nview || !__instance.m_nview.IsValid()) return;
                string creature = __instance.m_nview.GetZDO().GetString(RagdollCreature, "");
                if (creature.Length > 0) CreatureLoot(creature, center + Vector3.up * .75f);
            }
        }

        // Ground aura of the item's rarity.
        [HarmonyPatch(typeof(ItemDrop), "Start")]
        private static class GroundAura
        {
            private static void Postfix(ItemDrop __instance)
            {
                if (ZNet.instance && ZNet.instance.IsDedicated()) return;
                if (!__instance || !__instance.m_nview || !__instance.m_nview.IsValid()) return;
                var rarity = Of(__instance.m_itemData);
                if (rarity != null && !string.IsNullOrEmpty(rarity.Aura)) LootAura.Spawn(rarity.Aura, __instance.transform.position, __instance.transform);
            }
        }

        // Tooltip: the rarity name in its colour above the item type, and the catalyst slots.
        internal static void Initialize() => ComplexTooltip.OnComplexTooltipGeneratedForItem += Tooltip;

        private static void Tooltip(ComplexTooltip tooltip, ItemDrop.ItemData item)
        {
            var rarity = Of(item);
            if (rarity == null) return;
            tooltip.SetSubtitle("<color=#" + ColorUtility.ToHtmlStringRGB(rarity.Color) + ">" + rarity.Name + "</color>\n" + tooltip.GenerateItemSubtext(item));
            var rows = new List<TooltipRow>();
            // Enchantment lines; an empty one shows as "Empty bonus +0" until the enchantment list exists.
            foreach (string enchant in Enchants(item))
                rows.Add(enchant == EmptyEnchant ? new TooltipRow(Localization.instance.Localize("$overhaul_rarity_empty_enchant"), "+0") : new TooltipRow(enchant));
            if (rarity.CatalystSlots > 0) rows.Add(new TooltipRow(Localization.instance.Localize("$overhaul_rarity_catalysts") + " (0/" + rarity.CatalystSlots + ")"));
            for (int i = 0; i < rarity.CatalystSlots; i++)
                rows.Add(new TooltipRow("  <color=#808080>◊ " + Localization.instance.Localize("$overhaul_rarity_empty_catalyst") + "</color>"));
            if (rows.Count > 0) TooltipRowAligner.Add(tooltip, rows);
        }
    }
}
