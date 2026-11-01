using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using AugaUnity;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Rarity
{
    // Weapons gain experience with each swing or shot that lands on a creature or dummy (axes on trees, pickaxes on what they mine too), shields with each hit blocked. Each level adds 0.5 % damage or block power;
    // from level 10 a glow grows every 10 levels. The experience lives in the item's custom data, so it follows the item.
    internal static class WeaponLevels
    {
        internal const int MaxLevel = 100;
        const string XpKey = "overhaul_weapon_xp";
        const float BonusPerLevel = .5f, FirstLevelXp = 20, XpGrowth = 1.04f;
        static readonly int RightGlow = "overhaul_glow_right".GetStableHashCode(), LeftGlow = "overhaul_glow_left".GetStableHashCode();
        static readonly int[] Totals = BuildTotals();
        static AssetBundle bundle;
        static GameObject barPrefab, glowPrefab;

        // Total experience needed to reach each level.
        static int[] BuildTotals()
        {
            var totals = new int[MaxLevel + 1];
            float need = FirstLevelXp, sum = 0;
            for (int level = 2; level <= MaxLevel; level++) { sum += Mathf.Round(need); totals[level] = (int)sum; need *= XpGrowth; }
            return totals;
        }
        internal static void Initialize() => ComplexTooltip.OnComplexTooltipGeneratedForItem += Tooltip;

        static bool Levels(ItemDrop.ItemData item) => item != null && item.m_dropPrefab && (IsShield(item) || item.IsWeapon());
        static bool IsShield(ItemDrop.ItemData item) => item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield;
        static int Xp(ItemDrop.ItemData item) =>
            item.m_customData.TryGetValue(XpKey, out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int xp) ? xp : 0;
        internal static int Level(ItemDrop.ItemData item)
        {
            if (!Levels(item)) return 1;
            int xp = Xp(item), level = 1;
            while (level < MaxLevel && xp >= Totals[level + 1]) level++;
            return level;
        }
        // Level 1 is an unused weapon: no bonus yet.
        static float BonusPercent(ItemDrop.ItemData item)
        {
            int level = Level(item);
            return level < 2 ? 0 : level * BonusPerLevel;
        }
        static int Tier(ItemDrop.ItemData item) => item == null ? 0 : Level(item) / 10;

        static void Gain(Player player, ItemDrop.ItemData item)
        {
            if (!Levels(item)) return;
            int before = Level(item);
            if (before >= MaxLevel) return;
            item.m_customData[XpKey] = (Xp(item) + 1).ToString(CultureInfo.InvariantCulture);
            int after = Level(item);
            if (after != before)
                player.Message(MessageHud.MessageType.TopLeft, string.Format(Localization.instance.Localize("$overhaul_weapon_level_up"), Localization.instance.Localize(item.m_shared.m_name), after));
        }

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetDamage), typeof(int), typeof(float))]
        private static class Damage
        {
            private static void Postfix(ItemDrop.ItemData __instance, ref HitData.DamageTypes __result)
            {
                float bonus = BonusPercent(__instance);
                if (bonus > 0) __result.Modify(1 + bonus / 100);
            }
        }
        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetBaseBlockPower), typeof(int))]
        private static class BlockPower
        {
            private static void Postfix(ItemDrop.ItemData __instance, ref float __result)
            {
                float bonus = BonusPercent(__instance);
                if (bonus > 0) __result = Mathf.Round(__result * (1 + bonus / 100) * 10) / 10;
            }
        }
        // One point per swing or shot that lands, however many targets or projectiles it has: Attack.Start arms it, the first valid hit uses it.
        static bool armed;
        static ItemDrop.ItemData swung;
        [HarmonyPatch(typeof(Attack), nameof(Attack.Start))]
        private static class Swing
        {
            private static void Postfix(Humanoid character, ItemDrop.ItemData weapon, bool __result)
            {
                if (__result && character == Player.m_localPlayer) { armed = true; swung = weapon; }
            }
        }
        static void Landed(HitData hit)
        {
            var player = Player.m_localPlayer;
            if (!armed || !player || hit.GetAttacker() != player) return;
            armed = false;
            Gain(player, swung);
        }
        // Creatures and training dummies, on the attacker's machine.
        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class Hit
        {
            private static void Prefix(Character __instance, HitData hit) { if (!__instance.IsPlayer()) Landed(hit); }
        }
        // Axes also train on trees, pickaxes on what they mine.
        [HarmonyPatch]
        private static class Harvest
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(TreeBase), nameof(TreeBase.Damage));
                yield return AccessTools.Method(typeof(TreeLog), nameof(TreeLog.Damage));
                yield return AccessTools.Method(typeof(MineRock), nameof(MineRock.Damage));
                yield return AccessTools.Method(typeof(MineRock5), nameof(MineRock5.Damage));
                yield return AccessTools.Method(typeof(Destructible), nameof(Destructible.Damage));
            }
            private static void Prefix(object __instance, HitData hit)
            {
                if (swung == null) return;
                var skill = swung.m_shared.m_skillType;
                var destructible = __instance as Destructible;
                bool tree = __instance is TreeBase || __instance is TreeLog || (destructible && destructible.GetDestructibleType() == DestructibleType.Tree);
                if (tree ? skill == Skills.SkillType.Axes : skill == Skills.SkillType.Pickaxes && hit.m_damage.m_pickaxe > 0) Landed(hit);
            }
        }
        [HarmonyPatch(typeof(Humanoid), "BlockAttack")]
        private static class Block
        {
            private static void Postfix(Humanoid __instance, bool __result)
            {
                if (__result && __instance == Player.m_localPlayer)
                {
                    var blocker = __instance.GetCurrentBlocker();
                    if (blocker != null && IsShield(blocker)) Gain(Player.m_localPlayer, blocker);
                }
            }
        }

        static void LoadAssets()
        {
            if (bundle) return;
            bundle = Utility.EmbeddedAssets.LoadBundle("Overhaul.Assets.overhaul_weaponlevel");
            if (!bundle) { Utility.Log.LogError("Weapon levels: overhaul_weaponlevel missing"); return; }
            barPrefab = bundle.LoadAsset<GameObject>("WeaponLevelBar");
            glowPrefab = bundle.LoadAsset<GameObject>("WeaponGlow");
        }

        static void Tooltip(ComplexTooltip tooltip, ItemDrop.ItemData item)
        {
            if (!Levels(item)) return;
            LoadAssets();
            int level = Level(item), xp = Xp(item);
            float percent = BonusPercent(item);
            string gain = percent <= 0 ? "" : "<align=right><color=#9be36b>" + string.Format(Text(IsShield(item) ? "$overhaul_weapon_block_bonus" : "$overhaul_weapon_damage_bonus"), percent.ToString("0.#")) + "</color>" + Range(item, percent) + "</align>";
            bool max = level >= MaxLevel;
            int from = Totals[level], to = max ? from : Totals[level + 1];
            float progress = max ? 1 : (xp - from) / (float)(to - from);
            var box = tooltip.AddTextBox(tooltip.LeftAlignedTextBoxPrefab);
            box.Text.text = "<line-height=0><color=#ffd479>" + string.Format(Text("$overhaul_weapon_level"), level, MaxLevel) + "</color>\n</line-height>" + gain + "\n \n" +
                (max ? Text("$overhaul_weapon_xp_max") : string.Format(Text("$overhaul_weapon_xp"), (xp - from).ToString("N0"), (to - from).ToString("N0")));
            // Right under the damage box, not below the enchantments.
            var first = box.transform.parent.GetComponentInChildren<TooltipTextBox>();
            if (first && first != box) box.transform.SetSiblingIndex(first.transform.GetSiblingIndex() + 1);
            if (!barPrefab) return;
            var bar = Object.Instantiate(barPrefab, box.transform, false);
            ((RectTransform)bar.transform.Find("fill")).anchorMax = new Vector2(progress, 1);
        }
        static string Text(string key) => Localization.instance.Localize(key);

        // What the level adds to the shown damage: the bonus part of the total, within the skill's range.
        static string Range(ItemDrop.ItemData item, float percent)
        {
            var player = Player.m_localPlayer;
            if (IsShield(item) || !player) return "";
            float total = item.GetDamage().GetTotalDamage();
            float gain = total * percent / (100 + percent);
            player.GetSkills().GetRandomSkillRange(out float min, out float max, item.m_shared.m_skillType);
            return " <color=#c0c0c0>(" + (gain * min).ToString("0.#") + "-" + (gain * max).ToString("0.#") + ")</color>";
        }

        // The glow shows on every machine: the owner publishes the tiers of its held items in its ZDO.
        sealed class Worn { internal GameObject Right, Left, RightGlowCopy, LeftGlowCopy; internal int RightTier, LeftTier; }
        static readonly ConditionalWeakTable<VisEquipment, Worn> Wearers = new ConditionalWeakTable<VisEquipment, Worn>();

        [HarmonyPatch(typeof(VisEquipment), "UpdateVisuals")]
        private static class Glow
        {
            private static void Postfix(VisEquipment __instance)
            {
                if (!__instance.m_isPlayer || !__instance.m_nview || !__instance.m_nview.IsValid()) return;
                var data = __instance.m_nview.GetZDO();
                if (__instance.m_nview.IsOwner() && __instance.GetComponent<Player>() is Player player)
                {
                    int right = Tier(player.GetRightItem()), left = Tier(player.GetLeftItem());
                    if (data.GetInt(RightGlow) != right) data.Set(RightGlow, right);
                    if (data.GetInt(LeftGlow) != left) data.Set(LeftGlow, left);
                }
                var worn = Wearers.GetOrCreateValue(__instance);
                Refresh(__instance.m_rightItemInstance, data.GetInt(RightGlow), ref worn.Right, ref worn.RightTier, ref worn.RightGlowCopy);
                Refresh(__instance.m_leftItemInstance, data.GetInt(LeftGlow), ref worn.Left, ref worn.LeftTier, ref worn.LeftGlowCopy);
            }
        }
        static void Refresh(GameObject held, int tier, ref GameObject shown, ref int shownTier, ref GameObject copy)
        {
            if (held == shown && tier == shownTier) return;
            shown = held; shownTier = tier;
            if (copy) Object.Destroy(copy);
            copy = null;
            if (!held || tier <= 0) return;
            LoadAssets();
            if (!glowPrefab) return;
            copy = Object.Instantiate(glowPrefab, held.transform, false);
            float strength = tier / 10f;
            var bounds = new Bounds(); bool first = true;
            foreach (var filter in held.GetComponentsInChildren<MeshFilter>())
            {
                if (!filter.sharedMesh) continue;
                var local = filter.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = held.transform.InverseTransformPoint(filter.transform.TransformPoint(local.center + Vector3.Scale(local.extents, new Vector3((i & 1) * 2 - 1, (i & 2) - 1, (i & 4) / 2 - 1))));
                    if (first) { bounds = new Bounds(corner, Vector3.zero); first = false; } else bounds.Encapsulate(corner);
                }
            }
            foreach (var system in copy.GetComponentsInChildren<ParticleSystem>(true))
            {
                bool sparks = system.gameObject != copy;
                var shape = system.shape; shape.position = bounds.center; shape.scale = bounds.size;
                var main = system.main;
                main.startSize = sparks ? .05f : .12f + .1f * strength;
                main.startColor = sparks ? new Color(1f, .7f, .25f, .9f) : new Color(1f, .6f, .18f, .05f + .13f * strength);
                var emission = system.emission;
                emission.rateOverTime = sparks ? 14 * strength : 20 + 60 * strength;
                system.Play(true);
            }
        }
    }
}
