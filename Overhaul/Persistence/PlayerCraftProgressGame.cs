using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Overhaul.Leveling;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class PlayerCraftProgressGame
    {
        internal static float Factor(PlayerSnapshot snapshot,Skills.SkillType type)
        {
            var row = snapshot.Rows.FirstOrDefault(r => r.Table == "skills" && Convert.ToInt32(r.Values[0]) == (int)type);
            if (type == Skills.SkillType.None) return 0;
            float level = row == null ? 0 : Convert.ToSingle(row.Values[1]);
            foreach (var effect in PlayerPotionGame.Active(snapshot)) effect.ModifySkillLevel(type,ref level);
            return Mathf.Clamp01(Mathf.Floor(level)/100f);
        }
        internal static float LootChance(PlayerSnapshot snapshot)=>Mathf.Clamp01(Bonus(snapshot,"bonus_loot"));
        internal static bool Passive(PlayerSnapshot snapshot,string id)
        {
            var row=snapshot.Rows.FirstOrDefault(r=>r.Table=="custom_data" && (string)r.Values[0]==OverhaulCharacter.SaveKey);
            if(row==null)return false;
            var data=JsonConvert.DeserializeObject<OverhaulCharacterData>((string)row.Values[1]);LevelingSystem.Reconcile(data);
            return data.Passive==id && data.Level>=LevelingConfig.Current.PassiveLevel;
        }
        internal static float Bonus(PlayerSnapshot snapshot,string id)
        {
            var row = snapshot.Rows.FirstOrDefault(r => r.Table == "custom_data" && (string)r.Values[0] == OverhaulCharacter.SaveKey);
            if (row == null) return 0;
            var state = JsonConvert.DeserializeObject<OverhaulCharacterData>((string)row.Values[1]);
            LevelingSystem.Reconcile(state);
            return state.AllocatedStats.TryGetValue(id,out int rank) && LevelingConfig.Current.Stats.TryGetValue(id,out var rule)
                ? (float)(rank*rule.PerPoint) : 0;
        }
        internal static IEnumerable<PlayerChange> Raise(PlayerSnapshot snapshot,Skills.SkillType type,float amount)
        {
            var prefab = ZNetScene.instance.GetPrefab("Player"); var definitions = prefab ? prefab.GetComponent<Skills>() : null;
            if (!definitions) throw new InvalidOperationException("Server skill definitions are unavailable");
            var info = definitions.m_skills.FirstOrDefault(s => s.m_skill == type);
            if (info == null) throw new InvalidOperationException("Unknown crafting skill");
            var rows = snapshot.Rows.Where(r => r.Table == "skills").Select(r => r.Values).ToDictionary(r => Convert.ToInt32(r[0]));
            var value = new Skills.Skill(info);
            if (rows.TryGetValue((int)type,out var before)) { value.m_level = Convert.ToSingle(before[1]); value.m_accumulator = Convert.ToSingle(before[2]); }
            float multiplier = 1;
            foreach (var effect in PlayerPotionGame.Active(snapshot)) effect.ModifyRaiseSkill(type,ref multiplier);
            bool raised = value.Raise(amount * multiplier);
            yield return new PlayerChange("skills",false,(int)type,value.m_level,value.m_accumulator);
            if (raised && definitions.m_useSkillCap)
            {
                float others = rows.Where(p => p.Key != (int)type).Sum(p => Convert.ToSingle(p.Value[1]));
                if (others > 0 && others+value.m_level >= definitions.m_totalSkillCap)
                    foreach (var pair in rows.Where(p => p.Key != (int)type))
                        yield return new PlayerChange("skills",false,pair.Key,Convert.ToSingle(pair.Value[1])/others*(definitions.m_totalSkillCap-value.m_level),pair.Value[2]);
            }
        }
        internal static IEnumerable<PlayerChange> Statistics(PlayerSnapshot snapshot,ItemDrop.ItemData item,bool upgrade,int count)
        {
            var increments = new Dictionary<PlayerStatType,float> { [PlayerStatType.CraftsOrUpgrades] = 1,[upgrade ? PlayerStatType.Upgrades : PlayerStatType.Crafts] = count };
            if (!upgrade)
            {
                PlayerStatType category;
                switch (item.m_shared.m_itemType)
                {
                    case ItemDrop.ItemData.ItemType.Material: category = PlayerStatType.CraftMaterial; break;
                    case ItemDrop.ItemData.ItemType.Consumable: category = item.m_shared.m_name.ToLowerInvariant().Contains("bait") ? PlayerStatType.CraftBait : PlayerStatType.CraftFood; break;
                    case ItemDrop.ItemData.ItemType.OneHandedWeapon: case ItemDrop.ItemData.ItemType.Bow: case ItemDrop.ItemData.ItemType.Shield:
                    case ItemDrop.ItemData.ItemType.Hands: case ItemDrop.ItemData.ItemType.TwoHandedWeapon: case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft: category = PlayerStatType.CraftWeapon; break;
                    case ItemDrop.ItemData.ItemType.Helmet: case ItemDrop.ItemData.ItemType.Chest: case ItemDrop.ItemData.ItemType.Legs: case ItemDrop.ItemData.ItemType.Shoulder: category = PlayerStatType.CraftArmor; break;
                    case ItemDrop.ItemData.ItemType.Ammo: case ItemDrop.ItemData.ItemType.AmmoNonEquipable: category = PlayerStatType.CraftAmmo; break;
                    case ItemDrop.ItemData.ItemType.Torch: category = PlayerStatType.CraftTorch; break;
                    case ItemDrop.ItemData.ItemType.Utility: case ItemDrop.ItemData.ItemType.Tool: category = PlayerStatType.CraftTool; break;
                    case ItemDrop.ItemData.ItemType.Trinket: category = PlayerStatType.CraftTrinket; break;
                    default: category = PlayerStatType.CraftOther; break;
                }
                increments[category] = count;
                yield return Increment(snapshot,"statistics:0:craft",item.m_shared.m_name,count);
            }
            foreach (var pair in increments) yield return Increment(snapshot,"statistics:0:values",((int)pair.Key).ToString(CultureInfo.InvariantCulture),pair.Value);
        }
        internal static PlayerChange Increment(PlayerSnapshot snapshot,string category,string key,float amount)
        {
            var before = snapshot.Rows.FirstOrDefault(r => r.Table == "knowledge" && (string)r.Values[0] == category && (string)r.Values[1] == key);
            float value = before == null ? 0 : float.Parse((string)before.Values[2],CultureInfo.InvariantCulture);
            return new PlayerChange("knowledge",false,category,key,(value+amount).ToString("R",CultureInfo.InvariantCulture));
        }
        internal static Action Presentation(IEnumerable<PlayerChange> source,Player player)
        {
            var rows = source.ToArray();
            var effects = new List<Action>();
            if (rows.Length != 0 && !player) throw new System.IO.InvalidDataException("Player progression view is unavailable");
            foreach (var row in rows)
            {
                var v = row.Values;
                if (row.Table == "skills")
                {
                    var type = (Skills.SkillType)Convert.ToInt32(v[0]); var skills = player.GetSkills();
                    float level = row.Delete ? 0 : Convert.ToSingle(v[1]), accumulator = row.Delete ? 0 : Convert.ToSingle(v[2]);
                    effects.Add(() => { if (row.Delete) skills.m_skillData.Remove(type); else { var skill = skills.GetSkill(type); skill.m_level = level; skill.m_accumulator = accumulator; } });
                }
                else if (row.Table == "knowledge")
                {
                    string category = (string)v[0], key = (string)v[1];
                    HashSet<string> set = category == "uniques" ? player.m_uniques : category == "recipes" ? player.m_knownRecipes : category == "materials" ? player.m_knownMaterial : null;
                    if (set != null) { effects.Add(() => { if (row.Delete) set.Remove(key); else set.Add(key); }); continue; }
                    if(category=="trophies")
                    {effects.Add(()=>{if(row.Delete)player.m_trophies.Remove(key);else if(!player.m_trophies.Contains(key))player.m_trophies.Add(key);});continue;}
                    if(category=="stations")
                    {int level=row.Delete?0:int.Parse((string)v[2],CultureInfo.InvariantCulture);if(level<0)throw new System.IO.InvalidDataException("Invalid station level");effects.Add(()=>{if(row.Delete)player.m_knownStations.Remove(key);else player.m_knownStations[key]=level;});continue;}
                    float value = row.Delete ? 0 : float.Parse((string)v[2],CultureInfo.InvariantCulture);
                    var stats = Game.instance.GetPlayerProfile().m_playerStats[0];
                    if (category == "statistics:0:values")
                    { var type = (PlayerStatType)int.Parse(key,CultureInfo.InvariantCulture); effects.Add(() => stats.m_stats[type] = value); }
                    else if (category == "statistics:0:craft") effects.Add(() => stats.m_itemCraftStats[key] = value);
                    else if (category == "statistics:0:pickable") effects.Add(() => stats.m_pickableStats[key] = value);
                    else if (category == "statistics:0:pieces") effects.Add(() => stats.m_piecesPlacedStats[key] = value);
                    else if (category == "statistics:0:food") effects.Add(() => stats.m_foodEatenStats[key] = value);
                    else throw new System.IO.InvalidDataException("Unsupported progression effect");
                }
                else throw new System.IO.InvalidDataException("Unsupported progression table");
            }
            return () => { foreach (var apply in effects) apply(); };
        }
    }
}

