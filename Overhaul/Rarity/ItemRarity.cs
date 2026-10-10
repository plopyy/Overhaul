using System.Collections.Generic;
using System.Linq;
using AugaUnity;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Overhaul.Rarity
{
    // An item's rarity, stored in its custom data (saved with the item and synchronised in multiplayer),
    // or given by the catalyst list for catalyst items. Rarity raises the item's base stats (damage, armour,
    // block, upgrades included) by its BaseStatBonus %, and rolls its enchantment lines (see Enchantments).
    internal static class ItemRarity
    {
        internal const string RarityKey = "overhaul_rarity", EnchantKey = "overhaul_enchants";
        internal const string EmptyEnchant = "None";

        internal static RarityDef Of(ItemDrop.ItemData item)
        {
            if (item == null) return null;
            var data = RarityConfig.Current;
            if (item.m_customData != null && item.m_customData.TryGetValue(RarityKey, out string id)) return data.Rarity(id);
            if (IsCatalyst(item, out string catalyst)) return data.Rarity(catalyst);
            // Weapons, armour, shields, accessories and trinkets are at least of the first (weakest) rarity.
            return Gear(item) ? data.Rarities.FirstOrDefault() : null;
        }

        internal static bool IsCatalyst(ItemDrop.ItemData item, out string rarity)
        {
            rarity = null;
            string prefab = item?.m_dropPrefab ? item.m_dropPrefab.name : null;
            return prefab != null && RarityConfig.Current.Catalysts.TryGetValue(prefab, out rarity);
        }

        private static bool Gear(ItemDrop.ItemData item)
        {
            switch (item.m_shared?.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Bow:
                case ItemDrop.ItemData.ItemType.Shield:
                case ItemDrop.ItemData.ItemType.Helmet:
                case ItemDrop.ItemData.ItemType.Chest:
                case ItemDrop.ItemData.ItemType.Legs:
                case ItemDrop.ItemData.ItemType.Shoulder:
                case ItemDrop.ItemData.ItemType.Utility:
                case ItemDrop.ItemData.ItemType.Trinket:
                    return true;
                default:
                    return false;
            }
        }

        // Slot colour: the Overhaul rarity, else Epic Loot's when it is installed.
        internal static Color? ColorOf(ItemDrop.ItemData item) => Of(item)?.Color ?? EpicLootVisuals.RarityOf(item);

        // Every colour of a multicolour rarity (drawn as a gradient), or null.
        internal static Color[] GradientOf(ItemDrop.ItemData item) { var rarity = Of(item); return rarity != null && rarity.Colors.Length > 1 ? rarity.Gradient : null; }

        internal static string[] Enchants(ItemDrop.ItemData item) =>
            item?.m_customData != null && item.m_customData.TryGetValue(EnchantKey, out string value) && value.Length > 0 ? value.Split(',') : new string[0];

        internal static void Assign(ItemDrop.ItemData item, RarityDef rarity)
        {
            if (item == null || rarity == null) return;
            item.m_customData[RarityKey] = rarity.Id;
            if (rarity.EnchantCount > 0) item.m_customData[EnchantKey] = string.Join(",", Enchantments.Roll(rarity));
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
                drop.m_itemData.m_durability = drop.m_itemData.GetMaxDurability();
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
        private static class Armor { private static void Postfix(ItemDrop.ItemData __instance, ref float __result) => __result = Raise(__result, __instance); }

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetBaseBlockPower), typeof(int))]
        private static class Block { private static void Postfix(ItemDrop.ItemData __instance, ref float __result) => __result = Raise(__result, __instance); }

        // Raised values are kept to one decimal, so tooltips do not show float noise such as 7,349999.
        private static float Raise(float value, ItemDrop.ItemData item)
        {
            float multiplier = Multiplier(item);
            return multiplier == 1f ? value : Mathf.Round(value * multiplier * 10f) / 10f;
        }

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
        internal static void Initialize()
        {
            ComplexTooltip.OnComplexTooltipGeneratedForItem += Tooltip;
            Enchantments.Initialize();
        }

        private static void Tooltip(ComplexTooltip tooltip, ItemDrop.ItemData item)
        {
            var rarity = Of(item);
            // The tooltip is reused for every item: restore the default colours first.
            var tint = tooltip.GetComponent<TooltipRarityTint>() ?? tooltip.gameObject.AddComponent<TooltipRarityTint>();
            tint.Apply(tooltip, rarity);
            if (rarity == null) return;
            // A catalyst is shown as such instead of a material, without enchantment lines or catalyst slots.
            bool catalyst = IsCatalyst(item, out _);
            tooltip.SetSubtitle(rarity.Paint(rarity.Name) + "\n" + (catalyst ? Localization.instance.Localize("$overhaul_rarity_catalyst") : tooltip.GenerateItemSubtext(item)));
            var rows = new List<TooltipRow>();
            if (catalyst) { tint.Texts(tooltip, rarity); return; }
            // Enchantment lines in the rarity colour, with their value for the item's biome; an empty line
            // (or one whose enchantment or effect left the files) shows as "Empty bonus +0".
            int biome = Enchantments.Biome(item);
            foreach (string id in Enchants(item))
            {
                EnchantDef enchant = null; Effects.EffectDef effect = null;
                bool filled = id != EmptyEnchant && RarityConfig.Current.Enchants.TryGetValue(id, out enchant)
                    && Effects.EffectConfig.Current.Effects.TryGetValue(enchant.Effect ?? "", out effect);
                rows.Add(filled
                    ? new TooltipRow(rarity.Paint(enchant.Name), rarity.Paint(effect.Format(enchant.Value(biome))))
                    : new TooltipRow(rarity.Paint(Localization.instance.Localize("$overhaul_rarity_empty_enchant")), rarity.Paint("+0")));
            }
            if (rarity.CatalystSlots > 0) rows.Add(new TooltipRow(Localization.instance.Localize("$overhaul_rarity_catalysts") + " (0/" + rarity.CatalystSlots + ")"));
            for (int i = 0; i < rarity.CatalystSlots; i++)
                rows.Add(new TooltipRow("  <color=#808080>◊ " + Localization.instance.Localize("$overhaul_rarity_empty_catalyst") + "</color>"));
            if (rows.Count > 0) TooltipRowAligner.Add(tooltip, rows);
            tint.Texts(tooltip, rarity);
        }
    }

    // Item name and the top and bottom dividers of a tooltip in the rarity colour (a 45° gradient for a
    // multicolour rarity); the default colours come back for an item without rarity.
    internal sealed class TooltipRarityTint : MonoBehaviour
    {
        private Color topic;
        private Image[] dividers;
        private Color[] originals;
        private RectTransform[] roots;

        public void Apply(ComplexTooltip tooltip, RarityDef rarity)
        {
            if (dividers == null)
            {
                topic = tooltip.Topic.color;
                dividers = new[] { tooltip.NormalDivider, tooltip.BottomDivider }.Where(d => d).SelectMany(d => d.GetComponentsInChildren<Image>(true)).ToArray();
                roots = dividers.Select(d => (RectTransform)(tooltip.NormalDivider && d.transform.IsChildOf(tooltip.NormalDivider.transform) ? tooltip.NormalDivider.transform : tooltip.BottomDivider.transform)).ToArray();
                originals = dividers.Select(d => d.color).ToArray();
            }
            Color[] colors = rarity == null ? null : rarity.Gradient;
            tooltip.Topic.enableVertexGradient = false;
            // A multicolour name is painted letter by letter; a single colour tints the whole text.
            tooltip.Topic.color = topic;
            if (rarity != null) tooltip.Topic.text = rarity.Paint(System.Text.RegularExpressions.Regex.Replace(tooltip.Topic.text, "<[^>]*>", ""));
            for (int i = 0; i < dividers.Length; i++)
            {
                if (!dividers[i]) continue;
                var gradient = dividers[i].GetComponent<ItemSlotStyle.SlotGradient>();
                if (colors != null && colors.Length > 1)
                {
                    if (!gradient) gradient = dividers[i].gameObject.AddComponent<ItemSlotStyle.SlotGradient>();
                    // One gradient over the whole divider, not one per stroke.
                    gradient.Colors = colors; gradient.Reference = roots[i]; gradient.enabled = true;
                    dividers[i].color = new Color(1, 1, 1, originals[i].a);
                }
                else
                {
                    if (gradient) gradient.enabled = false;
                    dividers[i].color = rarity != null ? new Color(rarity.Color.r, rarity.Color.g, rarity.Color.b, originals[i].a) : originals[i];
                }
                dividers[i].SetVerticesDirty();
            }
        }

        // Texts painted with the gradient mark (name, rarity, enchantment lines) get the 45° gradient; every text
        // of the tooltip is watched since the rows are created on the fly.
        public void Texts(ComplexTooltip tooltip, RarityDef rarity)
        {
            Color[] colors = rarity != null && rarity.Colors.Length > 1 ? rarity.Gradient : null;
            foreach (var text in tooltip.GetComponentsInChildren<TMPro.TMP_Text>(true))
            {
                var gradient = text.GetComponent<TextGradient>();
                if (colors == null) { if (gradient) gradient.Colors = null; continue; }
                if (!gradient) gradient = text.gameObject.AddComponent<TextGradient>();
                gradient.Colors = colors;
                text.SetVerticesDirty();
            }
        }
    }

    // Replaces the characters written in the gradient mark colour with one 45° gradient spanning them all.
    internal sealed class TextGradient : MonoBehaviour
    {
        private static readonly Color32 Mark = new Color32(1, 2, 3, 255);
        public Color[] Colors;
        private TMPro.TMP_Text text;

        private void OnEnable() { text = GetComponent<TMPro.TMP_Text>(); if (text) text.OnPreRenderText += Paint; }
        private void OnDisable() { if (text) text.OnPreRenderText -= Paint; }

        private static bool Marked(Color32 c) => c.r == Mark.r && c.g == Mark.g && c.b == Mark.b;

        private void Paint(TMPro.TMP_TextInfo info)
        {
            if (Colors == null || Colors.Length < 2) return;
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            bool any = false;
            for (int i = 0; i < info.characterCount; i++)
            {
                var c = info.characterInfo[i];
                if (!c.isVisible || !Marked(c.color)) continue;
                any = true;
                minX = Mathf.Min(minX, c.bottomLeft.x); maxX = Mathf.Max(maxX, c.topRight.x);
                minY = Mathf.Min(minY, c.bottomLeft.y); maxY = Mathf.Max(maxY, c.topRight.y);
            }
            if (!any) return;
            var area = Rect.MinMaxRect(minX, minY, maxX, maxY);
            for (int i = 0; i < info.characterCount; i++)
            {
                var c = info.characterInfo[i];
                if (!c.isVisible || !Marked(c.color)) continue;
                var mesh = info.meshInfo[c.materialReferenceIndex];
                for (int v = 0; v < 4; v++)
                {
                    int index = c.vertexIndex + v;
                    Color32 color = ItemSlotStyle.Diagonal(Colors, area, mesh.vertices[index]);
                    color.a = mesh.colors32[index].a;
                    mesh.colors32[index] = color;
                }
            }
        }
    }
}
