using System.Collections.Generic;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using UnityEngine;
using static HitData.DamageType;

namespace Overhaul.Effects
{
    // Resistances in percent, one per damage type. Every source adds up (the body or the weak spot hit, each worn armour
    // piece, each status effect, the Resist enchantments) and the sum is applied once: damage × (1 − resistance), 100 % or
    // more being immune and below 0 a weakness. The game's levels count as SlightlyResistant 25, Resistant 50,
    // VeryResistant 75, Immune 100, SlightlyWeak −25, Weak −50, VeryWeak −100; Ignore takes the type out. Creatures are never
    // immune to spirit; Yagluth resists it by 40 %, skeletons and draugrs are weak to it (−50 %).
    internal static class Resistances
    {
        // The types shown in tooltips, in this order (chop and pickaxe only serve harvesting).
        internal static readonly HitData.DamageType[] Shown = { Blunt, Slash, Pierce, HitData.DamageType.Fire, Frost, Lightning, Poison, Spirit };

        static readonly Dictionary<string, Dictionary<HitData.DamageType, float>> Creatures = new Dictionary<string, Dictionary<HitData.DamageType, float>>
        {
            ["GoblinKing"] = new Dictionary<HitData.DamageType, float> { [Spirit] = 40 },
        };

        static bool Undead(Character character)
        {
            string name = Utils.GetPrefabName(character.gameObject);
            return name.StartsWith("Skeleton", System.StringComparison.OrdinalIgnoreCase) && name != "Skeleton_Friendly" || name.StartsWith("Draugr", System.StringComparison.OrdinalIgnoreCase);
        }

        internal static float Percent(HitData.DamageModifier modifier)
        {
            switch (modifier)
            {
                case HitData.DamageModifier.SlightlyResistant: return 25;
                case HitData.DamageModifier.Resistant: return 50;
                case HitData.DamageModifier.VeryResistant: return 75;
                case HitData.DamageModifier.Immune: case HitData.DamageModifier.Ignore: return 100;
                case HitData.DamageModifier.SlightlyWeak: return -25;
                case HitData.DamageModifier.Weak: return -50;
                case HitData.DamageModifier.VeryWeak: return -100;
                default: return 0;
            }
        }

        static float Sum(List<HitData.DamageModPair> mods, HitData.DamageType type)
        {
            float total = 0;
            foreach (var pair in mods) if (pair.m_type == type) total += Percent(pair.m_modifier);
            return total;
        }

        static string EffectId(HitData.DamageType type)
        {
            switch (type)
            {
                case Blunt: return "ResistBlunt"; case Slash: return "ResistSlash"; case Pierce: return "ResistPierce";
                case HitData.DamageType.Fire: return "ResistFire"; case Frost: return "ResistFrost"; case Lightning: return "ResistLightning";
                case Poison: return "ResistPoison"; case Spirit: return "ResistSpirit"; default: return null;
            }
        }

        // The character's resistance to the type, in percent (−200 to 100).
        internal static float Get(Character character, HitData.DamageType type, WeakSpot spot = null)
        {
            var body = (spot ? spot.m_damageModifiers : character.m_damageModifiers).GetModifier(type);
            if (body == HitData.DamageModifier.Ignore) return 100;
            float total = Percent(body);
            var player = character as Player;
            if (!player)
            {
                if (Creatures.TryGetValue(Utils.GetPrefabName(character.gameObject), out var set) && set.TryGetValue(type, out float value)) total = value;
                // Skeletons and draugrs (every variant) are undead: weak to spirit.
                else if (type == Spirit && Undead(character)) total = -50;
                else if (type == Spirit && body == HitData.DamageModifier.Immune) total = 0;
            }
            else
            {
                foreach (var item in new[] { player.m_chestItem, player.m_legItem, player.m_helmetItem, player.m_shoulderItem })
                    if (item != null) total += Sum(item.m_shared.m_damageModifiers, type);
                string id = EffectId(type);
                if (id != null) total += Effects.Get(player, id);
                if (type == HitData.DamageType.Fire || type == Frost || type == Lightning) total += Effects.Get(player, "ResistMagic");
                if ((type == HitData.DamageType.Fire && Effects.Get(player, "FireImmunity") >= 1) || (type == Frost && Effects.Get(player, "FrostImmunity") >= 1)) total = 100;
            }
            // A creature prefab (bestiary) has no status effects yet.
            var seman = character.GetSEMan();
            if (seman != null)
                foreach (var effect in seman.GetStatusEffects())
                    if (effect is SE_Stats stats) total += Sum(stats.m_mods, type);
            if (type == HitData.DamageType.Fire) total -= Dots.FireWeaknessPercent(character);
            return Mathf.Clamp(total, -200, 100);
        }

        // "+50 %" in green, "−25 %" in red.
        internal static string Format(float percent) =>
            "<color=" + (percent >= 0 ? "#7fd97f" : "#ff6b5e") + ">" + (percent >= 0 ? "+" : "−") + Mathf.Abs(percent).ToString("0.#") + " %</color>";

        internal static string Label(HitData.DamageType type) => "$overhaul_effect_" + EffectId(type)?.ToLowerInvariant();

        // The hit's damage once the target's resistances are applied, and which of them weighed most (the damage text's colour).
        static void Apply(HitData hit, HitData.DamageModifiers modifiers, ref HitData.DamageModifier significant, Character target, WeakSpot spot)
        {
            float normal = 0, resisted = 0, weak = 0, immune = 0;
            ref var d = ref hit.m_damage;
            d.m_blunt = Scale(d.m_blunt, Blunt); d.m_slash = Scale(d.m_slash, Slash); d.m_pierce = Scale(d.m_pierce, Pierce);
            d.m_chop = Scale(d.m_chop, Chop); d.m_pickaxe = Scale(d.m_pickaxe, Pickaxe);
            d.m_fire = Scale(d.m_fire, HitData.DamageType.Fire); d.m_frost = Scale(d.m_frost, Frost); d.m_lightning = Scale(d.m_lightning, Lightning);
            d.m_poison = Scale(d.m_poison, Poison); d.m_spirit = Scale(d.m_spirit, Spirit);
            d.m_nonPlayer = Scale(d.m_nonPlayer, HitData.DamageType.Damage, Percent(modifiers.m_nonPlayer));
            normal += d.m_damage;
            float most = Mathf.Max(normal, resisted, weak, immune);
            significant = most == weak ? HitData.DamageModifier.Weak : most == resisted ? HitData.DamageModifier.Resistant :
                most == normal ? HitData.DamageModifier.Normal : HitData.DamageModifier.Immune;

            float Scale(float damage, HitData.DamageType type, float? fixedPercent = null)
            {
                if (damage <= 0) return damage;
                float percent = fixedPercent ?? Get(target, type, spot);
                if (percent >= 100) immune += damage; else if (percent > 0) resisted += damage; else if (percent < 0) weak += damage; else normal += damage;
                return damage * Mathf.Max(0, 1 - percent / 100f);
            }
        }

        // The character's own resistances, in place of the game's levels; blocking with a shield still uses the game's.
        [HarmonyPatch(typeof(Character), "RPC_Damage")]
        private static class Damage
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var resist = AccessTools.Method(typeof(HitData), nameof(HitData.ApplyResistance));
                var weakSpot = AccessTools.Method(typeof(Character), "GetWeakSpot");
                var codes = new List<CodeInstruction>(instructions);
                int spot = -1;
                bool done = false;
                for (int i = 0; i < codes.Count; i++)
                {
                    // The weak spot local, stored right after GetWeakSpot.
                    if (spot < 0 && codes[i].Calls(weakSpot) && i + 1 < codes.Count && codes[i + 1].IsStloc()) spot = i + 1;
                    if (done || !codes[i].Calls(resist)) continue;
                    done = true;
                    var load = spot < 0 ? new CodeInstruction(OpCodes.Ldnull) : LoadOf(codes[spot]);
                    codes[i] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Resistances), nameof(Apply))).MoveLabelsFrom(codes[i]);
                    codes.Insert(i, load);
                    codes.Insert(i, new CodeInstruction(OpCodes.Ldarg_0));
                    i += 2;
                }
                if (!done) Utility.Log.LogError("Resistances: damage resistance not found");
                return codes;
            }

            static CodeInstruction LoadOf(CodeInstruction store)
            {
                if (store.opcode == OpCodes.Stloc_0) return new CodeInstruction(OpCodes.Ldloc_0);
                if (store.opcode == OpCodes.Stloc_1) return new CodeInstruction(OpCodes.Ldloc_1);
                if (store.opcode == OpCodes.Stloc_2) return new CodeInstruction(OpCodes.Ldloc_2);
                if (store.opcode == OpCodes.Stloc_3) return new CodeInstruction(OpCodes.Ldloc_3);
                return new CodeInstruction(store.opcode == OpCodes.Stloc_S ? OpCodes.Ldloc_S : OpCodes.Ldloc, store.operand);
            }
        }

        // The bestiary: a creature's health with the mod's changes (GetMaxHealthBase is patched), its 3 star dungeon guardian
        // (level 4 and its health bonus), its resistances under short damage type names.
        internal static void Initialize()
        {
            AugaUnity.BestiaryData.Health = (creature, level) => creature.GetMaxHealthBase() * level;
            AugaUnity.BestiaryData.GuardianLevel = creature => Dungeons.BossData.TryFor(creature.name, out var entry) && entry.Stars >= 3 ? entry.Stars + 1 : 0;
            AugaUnity.BestiaryData.GuardianHealth = creature => Dungeons.BossData.TryFor(creature.name, out var entry)
                ? creature.GetMaxHealthBase() * (entry.Stars + 1) * (1 + entry.HealthBonusPercent / 100f) : 0;
            AugaUnity.BestiaryData.Resistances = creature =>
            {
                var list = new List<KeyValuePair<string, float>>();
                foreach (var type in Shown) list.Add(new KeyValuePair<string, float>("$overhaul_bestiary_" + type.ToString().ToLowerInvariant(), Get(creature, type)));
                return list;
            };
            AugaUnity.BestiaryData.Behavior = Behavior;
            AugaUnity.BestiaryData.Origin = creature => Leveling.LevelingConfig.Current.Monsters.TryGetValue(Utils.GetPrefabName(creature.gameObject), out var rule)
                ? "$biome_" + rule.Biome.ToLowerInvariant() + "  ·  $overhaul_rank_" + rule.Category.ToLowerInvariant() : "";
        }

        // Its behaviour as MobBehaviors.cfg sets it (the server's values): one line per trait, "Ordinary" when it has none.
        static IList<AugaUnity.BestiaryTrait> Behavior(Character creature)
        {
            var traits = new List<AugaUnity.BestiaryTrait>();
            void Add(string id, bool smart = false, string suffix = "") => traits.Add(new AugaUnity.BestiaryTrait
                { Name = "$overhaul_trait_" + id + suffix, Description = "$overhaul_trait_" + id + "_desc", Smart = smart });
            if (AI.MobBehaviorConfig.Current.TryGetValue(Utils.GetPrefabName(creature.gameObject), out var rule))
            {
                if (rule.Intelligence == AI.Intelligence.Smart) Add("smart", true);
                if (rule.Aggression == AI.Aggression.Aggressive) Add("aggressive");
                if (rule.Aggression == AI.Aggression.Cunning) Add("cunning");
                if (rule.Group == AI.GroupBehavior.Pack) Add("pack");
                if (rule.Group == AI.GroupBehavior.Helper) Add("helper");
                if (rule.Charge) Add("charge");
                if (rule.DumbChance > 0) Add("dumb", false, " (" + rule.DumbChance.ToString("0.#") + " %)");
            }
            if (traits.Count == 0) Add("none");
            return traits;
        }

        // Item and status effect tooltips: one line per type, "Fire resistance: +50 %".
        [HarmonyPatch(typeof(SE_Stats), nameof(SE_Stats.GetDamageModifiersTooltipString))]
        private static class Tooltip
        {
            private static bool Prefix(List<HitData.DamageModPair> mods, ref string __result)
            {
                var text = new StringBuilder();
                foreach (var type in Shown)
                {
                    float percent = Sum(mods, type);
                    if (percent != 0) text.Append('\n').Append(Label(type)).Append(": ").Append(Format(percent));
                }
                __result = text.ToString();
                return false;
            }
        }
    }
}
