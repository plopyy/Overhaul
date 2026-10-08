using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Overhaul
{
    // Configuration changes base stats only. Vanilla skill/world modifiers still apply.
    internal static class AlterItemStat
    {
        private sealed class Rule
        {
            internal string Path;
            internal double[] Values;
            internal int Line;
            internal FieldInfo[] Fields;
        }
        private sealed class Definition
        {
            internal GameObject Prefab;
            internal ItemDrop.ItemData.SharedData Original;
            internal List<Rule> Rules;
            internal readonly Dictionary<int, ItemDrop.ItemData.SharedData> Levels = new Dictionary<int, ItemDrop.ItemData.SharedData>();
        }
        private static readonly Dictionary<string, List<Rule>> Config = new Dictionary<string, List<Rule>>(StringComparer.Ordinal);
        private static readonly Dictionary<ItemDrop.ItemData.SharedData, Definition> Definitions = new Dictionary<ItemDrop.ItemData.SharedData, Definition>();
        private static readonly Dictionary<string, Definition> ById = new Dictionary<string, Definition>(StringComparer.Ordinal);
        private static readonly HashSet<string> UnknownItemsReported = new HashSet<string>(StringComparer.Ordinal);
        private static readonly MethodInfo Memberwise = AccessTools.Method(typeof(object), "MemberwiseClone");
        private sealed class AttackOrigin { internal Definition Definition; internal string Prefix; }
        private static readonly ConditionalWeakTable<Attack, AttackOrigin> AttackOrigins = new ConditionalWeakTable<Attack, AttackOrigin>();
        private static readonly Dictionary<string, string> Increments = new Dictionary<string, string>
        {
            { "m_maxDurability", "m_durabilityPerLevel" }, { "m_armor", "m_armorPerLevel" },
            { "m_blockPower", "m_blockPowerPerLevel" }, { "m_deflectionForce", "m_deflectionForcePerLevel" }
        };
        // References, identity, enums, AI settings and inventory topology are not editable.
        private static readonly HashSet<string> SharedFields = new HashSet<string>((
            "m_weight m_equipDuration m_maxStackSize m_value m_teleportable " +
            "m_movementModifier m_eitrRegenModifier m_homeItemsStaminaModifier m_heatResistanceModifier " +
            "m_jumpStaminaModifier m_attackStaminaModifier m_blockStaminaModifier m_dodgeStaminaModifier " +
            "m_swimStaminaModifier m_sneakStaminaModifier m_runStaminaModifier " +
            "m_food m_foodStamina m_foodEitr m_foodBurnTime m_foodRegen m_foodEatAnimTime " +
            "m_armor m_armorPerLevel m_blockPower m_blockPowerPerLevel m_deflectionForce m_deflectionForcePerLevel " +
            "m_timedBlockBonus m_perfectBlockStaminaRegen m_attackForce m_backstabBonus m_dodgeable m_blockable " +
            "m_maxDurability m_durabilityPerLevel m_useDurabilityDrain m_durabilityDrain m_useDurability").Split(' '));
        private static readonly HashSet<string> AttackFields = new HashSet<string>((
            "m_attackStamina m_attackEitr m_attackHealth m_attackHealthPercentage m_speedFactor m_speedFactorRotation " +
            "m_attackStartNoise m_attackHitNoise m_damageMultiplier m_forceMultiplier m_staggerMultiplier " +
            "m_recoilPushback m_attackRange m_attackHeight m_attackOffset m_attackAngle m_attackRayWidth " +
            "m_lowerDamagePerHit m_multiHit m_lastChainDamageMultiplier m_projectileVel m_projectileVelMin " +
            "m_projectileAccuracy m_projectileAccuracyMin m_launchAngle m_projectiles m_projectileBursts " +
            "m_burstInterval m_perBurstResourceUsage m_reloadTime m_reloadStaminaDrain m_reloadEitrDrain " +
            "m_drawDurationMin m_drawStaminaDrain m_drawEitrDrain").Split(' '));

        internal static void Initialize()
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(AlterItemStat).Assembly.Location), "AlterItemStat.cfg");
            if (!File.Exists(path)) { Debug.LogWarning("[Overhaul] AlterItemStat.cfg missing beside the DLL; no item overrides."); return; }
            try { Parse(File.ReadAllLines(path)); }
            catch (Exception error) { Debug.LogError("[Overhaul] Unable to read AlterItemStat.cfg: " + error.Message); }
        }

        internal static void Parse(string[] lines)
        {
            Config.Clear();
            List<Rule> section = null;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Split('#', ';')[0].Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal) && !line.Contains("="))
                {
                    string id = line.Substring(1, line.Length - 2).Trim();
                    if (id.Length == 0) { Warn(i + 1, "Empty item ID"); section = null; continue; }
                    if (!Config.TryGetValue(id, out section)) Config.Add(id, section = new List<Rule>());
                    continue;
                }
                int equals = line.IndexOf('=');
                if (section == null || equals < 1) { Warn(i + 1, "Expected [ItemID] then field = value"); continue; }
                string key = Normalize(line.Substring(0, equals).Trim());
                string value = line.Substring(equals + 1).Trim();
                bool list = value.StartsWith("[", StringComparison.Ordinal) && value.EndsWith("]", StringComparison.Ordinal);
                string[] tokens = list ? value.Substring(1, value.Length - 2).Split(',') : new[] { value };
                var values = new List<double>();
                foreach (string token in tokens)
                {
                    double number;
                    bool boolean;
                    if (bool.TryParse(token.Trim(), out boolean)) number = boolean ? 1 : 0;
                    else if (!double.TryParse(token.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) break;
                    if (double.IsNaN(number) || double.IsInfinity(number)) break;
                    values.Add(number);
                }
                if (values.Count == 0 || values.Count != tokens.Length) { Warn(i + 1, "Invalid value for " + key); continue; }
                if (section.Any(r => r.Path == key)) { Warn(i + 1, "Duplicate field " + key + " ignored"); continue; }
                section.Add(new Rule { Path = key, Values = values.ToArray(), Line = i + 1 });
            }
        }

        private static string Normalize(string key)
        {
            foreach (string prefix in new[] { "m_itemData.m_shared.", "m_shared." })
                if (key.StartsWith(prefix, StringComparison.Ordinal)) key = key.Substring(prefix.Length);
            if (!key.Contains("."))
            {
                if (AttackFields.Contains(key)) return "m_attack." + key;
                if (typeof(HitData.DamageTypes).GetField(key) != null) return "m_damages." + key;
            }
            return key;
        }

        internal static void Register(ObjectDB database)
        {
            foreach (var prefab in database.m_items) DvergerCirclet.Configure(prefab);
            foreach (GameObject prefab in database.m_items)
            {
                if (!prefab || !Config.ContainsKey(prefab.name)) continue;
                var drop = prefab.GetComponent<ItemDrop>();
                if (!drop) continue;
                Definition definition;
                if (!Definitions.TryGetValue(drop.m_itemData.m_shared, out definition))
                {
                    definition = new Definition { Prefab = prefab, Original = drop.m_itemData.m_shared, Rules = new List<Rule>() };
                    foreach (Rule rule in Config[prefab.name])
                    {
                        string reason;
                        if (Validate(rule, definition.Original, out reason)) definition.Rules.Add(rule);
                        else Warn(rule.Line, prefab.name + ": " + reason);
                    }
                    // Final values take priority over increment fields, regardless of file order.
                    definition.Rules.RemoveAll(r => Increments.Any(p => p.Value == r.Path && definition.Rules.Any(b => b.Path == p.Key))
                        || (r.Path.StartsWith("m_damagesPerLevel.", StringComparison.Ordinal)
                        && definition.Rules.Any(b => b.Path == r.Path.Replace("m_damagesPerLevel.", "m_damages."))));
                    foreach (string prefix in new[] { "m_attack.", "m_secondaryAttack." })
                    {
                        var count = definition.Rules.FirstOrDefault(r => r.Path == prefix + "m_projectiles");
                        var bursts = definition.Rules.FirstOrDefault(r => r.Path == prefix + "m_projectileBursts");
                        var originalAttack = prefix == "m_attack." ? definition.Original.m_attack : definition.Original.m_secondaryAttack;
                        for (int level = 0; level < definition.Original.m_maxQuality; level++)
                        {
                            double projectiles = count == null ? originalAttack?.m_projectiles ?? 1 : count.Values[Math.Min(level, count.Values.Length - 1)];
                            double burstCount = bursts == null ? originalAttack?.m_projectileBursts ?? 1 : bursts.Values[Math.Min(level, bursts.Values.Length - 1)];
                            if (projectiles * burstCount <= 64 || (count == null && bursts == null)) continue;
                            Warn((count ?? bursts).Line, prefab.name + ": more than 64 projectiles across all bursts; count overrides ignored");
                            definition.Rules.Remove(count); definition.Rules.Remove(bursts); break;
                        }
                    }
                    Definitions[definition.Original] = definition;
                    RememberAttack(definition.Original.m_attack, definition, "m_attack.");
                    RememberAttack(definition.Original.m_secondaryAttack, definition, "m_secondaryAttack.");
                    ById[prefab.name] = definition;
                    Debug.Log("[Overhaul] AlterItemStat " + prefab.name + ": " + definition.Rules.Count + " fields accepted");
                }
                drop.m_itemData.m_shared = Variant(definition, 1);
                for (int quality = 1; quality <= definition.Original.m_maxQuality; quality++)
                    database.m_itemByData[Variant(definition, quality)] = prefab;
            }
            foreach (var prefab in database.m_items)
                if (prefab) NormalizeStack(prefab.GetComponent<ItemDrop>()?.m_itemData?.m_shared);
            if (database.m_items.Count > 0)
                foreach (string id in Config.Keys)
                    if (!ById.ContainsKey(id) && UnknownItemsReported.Add(id))
                        Debug.LogWarning("[Overhaul] AlterItemStat: item '" + id + "' is not registered yet; check its prefab ID if it never loads.");
        }

        private static bool Validate(Rule rule, ItemDrop.ItemData.SharedData original, out string reason)
        {
            reason = "Unsupported or unsafe field " + rule.Path;
            string[] parts = rule.Path.Split('.');
            bool supported = parts.Length == 1 && SharedFields.Contains(parts[0]);
            supported |= parts.Length == 2 && (parts[0] == "m_attack" || parts[0] == "m_secondaryAttack") && AttackFields.Contains(parts[1]);
            supported |= parts.Length == 2 && (parts[0] == "m_damages" || parts[0] == "m_damagesPerLevel");
            if (!supported) return false;
            Type type = typeof(ItemDrop.ItemData.SharedData);
            var fields = new List<FieldInfo>();
            foreach (string part in parts)
            {
                FieldInfo field = type.GetField(part, BindingFlags.Instance | BindingFlags.Public);
                if (field == null) return false;
                fields.Add(field);
                type = field.FieldType;
            }
            if (type != typeof(float) && type != typeof(int) && type != typeof(bool)) return false;
            rule.Fields = fields.ToArray();
            reason = "Expected one value or exactly " + original.m_maxQuality + " level values for " + rule.Path;
            if (rule.Values.Length != 1 && rule.Values.Length != original.m_maxQuality) return false;
            if (rule.Path == "m_maxStackSize")
            {
                reason = "m_maxStackSize is locked on originally unstackable items, cannot be reduced, and must be constant";
                if (original.m_maxStackSize <= 1 || rule.Values.Length != 1 || rule.Values[0] < original.m_maxStackSize) return false;
            }
            string leaf = parts[parts.Length - 1];
            foreach (double value in rule.Values)
            {
                reason = "Out-of-range value for " + rule.Path + ": " + value.ToString(CultureInfo.InvariantCulture);
                if (type == typeof(bool)) { if (value != 0 && value != 1) return false; continue; }
                if (type == typeof(int) && value != Math.Truncate(value)) return false;
                double min = 0, max = 100000;
                if (leaf.EndsWith("Modifier", StringComparison.Ordinal)) { min = -0.95; max = 10; }
                if (leaf == "m_launchAngle") { min = -90; max = 90; }
                if (leaf == "m_attackHeight" || leaf == "m_attackOffset") { min = -10; max = 10; }
                if (leaf == "m_maxDurability" || leaf == "m_maxStackSize") min = 1;
                if (leaf == "m_maxStackSize") max = 9999;
                if (leaf == "m_projectiles" || leaf == "m_projectileBursts") { min = 1; max = 32; }
                if (leaf == "m_projectileVel" || leaf == "m_projectileVelMin") { min = 0.1; max = 200; }
                if (leaf == "m_attackRange" || leaf == "m_attackRayWidth") max = 20;
                if (leaf == "m_attackAngle") max = 360;
                if (leaf == "m_speedFactor" || leaf == "m_speedFactorRotation") { min = 0.01; max = 5; }
                if (leaf == "m_attackHealthPercentage") max = 99;
                if (value < min || value > max) return false;
            }
            return true;
        }

        private static ItemDrop.ItemData.SharedData Variant(Definition definition, int quality)
        {
            quality = Mathf.Clamp(quality, 1, definition.Original.m_maxQuality);
            ItemDrop.ItemData.SharedData shared;
            if (definition.Levels.TryGetValue(quality, out shared)) return shared;
            shared = (ItemDrop.ItemData.SharedData)Memberwise.Invoke(definition.Original, null);
            shared.m_attack = definition.Original.m_attack?.Clone() ?? new Attack();
            shared.m_secondaryAttack = definition.Original.m_secondaryAttack?.Clone() ?? new Attack();
            RememberAttack(shared.m_attack, definition, "m_attack.");
            RememberAttack(shared.m_secondaryAttack, definition, "m_secondaryAttack.");
            foreach (Rule rule in definition.Rules)
            {
                double value = rule.Values.Length == 1 ? rule.Values[0] : rule.Values[quality - 1];
                Set(shared, rule.Fields, 0, value);
                if (rule.Path == "m_weight") shared.m_scaleWeightByQuality = 0f;
                string increment;
                if (Increments.TryGetValue(rule.Path, out increment))
                    typeof(ItemDrop.ItemData.SharedData).GetField(increment).SetValue(shared, 0f);
                if (rule.Path.StartsWith("m_damages.", StringComparison.Ordinal))
                    Set(shared, new[] { typeof(ItemDrop.ItemData.SharedData).GetField("m_damagesPerLevel"), rule.Fields[1] }, 0, 0);
            }
            NormalizeStack(shared);
            definition.Levels.Add(quality, shared);
            Definitions[shared] = definition;
            return shared;
        }

        private static void Set(object owner, FieldInfo[] fields, int index, double value)
        {
            FieldInfo field = fields[index];
            if (index == fields.Length - 1)
            {
                object converted = field.FieldType == typeof(bool) ? (object)(value != 0)
                    : field.FieldType == typeof(int) ? (object)(int)value : (float)value;
                field.SetValue(owner, converted);
            }
            else
            {
                object child = field.GetValue(owner);
                Set(child, fields, index + 1, value);
                field.SetValue(owner, child); // Write back boxed damage structs.
            }
        }

        private static Definition Find(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null) return null;
            Definition definition;
            if (Definitions.TryGetValue(item.m_shared, out definition)) return definition;
            return item.m_dropPrefab && ById.TryGetValue(item.m_dropPrefab.name, out definition) ? definition : null;
        }
        internal static void Apply(ItemDrop.ItemData item)
        {
            Definition definition = Find(item);
            if (definition != null) item.m_shared = Variant(definition, item.m_quality);
            NormalizeStack(item?.m_shared);
        }
        // Native stack sizes, remembered so the option can be turned off again during a session.
        private static readonly Dictionary<ItemDrop.ItemData.SharedData, int> NativeStacks = new Dictionary<ItemDrop.ItemData.SharedData, int>();
        internal static void NormalizeStack(ItemDrop.ItemData.SharedData shared)
        {
            if (shared == null) return;
            int native;
            if (!NativeStacks.TryGetValue(shared, out native)) NativeStacks[shared] = native = shared.m_maxStackSize;
            bool raise = Utility.OverhaulConfig.StackSize100 == null || Utility.OverhaulConfig.StackSize100.Value;
            shared.m_maxStackSize = raise && native > 1 && native < 100 ? 100 : native;
        }
        internal static void RefreshStacks()
        {
            foreach (var shared in NativeStacks.Keys.ToArray()) NormalizeStack(shared);
        }
        internal static bool Has(ItemDrop.ItemData item, string field)
        {
            Definition definition = Find(item);
            string path = Normalize(field);
            return definition != null && definition.Rules.Any(r => r.Path == path);
        }
        private static void RememberAttack(Attack attack, Definition definition, string prefix)
        {
            if (attack == null) return;
            AttackOrigins.Remove(attack);
            AttackOrigins.Add(attack, new AttackOrigin { Definition = definition, Prefix = prefix });
        }
        internal static bool HasAttack(Attack attack, string field)
        {
            AttackOrigin origin;
            return attack != null && AttackOrigins.TryGetValue(attack, out origin)
                && origin.Definition.Rules.Any(r => r.Path == origin.Prefix + field);
        }
        [HarmonyPatch(typeof(Attack), "Clone")]
        private static class AttackClonePatch
        {
            private static void Postfix(Attack __instance, Attack __result)
            {
                AttackOrigin origin;
                if (__result != null && AttackOrigins.TryGetValue(__instance, out origin))
                    RememberAttack(__result, origin.Definition, origin.Prefix);
            }
        }
        private static void Warn(int line, string message) => Debug.LogWarning("[Overhaul] AlterItemStat.cfg line " + line + ": " + message);

        [HarmonyPatch(typeof(ObjectDB), "UpdateRegisters")]
        private static class DatabasePatch { [HarmonyPriority(Priority.Last)] private static void Postfix(ObjectDB __instance) => Register(__instance); }

        [HarmonyPatch(typeof(ItemDrop), "SetQuality")]
        private static class DropQualityPatch { private static void Postfix(ItemDrop __instance) => Apply(__instance.m_itemData); }

        [HarmonyPatch(typeof(Inventory), "Changed")]
        private static class InventoryPatch
        {
            private static void Prefix(Inventory __instance) { foreach (var item in __instance.GetAllItems()) Apply(item); }
        }

        [HarmonyPatch(typeof(Humanoid), "GetCurrentWeapon")]
        private static class WeaponPatch { private static void Postfix(ItemDrop.ItemData __result) => Apply(__result); }

        [HarmonyPatch(typeof(ItemDrop.ItemData), "Clone")]
        private static class ClonePatch { private static void Postfix(ItemDrop.ItemData __result) => Apply(__result); }

        // Quality-aware queries also power crafting previews. Restore the correct actual
        // item data even when a nested getter or another patch throws.
        [ThreadStatic] private static Dictionary<ItemDrop.ItemData, int> QueryLevels;
        private sealed class QueryState
        {
            internal ItemDrop.ItemData Item;
            internal ItemDrop.ItemData.SharedData Restore;
            internal int? Previous;
        }
        [HarmonyPatch]
        private static class QueryPatch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                return typeof(ItemDrop.ItemData).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name.StartsWith("Get", StringComparison.Ordinal) && (!m.IsStatic || m.Name == "GetTooltip"));
            }
            private static void Prefix(object __instance, MethodBase __originalMethod, object[] __args, out QueryState __state)
            {
                __state = null;
                var item = __instance as ItemDrop.ItemData ?? __args.OfType<ItemDrop.ItemData>().FirstOrDefault();
                Definition definition = Find(item);
                if (definition == null) return;
                if (QueryLevels == null) QueryLevels = new Dictionary<ItemDrop.ItemData, int>();
                int previous;
                bool nested = QueryLevels.TryGetValue(item, out previous);
                int quality = nested ? previous : item.m_quality;
                ParameterInfo[] parameters = __originalMethod.GetParameters();
                for (int i = 0; i < parameters.Length; i++)
                    if (parameters[i].ParameterType == typeof(int) && (parameters[i].Name == "quality" || parameters[i].Name == "qualityLevel")) quality = (int)__args[i];
                __state = new QueryState { Item = item, Restore = nested ? item.m_shared : Variant(definition, item.m_quality), Previous = nested ? (int?)previous : null };
                QueryLevels[item] = quality;
                item.m_shared = Variant(definition, quality);
            }
            private static void Finalizer(QueryState __state)
            {
                if (__state == null) return;
                __state.Item.m_shared = __state.Restore;
                if (__state.Previous.HasValue) QueryLevels[__state.Item] = __state.Previous.Value;
                else QueryLevels.Remove(__state.Item);
            }
        }
    }
}
