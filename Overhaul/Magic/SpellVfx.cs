using System;
using System.Collections.Generic;
using System.Linq;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace Overhaul.Magic
{
    // The spells' visual effects: the prefabs of overhaul_spellvfx (OverhaulLab SpellVfxBundle), network objects removing
    // themselves, registered with the game so a copy spawned by its caster shows for every player. Casts lists the
    // effects by spell name for the o_spell test command.
    internal static class SpellVfx
    {
        static AssetBundle bundle;

        // Spell name -> casting it from a player: full = the whole spell (damage and effects), otherwise its visual only.
        internal static readonly Dictionary<string, Action<Player, bool>> Casts = new Dictionary<string, Action<Player, bool>>(StringComparer.OrdinalIgnoreCase)
        {
            ["icenova"] = (player, full) => { IceNova.Cast(player.transform.position); if (full) IceNova.Damage(player); },
        };

        internal static void Initialize() => PrefabManager.OnVanillaPrefabsAvailable += Register;
        internal static void Shutdown() => PrefabManager.OnVanillaPrefabsAvailable -= Register;

        static void Register()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= Register;
            if (!bundle) bundle = Utility.EmbeddedAssets.LoadBundle("Overhaul.Assets.overhaul_spellvfx");
            if (!bundle) { Utility.Log.LogError("Spell effects: overhaul_spellvfx missing"); return; }
            foreach (var prefab in bundle.LoadAllAssets<GameObject>())
            {
                if (prefab.name == "Overhaul_IceNova" && !prefab.GetComponent<IceNova>()) prefab.AddComponent<IceNova>();
                if (!PrefabManager.Instance.GetPrefab(prefab.name)) PrefabManager.Instance.AddPrefab(new CustomPrefab(prefab, false));
            }
        }

        // A copy of the effect at position, facing rotation; it removes itself once played.
        internal static GameObject Spawn(string name, Vector3 position, Quaternion rotation)
        {
            var prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab(name) : null;
            if (!prefab) { Utility.Log.LogWarning("Spell effect " + name + " not found"); return null; }
            var copy = UnityEngine.Object.Instantiate(prefab, position, rotation);
            var sfx = AudioMan.instance ? AudioMan.instance.m_masterMixer.FindMatchingGroups("SFX").FirstOrDefault() : null;
            if (sfx) foreach (var audio in copy.GetComponentsInChildren<AudioSource>(true)) audio.outputAudioMixerGroup = sfx;
            return copy;
        }
    }

    // Frost nova (Overhaul_IceNova): a wave runs from the centre to Radius; spikes pop out of the ground as it passes
    // (bigger towards the edge, leaning outwards) under an even layer of cold smoke; after Hold the spikes shatter from the
    // centre outwards into shards that fall and sink into the ground, and the frozen patch fades. The prefab holds the
    // particle systems (spikes are mesh particles, GPU instanced) and the ground; this lays out the spikes and drives the
    // timing. The layout comes from a seed in the ZDO, so every player sees the same nova.
    // Same behaviour as the lab's IceNova (Tools/OverhaulLab/Assets/OverhaulIceNova/IceNova.cs): change both together.
    internal sealed class IceNova : MonoBehaviour
    {
        internal static readonly int SeedKey = "overhaul_icenova_seed".GetStableHashCode();

        const float Radius = 8f, Wave = 0.2f, Hold = 2.2f, SmokeSpacing = 2.2f;
        const int Count = 300;
        const float GroundSize = 2f * 512f / 440f; // frost texture: patch radius 440 of 512 px
        const float Gravity = 12f, SinkSpeed = 1.6f;

        static readonly int AlphaId = Shader.PropertyToID("_Alpha"), FlashId = Shader.PropertyToID("_Flash");

        struct Spike { public int Kind; public Vector3 Pos, Rot; public float H, W, Angle, Delay, Break; public bool Broken; }
        struct Puff { public Vector3 Pos; public float R; }

        ParticleSystem[] spikesPs, fragments;
        ParticleSystem smoke;
        Renderer ground;
        ZNetView view;
        Spike[] spikes;
        Puff[] puffs;
        int puffsOut, puffsBreak;
        float time = -1f;
        MaterialPropertyBlock block;
        readonly ParticleSystem.Particle[] buffer = new ParticleSystem.Particle[1024];
        ParticleSystem.Particle[][] spikeBuffers;
        int[] counts;

        // A nova at the caster's feet, seen by every player.
        internal static void Cast(Vector3 position)
        {
            var copy = SpellVfx.Spawn("Overhaul_IceNova", position, Quaternion.identity);
            var view = copy ? copy.GetComponent<ZNetView>() : null;
            if (view && view.IsValid()) view.GetZDO().Set(SeedKey, UnityEngine.Random.Range(1, int.MaxValue));
        }

        // Provisional numbers until the spell gets its Spells.cfg entry: frost damage (the game adds its own frost slow) to
        // the enemies in a cylinder of the nova's radius, twice their height and centred on the caster.
        const float FrostDamage = 40f, HalfHeight = 1.8f;
        internal static void Damage(Player caster)
        {
            Vector3 center = caster.transform.position + Vector3.up * HalfHeight / 2;
            foreach (var c in Character.GetAllCharacters())
            {
                if (!c || c == caster || c.IsDead() || !BaseAI.IsEnemy(caster, c)) continue;
                Vector3 offset = c.GetCenterPoint() - center;
                if (Mathf.Abs(offset.y) > HalfHeight || new Vector2(offset.x, offset.z).sqrMagnitude > Radius * Radius) continue;
                var hit = new HitData { m_point = c.GetCenterPoint(), m_dir = (c.transform.position - caster.transform.position).normalized, m_dodgeable = false, m_blockable = false };
                hit.m_damage.m_frost = FrostDamage; hit.SetAttacker(caster);
                c.Damage(hit);
            }
        }

        void Awake()
        {
            view = GetComponent<ZNetView>();
            spikesPs = new[] { "Spikes1", "Spikes2", "Spikes3", "SpikesBig" }.Select(n => transform.Find(n).GetComponent<ParticleSystem>()).ToArray();
            fragments = new[] { "Fragments1", "Fragments2", "Fragments3" }.Select(n => transform.Find(n).GetComponent<ParticleSystem>()).ToArray();
            smoke = transform.Find("Smoke").GetComponent<ParticleSystem>();
            ground = transform.Find("Ground").GetComponent<Renderer>();
            ground.transform.localScale = new Vector3(0.001f, 0.001f, 1f);
        }

        void Update()
        {
            if (time < 0f)
            {
                int seed = view && view.IsValid() ? view.GetZDO().GetInt(SeedKey) : 0;
                if (seed == 0) return;
                Play(seed);
            }
            else if (time <= Wave + Hold + 1.8f) Step(Time.deltaTime);
        }

        void Play(int seed)
        {
            var saved = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed);
            spikeBuffers = new ParticleSystem.Particle[spikesPs.Length][];
            for (int i = 0; i < spikesPs.Length; i++) spikeBuffers[i] = new ParticleSystem.Particle[1024];
            counts = new int[spikesPs.Length];
            spikes = new Spike[Count];
            for (int i = 0; i < Count; i++)
            {
                float u = Mathf.Pow(UnityEngine.Random.value, 0.7f), r = 0.15f + u * (Radius - 0.15f), n = r / Radius;
                float a = UnityEngine.Random.value * Mathf.PI * 2f;
                int kind = n > 0.72f && UnityEngine.Random.value < 0.75f ? spikesPs.Length - 1 : UnityEngine.Random.Range(0, spikesPs.Length - 1);
                float h = (0.6f + Mathf.Pow(n, 1.5f) * 2.3f) * UnityEngine.Random.Range(0.85f, 1.15f);
                var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                float lean = (0.15f + n * 0.5f) * UnityEngine.Random.Range(0.8f, 1.2f) * Mathf.Rad2Deg;
                // lean outwards (around the tangent), then a free spin around the leaning axis
                var rot = Quaternion.AngleAxis(lean, Vector3.Cross(Vector3.up, dir)) * Quaternion.Euler(0f, UnityEngine.Random.value * 360f, 0f);
                spikes[i] = new Spike { Kind = kind, Pos = dir * r + Vector3.down * 0.05f * h, Rot = rot.eulerAngles, H = h, W = h * UnityEngine.Random.Range(0.55f, 0.8f),
                    Angle = a, Delay = n * Wave + UnityEngine.Random.value * 0.04f, Break = Wave + Hold + n * 0.35f + UnityEngine.Random.value * 0.08f };
            }
            LayOutSmoke();
            ground.transform.localRotation = Quaternion.Euler(90f, UnityEngine.Random.value * 360f, 0f);
            UnityEngine.Random.state = saved;
            time = 0f;
            Step(0f);
        }

        // Even cover: one puff in the centre, then rings SmokeSpacing apart with as many puffs as fit on each ring,
        // ordered by radius so they leave with the wave.
        void LayOutSmoke()
        {
            var list = new List<Puff> { new Puff { Pos = Vector3.zero, R = 0f } };
            for (float r = SmokeSpacing; r < Radius + SmokeSpacing * 0.3f; r += SmokeSpacing)
            {
                float rr = Mathf.Min(r, Radius - 0.3f);
                int n = Mathf.Max(3, Mathf.RoundToInt(2f * Mathf.PI * rr / SmokeSpacing));
                float off = UnityEngine.Random.value * Mathf.PI * 2f;
                for (int i = 0; i < n; i++)
                {
                    float a = off + i * Mathf.PI * 2f / n + UnityEngine.Random.Range(-0.15f, 0.15f) / n * 6f;
                    float jr = rr + UnityEngine.Random.Range(-0.2f, 0.2f) * SmokeSpacing;
                    list.Add(new Puff { Pos = new Vector3(Mathf.Cos(a) * jr, 0f, Mathf.Sin(a) * jr), R = jr });
                }
            }
            puffs = list.ToArray();
        }

        void Step(float dt)
        {
            time += dt;
            float t = time, hold = Wave + Hold;

            // smoke leaves with the wave, then a lighter layer when the spikes shatter
            while (puffsOut < puffs.Length && t > puffs[puffsOut].R / Radius * Wave) { var p = puffs[puffsOut++]; EmitPuff(p.Pos, SmokeSpacing * 1.6f, SmokeSpacing * 0.55f, 0.8f, 0.9f); }
            while (puffsBreak < puffs.Length && t > hold + puffs[puffsBreak].R / Radius * 0.35f) { var p = puffs[puffsBreak++]; EmitPuff(p.Pos, SmokeSpacing * 1.3f, SmokeSpacing * 0.45f, 0.6f, 0.6f); }

            Array.Clear(counts, 0, counts.Length);
            for (int i = 0; i < spikes.Length; i++)
            {
                ref var s = ref spikes[i];
                if (t <= s.Delay) continue;
                float sc = Back(Mathf.Min(1f, (t - s.Delay) / 0.22f));
                var pos = s.Pos;
                if (t > s.Break)
                {
                    if (!s.Broken) { s.Broken = true; Shatter(ref s); }
                    float k = Mathf.Min(1f, (t - s.Break) / 0.18f);
                    if (k >= 1f) continue;
                    pos.y -= k * s.H; sc *= 1f - k * 0.5f;
                }
                ref var q = ref spikeBuffers[s.Kind][counts[s.Kind]++];
                q.position = transform.TransformPoint(pos);
                q.rotation3D = s.Rot + transform.eulerAngles;
                q.startSize3D = new Vector3(s.W * sc, s.H * sc, s.W * sc);
                q.startColor = Color.white;
                q.startLifetime = q.remainingLifetime = 100f;
                q.velocity = Vector3.zero; q.angularVelocity3D = Vector3.zero;
            }
            for (int k = 0; k < spikesPs.Length; k++) { spikesPs[k].Simulate(dt, true, false, false); spikesPs[k].SetParticles(spikeBuffers[k], counts[k]); }

            foreach (var f in fragments) StepFragments(f, dt);
            smoke.Simulate(dt, true, false, false);

            float fr = Mathf.Min(1f, t / Wave) * Radius * GroundSize;
            ground.transform.localScale = new Vector3(Mathf.Max(fr, 0.001f), Mathf.Max(fr, 0.001f), 1f);
            block ??= new MaterialPropertyBlock();
            block.SetFloat(AlphaId, t > hold ? Mathf.Max(0f, 1f - (t - hold - 0.2f) / 0.9f) : 1f);
            // the flash: the frozen patch glows brightly as it spreads, then settles (no real-time light)
            block.SetFloat(FlashId, t < Wave ? 3f : Mathf.Max(0f, 3f * (1f - (t - Wave) / 0.6f)));
            ground.SetPropertyBlock(block);
        }

        static float Back(float x) { const float c = 1.9f; return 1f + (c + 1f) * Mathf.Pow(x - 1f, 3f) + c * Mathf.Pow(x - 1f, 2f); }

        void EmitPuff(Vector3 local, float size, float y, float life, float alpha)
        {
            var e = new ParticleSystem.EmitParams
            {
                position = transform.TransformPoint(local + Vector3.up * y), velocity = Vector3.up * UnityEngine.Random.Range(0.3f, 0.6f),
                startSize = size * UnityEngine.Random.Range(0.9f, 1.1f), startLifetime = life, rotation = UnityEngine.Random.value * 360f,
                startColor = new Color(1f, 1f, 1f, alpha), applyShapeToPosition = false
            };
            smoke.Emit(e, 1);
        }

        void Shatter(ref Spike s)
        {
            int n = 2 + UnityEngine.Random.Range(0, 2);
            for (int i = 0; i < n; i++)
            {
                float a = s.Angle + UnityEngine.Random.Range(-1.1f, 1.1f), sp = UnityEngine.Random.Range(1f, 3.5f);
                var e = new ParticleSystem.EmitParams
                {
                    position = transform.TransformPoint(new Vector3(s.Pos.x, s.H * UnityEngine.Random.Range(0.15f, 0.75f), s.Pos.z)),
                    velocity = transform.TransformDirection(new Vector3(Mathf.Cos(a) * sp, UnityEngine.Random.Range(1.5f, 4f), Mathf.Sin(a) * sp)),
                    startSize = s.H * UnityEngine.Random.Range(0.12f, 0.26f), startLifetime = 1.1f,
                    rotation3D = new Vector3(UnityEngine.Random.value, UnityEngine.Random.value, UnityEngine.Random.value) * 360f,
                    angularVelocity3D = new Vector3(UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(-1f, 1f)) * 800f,
                    applyShapeToPosition = false
                };
                fragments[UnityEngine.Random.Range(0, fragments.Length)].Emit(e, 1);
            }
        }

        // Fragments fly under gravity, then sink into the ground once they touch it (y of the nova's origin).
        void StepFragments(ParticleSystem ps, float dt)
        {
            ps.Simulate(dt, true, false, false);
            int c = ps.GetParticles(buffer);
            float floor = transform.position.y + 0.02f;
            for (int i = 0; i < c; i++)
            {
                var p = buffer[i];
                bool sinking = p.position.y < floor && Mathf.Abs(p.velocity.y + SinkSpeed) < 0.01f && p.velocity.x == 0f;
                if (!sinking)
                {
                    var v = p.velocity; v.y -= Gravity * dt;
                    if (p.position.y < floor && v.y < 0f) { v = new Vector3(0f, -SinkSpeed, 0f); p.angularVelocity3D = Vector3.zero; }
                    p.velocity = v;
                }
                float age = p.startLifetime - p.remainingLifetime;
                if (age > 0.8f) p.startSize = Mathf.Max(0f, p.startSize * (1f - dt / Mathf.Max(0.01f, p.remainingLifetime)));
                buffer[i] = p;
            }
            ps.SetParticles(buffer, c);
        }
    }
}
