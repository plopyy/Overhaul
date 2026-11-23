using System.Reflection.Emit;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul
{
    // Lightning Strike staff (StaffThunderBlood): an aimed strike (Magic/AimedStrike: no projectile, the obliterator's
    // thunder, its lightning 2 s later on the aimed point). Every red colour of the lightning is turned blue-white.
    internal static class ThunderStaff
    {
        private const string LightningName = "staff_thunderblood_aoe";

        // Blue-white lightning: every red, orange or pink colour of the lightning prefab (lights, particle start colours,
        // colour over life and speed, trails, line renderers, materials) is turned cyan,
        // keeping its brightness and transparency. Materials are copied, as they may be shared with other effects.
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class Colours
        {
            // Cyan glow around white cores (cores are already near-white and stay so).
            private const float Hue = .56f, Saturation = .9f;

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
