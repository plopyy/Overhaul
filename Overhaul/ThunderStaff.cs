using HarmonyLib;
using UnityEngine;

namespace Overhaul
{
    // Lightning Strike staff (StaffThunderBlood): its lightning falls at once where the player aims instead of after a
    // flying projectile. The game still fires its projectiles (count and damage untouched); each one is traced from the
    // camera through the crosshair - the first exactly on it, the others spread around it (in a fan with Fan Volley,
    // else at random within the weapon's accuracy) - moved to the first surface met and hits there at once, so the
    // game spawns its own lightning (staff_thunderblood_aoe). Every red colour of the lightning is turned blue-white.
    internal static class ThunderStaff
    {
        private const string ProjectileName = "staff_thunderblood_projectile", LightningName = "staff_thunderblood_aoe";
        private const float MaxDistance = 100f;

        // The burst being fired: its attack (for the final projectile count) and the index of the next projectile.
        private static Attack burst;
        private static int index;

        [HarmonyPatch(typeof(Attack), "FireProjectileBurst")]
        private static class Burst
        {
            private static void Prefix(Attack __instance) { burst = __instance; index = 0; }
            private static void Finalizer() => burst = null;
        }

        [HarmonyPatch(typeof(Projectile), nameof(Projectile.Setup))]
        private static class Instant
        {
            // After the other Setup patches (explosive projectiles mark it there).
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Projectile __instance, Character owner, ItemDrop.ItemData item)
            {
                if (!owner || owner != Player.m_localPlayer || !__instance.name.StartsWith(ProjectileName, System.StringComparison.Ordinal)) return;
                var camera = Utils.GetMainCamera();
                if (!camera) return;
                int count = burst != null ? Mathf.Max(1, burst.m_projectiles) : 1, i = index++;
                Vector3 origin = camera.transform.position, direction = camera.transform.forward;
                if (Rarity.MythicEffects.Extra(item) > 0) direction = Rarity.MythicEffects.Turn(direction, Rarity.MythicEffects.Angle(i % count, count));
                else if (i > 0 && burst != null)
                {
                    float spread = burst.m_projectileAccuracy;
                    direction = Quaternion.Euler(Random.Range(-spread, spread), Random.Range(-spread, spread), 0) * direction;
                }
                if (!Trace(origin, direction, owner, out RaycastHit hit))
                {
                    // Nothing within reach: the lightning falls on the ground under the far end of the line.
                    if (!Physics.Raycast(origin + direction * MaxDistance + Vector3.up * 50f, Vector3.down, out hit, 500f, Projectile.s_rayMaskSolids)) return;
                }
                __instance.transform.position = hit.point;
                __instance.OnHit(hit.collider, hit.point, false, hit.normal);
            }

            // First surface along the line, the shooter left out.
            private static bool Trace(Vector3 start, Vector3 direction, Character owner, out RaycastHit first)
            {
                first = default;
                float best = float.MaxValue;
                foreach (var hit in Physics.RaycastAll(start, direction, MaxDistance, Projectile.s_rayMaskSolids, QueryTriggerInteraction.Ignore))
                {
                    if (hit.distance >= best) continue;
                    var target = Projectile.FindHitObject(hit.collider);
                    if (target && target == owner.gameObject) continue;
                    best = hit.distance; first = hit;
                }
                return best < float.MaxValue;
            }
        }

        // Blue-white lightning: every red, orange or pink colour of the lightning prefab (lights, particle start colours,
        // colour over life and speed, trails, line renderers, materials) is turned cyan,
        // keeping its brightness and transparency. Materials are copied, as they may be shared with other effects.
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class Colours
        {
            // Cyan glow around white cores (cores are already near-white and stay so).
            private const float Hue = .53f, Saturation = .9f;

            private static Color Blue(Color c)
            {
                Color.RGBToHSV(c, out float h, out float s, out float v);
                if (s < .15f || (h > .12f && h < .8f)) return c;
                // HDR colours (above 1) keep their intensity.
                Color result = Color.HSVToRGB(Hue, s * Saturation, Mathf.Min(1f, v), true) * Mathf.Max(1f, v);
                result.a = c.a;
                return result;
            }

            private static Gradient Blue(Gradient gradient)
            {
                if (gradient == null) return null;
                var copy = new Gradient { mode = gradient.mode };
                var keys = gradient.colorKeys;
                for (int i = 0; i < keys.Length; i++) keys[i].color = Blue(keys[i].color);
                copy.SetKeys(keys, gradient.alphaKeys);
                return copy;
            }

            private static ParticleSystem.MinMaxGradient Blue(ParticleSystem.MinMaxGradient g)
            {
                switch (g.mode)
                {
                    case ParticleSystemGradientMode.Color: return new ParticleSystem.MinMaxGradient(Blue(g.color));
                    case ParticleSystemGradientMode.TwoColors: return new ParticleSystem.MinMaxGradient(Blue(g.colorMin), Blue(g.colorMax));
                    case ParticleSystemGradientMode.Gradient: return new ParticleSystem.MinMaxGradient(Blue(g.gradient));
                    case ParticleSystemGradientMode.TwoGradients: return new ParticleSystem.MinMaxGradient(Blue(g.gradientMin), Blue(g.gradientMax));
                    case ParticleSystemGradientMode.RandomColor: return new ParticleSystem.MinMaxGradient(Blue(g.gradient)) { mode = ParticleSystemGradientMode.RandomColor };
                    default: return g;
                }
            }

            private static Material Blue(Material material)
            {
                if (!material) return material;
                var copy = new Material(material) { name = material.name + "_blue" };
                foreach (string property in new[] { "_Color", "_TintColor", "_EmissionColor", "_BaseColor" })
                    if (copy.HasProperty(property)) copy.SetColor(property, Blue(copy.GetColor(property)));
                return copy;
            }

            private static void Postfix(ZNetScene __instance)
            {
                GameObject lightning = __instance.GetPrefab(LightningName);
                if (!lightning) { Utility.Log.LogWarning("Lightning Strike staff: " + LightningName + " not found, colours unchanged"); return; }
                foreach (var light in lightning.GetComponentsInChildren<Light>(true)) light.color = Blue(light.color);
                foreach (var line in lightning.GetComponentsInChildren<LineRenderer>(true))
                {
                    line.startColor = Blue(line.startColor); line.endColor = Blue(line.endColor);
                    line.sharedMaterial = Blue(line.sharedMaterial);
                }
                foreach (var system in lightning.GetComponentsInChildren<ParticleSystem>(true))
                {
                    var main = system.main; main.startColor = Blue(main.startColor);
                    var life = system.colorOverLifetime; if (life.enabled) life.color = Blue(life.color);
                    var speed = system.colorBySpeed; if (speed.enabled) speed.color = Blue(speed.color);
                    var trails = system.trails;
                    if (trails.enabled) { trails.colorOverLifetime = Blue(trails.colorOverLifetime); trails.colorOverTrail = Blue(trails.colorOverTrail); }
                    var renderer = system.GetComponent<ParticleSystemRenderer>();
                    if (renderer)
                    {
                        renderer.sharedMaterial = Blue(renderer.sharedMaterial);
                        if (renderer.trailMaterial) renderer.trailMaterial = Blue(renderer.trailMaterial);
                    }
                }
            }
        }
    }
}
