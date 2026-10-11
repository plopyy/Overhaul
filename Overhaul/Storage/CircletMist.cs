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
        private float[] near;
        private float smooth, low, nextReport;
        // Seconds of the density smoothing and of the low point's fall and rise; Curve: power of the shown density.
        private const float DensitySmoothing = .75f, LowFall = 2f, LowRise = 20f, Curve = .45f;

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
            // Nearness of each particle (1 within the thinned area, 0 beyond the fade) and the mist density around the
            // wearer: the game emits the nearby particles in waves, so their number swells and falls back every few
            // seconds. Those swells are softened by lowering the nearby particles' opacity.
            if (near == null || near.Length < count) near = new float[particles.Length];
            float density = 0;
            for (int i = 0; i < count; i++)
            {
                near[i] = 0;
                if (!wearing) continue;
                float distance = Mathf.Max(0f, Vector3.Distance(particles[i].position, center) - particles[i].GetCurrentSize(system) * .5f);
                near[i] = 1f - Mathf.InverseLerp(inner, outer, distance);
                density += near[i];
            }
            // Smoothed density, and its recent low point (falls within a couple of seconds, rises back slowly).
            float dt = Time.deltaTime;
            smooth = smooth <= 0 ? density : smooth + (density - smooth) * Mathf.Min(1f, dt / DensitySmoothing);
            low = low <= 0 ? smooth : low + (smooth - low) * Mathf.Min(1f, dt / (smooth < low ? LowFall : LowRise));
            // The shown density follows a smooth power curve of the real one around the low point (no knee): the mist
            // still breathes, its peaks much lower (3 times the low point shows as about 1.6).
            float even = smooth > 0 && low > 0 ? Mathf.Min(1f, low * Mathf.Pow(smooth / low, Curve) / smooth) : 1f;
            for (int i = 0; i < count; i++)
            {
                uint seed = particles[i].randomSeed;
                alive.Add(seed);
                if (!originals.TryGetValue(seed, out Color32 color)) originals[seed] = color = particles[i].startColor;
                if (wearing) color.a = (byte)Mathf.RoundToInt(color.a * Mathf.Lerp(1f, Thinned * even, near[i]));
                particles[i].startColor = color;
            }
            if (wearing && Time.time >= nextReport)
            {
                nextReport = Time.time + 5f;
                Utility.Log.LogInfo("[CircletMist] nearby density " + density.ToString("0.0") + ", smoothed " + smooth.ToString("0.0") + ", low " + low.ToString("0.0") + ", opacity factor " + even.ToString("0.00") + " (" + count + " particles)");
            }
            system.SetParticles(particles, count);
            // Forget the particles that died, and everything once the circlet is off (colours restored above).
            if (!wearing) originals.Clear();
            else if (originals.Count > count * 2) foreach (uint seed in new List<uint>(originals.Keys)) if (!alive.Contains(seed)) originals.Remove(seed);
            thinned = wearing;
        }
    }
}
