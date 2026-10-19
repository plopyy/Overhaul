using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Overhaul.Rarity
{
    // Code of the mythic enchantments of RaritySystem.cfg.
    internal static class MythicEffects
    {
        private static readonly HashSet<string> Implemented = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FanVolley", "ZeusWrath", "DragonFist", "WindBlade", "BladeNova" };
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

        // --- BladeNova: each attack of the weapon has Value % chance to send 10 spectral daggers out from a circle around the
        // player (MythicVfx Mythic_BladeNova), to 6 m in 0.35 s: every enemy the widening circle of daggers reaches is
        // hit once for 60 % of the attack's damage, all as pierce damage ---

        private const float NovaReach = 6f, NovaFlight = .35f, NovaDamage = 60f;

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
                float nova = Enchantments.MythicValue(weapon, "BladeNova");
                if (nova > 0 && UnityEngine.Random.value * 100f < nova)
                {
                    MythicVfx.Spawn("Mythic_BladeNova", player.transform.position, Quaternion.LookRotation(forward));
                    var hit = AttackHit(__instance, player, NovaDamage);
                    hit.m_damage = new HitData.DamageTypes { m_pierce = hit.m_damage.GetTotalDamage() };
                    player.StartCoroutine(Ring(player, origin, hit, NovaReach, NovaFlight, t => 1 - (1 - t) * (1 - t)));
                }
            }
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
    }
}
