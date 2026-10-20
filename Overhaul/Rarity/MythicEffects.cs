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
        // 8 m in 0.4 s at a steady speed, along the character's heading tilted to the aim's pitch, growing as it goes (as its
        // visual, 0.85 to 1.6 times its size), hitting every enemy it crosses once for Value % of the attack's damage ---

        private const float WindReach = 8f, WindFlight = .4f, WindRadius = 1.1f, WindGrowth = 1.9f;

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
                        player.StartCoroutine(Wave(player, origin, forward, AttackHit(__instance, player, dragon), weapon.m_shared.m_hitEffect, DragonReach, DragonFlight, DragonRadius,
                            t => 1 - Mathf.Pow(1 - t, 2.5f)));
                    }
                }

                float wind = Enchantments.MythicValue(weapon, "WindBlade");
                if (wind > 0 && __instance.m_attackChainLevels > 1 && __instance.m_currentAttackCainLevel == __instance.m_attackChainLevels - 1)
                {
                    // Along the character's heading, tilted to the aim's pitch (its vertical direction only).
                    float pitch = Mathf.Asin(Mathf.Clamp(player.GetLookDir().y, -1, 1));
                    Vector3 aim = forward * Mathf.Cos(pitch) + Vector3.up * Mathf.Sin(pitch);
                    var rotation = Quaternion.LookRotation(aim);
                    // The prefab carries its wave 1 m up and 0.5 m ahead of its origin: placed so the wave starts at the chest.
                    MythicVfx.Spawn("Mythic_WindBlade", origin - rotation * Vector3.up, rotation);
                    player.StartCoroutine(Wave(player, origin, aim, AttackHit(__instance, player, wind), weapon.m_shared.m_hitEffect, WindReach, WindFlight, WindRadius, t => t, WindGrowth));
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
                player.StartCoroutine(Aftershock(player, point, hit, weapon.m_shared.m_hitEffect));
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

        private static System.Collections.IEnumerator Aftershock(Player owner, Vector3 point, HitData hit, EffectList impact)
        {
            yield return new WaitForSeconds(QuakeDelay);
            if (!owner || owner.IsDead()) yield break;
            MythicVfx.Spawn("Mythic_Earthquake", point, Quaternion.identity);
            yield return Ring(owner, point + Vector3.up * .5f, hit, impact, QuakeReach, QuakeSpread, t => t);
        }

        // The attack's hit at percent % of its damage: the weapon's damage with the attack's multiplier and the skill factor,
        // its tool tier, push, backstab and stagger.
        // A spectral weapon's damage: percent % of damage, its physical part (and its spirit) as spirit damage, its elements
        // (the weapon's elemental enchantments, the leveling elements) kept as they are; pierceShare of the physical part
        // stays pierce instead.
        private static HitData.DamageTypes Spectral(HitData.DamageTypes damage, float percent, float pierceShare = 0)
        {
            float k = percent / 100f, physical = (damage.m_damage + damage.m_blunt + damage.m_slash + damage.m_pierce) * k;
            return new HitData.DamageTypes
            {
                m_pierce = physical * pierceShare, m_spirit = physical * (1 - pierceShare) + damage.m_spirit * k,
                m_fire = damage.m_fire * k, m_frost = damage.m_frost * k, m_lightning = damage.m_lightning * k, m_poison = damage.m_poison * k,
            };
        }

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

        // What a wave or ring can hit, as a melee attack does: creatures, the world's destructibles (rocks, trees, logs...)
        // and pieces' layers; the latter are left alone below so player buildings never take wave damage.
        private static int WaveMask => waveMask != 0 ? waveMask : waveMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece",
            "piece_nonsolid", "terrain", "character", "character_net", "character_ghost", "hitbox", "character_noenv", "vehicle");

        // One contact of a wave or ring: the thing it hits (a creature that is an enemy or a training dummy, or a world
        // destructible, never a building piece), once per wave, takes the hit with the weapon's own impact effect and sound.
        // Returns the point struck, null when nothing was.
        private static Vector3? Strike(Player owner, Collider collider, Vector3 from, Vector3 dir, HitData hit, EffectList impact, HashSet<IDestructible> struck)
        {
            var target = collider.GetComponentInParent<IDestructible>();
            if (target == null || target is WearNTear || target as Component == owner) return null;
            if (target is Character c && (c.IsDead() || !(BaseAI.IsEnemy(owner, c) || c.m_faction == Character.Faction.TrainingDummy))) return null;
            if (!struck.Add(target)) return null;
            var each = hit.Clone();
            each.m_point = Contact(collider, from); each.m_dir = dir; each.m_hitCollider = collider;
            impact?.Create(each.m_point, Quaternion.identity, null, 1f, -1, owner.GetZDOID());
            target.Damage(each);
            return each.m_point;
        }

        // The point of collider nearest to from. ClosestPoint gives back from itself on concave mesh colliders (trees, rocks):
        // then where a ray from from towards the collider's middle meets it, else its bounds' nearest point.
        private static Vector3 Contact(Collider collider, Vector3 from)
        {
            Vector3 point = collider.ClosestPoint(from);
            if ((point - from).sqrMagnitude > 1e-6f) return point;
            Vector3 to = collider.bounds.center - from;
            if (to.sqrMagnitude > 1e-6f && collider.Raycast(new Ray(from, to), out var ray, to.magnitude + collider.bounds.extents.magnitude)) return ray.point;
            return collider.bounds.ClosestPoint(from);
        }

        // A wave flying from origin along forward up to reach in flight seconds, its front at progress(time / flight) of
        // reach: each frame, everything between the origin and the front, within radius (growing to grow times it by the
        // end), is struck if it was not yet.
        private static System.Collections.IEnumerator Wave(Player owner, Vector3 origin, Vector3 forward, HitData hit, EffectList impact, float reach, float flight, float radius, Func<float, float> progress, float grow = 1)
        {
            var struck = new HashSet<IDestructible>();
            for (float t = 0; t <= flight; t += Time.deltaTime)
            {
                if (!owner || owner.IsDead()) yield break;
                float front = Mathf.Max(reach * progress(t / flight), .3f);
                foreach (var collider in Physics.OverlapCapsule(origin + forward * .3f, origin + forward * front, radius * Mathf.Lerp(1, grow, t / flight), WaveMask))
                    Strike(owner, collider, origin, forward, hit, impact, struck);
                yield return null;
            }
        }

        // A ring widening around origin up to reach in flight seconds, its edge at progress(time / flight) of reach: each
        // frame, everything inside the edge is struck if it was not yet, pushed away from the centre.
        private static System.Collections.IEnumerator Ring(Player owner, Vector3 origin, HitData hit, EffectList impact, float reach, float flight, Func<float, float> progress)
        {
            var struck = new HashSet<IDestructible>();
            for (float t = 0; t <= flight; t += Time.deltaTime)
            {
                if (!owner || owner.IsDead()) yield break;
                foreach (var collider in Physics.OverlapSphere(origin, Mathf.Max(reach * progress(t / flight), .5f), WaveMask))
                {
                    Vector3 away = Contact(collider, origin) - origin; away.y = 0;
                    Strike(owner, collider, origin, away.sqrMagnitude > 1e-4f ? away.normalized : owner.transform.forward, hit, impact, struck);
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

        // --- RicochetAxe: the weapon's special (secondary) attack also throws three spectral copies of it (MythicVfx
        // Mythic_SpectralWeapon, seen by every player, 1.6 times as big), spinning flat on themselves (their trail following only their path):
        // one straight ahead, two curving out 1.5 m on each side in an arc, all three meeting 6 m ahead along the aim
        // (pitch included), where they vanish. Each axe strikes everything it passes through
        // within 1.5 m once (Strike: enemies and world destructibles, with the weapon's impact effect and a spectral hit), for
        // Value % of the attack's damage (Spectral: physical as spirit, elements kept) ---

        private const float RicochetRange = 6f, RicochetSpeed = 20f, RicochetSpin = 2520f, RicochetTrail = .55f, RicochetSpread = 1.5f, RicochetRadius = 1.5f, RicochetScale = 1.6f;

        [HarmonyPatch(typeof(Attack), "DoMeleeAttack")]
        private static class RicochetThrow
        {
            private static void Postfix(Attack __instance)
            {
                var player = __instance.m_character as Player;
                var weapon = __instance.m_weapon;
                if (!player || player != Player.m_localPlayer || weapon == null || !player.m_currentAttackIsSecondary) return;
                float percent = Enchantments.MythicValue(weapon, "RicochetAxe");
                if (percent <= 0) return;
                string item = Enchantments.Prefab(weapon);
                if (string.IsNullOrEmpty(item)) return;
                var hit = AttackHit(__instance, player, 100);
                hit.m_damage = Spectral(hit.m_damage, percent);
                Vector3 flat = player.transform.forward; flat.y = 0; flat.Normalize();
                Vector3 forward = Quaternion.AngleAxis(-Mathf.Asin(Mathf.Clamp(player.GetLookDir().y, -1, 1)) * Mathf.Rad2Deg, Vector3.Cross(Vector3.up, flat)) * flat;
                Vector3 start = player.transform.position + Vector3.up * 1.2f + flat * .6f;
                Vector3 side = Vector3.Cross(Vector3.up, flat);
                for (int lane = -1; lane <= 1; lane++)
                {
                    var axe = MythicVfx.SpawnSpectral(item, start, Quaternion.LookRotation(forward), RicochetTrail, RicochetScale, false, RicochetSpin);
                    if (axe) player.StartCoroutine(Ricochet(player, axe, hit, weapon.m_shared.m_hitEffect, start + forward * RicochetRange, side * (lane * RicochetSpread)));
                }
            }
        }

        // One axe's flight: a quadratic curve from its start to end, bowed out by bow at its middle; everything within
        // RicochetRadius of the axe is struck once.
        private static System.Collections.IEnumerator Ricochet(Player owner, GameObject axe, HitData hit, EffectList impact, Vector3 end, Vector3 bow)
        {
            var struck = new HashSet<IDestructible>();
            Vector3 start = axe.transform.position, control = (start + end) / 2 + bow * 2;
            float length = Vector3.Distance(start, end) + bow.magnitude * 1.2f, t = 0;
            // The axe's plane: its path and the throw's sideways axis (tilted with the aim); the spin is the model's own.
            Vector3 up = Vector3.Cross(end - start, bow.sqrMagnitude > 1e-4f ? bow : Vector3.Cross(Vector3.up, end - start));
            if (Vector3.Dot(up, Vector3.up) < 0) up = -up;
            while (axe && t < 1)
            {
                t = Mathf.Min(1, t + RicochetSpeed * Time.deltaTime / length);
                Vector3 position = (1 - t) * (1 - t) * start + 2 * (1 - t) * t * control + t * t * end;
                Vector3 dir = 2 * (1 - t) * (control - start) + 2 * t * (end - control);
                axe.transform.SetPositionAndRotation(position, Quaternion.LookRotation(dir, up));
                foreach (var collider in Physics.OverlapSphere(position, RicochetRadius, WaveMask))
                    if (Strike(owner, collider, position, dir.normalized, hit, impact, struck) is Vector3 point) MythicVfx.SpectralHit(point, dir);
                yield return null;
            }
            if (axe && ZNetScene.instance) ZNetScene.instance.Destroy(axe);
        }

        // --- TwinBlade: each hit of the weapon on a creature has Value % chance to be doubled: a spectral copy of the sword
        // (MythicVfx Mythic_SpectralWeapon, seen by every player) appears 1 m from the target on the player's side and cuts
        // through it in a 100 degree arc in 0.45 s, half as big again as the sword, dealing the hit's damage again halfway through
        // (Spectral: physical as spirit, elements kept), with a spectral hit ---

        private const float TwinSwing = .45f, TwinArc = 100f, TwinReach = 1.3f, TwinScale = 1.5f;
        private static bool twinStriking;

        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class TwinBlade
        {
            // Before the hit gets the player's gear elements (EffectPatches.Dealt), which the copy's own hit gets again.
            [HarmonyPriority(Priority.First)]
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
                twin.m_damage = Spectral(hit.m_damage, 100);
                player.StartCoroutine(Twin(player, __instance, item, twin));
            }
        }

        private static System.Collections.IEnumerator Twin(Player owner, Character target, string item, HitData hit)
        {
            Vector3 center = target.GetCenterPoint();
            Vector3 side = owner.transform.position - target.transform.position; side.y = 0;
            side = side.sqrMagnitude > 1e-4f ? side.normalized : -target.transform.forward;
            Vector3 pivot = center + side * TwinReach;
            var sword = MythicVfx.SpawnSpectral(item, pivot, Quaternion.LookRotation(-side), 0, TwinScale);
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
                        MythicVfx.SpectralHit(hit.m_point, hit.m_dir);
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
        // to the player at 25 m/s, shaft first, and once the throw is over the weapon is back in the inventory, and in hand
        // if the hand is empty (dropped at the player's feet when the inventory is full) ---

        private const float HomingSpeed = 25f;
        private sealed class Pierce { internal int Left; internal readonly HashSet<Character> Through = new HashSet<Character>(); }
        private sealed class PierceHit { internal bool Through; internal ItemDrop.ItemData Kept, Returning; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Projectile, Pierce> Piercing = new System.Runtime.CompilerServices.ConditionalWeakTable<Projectile, Pierce>();

        // The thrown weapon each live projectile of the player carries: extra projectiles of a throw (projectile bonuses) carry
        // the same item, so only the first one does; the others deal their hit and vanish (no copy of the weapon falls or
        // comes back).
        private static readonly Dictionary<ItemDrop.ItemData, Projectile> Carried = new Dictionary<ItemDrop.ItemData, Projectile>();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Projectile, object> ExtraThrown = new System.Runtime.CompilerServices.ConditionalWeakTable<Projectile, object>();

        // An extra projectile of a throw vanishes where it stops instead of staying stuck there.
        [HarmonyPatch(typeof(Projectile), "OnHit")]
        private static class ExtraVanish
        {
            private static void Postfix(Projectile __instance)
            {
                if (!__instance.m_didHit || !ExtraThrown.TryGetValue(__instance, out _)) return;
                ExtraThrown.Remove(__instance);
                var view = __instance.GetComponent<ZNetView>();
                if (view && view.IsValid() && view.IsOwner() && ZNetScene.instance) ZNetScene.instance.Destroy(__instance.gameObject);
            }
        }

        [HarmonyPatch(typeof(Projectile), nameof(Projectile.Setup))]
        private static class HomingThrow
        {
            private static void Postfix(Projectile __instance, Character owner, ItemDrop.ItemData item)
            {
                if (!owner || owner != Player.m_localPlayer || item == null || !__instance.m_respawnItemOnHit) return;
                // Only a carrier still in flight makes this one an extra of the same throw: the previous throw's projectile can
                // linger where it stopped, and a new throw is a carrier of its own.
                if (Carried.TryGetValue(item, out var carrier) && carrier && carrier != __instance && !carrier.m_didHit) { __instance.m_spawnItem = null; ExtraThrown.Add(__instance, ExtraThrown); }
                else Carried[item] = __instance;
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
                // Where it stops: no weapon falls, it comes back (an extra projectile carries none and just vanishes).
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
            // The throw has taken the spear out of the inventory: it comes back once that attack is over (3 s at most), as
            // equipping in the middle of an attack leaves the character stuck in it.
            for (float wait = 0; owner && !owner.IsDead() && owner.InAttack() && wait < 3f; wait += Time.deltaTime) yield return null;
            if (!owner || owner.IsDead()) { ItemDrop.DropItem(item, 1, position, Quaternion.identity); yield break; }
            // Back in hand unless another weapon was taken up meanwhile.
            if (owner.GetInventory().AddItem(item)) { if (!owner.IsItemEquiped(item) && owner.m_rightItem == null) owner.EquipItem(item); }
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

        // --- SpectralDagger: each hit of the weapon on a creature has Value % chance to call spectral copies of the dagger
        // (MythicVfx Mythic_SpectralWeapon with a 40 cm trail, seen by every player): one, plus the player's extra projectiles
        // (enchantment lines and leveling bonus). Each pops up (with Mythic_DaggerPop) point up in the next free place of a half
        // circle over the head, from the right shoulder to the left one (4 places, a place is free again once its dagger flies
        // off; none left, no more daggers), front-flips once, turns to the creature hit and flies at it at 25 m/s, point first,
        // dealing 25 % of the hit's damage (Spectral: half its physical part as pierce, half as spirit, elements kept), in a blue burst (Mythic_DaggerHit).
        // Daggers of one call pop 0.08 s apart ---

        private const float DaggerPop = .1f, DaggerFlip = .35f, DaggerTurn = .1f, DaggerSpeed = 25f, DaggerDamage = 25f, DaggerStagger = .08f;
        private static bool daggerStriking;
        // Until when each place is taken (a dagger holding it until it flies off, at the latest once its flip is due to end).
        private static readonly float[] DaggerPlaces = new float[4];

        // Place i of the half circle over the player's head (0: right shoulder, 3: left shoulder).
        private static Vector3 DaggerPlace(Player owner, int i)
        {
            float a = (15 + 50 * i) * Mathf.Deg2Rad;
            var t = owner.transform;
            return t.position + Vector3.up * 1.75f - t.forward * .1f + (t.right * Mathf.Cos(a) + Vector3.up * Mathf.Sin(a)) * .6f;
        }

        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class SpectralDaggerCall
        {
            // Before the hit gets the player's gear elements (EffectPatches.Dealt), which the dagger's own hit gets again.
            [HarmonyPriority(Priority.First)]
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
                strike.m_damage = Spectral(hit.m_damage, DaggerDamage, .5f);
                int count = 1 + Mathf.RoundToInt(Effects.Effects.Get(player, "ProjectileCount"))
                    + (UnityEngine.Random.value < Leveling.LevelingEffects.Bonus(player, "projectile") ? 1 : 0);
                for (int i = 0, place = 0; i < count; i++)
                {
                    while (place < DaggerPlaces.Length && Time.time < DaggerPlaces[place]) place++;
                    if (place >= DaggerPlaces.Length) break;
                    DaggerPlaces[place] = Time.time + i * DaggerStagger + DaggerPop + DaggerFlip + DaggerTurn + .1f;
                    player.StartCoroutine(SpectralDagger(player, __instance, item, strike.Clone(), place, i * DaggerStagger));
                }
            }
        }

        private static System.Collections.IEnumerator SpectralDagger(Player owner, Character target, string item, HitData hit, int place, float delay)
        {
            if (delay > 0) yield return new WaitForSeconds(delay);
            if (!owner) yield break;
            Vector3 Shoulder() => DaggerPlace(owner, place);
            // Point up, the blade's curve towards the back (the weapon models lie in their XZ plane).
            Quaternion Up() => Quaternion.LookRotation(Vector3.up, owner.transform.right);
            var dagger = MythicVfx.SpawnSpectral(item, Shoulder(), Up(), .4f, 1, true);
            if (!dagger) yield break;
            for (float t = 0; t < DaggerPop + DaggerFlip + DaggerTurn && dagger && owner; t += Time.deltaTime)
            {
                Quaternion rotation = Up();
                if (t < DaggerPop) dagger.transform.localScale = Vector3.one * Mathf.Lerp(.2f, 1, t / DaggerPop);
                else
                {
                    dagger.transform.localScale = Vector3.one;
                    if (t < DaggerPop + DaggerFlip) rotation = Quaternion.AngleAxis(360 * Mathf.SmoothStep(0, 1, (t - DaggerPop) / DaggerFlip), owner.transform.right) * rotation;
                    else if (target) rotation = Quaternion.Slerp(rotation, Quaternion.LookRotation(target.GetCenterPoint() - dagger.transform.position, owner.transform.right), (t - DaggerPop - DaggerFlip) / DaggerTurn);
                }
                dagger.transform.SetPositionAndRotation(Shoulder(), rotation);
                yield return null;
            }
            // The place is free for the next dagger once this one flies off.
            DaggerPlaces[place] = 0;
            while (dagger && target && !target.IsDead())
            {
                Vector3 to = target.GetCenterPoint() - dagger.transform.position;
                float step = DaggerSpeed * Time.deltaTime;
                // Point first, the blade upright and its curve up (the front flip carried on by a quarter turn).
                dagger.transform.rotation = Quaternion.LookRotation(to, owner.transform.right);
                if (to.magnitude > step) { dagger.transform.position += to.normalized * step; yield return null; continue; }
                hit.m_point = target.GetCenterPoint(); hit.m_dir = to.normalized;
                MythicVfx.SpectralHit(hit.m_point, hit.m_dir, true);
                owner.GetCurrentWeapon()?.m_shared.m_hitEffect.Create(hit.m_point, Quaternion.identity, null, 1f, -1, owner.GetZDOID());
                daggerStriking = true;
                try { target.Damage(hit); } finally { daggerStriking = false; }
                break;
            }
            if (dagger && ZNetScene.instance) ZNetScene.instance.Destroy(dagger);
        }
    }
}
