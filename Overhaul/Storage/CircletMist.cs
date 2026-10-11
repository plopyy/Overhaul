using System.Collections.Generic;
using EquipmentAndQuickSlots.src.MultiUtility;
using UnityEngine;

namespace Overhaul.Storage
{
    // Spirit circlet: thins only the Mistlands mist (ParticleMist) around its wearer. Mist particles are at 10 %
    // of their opacity up to twice the radius of a wisp's clearing, then fade back to full opacity at three times
    // that radius, measured to the particle's edge: the mist also has huge distant particles that can wrap the wearer
    // while their centre is far away. Distance fog, smoke and every other effect stay untouched. Each particle keeps
    // the colour the game gave it when emitted (remembered by its random seed): only its opacity is scaled, so
    // particles never jump between the game's colour and another one.
    internal sealed class CircletMist : MonoBehaviour
    {
        private const float Thinned = .1f, InnerRadius = 2f, OuterRadius = 3f, DefaultWispRadius = 6f;
        private static float wispRadius;
        private ParticleSystem system;
        private ParticleSystem.Particle[] particles;
        private readonly Dictionary<uint, Color32> originals = new Dictionary<uint, Color32>();
        private readonly HashSet<uint> alive = new HashSet<uint>();
        private bool thinned;

        // Diagnostic of the mist breathing, once a second while the circlet is worn: average start opacity and life
        // curve of the nearby particles, and the colours of the mist material (to tell which one changes over time).
        private int nearCount;
        private float startSum, lifeSum, nextReport;

        private void Report()
        {
            nextReport = Time.time + 1f;
            var material = GetComponent<ParticleSystemRenderer>() ? GetComponent<ParticleSystemRenderer>().sharedMaterial : null;
            string colours = "";
            if (material)
                foreach (string property in new[] { "_Color", "_TintColor", "_BaseColor", "_EmissionColor" })
                    if (material.HasProperty(property)) colours += " " + property + "=" + material.GetColor(property).a.ToString("0.000");
            Utility.Log.LogInfo("[CircletMist] near " + nearCount + ", start alpha " + (nearCount > 0 ? startSum / nearCount : 0).ToString("0.000")
                + ", life alpha " + (nearCount > 0 && system.colorOverLifetime.enabled ? (lifeSum / nearCount).ToString("0.000") : "off")
                + ", material " + (material ? material.name + "/" + material.shader.name : "none") + colours
                + ", main alpha " + system.main.startColor.color.a.ToString("0.000"));
            nearCount = 0; startSum = lifeSum = 0;
        }

        internal static void Tick()
        {
            ParticleMist mist = ParticleMist.instance;
            if (mist && !mist.GetComponent<CircletMist>()) mist.gameObject.AddComponent<CircletMist>();
        }

        private void Awake() => system = GetComponent<ParticleSystem>();

        // Worn as helmet, utility or extra utility slot.
        private static bool SpiritWorn(Player player)
        {
            if (DvergerCirclet.IsSpirit(player.m_helmetItem) || DvergerCirclet.IsSpirit(player.m_utilityItem)) return true;
            for (int i = 0; i < MultiUtility.GetExtraCount(player); i++)
                if (DvergerCirclet.IsSpirit(MultiUtility.GetExtra(player, i))) return true;
            return false;
        }

        // The clearing radius of the wisp (demister_ball), read once from its force field.
        private static float WispRadius()
        {
            if (wispRadius > 0) return wispRadius;
            GameObject ball = ZNetScene.instance ? ZNetScene.instance.GetPrefab("demister_ball") : null;
            var field = ball ? ball.GetComponentInChildren<ParticleSystemForceField>(true) : null;
            wispRadius = field && field.endRange > 0 ? field.endRange : DefaultWispRadius;
            return wispRadius;
        }

        private void LateUpdate()
        {
            Player player = Player.m_localPlayer;
            bool wearing = player && !player.IsDead() && SpiritWorn(player);
            // Particles keep the colour they were given: restore the base once when the circlet comes off.
            if (!wearing && !thinned) return;
            int count = system.particleCount;
            if (count == 0) { thinned = wearing; originals.Clear(); return; }
            if (particles == null || particles.Length < system.main.maxParticles) particles = new ParticleSystem.Particle[system.main.maxParticles];
            count = system.GetParticles(particles);
            Vector3 center = wearing ? player.transform.position : Vector3.zero;
            float inner = WispRadius() * InnerRadius, outer = WispRadius() * OuterRadius;
            alive.Clear();
            for (int i = 0; i < count; i++)
            {
                uint seed = particles[i].randomSeed;
                alive.Add(seed);
                if (!originals.TryGetValue(seed, out Color32 color)) originals[seed] = color = particles[i].startColor;
                if (wearing)
                {
                    float distance = Mathf.Max(0f, Vector3.Distance(particles[i].position, center) - particles[i].GetCurrentSize(system) * .5f);
                    // Diagnostic: what the nearby particles show (start opacity and life curve).
                    if (distance < inner)
                    {
                        nearCount++; startSum += color.a / 255f;
                        if (system.colorOverLifetime.enabled)
                            lifeSum += system.colorOverLifetime.color.Evaluate(1f - particles[i].remainingLifetime / Mathf.Max(.01f, particles[i].startLifetime)).a;
                    }
                    color.a = (byte)Mathf.RoundToInt(color.a * Mathf.Lerp(Thinned, 1f, Mathf.InverseLerp(inner, outer, distance)));
                }
                particles[i].startColor = color;
            }
            system.SetParticles(particles, count);
            if (wearing && Time.time >= nextReport) Report();
            // Forget the particles that died, and everything once the circlet is off (colours restored above).
            if (!wearing) originals.Clear();
            else if (originals.Count > count * 2) foreach (uint seed in new List<uint>(originals.Keys)) if (!alive.Contains(seed)) originals.Remove(seed);
            thinned = wearing;
        }
    }
}
