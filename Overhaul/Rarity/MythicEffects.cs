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
        private static readonly HashSet<string> Implemented = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FanVolley" };
        internal static bool Known(string id) => Implemented.Contains(id);

        // --- FanVolley: the weapon fires its Value extra projectiles, every projectile (others bonuses included) spread
        // evenly over a narrow fan around the aim, with perfect accuracy ---

        private const float FanAngle = 10f;

        // Angle of projectile index among count, evenly spread over the fan (0 for a single projectile).
        private static float Angle(int index, int count) => count < 2 ? 0 : -FanAngle / 2 + index * FanAngle / (count - 1);

        // Rotation of a direction by a fan angle, around the aim's own up axis (so the fan stays flat on screen).
        private static Vector3 Turn(Vector3 direction, float angle)
        {
            Vector3 right = Vector3.Cross(Vector3.up, direction);
            if (right.sqrMagnitude < 1e-4f) right = Vector3.right;
            return Quaternion.AngleAxis(angle, Vector3.Cross(direction, right.normalized)) * direction;
        }

        private static int Extra(ItemDrop.ItemData weapon) => Mathf.RoundToInt(Enchantments.MythicValue(weapon, "FanVolley"));

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
    }
}
