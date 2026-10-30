using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Overhaul.Effects;
using RootEffect = Overhaul.Effects.Root;
using UnityEngine;

namespace Overhaul.Rarity
{
    // Catalyst effects (Catalyst sections of RaritySystem.cfg): every catalyst socketed in the local player's worn gear acts,
    // each one once however many pieces carry it, and only in the kind of gear its effect belongs to (Kind). Its Bonus effects join the player's effect totals;
    // its Effect is one of the behaviours below, with the section's fixed values.
    internal static class CatalystEffects
    {
        internal static void Initialize() { Effects.Effects.Register(Bonuses); IceShards.Initialize(); }

        // The catalysts socketed in the player's worn gear, each once.
        internal static List<CatalystDef> Active(Player player)
        {
            var list = new List<CatalystDef>();
            if (!player) return list;
            var data = RarityConfig.Current; var seen = new HashSet<string>();
            foreach (var item in player.GetInventory().GetEquippedItems())
                foreach (var name in ItemRarity.Catalysts(item))
                    if (name.Length > 0 && data.CatalystEffects.TryGetValue(name, out var def) && Kind(item) == Kind(def.Effect) && seen.Add(name)) list.Add(def);
            return list;
        }
        internal const string Weapon = "weapon", Armor = "armor", Trinket = "trinket";
        static string Kind(ItemDrop.ItemData item)
        {
            switch (item.m_shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.Helmet: case ItemDrop.ItemData.ItemType.Chest: case ItemDrop.ItemData.ItemType.Legs:
                case ItemDrop.ItemData.ItemType.Shoulder: case ItemDrop.ItemData.ItemType.Shield: return Armor;
                case ItemDrop.ItemData.ItemType.Utility: case ItemDrop.ItemData.ItemType.Trinket: return Trinket;
                default: return Weapon;
            }
        }
        // The kind of gear each effect works in (the freeze gland and molten core also have the side effects of InKind).
        static string Kind(string effect)
        {
            switch (effect)
            {
                case "FireThorns": case "ParryReflect": case "LastStand": case "HighJump": return Armor;
                case "ColdWalker": case "LavaWalker": return Trinket;
                default: return Weapon;
            }
        }
        // The catalyst with this effect, socketed in worn gear of that kind.
        internal static CatalystDef InKind(Player player, string effect, string kind)
        {
            var data = RarityConfig.Current;
            foreach (var item in player.GetInventory().GetEquippedItems())
                if (Kind(item) == kind)
                    foreach (var name in ItemRarity.Catalysts(item))
                        if (name.Length > 0 && data.CatalystEffects.TryGetValue(name, out var def) && def.Effect == effect) return def;
            return null;
        }
        // What a catalyst does, in the player's language, with the section's values: every effect it has with the kind of gear
        // each one works in, or, with gear given, only the effect for that kind of gear ("" if none).
        internal static string Describe(string prefab, ItemDrop.ItemData gear = null)
        {
            if (prefab == null || !RarityConfig.Current.CatalystEffects.TryGetValue(prefab, out var def) || string.IsNullOrEmpty(def.Effect)) return "";
            string id = def.Effect.ToLowerInvariant();
            var effects = new List<(string kind, string key)> { (Kind(def.Effect), id) };
            if (def.Effect == "ColdWalker") { effects.Add((Armor, id + "_armor")); effects.Add((Weapon, id + "_weapon")); }
            if (def.Effect == "LavaWalker") effects.Add((Weapon, id + "_weapon"));
            if (gear != null) { effects.RemoveAll(e => e.kind != Kind(gear)); return effects.Count == 0 ? "" : Fill(def, effects[0].key); }
            return string.Join("\n", effects.Select(e => Localization.instance.Localize("$overhaul_catalyst_slot_" + e.kind) + Fill(def, e.key)));
        }
        static string Fill(CatalystDef def, string key)
        {
            string text = Localization.instance.Localize("$overhaul_catalyst_effect_" + key);
            foreach (var pair in def.Values) text = text.Replace("{" + pair.Key + "}", pair.Value.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture));
            if (def.Dot != null) text = text.Replace("{Dot}", Localization.instance.Localize("$overhaul_dot_" + def.Dot.ToLowerInvariant()));
            if (def.Items.Length > 0) text = text.Replace("{Items}", string.Join(", ", def.Items.Select(i => ObjectDB.instance && ObjectDB.instance.GetItemPrefab(i) ? Localization.instance.Localize(ObjectDB.instance.GetItemPrefab(i).GetComponent<ItemDrop>().m_itemData.m_shared.m_name) : i)));
            if (def.Bonuses.Length > 0) text = text.Replace("{Bonus}", def.Bonuses[0].Value.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture));
            return text;
        }
        static CatalystDef Find(Player player, string effect) => Active(player).FirstOrDefault(d => string.Equals(d.Effect, effect, StringComparison.OrdinalIgnoreCase));
        internal static CatalystDef InWeapon(Player player, string effect) => InKind(player, effect, Weapon);
        static bool Local(Character c) => c && c == Player.m_localPlayer;

        static void Bonuses(Player player, Dictionary<string, float> totals)
        {
            foreach (var def in Active(player)) foreach (var (effect, value) in def.Bonuses) Effects.Effects.Add(totals, effect, value);
        }

        // ------------------------------------------------------------------ attacks of the local player
        static int attacks;
        static bool thunderPending, dotAttack;
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
        private static class Attacks
        {
            private static void Postfix(Humanoid __instance, bool __result)
            {
                if (!__result || !(__instance is Player player) || !Local(player)) return;
                IceShards.Release(player);
                attacks++;
                var thunder = Find(player, "ThunderStrike"); thunderPending = thunder != null && attacks % Mathf.Max(1, (int)thunder.Get("Count", 4)) == 0;
                var dot = Find(player, "DotEvery"); dotAttack = dot != null && attacks % Mathf.Max(1, (int)dot.Get("Count", 3)) == 0;
                // EitrSurge: a spell (an attack spending eitr) may refill all the eitr.
                var surge = Find(player, "EitrSurge"); var attack = player.m_currentAttack;
                if (surge != null && attack != null && attack.m_attackEitr > 0 && UnityEngine.Random.value * 100f < surge.Get("Chance", 15))
                    player.AddEitr(player.GetMaxEitr());
            }
        }

        static float killFrenzyUntil; static int killFrenzyStacks;

        // The local player's hits: extra damage, life steal, root, bolts and DoTs.
        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class Hits
        {
            private static void Prefix(Character __instance, HitData hit)
            {
                if (hit == null || Dots.IsDot(hit) || !(hit.GetAttacker() is Player player) || !Local(player) || __instance == player || __instance.IsDead()) return;
                if (hit.GetTotalDamage() <= 0) return;
                var molten = InWeapon(player, "LavaWalker");
                if (molten != null && UnityEngine.Random.value * 100f < molten.Get("WeakChance", 25))
                    Network.FireWeakness(__instance, molten.Get("WeakValue", 10), (int)molten.Get("WeakStacks", 5), molten.Get("WeakDuration", 4));
                var active = Active(player); if (active.Count == 0) return;
                float bonus = 0;
                foreach (var def in active)
                {
                    switch (def.Effect)
                    {
                        case "FirstStrike": if (__instance.GetHealthPercentage() >= .999f) bonus += def.Get("Value"); break;
                        case "Execute": if (__instance.GetHealthPercentage() < def.Get("Threshold") / 100f) bonus += def.Get("Value"); break;
                        case "KillFrenzy": if (Time.time < killFrenzyUntil) bonus += def.Get("Value") * killFrenzyStacks; break;
                    }
                }
                if (bonus != 0) hit.ApplyModifier(1 + bonus / 100f);
                foreach (var def in active)
                {
                    switch (def.Effect)
                    {
                        case "DesperateLifeSteal":
                            if (player.GetHealthPercentage() < def.Get("Threshold") / 100f) player.Heal(hit.GetTotalDamage() * def.Get("Value") / 100f, true);
                            break;
                        case "Entangle":
                            if (UnityEngine.Random.value * 100f < def.Get("Chance")) Network.Entangle(__instance, def.Get("Duration", 3));
                            break;
                        case "ThunderStrike":
                            if (!thunderPending) break;
                            thunderPending = false;
                            var bolt = new HitData { m_point = __instance.GetCenterPoint(), m_dir = Vector3.down, m_dodgeable = false, m_blockable = false };
                            bolt.m_damage.m_lightning = def.Get("Damage", 10); bolt.SetAttacker(player);
                            __instance.Damage(bolt);
                            break;
                        case "Lightning": Lightning(player, def, hit, __instance, __instance.transform.position); break;
                        case "DotEvery":
                            if (dotAttack && Enum.TryParse(def.Dot, true, out DotType type)) Network.Dot(__instance, type, def.Get("Damage", 4), player.GetZDOID());
                            break;
                    }
                }
            }
        }

        // Hard antler: each weapon hit has Chance % to call a bolt on what was hit (the Lightning Strike staff's strike effect
        // only, no area): Damage lightning to that target alone, plus Chop chop damage when the catalyst sits in the axe that
        // hits, or Mine pickaxe damage when it sits in the pickaxe that hits (then it also falls on the trees, logs and rocks
        // the tool hits). The bolt's hit carries no skill, so it never calls another.
        static void Lightning(Player player, CatalystDef def, HitData hit, IDestructible target, Vector3 point)
        {
            if (target == null || hit.m_skill == Skills.SkillType.None || UnityEngine.Random.value * 100f >= def.Get("Chance", 15)) return;
            var bolt = new HitData { m_point = hit.m_point, m_dir = Vector3.down, m_dodgeable = false, m_blockable = false, m_skill = Skills.SkillType.None, m_toolTier = hit.m_toolTier };
            bolt.m_damage.m_lightning = def.Get("Damage", 20);
            var weapon = player.GetCurrentWeapon();
            bool carried = weapon != null && ItemRarity.Catalysts(weapon).Contains(def.Id);
            if (carried && hit.m_skill == Skills.SkillType.Axes && weapon.m_shared.m_skillType == Skills.SkillType.Axes) bolt.m_damage.m_chop = def.Get("Chop", 100);
            if (carried && hit.m_skill == Skills.SkillType.Pickaxes && weapon.m_shared.m_skillType == Skills.SkillType.Pickaxes) bolt.m_damage.m_pickaxe = def.Get("Mine", 100);
            bolt.SetAttacker(player);
            var staff = ZNetScene.instance ? ZNetScene.instance.GetPrefab("staff_thunderblood_projectile") : null;
            var strike = staff ? staff.GetComponent<Projectile>() : null;
            if (strike) strike.m_hitEffects.Create(Physics.Raycast(point + Vector3.up * 40f, Vector3.down, out RaycastHit ground, 100f, Projectile.s_rayMaskSolids) ? ground.point : point, Quaternion.identity);
            target.Damage(bolt);
        }

        [HarmonyPatch]
        private static class LightningTools
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                foreach (var type in new[] { typeof(TreeBase), typeof(TreeLog), typeof(MineRock), typeof(MineRock5), typeof(Destructible) })
                    yield return AccessTools.Method(type, "Damage", new[] { typeof(HitData) });
            }
            private static void Prefix(Component __instance, HitData hit)
            {
                if (hit == null || hit.m_skill != Skills.SkillType.Axes && hit.m_skill != Skills.SkillType.Pickaxes) return;
                if (!(hit.GetAttacker() is Player player) || !Local(player)) return;
                var def = Find(player, "Lightning");
                if (def != null) Lightning(player, def, hit, __instance as IDestructible, hit.m_point);
            }
        }

        // ------------------------------------------------------------------ hits the local player takes
        static float lastStandReady;
        [HarmonyPatch(typeof(Character), "RPC_Damage")]
        private static class Taken
        {
            [HarmonyPriority(Priority.High)]
            private static void Prefix(Character __instance, HitData hit)
            {
                if (!(__instance is Player player) || !Local(player) || hit == null || Dots.IsDot(hit) || player.IsDead()) return;
                var nova = InKind(player, "ColdWalker", Armor); var by = hit.GetAttacker();
                if (nova != null && by && by != player && hit.GetTotalDamage() > 0 && UnityEngine.Random.value * 100f < nova.Get("NovaChance", 15))
                    FrostNova(player, nova.Get("NovaDamage", 35), nova.Get("NovaRadius", 6));
                var active = Active(player); if (active.Count == 0) return;
                var lastStand = active.FirstOrDefault(d => d.Effect == "LastStand");
                if (lastStand != null && Time.time >= lastStandReady && player.GetHealthPercentage() < lastStand.Get("Threshold", 25) / 100f && hit.GetTotalDamage() > 0)
                {
                    hit.m_damage = new HitData.DamageTypes(); hit.m_staggerMultiplier = 0; hit.m_pushForce = 0;
                    lastStandReady = Time.time + lastStand.Get("Cooldown", 120);
                    player.Message(MessageHud.MessageType.TopLeft, "$overhaul_catalyst_laststand_triggered");
                }
                var thorns = active.FirstOrDefault(d => d.Effect == "FireThorns");
                var attacker = hit.GetAttacker();
                if (thorns != null && attacker && attacker != player && !hit.m_ranged && !attacker.IsDead())
                {
                    var burn = new HitData { m_point = attacker.GetCenterPoint(), m_dir = (attacker.transform.position - player.transform.position).normalized, m_dodgeable = false, m_blockable = false };
                    burn.m_damage.m_fire = thorns.Get("Damage", 10); burn.SetAttacker(player);
                    attacker.Damage(burn);
                }
            }
        }

        // Freeze gland in armour: a frost burst (Overhaul_FrostNova, seen and heard by everyone) and frost damage to the enemies
        // in a cylinder around the player, twice their height and centred on them.
        const float NovaHalfHeight = 1.8f;
        static void FrostNova(Player player, float damage, float radius)
        {
            MythicVfx.Spawn("Overhaul_FrostNova", player.transform.position, Quaternion.identity);
            Vector3 center = player.transform.position + Vector3.up * NovaHalfHeight / 2;
            foreach (var c in Character.GetAllCharacters())
            {
                if (!c || c == player || c.IsDead() || !BaseAI.IsEnemy(player, c)) continue;
                Vector3 offset = c.GetCenterPoint() - center;
                if (Mathf.Abs(offset.y) > NovaHalfHeight || new Vector2(offset.x, offset.z).sqrMagnitude > radius * radius) continue;
                var hit = new HitData { m_point = c.GetCenterPoint(), m_dir = (c.transform.position - player.transform.position).normalized, m_dodgeable = false, m_blockable = false };
                hit.m_damage.m_frost = damage; hit.SetAttacker(player);
                c.Damage(hit);
            }
        }

        // Crystal: a parry sends back part of the parried damage to the attacker.
        [HarmonyPatch(typeof(Humanoid), "BlockAttack")]
        private static class Parry
        {
            private static void Prefix(Humanoid __instance, HitData hit, Character attacker, out float __state)
            {
                __state = 0;
                if (!(__instance is Player player) || !Local(player) || !attacker || hit == null) return;
                var def = Find(player, "ParryReflect"); var blocker = player.GetCurrentBlocker();
                bool parry = blocker != null && blocker.m_shared.m_timedBlockBonus > 1f && player.m_blockTimer != -1f && player.m_blockTimer < .25f;
                if (def != null && parry) __state = hit.GetTotalBlockableDamage() * def.Get("Value", 50) / 100f;
            }
            private static void Postfix(Humanoid __instance, Character attacker, bool __result, float __state)
            {
                if (!__result || __state <= 0 || !attacker || attacker.IsDead()) return;
                var back = new HitData { m_point = attacker.GetCenterPoint(), m_dir = (attacker.transform.position - __instance.transform.position).normalized, m_dodgeable = false, m_blockable = false };
                back.m_damage.m_blunt = __state; back.SetAttacker(__instance);
                attacker.Damage(back);
            }
        }

        // ------------------------------------------------------------------ kills of the local player
        internal static void Killed(Vector3 position)
        {
            var player = Player.m_localPlayer; if (!player) return;
            MythicEffects.Killed();
            foreach (var def in Active(player))
            {
                if (def.Effect == "KillFrenzy")
                {
                    killFrenzyStacks = Time.time < killFrenzyUntil ? Mathf.Min((int)def.Get("Stacks", 3), killFrenzyStacks + 1) : 1;
                    killFrenzyUntil = Time.time + def.Get("Duration", 10);
                }
                else if (def.Effect == "KillExplosion") Explosion(player, position, def.Get("Damage", 20));
            }
        }

        // ------------------------------------------------------------------ amber pearl: precious items from rocks and trees
        // A grown tree felled, or a rock, ore vein or mud pile broken all the way, by a player wearing it: Chance % to drop
        // Min to Max items, each one drawn among Items. The rock or tree is handled on its owner's machine: the player's
        // wearing it is published in their ZDO.
        static readonly int PreciousKey = "overhaul_precious".GetStableHashCode();

        [HarmonyPatch(typeof(Player), "Update")]
        private static class PreciousPublish
        {
            private static void Postfix(Player __instance)
            {
                if (!Local(__instance) || !__instance.m_nview || !__instance.m_nview.IsValid()) return;
                bool wears = Find(__instance, "PreciousLoot") != null; var zdo = __instance.m_nview.GetZDO();
                if (zdo.GetBool(PreciousKey) != wears) zdo.Set(PreciousKey, wears);
            }
        }

        static void Precious(HitData hit, Vector3 point)
        {
            if (hit == null || !(hit.GetAttacker() is Player player) || !player.m_nview || !player.m_nview.IsValid()) return;
            bool wears = Local(player) ? Find(player, "PreciousLoot") != null : player.m_nview.GetZDO().GetBool(PreciousKey);
            var def = RarityConfig.Current.CatalystEffects.Values.FirstOrDefault(d => d.Effect == "PreciousLoot");
            if (!wears || def == null) return;
            var items = def.Items.Where(i => ObjectDB.instance.GetItemPrefab(i)).ToArray();
            if (items.Length == 0 || UnityEngine.Random.value * 100f >= def.Get("Chance", 15)) return;
            int count = UnityEngine.Random.Range((int)def.Get("Min", 1), (int)def.Get("Max", 3) + 1);
            for (int i = 0; i < count; i++)
            {
                var prefab = ObjectDB.instance.GetItemPrefab(items[UnityEngine.Random.Range(0, items.Length)]);
                UnityEngine.Object.Instantiate(prefab, point + Vector3.up * .5f + UnityEngine.Random.insideUnitSphere * .3f, Quaternion.identity);
            }
        }

        [HarmonyPatch(typeof(TreeBase), "RPC_Damage")]
        private static class PreciousTree
        {
            private static void Prefix(TreeBase __instance, out bool __state) => __state = __instance.m_nview && __instance.m_nview.IsValid() && __instance.m_nview.IsOwner();
            private static void Postfix(TreeBase __instance, HitData hit, bool __state)
            {
                if (__state && (!__instance.m_nview.IsValid() || __instance.m_nview.GetZDO().GetFloat(ZDOVars.s_health, __instance.m_health) <= 0)) Precious(hit, __instance.transform.position);
            }
        }

        [HarmonyPatch(typeof(MineRock5), "RPC_Damage")]
        private static class PreciousVein
        {
            private static void Prefix(MineRock5 __instance, out bool __state) => __state = !MythicEffects.shattering && __instance.m_nview && __instance.m_nview.IsValid() && __instance.m_nview.IsOwner() && !__instance.AllDestroyed();
            // After the Prospector's shatter, which may finish the rock.
            [HarmonyPriority(Priority.Low)]
            private static void Postfix(MineRock5 __instance, HitData hit, bool __state)
            {
                if (__state && (!__instance.m_nview.IsValid() || __instance.AllDestroyed())) Precious(hit, hit.m_point);
            }
        }

        [HarmonyPatch(typeof(MineRock), "RPC_Hit")]
        private static class PreciousRock
        {
            private static void Prefix(MineRock __instance, out bool __state) => __state = !MythicEffects.shattering && __instance.m_nview && __instance.m_nview.IsValid() && __instance.m_nview.IsOwner() && !__instance.AllDestroyed();
            [HarmonyPriority(Priority.Low)]
            private static void Postfix(MineRock __instance, HitData hit, bool __state)
            {
                if (__state && (!__instance.m_nview.IsValid() || __instance.AllDestroyed())) Precious(hit, hit.m_point);
            }
        }

        // Deposits the pickaxe is made for (tin, obsidian, small rocks...); bushes, stumps, logs and the rest give nothing.
        [HarmonyPatch(typeof(Destructible), "RPC_Damage")]
        private static class PreciousStone
        {
            private static void Prefix(Destructible __instance, out bool __state) => __state = !MythicEffects.shattering && !__instance.m_destroyed && __instance.m_nview && __instance.m_nview.IsValid() && __instance.m_nview.IsOwner();
            private static void Postfix(Destructible __instance, HitData hit, bool __state)
            {
                if (__state && __instance.m_destroyed && MythicEffects.Mineable(__instance)) Precious(hit, __instance.transform.position);
            }
        }

        // Yagluth's drop: the Staff of Embers fireball's burst on the corpse, fire damage to the creatures around.
        static void Explosion(Player player, Vector3 point, float damage)
        {
            var model = EffectPatches.Explosive.Fireball(); if (!model) return;
            model.m_hitEffects.Create(point, Quaternion.identity, null, 1f);
            var seen = new HashSet<Character>();
            foreach (var collider in Physics.OverlapSphere(point, Mathf.Max(2f, model.m_aoe), Projectile.s_rayMaskSolids, QueryTriggerInteraction.UseGlobal))
            {
                var c = collider.GetComponentInParent<Character>();
                if (!c || c == player || c.IsDead() || !seen.Add(c) || !BaseAI.IsEnemy(player, c)) continue;
                var hit = new HitData { m_point = collider.ClosestPointOnBounds(point), m_dir = (c.transform.position - point).normalized, m_dodgeable = false, m_blockable = false };
                hit.m_damage.m_fire = damage; hit.SetAttacker(player);
                c.Damage(hit);
            }
        }

        // ------------------------------------------------------------------ movement
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.ApplyStatusEffectSpeedMods))]
        private static class ColdWalker
        {
            private static void Postfix(SEMan __instance, ref float speed)
            {
                if (!(__instance.m_character is Player player) || !Local(player)) return;
                var def = Find(player, "ColdWalker"); if (def == null) return;
                var biome = player.GetCurrentBiome();
                if (biome == Heightmap.Biome.Mountain || biome == Heightmap.Biome.DeepNorth) speed *= 1 + def.Get("Value", 10) / 100f;
            }
        }
        [HarmonyPatch(typeof(SEMan), nameof(SEMan.ApplyStatusEffectJumpMods))]
        private static class HighJump
        {
            private static void Postfix(SEMan __instance, ref Vector3 jump)
            {
                if (!(__instance.m_character is Player player) || !Local(player)) return;
                var def = Find(player, "HighJump"); if (def != null) jump.y *= 1 + def.Get("Value", 25) / 100f;
            }
        }
        // Molten core: no heat damage from lava or the hot water around it.
        [HarmonyPatch(typeof(Character), "UpdateHeatDamage")]
        private static class LavaWalker
        {
            private static bool Prefix(Character __instance) => !(__instance is Player player && Local(player) && Find(player, "LavaWalker") != null);
        }

        // ------------------------------------------------------------------ network: kills, roots and DoTs reach the right machine
        internal static class Network
        {
            const string DotRpc = "Overhaul_CatalystDot", RootRpc = "Overhaul_CatalystRoot", KillRpc = "Overhaul_CatalystKill", WeakRpc = "Overhaul_CatalystFireWeakness";
            internal static void Dot(Character target, DotType type, float baseDamage, ZDOID attacker)
            {
                if (target.m_nview.IsOwner()) Dots.Apply(target, type, baseDamage, attacker);
                else target.m_nview.InvokeRPC(DotRpc, (int)type, baseDamage, attacker);
            }
            internal static void Entangle(Character target, float seconds)
            {
                if (target.m_nview.IsOwner()) RootEffect.Apply(target, seconds);
                else target.m_nview.InvokeRPC(RootRpc, seconds);
            }
            internal static void FireWeakness(Character target, float value, int maxStacks, float seconds)
            {
                if (target.m_nview.IsOwner()) Dots.ApplyFireWeakness(target, value, maxStacks, seconds);
                else target.m_nview.InvokeRPC(WeakRpc, value, maxStacks, seconds);
            }
            [HarmonyPatch(typeof(Character), "Awake")]
            private static class Register
            {
                private static void Postfix(Character __instance)
                {
                    var view = __instance.m_nview; if (!view || !view.IsValid()) return;
                    view.Register<int, float, ZDOID>(DotRpc, (sender, type, damage, attacker) => { if (view.IsOwner()) Dots.Apply(__instance, (DotType)type, damage, attacker); });
                    view.Register<float, int, float>(WeakRpc, (sender, value, maxStacks, seconds) => { if (view.IsOwner()) Dots.ApplyFireWeakness(__instance, value, maxStacks, seconds); });
                    view.Register<float>(RootRpc, (sender, seconds) => { if (view.IsOwner()) RootEffect.Apply(__instance, seconds); });
                    if (__instance is Player) view.Register<Vector3>(KillRpc, (sender, position) => { if (__instance == Player.m_localPlayer) Killed(position); });
                }
            }
            // On the creature's owner: a kill by a player reaches that player.
            [HarmonyPatch(typeof(Character), "RPC_Damage")]
            private static class Kill
            {
                private static void Prefix(Character __instance, out bool __state) => __state = __instance.IsDead() || __instance.GetHealth() <= 0;
                private static void Postfix(Character __instance, HitData hit, bool __state)
                {
                    if (__state || __instance.IsPlayer() || __instance.GetHealth() > 0 || !(hit?.GetAttacker() is Player player)) return;
                    if (player == Player.m_localPlayer) Killed(__instance.GetCenterPoint());
                    else player.m_nview.InvokeRPC(KillRpc, __instance.GetCenterPoint());
                }
            }
        }
    }
}
