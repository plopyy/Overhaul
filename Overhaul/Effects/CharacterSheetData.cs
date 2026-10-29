using System.Collections.Generic;
using AugaUnity;
using Overhaul.Leveling;
using UnityEngine;
using static HitData.DamageType;

namespace Overhaul.Effects
{
    // Lines of the character tab's sheet (AugaUnity.CharacterSheet): the totals the game really applies to the local
    // player, only those that are not neutral. Effects.cfg effects keep their own name and format; where the game,
    // Overhaul's effects and the level stats act on the same value (regenerations, fall damage, carry weight...), the
    // total is read back from the game's own function, which every one of them patches. Effects carried by an item
    // (its damage, armour, Indestructible...) stay in that item's tooltip: Effects.Get holds the player's totals only.
    internal static class CharacterSheetData
    {
        internal static void Initialize() => CharacterSheet.Provider = Build;

        static string L(string key) => Localization.instance.Localize("$" + key);

        static List<CharacterSheet.Section> Build()
        {
            var player = Player.m_localPlayer;
            var sections = new List<CharacterSheet.Section>();
            if (!player) return sections;
            var seman = player.GetSEMan();

            var offense = Section(sections, "overhaul_sheet_offense");
            Effect(offense, player, "AttackSpeed");
            Effect(offense, player, "CastSpeed");
            Percent(offense, Effect(player, "CritChance") + LevelingEffects.Bonus(player, "critical") * 100, NameOf("CritChance", "overhaul_sheet_crit_chance"), true);
            if (LevelingEffects.Bonus(player, "critical") > 0)
                Percent(offense, ((float)LevelingConfig.Current.CriticalMultiplier - 1) * 100, L("overhaul_sheet_crit_damage"), true);
            Effect(offense, player, "PhysicDmg");
            Effect(offense, player, "MagicPower");
            Effect(offense, player, "MagicDmg");
            Flat(offense, Effect(player, "FireDmg") + LevelingEffects.Bonus(player, "element_fire"), NameOf("FireDmg", "overhaul_sheet_fire"));
            Flat(offense, Effect(player, "FrostDmg") + LevelingEffects.Bonus(player, "element_frost"), NameOf("FrostDmg", "overhaul_sheet_frost"));
            Flat(offense, Effect(player, "LightningDmg") + LevelingEffects.Bonus(player, "element_spirit"), NameOf("LightningDmg", "overhaul_sheet_lightning"));
            Flat(offense, Effect(player, "PoisonDmg") + LevelingEffects.Bonus(player, "element_poison"), NameOf("PoisonDmg", "overhaul_sheet_poison"));
            Effect(offense, player, "SpiritDmg");
            Effect(offense, player, "BackstabBonus");
            Effect(offense, player, "Knockback");
            Effect(offense, player, "LifeSteal");
            Effect(offense, player, "ProjectileCount");
            Percent(offense, Mathf.Min(100, LevelingEffects.Bonus(player, "projectile") * 100), L("overhaul_projectile_extra"), true);
            Effect(offense, player, "ExplosiveProjectile");
            foreach (var dot in new[] { "BurnDamage", "FrostbiteDamage", "ShockDamage", "PoisonDamage", "BleedDamage" }) Effect(offense, player, dot);

            var defense = Section(sections, "overhaul_sheet_defense");
            Pool(defense, "overhaul_sheet_health", player.GetMaxHealth(), player.m_baseHP, Foods(player, food => food.m_health));
            Multiplier(defense, Regen(seman.ModifyHealthRegen), NameOf("HealthRegen", "overhaul_sheet_health_regen"), true);
            defense.Lines.Add(new CharacterSheet.Line { Label = L("overhaul_sheet_armor"), Value = Mathf.RoundToInt(player.GetBodyArmor()).ToString(), Sign = 0 });
            Percent(defense, LevelingEffects.Bonus(player, "block") * 100, L("overhaul_sheet_block"), true);
            Effect(defense, player, "ParryBonus");
            Effect(defense, player, "DamageReflect");
            float fall = 1; seman.ModifyFallDamage(1, ref fall);
            Multiplier(defense, fall, NameOf("FallDmg", "overhaul_sheet_fall_damage"), false);
            Effect(defense, player, "FallSpeed", higherIsBetter: false);
            float stagger = 1; seman.ModifyStagger(1, ref stagger);
            Multiplier(defense, stagger, L("overhaul_sheet_stagger"), false);
            float adrenaline = 1; seman.ModifyAdrenaline(1, ref adrenaline);
            Multiplier(defense, adrenaline, L("overhaul_sheet_adrenaline"), true);
            Multiplier(defense, NutritionDuration.Multiplier(player) * Mathf.Max(.01f, 1 + Effect(player, "FoodDuration") / 100), NameOf("FoodDuration", "overhaul_sheet_food_duration"), true);
            foreach (var type in Resistances.Shown)
            {
                float resist = Resistances.Get(player, type) + (type == Poison ? LevelingEffects.Bonus(player, "poison_resist") * 100 : 0);
                // The final resistance to each type, always shown (0 % included).
                resist = Mathf.Min(100, resist);
                defense.Lines.Add(new CharacterSheet.Line
                {
                    Label = L("overhaul_sheet_resist_" + type.ToString().ToLowerInvariant()),
                    Value = Mathf.Abs(resist) < .05f ? "0 %" : (resist > 0 ? "+" : "−") + Mathf.Abs(resist).ToString("0.#") + " %",
                    Sign = Mathf.Abs(resist) < .05f ? 0 : resist > 0 ? 1 : -1,
                });
            }

            var resources = Section(sections, "overhaul_sheet_resources");
            Pool(resources, "overhaul_sheet_stamina", player.GetMaxStamina(), player.m_baseStamina, Foods(player, food => food.m_stamina));
            Multiplier(resources, Regen(seman.ModifyStaminaRegen), L("overhaul_sheet_stamina_regen"), true);
            Pool(resources, "overhaul_sheet_eitr", player.GetMaxEitr(), 0, Foods(player, food => food.m_eitr));
            Multiplier(resources, Regen(seman.ModifyEitrRegen) + player.GetEquipmentEitrRegenModifier(), NameOf("EitrRegen", "overhaul_sheet_eitr_regen"), true);
            float eitrCost = Mathf.Max(0, 1 + Effect(player, "EitrCost") / 100) * (1 - Mathf.Clamp01(LevelingEffects.Bonus(player, "eitr_cost")));
            Multiplier(resources, eitrCost, NameOf("EitrCost", "overhaul_sheet_eitr_cost"), false);

            var other = Section(sections, "overhaul_sheet_other");
            other.Lines.Add(new CharacterSheet.Line { Label = L("overhaul_sheet_carry"), Value = Mathf.RoundToInt(player.GetMaxCarryWeight()).ToString(), Sign = 0 });
            float speed = 1; seman.ApplyStatusEffectSpeedMods(ref speed, player.transform.forward);
            Multiplier(other, speed * (1 - player.GetEquipmentMovementModifier()), NameOf("MoveSpeed", "overhaul_sheet_move_speed"), true);
            float noise = 1; seman.ModifyNoise(1, ref noise);
            Multiplier(other, noise, L("overhaul_sheet_noise"), false);
            float stealth = 1; seman.ModifyStealth(1, ref stealth);
            Multiplier(other, stealth, L("overhaul_sheet_stealth"), false);
            Effect(other, player, "HarvestBonus");
            Effect(other, player, "MiningDmg");
            Effect(other, player, "ChoppingDmg");
            Percent(other, Effect(player, "LootBonus") + LevelingEffects.Bonus(player, "bonus_loot") * 100, NameOf("LootBonus", "overhaul_sheet_loot"), true);
            Percent(other, LevelingEffects.Bonus(player, "durability") * 100, L("overhaul_sheet_durability"), true);
            return sections;
        }

        static CharacterSheet.Section Section(List<CharacterSheet.Section> sections, string key)
        {
            var section = new CharacterSheet.Section { Title = L(key) };
            sections.Add(section);
            return section;
        }

        static float Effect(Player player, string id) => Effects.Get(player, id);

        static string NameOf(string id, string fallbackKey) =>
            EffectConfig.Current.Effects.TryGetValue(id, out var def) ? def.Name : L(fallbackKey);

        // An Effects.cfg effect of the player, under its own name and format (on/off effects without a value).
        static void Effect(CharacterSheet.Section section, Player player, string id, bool higherIsBetter = true)
        {
            if (!EffectConfig.Current.Effects.TryGetValue(id, out var def)) return;
            float value = Effects.Get(player, id);
            if (Mathf.Abs(value) < .05f) return;
            section.Lines.Add(new CharacterSheet.Line { Label = def.Name, Value = def.Format(value), Sign = (value > 0) == higherIsBetter ? 1 : -1 });
        }

        static void Percent(CharacterSheet.Section section, float percent, string label, bool higherIsBetter)
        {
            if (Mathf.Abs(percent) < .05f) return;
            section.Lines.Add(new CharacterSheet.Line
            {
                Label = label,
                Value = (percent > 0 ? "+" : "−") + Mathf.Abs(percent).ToString("0.#") + " %",
                Sign = (percent > 0) == higherIsBetter ? 1 : -1,
            });
        }

        // A multiplier the game applies (1 = unchanged), shown as a percent change.
        static void Multiplier(CharacterSheet.Section section, float multiplier, string label, bool higherIsBetter) =>
            Percent(section, (multiplier - 1) * 100, label, higherIsBetter);

        static void Flat(CharacterSheet.Section section, float value, string label)
        {
            if (Mathf.Abs(value) < .05f) return;
            section.Lines.Add(new CharacterSheet.Line { Label = label, Value = (value > 0 ? "+" : "−") + Mathf.Abs(value).ToString("0.#"), Sign = value > 0 ? 1 : -1 });
        }

        delegate void RegenModifier(ref float multiplier);

        static float Regen(RegenModifier modify)
        {
            float multiplier = 1;
            modify(ref multiplier);
            return multiplier;
        }

        static float Foods(Player player, System.Func<Player.Food, float> value)
        {
            float total = 0;
            foreach (var food in player.GetFoods()) total += value(food);
            return total;
        }

        // Health, stamina or eitr: the maximum, and where it comes from in the tooltip (base, food, the rest: gear,
        // effects, passives).
        static void Pool(CharacterSheet.Section section, string key, float max, float baseValue, float food)
        {
            if (max <= 0) return;
            float bonus = max - baseValue - food;
            var detail = new System.Text.StringBuilder();
            if (baseValue > 0) detail.Append(L("overhaul_sheet_from_base")).Append(" ").Append(Mathf.RoundToInt(baseValue)).Append('\n');
            if (food > 0) detail.Append(L("overhaul_sheet_from_food")).Append(" +").Append(Mathf.RoundToInt(food)).Append('\n');
            if (Mathf.Abs(bonus) >= .5f) detail.Append(L("overhaul_sheet_from_bonus")).Append(bonus > 0 ? " +" : " −").Append(Mathf.RoundToInt(Mathf.Abs(bonus))).Append('\n');
            section.Lines.Add(new CharacterSheet.Line { Label = L(key), Value = Mathf.RoundToInt(max).ToString(), Sign = 0, Tooltip = detail.ToString().TrimEnd('\n') });
        }
    }
}
