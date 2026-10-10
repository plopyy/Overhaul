using System.Collections.Generic;
using EquipmentAndQuickSlots.src.MultiUtility;
using UnityEngine;

namespace Overhaul.Storage
{
    // Spirit circlet: thins only the Mistlands mist (ParticleMist) around its wearer. Mist particles are at 10 %
    // of their opacity up to twice the radius of a wisp's clearing, then fade back to full opacity at three times
    // that radius. Distance fog, smoke and every other effect stay untouched. Each particle keeps the colour the game
    // gave it when emitted (remembered by its random seed): only its opacity is scaled, so particles never jump between
    // the game's colour and another one.
    internal sealed class CircletMist : MonoBehaviour
    {
        private const float Thinned = .1f, InnerRadius = 2f, OuterRadius = 3f, DefaultWispRadius = 6f;
        private static float wispRadius;
        private ParticleSystem system;
        private ParticleSystem.Particle[] particles;
        private readonly Dictionary<uint, Color32> originals = new Dictionary<uint, Color32>();
        private readonly HashSet<uint> alive = new HashSet<uint>();
        private bool thinned;

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
                    float distance = Vector3.Distance(particles[i].position, center);
                    color.a = (byte)Mathf.RoundToInt(color.a * Mathf.Lerp(Thinned, 1f, Mathf.InverseLerp(inner, outer, distance)));
                }
                particles[i].startColor = color;
            }
            system.SetParticles(particles, count);
            // Forget the particles that died, and everything once the circlet is off (colours restored above).
            if (!wearing) originals.Clear();
            else if (originals.Count > count * 2) foreach (uint seed in new List<uint>(originals.Keys)) if (!alive.Contains(seed)) originals.Remove(seed);
            thinned = wearing;
        }
    }
}
