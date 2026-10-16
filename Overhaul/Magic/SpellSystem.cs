using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Jotunn.Entities;
using Jotunn.Managers;
using Overhaul.Rarity;
using UnityEngine;

namespace Overhaul.Magic
{
    // Grimoire spells, read from Spells.cfg (the server's file is the reference, sent to every player). A spell is cast
    // by an Overhaul staff: its light attack casts the spell of the NormalSpell1 slot, its heavy attack the NormalSpell2
    // one (StaffMagic). The cast is a game staff's attack (Attack), its projectile possibly another staff's (Projectile)
    // at another size, for the spell's eitr. Its hits turn Power % of the staff's magic power into the spell's element
    // (the hit's other damage is left as it is) and carry the spell's status effect: slow (movement and attack speed) or
    // a chance to stun (no move, no attack).
    internal static class SpellSystem
    {
        internal enum Kind { Normal, Special, Shield }
        internal sealed class Spell
        {
            public string Id, NameKey, DescriptionKey, Element, Icon, Attack, Projectile;
            public Kind Kind;
            public int Tier, Level;
            // Rank of the spell itself (the grimoire's pips): 1 until spells rank up with the class level.
            public int Rank = 1;
            public float Power, Eitr, ProjectileScale, ProjectileSpeed, ProjectileGravity, SlowPercent, SlowDuration, StunChance, StunDuration;
            public bool Known => Level <= 0;
            public bool HasEffect => SlowPercent > 0 && SlowDuration > 0 || StunChance > 0 && StunDuration > 0;
            public string EffectName => "Overhaul_Spell_" + Id;
        }
        internal sealed class SpellData
        {
            public string Text = "";
            public readonly List<Spell> Spells = new List<Spell>();
            public readonly Dictionary<string, Spell> ById = new Dictionary<string, Spell>(StringComparer.OrdinalIgnoreCase);
        }

        internal const string FileName = "Spells.cfg";
        const string RequestRpc = "Overhaul_SpellsRequest", ConfigRpc = "Overhaul_SpellsConfig";
        internal static SpellData Local, Current = new SpellData();
        internal static event Action Changed;

        internal static Spell Get(string id) => id != null && Current.ById.TryGetValue(id, out var spell) ? spell : null;
        internal static IEnumerable<Spell> All => Current.Spells;

        internal static void Initialize()
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(SpellSystem).Assembly.Location), FileName);
            if (!File.Exists(path)) File.WriteAllText(path, Utility.ConfigSections.Defaults(FileName));
            try { Current = Local = Parse(File.ReadAllText(path)); }
            catch (Exception e) { Utility.Log.LogError("Spells.cfg unreadable: " + e.Message); }
            PrefabManager.OnVanillaPrefabsAvailable += Register;
        }
        internal static void Shutdown() => PrefabManager.OnVanillaPrefabsAvailable -= Register;

        static void Use(SpellData data) { Current = data; Attacks.Clear(); Changed?.Invoke(); }

        internal static SpellData Parse(string text)
        {
            var data = new SpellData { Text = text };
            foreach (var section in RarityConfig.Sections(text))
            {
                if (!section.Key.StartsWith("Spell_", StringComparison.OrdinalIgnoreCase)) { Utility.Log.LogWarning("Spells.cfg: unknown section [" + section.Key + "]"); continue; }
                var v = section.Value;
                string id = section.Key.Substring("Spell_".Length).ToLowerInvariant();
                var spell = new Spell
                {
                    Id = id,
                    NameKey = RarityConfig.Get(v, "Name", "overhaul_spell_" + id),
                    DescriptionKey = RarityConfig.Get(v, "Description", "overhaul_spell_" + id + "_desc"),
                    Element = RarityConfig.Get(v, "Element", "Fire").ToLowerInvariant(),
                    Kind = Enum.TryParse(RarityConfig.Get(v, "Type", "Normal"), true, out Kind kind) ? kind : Kind.Normal,
                    Tier = Mathf.Clamp((int)RarityConfig.Float(v, "Tier", 1), 1, 3),
                    Level = (int)RarityConfig.Float(v, "Level", 0),
                    Power = RarityConfig.Float(v, "Power", 100),
                    Eitr = RarityConfig.Float(v, "Eitr", 0),
                    Icon = RarityConfig.Get(v, "Icon", "Spell_" + id),
                    Attack = RarityConfig.Get(v, "Attack", "StaffFireball"),
                    Projectile = RarityConfig.Get(v, "Projectile", null),
                    ProjectileScale = RarityConfig.Float(v, "ProjectileScale", 1),
                    ProjectileSpeed = RarityConfig.Float(v, "ProjectileSpeed", 1),
                    // Negative: the projectile keeps its own gravity.
                    ProjectileGravity = RarityConfig.Float(v, "ProjectileGravity", -1),
                    SlowPercent = Mathf.Clamp(RarityConfig.Float(v, "SlowPercent", 0), 0, 95),
                    SlowDuration = RarityConfig.Float(v, "SlowDuration", 0),
                    StunChance = Mathf.Clamp(RarityConfig.Float(v, "StunChance", 0), 0, 100),
                    StunDuration = RarityConfig.Float(v, "StunDuration", 0),
                };
                if (spell.Element != "fire" && spell.Element != "ice" && spell.Element != "lightning") { Utility.Log.LogWarning("Spells.cfg: [" + section.Key + "] unknown element, fire used"); spell.Element = "fire"; }
                data.Spells.Add(spell); data.ById[id] = spell;
            }
            return data;
        }

        // ---------------------------------------------------------------- game objects
        // Resized projectiles and the spells' status effects, made once from the local file (a size change needs a restart).
        static readonly Dictionary<string, GameObject> Projectiles = new Dictionary<string, GameObject>();
        static void Register()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= Register;
            foreach (var spell in (Local ?? Current).Spells)
            {
                var source = ProjectileSource(spell);
                if (source && Mathf.Abs(spell.ProjectileScale - 1) > .001f)
                {
                    string name = "Overhaul_Spell_" + spell.Id + "_projectile";
                    var clone = PrefabManager.Instance.CreateClonedPrefab(name, source);
                    clone.transform.localScale = source.transform.localScale * spell.ProjectileScale;
                    var p = clone.GetComponent<Projectile>(); if (p) p.m_rayRadius *= spell.ProjectileScale;
                    PrefabManager.Instance.AddPrefab(clone);
                    Projectiles[spell.Id] = clone;
                }
                var se = ScriptableObject.CreateInstance<SE_Spell>();
                se.name = spell.EffectName; se.m_name = spell.NameKey.StartsWith("$") ? spell.NameKey : "$" + spell.NameKey; se.Spell = spell.Id;
                ItemManager.Instance.AddStatusEffect(new CustomStatusEffect(se, false));
            }
        }
        static GameObject StaffAttack(string staff, out Attack attack)
        {
            attack = PrefabManager.Instance.GetPrefab(staff)?.GetComponent<ItemDrop>()?.m_itemData.m_shared.m_attack;
            return attack?.m_attackProjectile;
        }
        static GameObject ProjectileSource(Spell spell) => StaffAttack(string.IsNullOrEmpty(spell.Projectile) ? spell.Attack : spell.Projectile, out _);

        // The attack a staff uses to cast the spell, made once per spell (and again when the spells change).
        static readonly Dictionary<string, Attack> Attacks = new Dictionary<string, Attack>();
        internal static Attack AttackFor(Spell spell)
        {
            if (Attacks.TryGetValue(spell.Id, out var cached)) return cached;
            StaffAttack(spell.Attack, out var template);
            if (template == null) { Utility.Log.LogWarning("Spell " + spell.Id + ": attack of " + spell.Attack + " not found"); return null; }
            var attack = template.Clone();
            if (Projectiles.TryGetValue(spell.Id, out var resized)) attack.m_attackProjectile = resized;
            else if (!string.IsNullOrEmpty(spell.Projectile)) attack.m_attackProjectile = ProjectileSource(spell) ?? attack.m_attackProjectile;
            attack.m_attackEitr = spell.Eitr;
            return Attacks[spell.Id] = attack;
        }

        // ---------------------------------------------------------------- casting
        // Spell each caster is casting: its projectiles and their areas are set up afterwards, during the attack.
        static readonly Dictionary<Character, Spell> Casting = new Dictionary<Character, Spell>();
        internal static Spell CastBy(Character owner) => owner && Casting.TryGetValue(owner, out var spell) ? spell : null;
        static float nextMessage;

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
        static class Cast
        {
            [HarmonyPriority(Priority.High)]
            static bool Prefix(Humanoid __instance, bool secondaryAttack, ref bool __result)
            {
                if (Stunned(__instance)) { __result = false; return false; }
                if (__instance != Player.m_localPlayer) return true;
                var staff = __instance.GetCurrentWeapon();
                if (!StaffMagic.IsStaff(staff)) return true;
                var spell = Get(StaffMagic.Spell(staff, secondaryAttack ? StaffMagic.Slot.NormalSpell2 : StaffMagic.Slot.NormalSpell1));
                var attack = spell != null && spell.Known ? AttackFor(spell) : null;
                if (attack == null)
                {
                    if (Time.time >= nextMessage) { nextMessage = Time.time + 2; __instance.Message(MessageHud.MessageType.Center, "$overhaul_spell_none"); }
                    __result = false; return false;
                }
                if (secondaryAttack) staff.m_shared.m_secondaryAttack = attack; else staff.m_shared.m_attack = attack;
                Casting[__instance] = spell;
                return true;
            }
        }

        // The spell's share of the magic power in its element, and its status effect, on each projectile and area of the cast.
        internal static void OnHitSetup(Character owner, HitData hit)
        {
            var spell = CastBy(owner);
            if (spell == null || hit == null) return;
            StaffMagic.Convert(ref hit.m_damage, spell.Element, spell.Power);
            if (spell.HasEffect) hit.m_statusEffectHash = spell.EffectName.GetStableHashCode();
        }

        // The spell's speed and gravity on each projectile of the cast.
        internal static void OnProjectileSetup(Character owner, Projectile projectile, ref Vector3 velocity)
        {
            var spell = CastBy(owner);
            if (spell == null || !projectile) return;
            velocity *= spell.ProjectileSpeed;
            if (spell.ProjectileGravity >= 0) projectile.m_gravity = spell.ProjectileGravity;
        }

        // ---------------------------------------------------------------- slow and stun
        internal static float AttackSpeed(Character character)
        {
            float speed = 1;
            var seman = character ? character.GetSEMan() : null;
            if (seman != null) foreach (var se in seman.GetStatusEffects()) if (se is SE_Spell s && s.Slowing) speed = Mathf.Min(speed, 1 - s.Data.SlowPercent / 100f);
            return speed;
        }
        internal static bool Stunned(Character character)
        {
            var seman = character ? character.GetSEMan() : null;
            if (seman != null) foreach (var se in seman.GetStatusEffects()) if (se is SE_Spell s && s.Stunning) return true;
            return false;
        }

        [HarmonyPatch]
        static class StunnedAI
        {
            static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(MonsterAI), nameof(MonsterAI.UpdateAI));
                yield return AccessTools.Method(typeof(AnimalAI), nameof(AnimalAI.UpdateAI));
            }
            [HarmonyPriority(Priority.First)]
            static bool Prefix(BaseAI __instance, ref bool __result)
            {
                if (!Stunned(__instance.m_character)) return true;
                __instance.StopMoving(); __result = true; return false;
            }
        }

        // ---------------------------------------------------------------- server rules
        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        static class Connection
        {
            static void Postfix(ZNet __instance, ZNetPeer peer)
            {
                if (__instance.IsServer()) peer.m_rpc.Register(RequestRpc, rpc => rpc.Invoke(ConfigRpc, Current.Text));
                else peer.m_rpc.Register<string>(ConfigRpc, (rpc, text) =>
                {
                    try { Use(Parse(text)); Utility.Log.LogInfo("Spells received from the server: " + Current.Spells.Count + " spells"); }
                    catch (Exception e) { Utility.Log.LogWarning("Spells from the server rejected: " + e.Message); }
                });
            }
        }
        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        static class Spawned
        {
            static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer || !ZNet.instance) return;
                if (ZNet.instance.IsServer()) { Use(Local ?? Current); return; }
                ZNet.instance.GetServerRPC()?.Invoke(RequestRpc);
            }
        }
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Awake))]
        static class LocalRules { static void Prefix() { if (Local != null) Use(Local); } }
    }

    // Status effect of a spell's hits, its values read from the spell when it lands: slow for SlowDuration, or, on a
    // StunChance roll, stun for StunDuration (a failed roll ends it at once).
    internal sealed class SE_Spell : SE_Stats
    {
        public string Spell;
        internal SpellSystem.Spell Data => SpellSystem.Get(Spell);
        internal bool Slowing, Stunning;

        public override void Setup(Character character)
        {
            var spell = Data;
            Slowing = spell != null && spell.SlowPercent > 0 && spell.SlowDuration > 0;
            Stunning = spell != null && spell.StunChance > 0 && spell.StunDuration > 0 && UnityEngine.Random.value * 100f < spell.StunChance;
            m_speedModifier = Slowing ? -spell.SlowPercent / 100f : 0;
            m_ttl = Stunning ? Mathf.Max(spell.StunDuration, Slowing ? spell.SlowDuration : 0) : Slowing ? spell.SlowDuration : .01f;
            base.Setup(character);
            if (Stunning && character) character.Stagger(-character.transform.forward);
        }
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            var spell = Data;
            if (Stunning && spell != null && m_time >= spell.StunDuration) Stunning = false;
            if (Slowing && spell != null && m_time >= spell.SlowDuration) { Slowing = false; m_speedModifier = 0; }
        }
    }
}
