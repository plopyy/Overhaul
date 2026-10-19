using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Overhaul.Rarity
{
    // Code of the mythic enchantments of RaritySystem.cfg.
    internal static class MythicEffects
    {
        private static readonly HashSet<string> Implemented = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FanVolley", "ZeusWrath", "DragonFist", "WindBlade", "Earthquake", "ArmorBreak", "Reaper", "RicochetAxe", "TwinBlade", "Whirlwind", "Homing", "Prospector", "SpectralDagger" };
        internal static bool Known(string id) => Implemented.Contains(id);

        // --- FanVolley: the weapon fires its Value extra projectiles, every projectile (others bonuses included) spread
        // evenly over a narrow fan around the aim, with perfect accuracy ---

        private const float FanAngle = 10f;

        // Angle of projectile index among count, evenly spread over the fan (0 for a single projectile).
        internal static float Angle(int index, int count) => count < 2 ? 0 : -FanAngle / 2 + index * FanAngle / (count - 1);

        // Rotation of a direction by a fan angle, around the aim's own up axis (so the fan stays flat on screen).
        internal static Vector3 Turn(Vector3 direction, float angle)
        {
            Vector3 right = Vector3.Cross(Vector3.up, direction);
            if (right.sqrMagnitude < 1e-4f) right = Vector3.right;
            return Quaternion.AngleAxis(angle, Vector3.Cross(direction, right.normalized)) * direction;
        }

        internal static int Extra(ItemDrop.ItemData weapon) => Mathf.RoundToInt(Enchantments.MythicValue(weapon, "FanVolley"));

        private sealed class Volley { internal Character Owner; internal int Count, Index, Extra; internal float Accuracy, AccuracyMin; }
        private static Volley current;

        // Runs after the other projectile bonuses, so the fan counts them all.
        [HarmonyPatch(typeof(Attack), "FireProjectileBurst")]
        private static class Burst
        {
            [HarmonyPriority(Priority.Last)]
            private static void Prefix(Attack __instance, out Volley __state)
            {
                __state = null;
                if (!__instance.m_character || __instance.m_character != Player.m_localPlayer || __instance.m_projectiles <= 0) return;
                int extra = Extra(__instance.m_weapon);
                if (extra <= 0) return;
                __instance.m_projectiles += extra;
                __state = current = new Volley
                {
                    Owner = __instance.m_character, Count = __instance.m_projectiles, Extra = extra,
                    Accuracy = __instance.m_projectileAccuracy, AccuracyMin = __instance.m_projectileAccuracyMin
                };
                __instance.m_projectileAccuracy = __instance.m_projectileAccuracyMin = 0;
            }

            private static void Finalizer(Attack __instance, Volley __state)
            {
                if (__state == null) return;
                __instance.m_projectiles -= __state.Extra;
                __instance.m_projectileAccuracy = __state.Accuracy;
                __instance.m_projectileAccuracyMin = __state.AccuracyMin;
                current = null;
            }
        }

        [HarmonyPatch(typeof(Projectile), nameof(Projectile.Setup))]
        private static class Spread
        {
            private static void Prefix(Character owner, ref Vector3 velocity)
            {
                var volley = current;
                if (volley == null || owner != volley.Owner || velocity.sqrMagnitude < 1e-6f) return;
                velocity = Turn(velocity, Angle(volley.Index++ % volley.Count, volley.Count));
            }
        }

        // Crosshair: one mark per projectile direction around the normal crosshair, while a FanVolley weapon is held.
        [HarmonyPatch(typeof(Hud), "UpdateCrosshair")]
        private static class Crosshair
        {
            private static readonly List<Image> Marks = new List<Image>();

            private static void Postfix(Hud __instance, Player player)
            {
                int count = 0;
                var weapon = player ? player.GetCurrentWeapon() : null;
                var camera = Utils.GetMainCamera();
                int extra = weapon != null ? Extra(weapon) : 0;
                if (extra > 0 && camera && __instance.m_crosshair && __instance.m_crosshair.enabled)
                    // Base projectiles, the mythic ones, the enchantment lines (ProjectileCount) and the leveling bonus once its chance reaches 100 %.
                    count = Mathf.Max(1, weapon.m_shared.m_attack.m_projectiles) + extra + Mathf.RoundToInt(Effects.Effects.Get(player, "ProjectileCount"))
                        + (Leveling.LevelingEffects.Bonus(player, "projectile") >= 1 ? 1 : 0);
                int shown = 0;
                for (int i = 0; i < count; i++)
                {
                    float angle = Angle(i, count);
                    if (Mathf.Approximately(angle, 0)) continue; // the normal crosshair already marks the centre
                    Vector3 point = camera.WorldToScreenPoint(camera.transform.position + Turn(camera.transform.forward, angle) * 50f);
                    var mark = Mark(__instance, shown++);
                    mark.rectTransform.position = new Vector3(point.x, point.y, mark.rectTransform.position.z);
                    mark.gameObject.SetActive(true);
                }
                for (int i = shown; i < Marks.Count; i++) if (Marks[i]) Marks[i].gameObject.SetActive(false);
                // An even count has no projectile in the centre: the normal crosshair is hidden.
                if (__instance.m_crosshair) __instance.m_crosshair.canvasRenderer.SetAlpha(count > 0 && count % 2 == 0 ? 0 : 1);
            }

            // A smaller copy of the crosshair, created once per index.
            private static Image Mark(Hud hud, int index)
            {
                while (Marks.Count <= index || !Marks[index])
                {
                    var copy = UnityEngine.Object.Instantiate(hud.m_crosshair, hud.m_crosshair.transform.parent);
                    copy.name = "OverhaulFanMark";
                    copy.raycastTarget = false;
                    foreach (Transform child in copy.transform) UnityEngine.Object.Destroy(child.gameObject);
                    copy.rectTransform.localScale = hud.m_crosshair.rectTransform.localScale * .6f;
                    if (Marks.Count <= index) Marks.Add(copy); else Marks[index] = copy;
                }
                return Marks[index];
            }
        }

        // --- ZeusWrath: each projectile of the weapon rolls its own Value % chance when it hits; if it wins, a rain of lightning falls
        // around its impact point, whatever it hit (a spell striking where it aims hits there): 16 bolts
        // at random moments over 6 seconds, on the ground within 10 m. Each bolt is the Lightning Strike staff's own
        // (its projectile hitting at once), dealing the triggering projectile's damage as lightning ---

        private const int RainBolts = 16;
        private const float RainSeconds = 6f, RainRadius = 10f;
        // Chance of the weapon that fired the projectile, rolled when it hits.
        private sealed class Wrath { internal float Chance; }
        // Set while a rain bolt strikes, so its lightning area deals the bolt's damage (StaffMagic).
        internal static bool RainStrike;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Projectile, Wrath> Wrathful = new System.Runtime.CompilerServices.ConditionalWeakTable<Projectile, Wrath>();

        [HarmonyPatch(typeof(Projectile), nameof(Projectile.Setup))]
        private static class WrathRoll
        {
            private static void Postfix(Projectile __instance, Character owner, ItemDrop.ItemData item)
            {
                if (!owner || owner != Player.m_localPlayer || item == null) return;
                float chance = Enchantments.MythicValue(item, "ZeusWrath");
                if (chance > 0) Wrathful.GetValue(__instance, _ => new Wrath()).Chance = chance;
            }
        }

        [HarmonyPatch(typeof(Projectile), "OnHit")]
        private static class WrathStrike
        {
            private static void Prefix(Projectile __instance, Collider collider, Vector3 hitPoint, bool __runOriginal)
            {
                if (!__runOriginal) return;   // a contact ignored (the caster: SpellSystem)
                if (!Wrathful.TryGetValue(__instance, out var wrath)) return;
                var player = Player.m_localPlayer;
                // Touching the caster is not the impact: the roll waits for the real one.
                var struck = collider ? collider.GetComponentInParent<Character>() : null;
                if (struck && struck == __instance.m_owner) return;
                Wrathful.Remove(__instance);
                // The roll happens on impact: a projectile that vanishes without hitting anything never rolls.
                if (!player || __instance.m_owner != player || UnityEngine.Random.value * 100f >= wrath.Chance) return;
                // Rain centre: the impact point, whatever was hit; a sphere cast that starts inside a collider reports no
                // point (zero), so the projectile's own position stands in when the point is off.
                Vector3 center = hitPoint == Vector3.zero || Vector3.Distance(hitPoint, __instance.transform.position) > 3f ? __instance.transform.position : hitPoint;
                var hit = __instance.m_originalHitData?.Clone() ?? new HitData();
                float damage = __instance.m_damage.GetTotalDamage();
                hit.m_damage = new HitData.DamageTypes { m_lightning = damage > 0 ? damage : hit.m_damage.GetTotalDamage() };
                player.StartCoroutine(Rain(player, center, hit));
            }
        }

        private static System.Collections.IEnumerator Rain(Player owner, Vector3 center, HitData hit)
        {
            var prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab("staff_thunderblood_projectile") : null;
            if (!prefab) { Utility.Log.LogWarning("Zeus's Wrath: staff_thunderblood_projectile not found"); yield break; }
            var moments = new List<float>();
            for (int i = 0; i < RainBolts; i++) moments.Add(UnityEngine.Random.Range(0f, RainSeconds));
            moments.Sort();
            float elapsed = 0;
            foreach (float moment in moments)
            {
                if (moment > elapsed) { yield return new WaitForSeconds(moment - elapsed); elapsed = moment; }
                if (!owner || owner.IsDead()) yield break;
                Vector2 offset = UnityEngine.Random.insideUnitCircle * RainRadius;
                Vector3 above = center + new Vector3(offset.x, 40f, offset.y);
                if (!Physics.Raycast(above, Vector3.down, out RaycastHit ground, 100f, Projectile.s_rayMaskSolids)) continue;
                var bolt = UnityEngine.Object.Instantiate(prefab, ground.point, Quaternion.identity).GetComponent<Projectile>();
                if (!bolt) continue;
                ThunderStaff.Raining = true;
                try { bolt.Setup(owner, Vector3.down, 0f, hit.Clone(), null, null); }
                finally { ThunderStaff.Raining = false; }
                RainStrike = true;
                try { bolt.OnHit(ground.collider, ground.point, false, ground.normal); }
                finally { RainStrike = false; }
            }
        }

        // --- DragonFist: every 4th attack of the weapon hurls a dragon of energy straight ahead (MythicVfx Mythic_DragonFist,
        // seen by every player): it hits every enemy on its way, up to 5 m, once each, for Value % of the attack's damage
        // (same damage types). The hits follow the dragon's head as it flies out fast then slows, as the effect does ---

        private const int DragonEvery = 4;
        private const float DragonReach = 5.2f, DragonFlight = .8f, DragonRadius = 1.2f;
        private static int dragonCount;
        private static ItemDrop.ItemData dragonWeapon;

        // --- WindBlade: the last hit of the weapon's combo sends a cutting wave straight ahead (MythicVfx Mythic_WindBlade):
        // 8 m in 0.4 s at a steady speed, hitting every enemy it crosses once for Value % of the attack's damage ---

        private const float WindReach = 8f, WindFlight = .4f, WindRadius = 1.3f;

        [HarmonyPatch(typeof(Attack), "DoMeleeAttack")]
        private static class MeleeWaves
        {
            private static void Postfix(Attack __instance)
            {
                var player = __instance.m_character as Player;
                var weapon = __instance.m_weapon;
                if (!player || player != Player.m_localPlayer || weapon == null) return;
                Vector3 forward = player.transform.forward; forward.y = 0; forward.Normalize();
                Vector3 origin = player.transform.position + Vector3.up;

                float dragon = Enchantments.MythicValue(weapon, "DragonFist");
                if (dragon > 0)
                {
                    // The count is the weapon's own: another weapon starts it again.
                    if (weapon != dragonWeapon) { dragonWeapon = weapon; dragonCount = 0; }
                    if (++dragonCount >= DragonEvery)
                    {
                        dragonCount = 0;
                        MythicVfx.Spawn("Mythic_DragonFist", player.transform.position, Quaternion.LookRotation(forward));
                        player.StartCoroutine(Wave(player, origin, forward, AttackHit(__instance, player, dragon), DragonReach, DragonFlight, DragonRadius,
                            t => 1 - Mathf.Pow(1 - t, 2.5f)));
                    }
                }

                float wind = Enchantments.MythicValue(weapon, "WindBlade");
                if (wind > 0 && __instance.m_attackChainLevels > 1 && __instance.m_currentAttackCainLevel == __instance.m_attackChainLevels - 1)
                {
                    MythicVfx.Spawn("Mythic_WindBlade", player.transform.position, Quaternion.LookRotation(forward));
                    player.StartCoroutine(Wave(player, origin, forward, AttackHit(__instance, player, wind), WindReach, WindFlight, WindRadius, t => t));
                }
            }
        }

        // --- Earthquake: 1 s after the weapon's ground hit (area attack), a second, wider shockwave bursts where it struck
        // (MythicVfx Mythic_Earthquake): every enemy within 6 m is hit once for Value % of the attack's damage and staggered.
        // The weapon's own ground-hit smoke is cut short so the aftershock shows ---

        private const float QuakeDelay = 1f, QuakeReach = 6f, QuakeSpread = .25f, QuakeStagger = 10f;
        private static int groundMask;

        [HarmonyPatch(typeof(Attack), "DoAreaAttack")]
        private static class Earthquake
        {
            private static void Postfix(Attack __instance)
            {
                var player = __instance.m_character as Player;
                var weapon = __instance.m_weapon;
                if (!player || player != Player.m_localPlayer || weapon == null) return;
                float quake = Enchantments.MythicValue(weapon, "Earthquake");
                if (quake <= 0) return;
                // Where the attack struck (as the game places it), down on the ground.
                var transform = player.transform;
                Vector3 point = transform.position + Vector3.up * __instance.m_attackHeight + transform.forward * __instance.m_attackRange + transform.right * __instance.m_attackOffset;
                if (groundMask == 0) groundMask = LayerMask.GetMask("terrain", "Default", "static_solid", "piece");
                if (Physics.Raycast(point + Vector3.up, Vector3.down, out RaycastHit ground, 6f, groundMask)) point = ground.point;
                var hit = AttackHit(__instance, player, quake);
                hit.m_staggerMultiplier = QuakeStagger;
                player.StartCoroutine(Aftershock(player, point, hit));
            }
        }

        // The weapon's own ground-hit smoke clears in under a second, so the aftershock shows: while the attack's area hit
        // creates its effects, the long-lived particle systems they hold (the dust cloud) get their lifetime scaled down.
        private const float QuakeSmoke = .9f;
        private static bool quakeHit;

        [HarmonyPatch(typeof(Attack), "DoAreaAttack")]
        private static class QuakeSmokeWatch
        {
            private static void Prefix(Attack __instance) =>
                quakeHit = __instance.m_character == Player.m_localPlayer && Enchantments.MythicValue(__instance.m_weapon, "Earthquake") > 0;
            private static void Finalizer() => quakeHit = false;
        }

        [HarmonyPatch(typeof(EffectList), nameof(EffectList.Create))]
        private static class QuakeSmokeClear
        {
            private static void Postfix(GameObject[] __result)
            {
                if (!quakeHit || __result == null) return;
                foreach (var effect in __result)
                    if (effect)
                        foreach (var ps in effect.GetComponentsInChildren<ParticleSystem>(true))
                        {
                            var main = ps.main;
                            float life = main.startLifetime.constantMax;
                            if (life > QuakeSmoke) main.startLifetimeMultiplier *= QuakeSmoke / life;
                        }
            }
        }

        private static System.Collections.IEnumerator Aftershock(Player owner, Vector3 point, HitData hit)
        {
            yield return new WaitForSeconds(QuakeDelay);
            if (!owner || owner.IsDead()) yield break;
            MythicVfx.Spawn("Mythic_Earthquake", point, Quaternion.identity);
            yield return Ring(owner, point + Vector3.up * .5f, hit, QuakeReach, QuakeSpread, t => t);
        }

        // The attack's hit at percent % of its damage: the weapon's damage with the attack's multiplier and the skill factor,
        // its tool tier, push, backstab and stagger.
        private static HitData AttackHit(Attack attack, Player player, float percent)
        {
            var weapon = attack.m_weapon;
            var hit = new HitData
            {
                m_damage = weapon.GetDamage(),
                m_toolTier = (short)weapon.m_shared.m_toolTier,
                m_pushForce = weapon.m_shared.m_attackForce,
                m_backstabBonus = weapon.m_shared.m_backstabBonus,
                m_staggerMultiplier = attack.m_staggerMultiplier,
                m_skill = weapon.m_shared.m_skillType,
                m_hitType = HitData.HitType.PlayerHit,
            };
            hit.m_damage.Modify(percent / 100f * attack.m_damageMultiplier * player.GetRandomSkillFactor(weapon.m_shared.m_skillType));
            hit.SetAttacker(player);
            return hit;
        }

        private static int waveMask;

        // A wave flying from origin along forward up to reach in flight seconds, its front at progress(time / flight) of
        // reach: each frame, every enemy between the origin and the front, within radius, is hit if it was not yet.
        private static System.Collections.IEnumerator Wave(Player owner, Vector3 origin, Vector3 forward, HitData hit, float reach, float flight, float radius, Func<float, float> progress)
        {
            if (waveMask == 0) waveMask = LayerMask.GetMask("character", "character_net", "character_ghost", "character_noenv", "hitbox");
            var struck = new HashSet<Character>();
            for (float t = 0; t <= flight; t += Time.deltaTime)
            {
                if (!owner || owner.IsDead()) yield break;
                float front = Mathf.Max(reach * progress(t / flight), .3f);
                foreach (var collider in Physics.OverlapCapsule(origin + forward * .3f, origin + forward * front, radius, waveMask))
                {
                    var target = collider.GetComponentInParent<Character>();
                    if (!target || target == owner || target.IsDead() || !BaseAI.IsEnemy(owner, target) || !struck.Add(target)) continue;
                    var each = hit.Clone();
                    each.m_point = collider.ClosestPoint(origin); each.m_dir = forward;
                    target.Damage(each);
                }
                yield return null;
            }
        }

        // A ring widening around origin up to reach in flight seconds, its edge at progress(time / flight) of reach: each
        // frame, every enemy inside the edge is hit if it was not yet.
        private static System.Collections.IEnumerator Ring(Player owner, Vector3 origin, HitData hit, float reach, float flight, Func<float, float> progress)
        {
            if (waveMask == 0) waveMask = LayerMask.GetMask("character", "character_net", "character_ghost", "character_noenv", "hitbox");
            var struck = new HashSet<Character>();
            for (float t = 0; t <= flight; t += Time.deltaTime)
            {
                if (!owner || owner.IsDead()) yield break;
                foreach (var collider in Physics.OverlapSphere(origin, Mathf.Max(reach * progress(t / flight), .5f), waveMask))
                {
                    var target = collider.GetComponentInParent<Character>();
                    if (!target || target == owner || target.IsDead() || !BaseAI.IsEnemy(owner, target) || !struck.Add(target)) continue;
                    var each = hit.Clone();
                    each.m_point = collider.ClosestPoint(origin);
                    Vector3 away = each.m_point - origin; away.y = 0;
                    each.m_dir = away.sqrMagnitude > 1e-4f ? away.normalized : owner.transform.forward;
                    target.Damage(each);
                }
                yield return null;
            }
        }

        // --- ArmorBreak: each hit of the weapon on a creature adds a stack of armour break to it (5 at most, 6 s, restarted
        // by each hit): it takes Value % more damage per stack, from everyone. The stacks live in the creature's ZDO, set by
        // its owner (the hitting player asks it with an RPC), and the owner applies them to every hit it receives ---

        private const int BreakMax = 5;
        private const float BreakDuration = 6f;
        private const string BreakRpc = "Overhaul_ArmorBreak";
        private static readonly int BreakStacksKey = "overhaul_armorbreak_stacks".GetStableHashCode(),
            BreakValueKey = "overhaul_armorbreak_value".GetStableHashCode(), BreakEndKey = "overhaul_armorbreak_end".GetStableHashCode();
        private static double NetNow => ZNet.instance ? ZNet.instance.GetTimeSeconds() : Time.time;

        // Active stacks of armour break on a character (0 once their time is up).
        internal static int BreakStacks(Character c)
        {
            var zdo = c && c.m_nview && c.m_nview.IsValid() ? c.m_nview.GetZDO() : null;
            if (zdo == null) return 0;
            int stacks = zdo.GetInt(BreakStacksKey, 0);
            return stacks > 0 && NetNow < zdo.GetFloat(BreakEndKey, 0) ? stacks : 0;
        }

        [HarmonyPatch(typeof(Game), "Start")]
        private static class BreakRegister { private static void Postfix() => ZRoutedRpc.instance.Register<ZDOID, float>(BreakRpc, OnBreak); }

        // The local player's weapon hit (its own skill, not a spell or a damage over time) on a creature: one more stack.
        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class BreakHit
        {
            private static void Prefix(Character __instance, HitData hit)
            {
                var player = Player.m_localPlayer;
                if (hit == null || !player || __instance == player || __instance is Player || hit.GetAttacker() != player) return;
                var weapon = player.GetCurrentWeapon();
                if (weapon == null || hit.m_skill != weapon.m_shared.m_skillType || Effects.Dots.IsDot(hit)) return;
                float value = Enchantments.MythicValue(weapon, "ArmorBreak");
                var zdo = value > 0 && __instance.m_nview && __instance.m_nview.IsValid() ? __instance.m_nview.GetZDO() : null;
                if (zdo == null || zdo.GetOwner() == 0) return;
                ZRoutedRpc.instance.InvokeRoutedRPC(zdo.GetOwner(), BreakRpc, __instance.GetZDOID(), value);
            }
        }

        private static void OnBreak(long sender, ZDOID id, float value)
        {
            var c = ZNetScene.instance ? ZNetScene.instance.FindInstance(id)?.GetComponent<Character>() : null;
            if (!c || !c.m_nview.IsOwner() || c.IsDead()) return;
            var zdo = c.m_nview.GetZDO();
            int stacks = BreakStacks(c);
            zdo.Set(BreakStacksKey, Mathf.Min(BreakMax, stacks + 1));
            zdo.Set(BreakValueKey, stacks > 0 ? Mathf.Max(value, zdo.GetFloat(BreakValueKey, 0)) : value);
            zdo.Set(BreakEndKey, (float)(NetNow + BreakDuration));
        }

        [HarmonyPatch(typeof(Character), "RPC_Damage")]
        private static class BreakTaken
        {
            private static void Prefix(Character __instance, HitData hit)
            {
                if (hit == null || !__instance.m_nview || !__instance.m_nview.IsOwner()) return;
                int stacks = BreakStacks(__instance);
                if (stacks > 0) hit.m_damage.Modify(1 + stacks * __instance.m_nview.GetZDO().GetFloat(BreakValueKey, 0) / 100f);
            }
        }

        // --- Reaper: the weapon executes: +40 % damage on a creature under 30 % health. Each creature killed after the
        // weapon hit it under that threshold stores a soul (3 at most, kept in the player's ZDO so every machine shows its
        // orbs, MythicVfx Mythic_Souls). The next heavy (secondary) attack spends them all: Value % more damage per soul,
        // 30 % more reach and a 40 % wider arc ---

        private const float ReaperThreshold = .3f, ReaperExecute = 40f, ReaperRange = 1.3f, ReaperArc = 1.4f;
        private const int ReaperSouls = 3;
        private static readonly int SoulsKey = "overhaul_reaper_souls".GetStableHashCode();
        // Creatures the weapon hit under the threshold, until they die or a while passes.
        private static readonly Dictionary<Character, float> Doomed = new Dictionary<Character, float>();

        internal static int Souls(Character c) => c && c.m_nview && c.m_nview.IsValid() ? c.m_nview.GetZDO().GetInt(SoulsKey, 0) : 0;
        private static void SetSouls(Player player, int souls) { if (player.m_nview && player.m_nview.IsValid()) player.m_nview.GetZDO().Set(SoulsKey, souls); }

        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class ReaperHit
        {
            private static void Prefix(Character __instance, HitData hit)
            {
                var player = Player.m_localPlayer;
                if (hit == null || !player || __instance == player || __instance is Player || hit.GetAttacker() != player || __instance.IsDead()) return;
                var weapon = player.GetCurrentWeapon();
                if (weapon == null || hit.m_skill != weapon.m_shared.m_skillType || Effects.Dots.IsDot(hit) || Enchantments.MythicValue(weapon, "Reaper") <= 0) return;
                if (__instance.GetHealthPercentage() >= ReaperThreshold) return;
                hit.ApplyModifier(1 + ReaperExecute / 100f);
                Doomed[__instance] = Time.time + 2f;
            }
        }

        // A kill of the local player (CatalystEffects.Killed): a doomed creature now gone or dead stores a soul.
        internal static void Killed()
        {
            var player = Player.m_localPlayer;
            if (!player || Doomed.Count == 0) return;
            bool reaped = false;
            foreach (var c in Doomed.Keys.ToList())
            {
                if (!c || c.IsDead() || c.GetHealth() <= 0) { reaped = true; Doomed.Remove(c); }
                else if (Time.time > Doomed[c]) Doomed.Remove(c);
            }
            if (reaped && Enchantments.MythicValue(player.GetCurrentWeapon(), "Reaper") > 0) SetSouls(player, Mathf.Min(ReaperSouls, Souls(player) + 1));
        }

        private sealed class Reach { internal float Range, Angle, Multiplier; }

        [HarmonyPatch(typeof(Attack), "DoMeleeAttack")]
        private static class ReaperHarvest
        {
            private static void Prefix(Attack __instance, out Reach __state)
            {
                __state = null;
                var player = __instance.m_character as Player;
                if (!player || player != Player.m_localPlayer || !player.m_currentAttackIsSecondary) return;
                float value = Enchantments.MythicValue(__instance.m_weapon, "Reaper");
                int souls = Souls(player);
                if (value <= 0 || souls <= 0) return;
                __state = new Reach { Range = __instance.m_attackRange, Angle = __instance.m_attackAngle, Multiplier = __instance.m_damageMultiplier };
                __instance.m_attackRange *= ReaperRange;
                __instance.m_attackAngle = Mathf.Min(360, __instance.m_attackAngle * ReaperArc);
                __instance.m_damageMultiplier *= 1 + souls * value / 100f;
                SetSouls(player, 0);
            }
            private static void Finalizer(Attack __instance, Reach __state)
            {
                if (__state == null) return;
                __instance.m_attackRange = __state.Range; __instance.m_attackAngle = __state.Angle; __instance.m_damageMultiplier = __state.Multiplier;
            }
        }

        // The souls' orbs on every player holding some, one orb per soul; none once spent. The souls stay stored with the
        // player when the weapon is put away.
        private static readonly Dictionary<Player, GameObject> Orbs = new Dictionary<Player, GameObject>();

        [HarmonyPatch(typeof(Player), "Update")]
        private static class SoulOrbs
        {
            private static void Postfix(Player __instance)
            {
                int souls = Souls(__instance);
                Orbs.TryGetValue(__instance, out var orbs);
                if (souls <= 0) { if (orbs) { UnityEngine.Object.Destroy(orbs); Orbs.Remove(__instance); } return; }
                if (!orbs)
                {
                    orbs = MythicVfx.Attach("Mythic_Souls", __instance.transform);
                    if (!orbs) return;
                    Orbs[__instance] = orbs;
                }
                for (int i = 1; i <= ReaperSouls; i++)
                {
                    var orb = orbs.transform.Find("Orbit/Soul" + i);
                    if (orb && orb.gameObject.activeSelf != i <= souls) orb.gameObject.SetActive(i <= souls);
                }
            }
        }

        // --- RicochetAxe: the weapon's special (secondary) attack also throws a spectral copy of it (MythicVfx
        // Mythic_SpectralWeapon, seen by every player) at the nearest enemy ahead within 12 m; from each enemy it hits it
        // bounces to the nearest one not yet hit within 8 m, up to Value enemies, then vanishes. Each takes 70 % of the
        // attack's damage as spirit damage. It flies at 20 m/s, spinning flat; with no enemy ahead it flies 10 m and vanishes ---

        private const float RicochetFirst = 12f, RicochetBounce = 8f, RicochetSpeed = 20f, RicochetSpin = 1440f, RicochetDamage = 70f, RicochetCone = 75f;

        [HarmonyPatch(typeof(Attack), "DoMeleeAttack")]
        private static class RicochetThrow
        {
            private static void Postfix(Attack __instance)
            {
                var player = __instance.m_character as Player;
                var weapon = __instance.m_weapon;
                if (!player || player != Player.m_localPlayer || weapon == null || !player.m_currentAttackIsSecondary) return;
                int bounces = Mathf.RoundToInt(Enchantments.MythicValue(weapon, "RicochetAxe"));
                if (bounces <= 0) return;
                string item = Enchantments.Prefab(weapon);
                if (string.IsNullOrEmpty(item)) return;
                var hit = AttackHit(__instance, player, RicochetDamage);
                hit.m_damage = new HitData.DamageTypes { m_spirit = hit.m_damage.GetTotalDamage() };
                Vector3 start = player.transform.position + Vector3.up * 1.2f + player.transform.forward * .6f;
                var axe = MythicVfx.SpawnSpectral(item, start, Quaternion.identity);
                if (axe) player.StartCoroutine(Ricochet(player, axe, hit, bounces));
            }
        }

        // The nearest living enemy of owner within range of from, not yet struck; ahead only within cone degrees of forward.
        private static Character NextTarget(Player owner, Vector3 from, float range, HashSet<Character> struck, Vector3 forward, float cone)
        {
            Character best = null; float bestDistance = range;
            foreach (var c in Character.GetAllCharacters())
            {
                if (!c || c == owner || c.IsDead() || struck.Contains(c) || !BaseAI.IsEnemy(owner, c)) continue;
                Vector3 to = c.GetCenterPoint() - from;
                float distance = to.magnitude;
                if (distance > bestDistance) continue;
                if (cone < 180 && Vector3.Angle(forward, new Vector3(to.x, 0, to.z)) > cone) continue;
                best = c; bestDistance = distance;
            }
            return best;
        }

        private static System.Collections.IEnumerator Ricochet(Player owner, GameObject axe, HitData hit, int bounces)
        {
            var struck = new HashSet<Character>();
            Vector3 forward = owner.transform.forward; forward.y = 0; forward.Normalize();
            var target = NextTarget(owner, axe.transform.position, RicochetFirst, struck, forward, RicochetCone);
            Vector3 end = axe.transform.position + forward * 10f;
            float spin = 0;
            while (axe)
            {
                Vector3 aim = target ? target.GetCenterPoint() : end;
                Vector3 to = aim - axe.transform.position;
                float step = RicochetSpeed * Time.deltaTime;
                spin += RicochetSpin * Time.deltaTime;
                axe.transform.rotation = Quaternion.Euler(0, spin, 0);
                if (to.magnitude > step) { axe.transform.position += to.normalized * step; yield return null; continue; }
                axe.transform.position = aim;
                if (!target) break;
                if (!target.IsDead())
                {
                    var each = hit.Clone(); each.m_point = aim; each.m_dir = to.normalized;
                    target.Damage(each);
                }
                struck.Add(target);
                target = struck.Count < bounces ? NextTarget(owner, aim, RicochetBounce, struck, Vector3.forward, 180) : null;
                if (!target) break;
            }
            if (axe && ZNetScene.instance) ZNetScene.instance.Destroy(axe);
        }

        // --- TwinBlade: each hit of the weapon on a creature has Value % chance to be doubled: a spectral copy of the sword
        // (MythicVfx Mythic_SpectralWeapon, seen by every player) appears 1 m from the target on the player's side and cuts
        // through it in a 100 degree arc in 0.25 s, dealing the hit's damage again as spirit damage halfway through ---

        private const float TwinSwing = .25f, TwinArc = 100f, TwinReach = 1f;
        private static bool twinStriking;

        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class TwinBlade
        {
            private static void Prefix(Character __instance, HitData hit)
            {
                var player = Player.m_localPlayer;
                if (twinStriking || hit == null || !player || __instance == player || __instance is Player || hit.GetAttacker() != player || __instance.IsDead()) return;
                var weapon = player.GetCurrentWeapon();
                if (weapon == null || hit.m_skill != weapon.m_shared.m_skillType || Effects.Dots.IsDot(hit)) return;
                float chance = Enchantments.MythicValue(weapon, "TwinBlade");
                if (chance <= 0 || UnityEngine.Random.value * 100f >= chance) return;
                string item = Enchantments.Prefab(weapon);
                if (string.IsNullOrEmpty(item)) return;
                var twin = hit.Clone();
                twin.m_damage = new HitData.DamageTypes { m_spirit = hit.m_damage.GetTotalDamage() };
                player.StartCoroutine(Twin(player, __instance, item, twin));
            }
        }

        private static System.Collections.IEnumerator Twin(Player owner, Character target, string item, HitData hit)
        {
            Vector3 center = target.GetCenterPoint();
            Vector3 side = owner.transform.position - target.transform.position; side.y = 0;
            side = side.sqrMagnitude > 1e-4f ? side.normalized : -target.transform.forward;
            Vector3 pivot = center + side * TwinReach;
            var sword = MythicVfx.SpawnSpectral(item, pivot, Quaternion.LookRotation(-side));
            bool struck = false;
            for (float t = 0; t <= TwinSwing && sword; t += Time.deltaTime)
            {
                float k = Mathf.SmoothStep(0, 1, t / TwinSwing);
                sword.transform.rotation = Quaternion.LookRotation(Quaternion.Euler(0, Mathf.Lerp(-TwinArc / 2, TwinArc / 2, k), 0) * -side);
                if (!struck && k >= .5f)
                {
                    struck = true;
                    if (target && !target.IsDead())
                    {
                        hit.m_point = target.GetCenterPoint(); hit.m_dir = -side;
                        twinStriking = true;
                        try { target.Damage(hit); } finally { twinStriking = false; }
                    }
                }
                yield return null;
            }
            yield return new WaitForSeconds(.05f);
            if (sword && ZNetScene.instance) ZNetScene.instance.Destroy(sword);
        }

        // --- Whirlwind: the weapon's special (secondary) attack, the atgeir's spin, pulls every creature it hits towards the
        // player and stuns it for Value seconds (half as long for bosses): it neither moves nor attacks. The stun lives in
        // the creature's ZDO, set by its owner (the hitting player asks it with an RPC) ---

        private const float WhirlPull = 120f;
        private const string StunRpc = "Overhaul_MythicStun";
        private static readonly int StunKey = "overhaul_mythic_stun_end".GetStableHashCode();

        internal static bool Stunned(Character c)
        {
            var zdo = c && c.m_nview && c.m_nview.IsValid() ? c.m_nview.GetZDO() : null;
            return zdo != null && NetNow < zdo.GetFloat(StunKey, 0);
        }

        [HarmonyPatch(typeof(Game), "Start")]
        private static class StunRegister { private static void Postfix() => ZRoutedRpc.instance.Register<ZDOID, float>(StunRpc, OnStun); }

        private static void OnStun(long sender, ZDOID id, float seconds)
        {
            var c = ZNetScene.instance ? ZNetScene.instance.FindInstance(id)?.GetComponent<Character>() : null;
            if (!c || !c.m_nview.IsOwner() || c.IsDead()) return;
            if (c.IsBoss()) seconds /= 2;
            var zdo = c.m_nview.GetZDO();
            zdo.Set(StunKey, Mathf.Max(zdo.GetFloat(StunKey, 0), (float)(NetNow + seconds)));
            c.Stagger(-c.transform.forward);
        }

        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class WhirlHit
        {
            private static void Prefix(Character __instance, HitData hit)
            {
                var player = Player.m_localPlayer;
                if (hit == null || !player || __instance == player || __instance is Player || hit.GetAttacker() != player || !player.m_currentAttackIsSecondary) return;
                var weapon = player.GetCurrentWeapon();
                if (weapon == null || hit.m_skill != weapon.m_shared.m_skillType || Effects.Dots.IsDot(hit)) return;
                float seconds = Enchantments.MythicValue(weapon, "Whirlwind");
                if (seconds <= 0) return;
                // Pulled in: the hit's push turned towards the player, and stronger.
                Vector3 pull = player.transform.position - __instance.transform.position; pull.y = 0;
                if (pull.sqrMagnitude > 1e-4f) { hit.m_dir = pull.normalized; hit.m_pushForce = Mathf.Max(hit.m_pushForce, WhirlPull); }
                var zdo = __instance.m_nview && __instance.m_nview.IsValid() ? __instance.m_nview.GetZDO() : null;
                if (zdo != null && zdo.GetOwner() != 0) ZRoutedRpc.instance.InvokeRoutedRPC(zdo.GetOwner(), StunRpc, __instance.GetZDOID(), seconds);
            }
        }

        // A stunned creature's AI stands still and it starts no attack.
        [HarmonyPatch]
        private static class StunnedAI
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(MonsterAI), nameof(MonsterAI.UpdateAI));
                yield return AccessTools.Method(typeof(AnimalAI), nameof(AnimalAI.UpdateAI));
            }
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(BaseAI __instance, ref bool __result)
            {
                if (!Stunned(__instance.m_character)) return true;
                __instance.StopMoving(); __result = true; return false;
            }
        }

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
        private static class StunnedAttack
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(Humanoid __instance, ref bool __result)
            {
                if (__instance is Player || !Stunned(__instance)) return true;
                __result = false; return false;
            }
        }

        // --- Homing: the thrown weapon (the spear's special attack) goes through up to Value enemies, each taking its hit,
        // and does not fall where it stops: a spectral copy of it (MythicVfx Mythic_SpectralWeapon, narrow trail) flies back
        // to the player at 25 m/s, shaft first, and the weapon is back in the inventory and in hand (dropped at the
        // player's feet when the inventory is full) ---

        private const float HomingSpeed = 25f;
        private sealed class Pierce { internal int Left; internal readonly HashSet<Character> Through = new HashSet<Character>(); }
        private sealed class PierceHit { internal bool Through; internal ItemDrop.ItemData Kept, Returning; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Projectile, Pierce> Piercing = new System.Runtime.CompilerServices.ConditionalWeakTable<Projectile, Pierce>();

        [HarmonyPatch(typeof(Projectile), nameof(Projectile.Setup))]
        private static class HomingThrow
        {
            private static void Postfix(Projectile __instance, Character owner, ItemDrop.ItemData item)
            {
                if (!owner || owner != Player.m_localPlayer || item == null || !__instance.m_respawnItemOnHit) return;
                int through = Mathf.RoundToInt(Enchantments.MythicValue(item, "Homing"));
                if (through > 0) Piercing.GetValue(__instance, _ => new Pierce()).Left = through;
            }
        }

        [HarmonyPatch(typeof(Projectile), "OnHit")]
        private static class HomingHit
        {
            private static bool Prefix(Projectile __instance, Collider collider, out PierceHit __state)
            {
                __state = null;
                if (!Piercing.TryGetValue(__instance, out var pierce)) return true;
                var owner = __instance.m_owner;
                var c = collider ? collider.GetComponentInParent<Character>() : null;
                if (c && c == owner) return true;
                // An enemy already gone through is no new contact.
                if (c && pierce.Through.Contains(c)) return false;
                __state = new PierceHit();
                if (c && pierce.Left > 0 && !c.IsDead() && BaseAI.IsEnemy(owner, c))
                {
                    // Through it: the hit is dealt, the weapon flies on and nothing falls.
                    pierce.Left--; pierce.Through.Add(c);
                    __state.Through = true; __state.Kept = __instance.m_spawnItem;
                    __instance.m_onlyStopOnTerrain = true; __instance.m_spawnItem = null;
                    return true;
                }
                // Where it stops: no weapon falls, it comes back.
                __state.Returning = __instance.m_spawnItem; __instance.m_spawnItem = null;
                return true;
            }

            private static void Postfix(Projectile __instance, Vector3 hitPoint, PierceHit __state)
            {
                if (__state == null) return;
                if (__state.Through) { __instance.m_onlyStopOnTerrain = false; __instance.m_spawnItem = __state.Kept; return; }
                if (__state.Returning == null) return;
                Piercing.Remove(__instance);
                if (__instance.m_owner is Player player && player) player.StartCoroutine(Return(player, __state.Returning, hitPoint == Vector3.zero ? __instance.transform.position : hitPoint));
                else ItemDrop.DropItem(__state.Returning, 1, hitPoint, Quaternion.identity);
            }
        }

        private static System.Collections.IEnumerator Return(Player owner, ItemDrop.ItemData item, Vector3 from)
        {
            var copy = MythicVfx.SpawnSpectral(Enchantments.Prefab(item), from, Quaternion.identity, .15f);
            Vector3 position = from;
            while (owner && !owner.IsDead())
            {
                Vector3 hand = owner.transform.position + Vector3.up * 1.3f + owner.transform.right * .3f;
                Vector3 to = hand - position;
                float step = HomingSpeed * Time.deltaTime;
                if (to.magnitude <= step) break;
                position += to.normalized * step;
                if (copy) { copy.transform.position = position; copy.transform.rotation = Quaternion.LookRotation(to); }
                yield return null;
            }
            if (copy && ZNetScene.instance) ZNetScene.instance.Destroy(copy);
            if (!owner || owner.IsDead()) { ItemDrop.DropItem(item, 1, position, Quaternion.identity); yield break; }
            if (owner.GetInventory().AddItem(item)) { if (!owner.IsItemEquiped(item)) owner.EquipItem(item); }
            else ItemDrop.DropItem(item, 1, owner.transform.position + Vector3.up, Quaternion.identity);
        }

        // --- Prospector: ore veins and rocks hit with the pickaxe drop each of their items twice with Value % chance. The rock
        // is handled on its owner's machine: the miner's chance is published in their ZDO, and the owner tells the miner
        // when the ore was doubled, who sees the game's bonus craft effect (its crystal chime) with a golden "+N" and a small
        // golden burst (MythicVfx Mythic_Prospect) where the pickaxe struck ---

        private const string ProspectRpc = "Overhaul_Prospect";
        private static readonly int ProspectKey = "overhaul_prospector".GetStableHashCode();
        private static Player miner;
        private static Vector3 minePoint;

        [HarmonyPatch(typeof(Game), "Start")]
        private static class ProspectRegister { private static void Postfix() => ZRoutedRpc.instance.Register<Vector3, int>(ProspectRpc, (sender, point, count) => Prospected(point, count)); }

        // The local player's chance, published for the rocks' owners.
        [HarmonyPatch(typeof(Player), "Update")]
        private static class ProspectPublish
        {
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer || !__instance.m_nview || !__instance.m_nview.IsValid()) return;
                float chance = Enchantments.MythicValue(__instance.GetCurrentWeapon(), "Prospector");
                var zdo = __instance.m_nview.GetZDO();
                if (!Mathf.Approximately(zdo.GetFloat(ProspectKey, 0), chance)) zdo.Set(ProspectKey, chance);
            }
        }

        [HarmonyPatch]
        private static class ProspectMining
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(MineRock), "RPC_Hit");
                yield return AccessTools.Method(typeof(MineRock5), "RPC_Damage");
            }
            private static void Prefix(HitData hit)
            {
                miner = hit != null && hit.m_skill == Skills.SkillType.Pickaxes ? hit.GetAttacker() as Player : null;
                if (miner) minePoint = hit.m_point;
            }
            private static void Finalizer() => miner = null;
        }

        [HarmonyPatch(typeof(DropTable), nameof(DropTable.GetDropList), typeof(int))]
        private static class ProspectDrops
        {
            private static void Postfix(List<GameObject> __result)
            {
                if (!miner || __result == null || __result.Count == 0 || !miner.m_nview || !miner.m_nview.IsValid()) return;
                float chance = miner == Player.m_localPlayer ? Enchantments.MythicValue(miner.GetCurrentWeapon(), "Prospector") : miner.m_nview.GetZDO().GetFloat(ProspectKey, 0);
                if (chance <= 0 || UnityEngine.Random.value * 100f >= chance) return;
                int count = __result.Count;
                for (int i = 0; i < count; i++) __result.Add(__result[i]);
                if (miner == Player.m_localPlayer) Prospected(minePoint, count);
                else ZRoutedRpc.instance.InvokeRoutedRPC(miner.m_nview.GetZDO().GetOwner(), ProspectRpc, minePoint, count);
            }
        }

        private static void Prospected(Vector3 point, int count)
        {
            if (InventoryGui.instance) InventoryGui.instance.m_craftBonusEffect.Create(point, Quaternion.identity);
            if (DamageText.instance) DamageText.instance.ShowText(DamageText.TextType.Bonus, point + Vector3.up * .5f, "+" + count, true);
            MythicVfx.Play("Mythic_Prospect", point, 2f);
        }

        // --- SpectralDagger: each hit of the weapon on a creature has Value % chance to call a spectral copy of the dagger
        // (MythicVfx Mythic_SpectralWeapon with a 40 cm trail, seen by every player): it pops up above the player's right
        // shoulder, point up and edge forward (with Mythic_DaggerPop), backflips once, turns to the creature hit and flies at it at 25 m/s, point first, dealing 80 % of the hit's damage,
        // half as pierce and half as spirit damage ---

        private const float DaggerPop = .1f, DaggerFlip = .35f, DaggerTurn = .1f, DaggerSpeed = 25f, DaggerDamage = .8f;
        private static bool daggerStriking;

        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class SpectralDaggerCall
        {
            private static void Prefix(Character __instance, HitData hit)
            {
                var player = Player.m_localPlayer;
                if (daggerStriking || hit == null || !player || __instance == player || __instance is Player || hit.GetAttacker() != player || __instance.IsDead()) return;
                var weapon = player.GetCurrentWeapon();
                if (weapon == null || hit.m_skill != weapon.m_shared.m_skillType || Effects.Dots.IsDot(hit)) return;
                float chance = Enchantments.MythicValue(weapon, "SpectralDagger");
                if (chance <= 0 || UnityEngine.Random.value * 100f >= chance) return;
                string item = Enchantments.Prefab(weapon);
                if (string.IsNullOrEmpty(item)) return;
                var strike = hit.Clone();
                float damage = hit.m_damage.GetTotalDamage() * DaggerDamage;
                strike.m_damage = new HitData.DamageTypes { m_pierce = damage / 2, m_spirit = damage / 2 };
                player.StartCoroutine(SpectralDagger(player, __instance, item, strike));
            }
        }

        private static System.Collections.IEnumerator SpectralDagger(Player owner, Character target, string item, HitData hit)
        {
            Vector3 Shoulder() => owner.transform.position + Vector3.up * 2.1f + owner.transform.right * .45f - owner.transform.forward * .1f;
            // Point up and edge forward (the weapon models lie in their XZ plane, edge along -X).
            Quaternion Up() => Quaternion.LookRotation(Vector3.up, -owner.transform.right);
            MythicVfx.Spawn("Mythic_DaggerPop", Shoulder(), Up());
            var dagger = MythicVfx.SpawnSpectral(item, Shoulder(), Up(), .4f);
            if (!dagger) yield break;
            for (float t = 0; t < DaggerPop + DaggerFlip + DaggerTurn && dagger && owner; t += Time.deltaTime)
            {
                Quaternion rotation = Up();
                if (t < DaggerPop) dagger.transform.localScale = Vector3.one * Mathf.Lerp(.2f, 1, t / DaggerPop);
                else
                {
                    dagger.transform.localScale = Vector3.one;
                    if (t < DaggerPop + DaggerFlip) rotation = Quaternion.AngleAxis(-360 * Mathf.SmoothStep(0, 1, (t - DaggerPop) / DaggerFlip), owner.transform.right) * rotation;
                    else if (target) rotation = Quaternion.Slerp(rotation, Quaternion.LookRotation(target.GetCenterPoint() - dagger.transform.position), (t - DaggerPop - DaggerFlip) / DaggerTurn);
                }
                dagger.transform.SetPositionAndRotation(Shoulder(), rotation);
                yield return null;
            }
            while (dagger && target && !target.IsDead())
            {
                Vector3 to = target.GetCenterPoint() - dagger.transform.position;
                float step = DaggerSpeed * Time.deltaTime;
                dagger.transform.rotation = Quaternion.LookRotation(to);
                if (to.magnitude > step) { dagger.transform.position += to.normalized * step; yield return null; continue; }
                hit.m_point = target.GetCenterPoint(); hit.m_dir = to.normalized;
                daggerStriking = true;
                try { target.Damage(hit); } finally { daggerStriking = false; }
                break;
            }
            if (dagger && ZNetScene.instance) ZNetScene.instance.Destroy(dagger);
        }
    }
}
