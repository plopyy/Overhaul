using System.Reflection.Emit;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul
{
    // Lightning Strike staff (StaffThunderBlood) and the grimoire's lightning spell: no projectile. The attack's projectile
    // is replaced by a lightning call that takes the hit the game computed (the staff's damage, quality, skill, spell); the
    // point is traced from the camera through the crosshair (the burst's others spread around it), the obliterator's
    // thunder rumbles there and the staff's own lightning (staff_thunderblood_aoe) falls on it 2 s later, dealing that hit.
    // Every red colour of the lightning is turned blue-white.
    internal static class ThunderStaff
    {
        private const string ProjectileName = "staff_thunderblood_projectile", LightningName = "staff_thunderblood_aoe";
        // How far from the player the lightning can fall.
        private const float MaxDistance = 30f;
        // The line traced from the camera: MaxDistance plus the camera's distance to the player.
        private static float reach = MaxDistance;


        // The burst being fired: its attack (for the final projectile count) and the index of the next projectile.
        private static Attack burst;
        private static int index;

        [HarmonyPatch(typeof(Attack), "FireProjectileBurst")]
        private static class Burst
        {
            private static void Prefix(Attack __instance) { burst = __instance; index = 0; }
            private static void Finalizer() => burst = null;
        }

        // The attack creates no projectile: where the game would instantiate the staff's projectile, it gets a bare lightning
        // call instead (no model, no network object), which the game sets up like the projectile with the hit it computed
        // (the staff's damage, quality, skill and modifiers). Other players' attacks are
        // left to the native code (their own game calls their lightning).
        [HarmonyPatch(typeof(Attack), "FireProjectileBurst")]
        private static class NoProjectile
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var instantiate = AccessTools.FirstMethod(typeof(Object), m => m.Name == nameof(Object.Instantiate) && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 3 && m.GetParameters()[1].ParameterType == typeof(Vector3)).MakeGenericMethod(typeof(GameObject));
                int count = 0;
                foreach (var code in instructions)
                {
                    if (code.Calls(instantiate))
                    {
                        count++;
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ThunderStaff), nameof(Spawn))).MoveLabelsFrom(code);
                        continue;
                    }
                    yield return code;
                }
                if (count != 1) throw new System.InvalidOperationException("Attack.FireProjectileBurst: projectile creation not found");
            }
        }

        private static GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation, Attack attack)
        {
            if (!prefab || !prefab.name.StartsWith(ProjectileName, System.StringComparison.Ordinal) || !attack.m_character || attack.m_character != Player.m_localPlayer)
                return Object.Instantiate(prefab, position, rotation);
            var call = new GameObject("Lightning call");
            call.AddComponent<LightningCall>();
            return call;
        }

        // Takes the hit the attack computed for its projectile, finds where the lightning falls and calls it there.
        private sealed class LightningCall : MonoBehaviour, IProjectile
        {
            public void Setup(Character owner, Vector3 velocity, float hitNoise, HitData hitData, ItemDrop.ItemData item, ItemDrop.ItemData ammo)
            {
                try
                {
                    // What the staff's projectile setup would do with the hit (the spell turns the magic power into its element).
                    if (Magic.StaffMagic.IsStaff(item)) Magic.SpellSystem.OnHitSetup(owner, hitData);
                    if (Aim(owner, item, out Vector3 point))
                        Thunder.Call(owner, item, hitData, point);
                }
                finally { Destroy(gameObject); }
            }

            public string GetTooltipString(int itemQuality) => "";
        }

        // The point the lightning falls on: traced from the camera through the crosshair - the first exactly on it, the others
        // of the burst spread around it (in a fan with Fan Volley, else at random within the weapon's accuracy) - to the first
        // surface met, or the ground under the far end of the line.
        private static bool Aim(Character owner, ItemDrop.ItemData item, out Vector3 point)
        {
            point = default;
            var camera = Utils.GetMainCamera();
            if (!camera) return false;
            int count = burst != null ? Mathf.Max(1, burst.m_projectiles) : 1, i = index++;
            Vector3 origin = camera.transform.position, direction = camera.transform.forward;
            reach = MaxDistance + Vector3.Distance(origin, owner.transform.position);
            if (Rarity.MythicEffects.Extra(item) > 0) direction = Rarity.MythicEffects.Turn(direction, Rarity.MythicEffects.Angle(i % count, count));
            else if (i > 0 && burst != null)
            {
                float spread = burst.m_projectileAccuracy;
                direction = Quaternion.Euler(Random.Range(-spread, spread), Random.Range(-spread, spread), 0) * direction;
            }
            if (!Trace(origin, direction, owner, out RaycastHit hit)
                && !Physics.Raycast(origin + direction * reach + Vector3.up * 50f, Vector3.down, out hit, 500f, Projectile.s_rayMaskSolids)) return false;
            point = hit.point;
            return true;
        }

        // First surface along the line, the shooter left out.
        private static bool Trace(Vector3 start, Vector3 direction, Character owner, out RaycastHit first)
        {
            first = default;
            float best = float.MaxValue;
            foreach (var hit in Physics.RaycastAll(start, direction, reach, Projectile.s_rayMaskSolids, QueryTriggerInteraction.Ignore))
            {
                if (hit.distance >= best) continue;
                var target = Projectile.FindHitObject(hit.collider);
                if (target && target == owner.gameObject) continue;
                best = hit.distance; first = hit;
            }
            return best < float.MaxValue;
        }

        // The obliterator's warning: a distant thunder rumble where the lightning will fall, then the strike after a delay
        // (shorter than the obliterator's 4.5 s) so it can be dodged. Every player hears the rumble (routed RPC).
        internal static class Thunder
        {
            internal const float StaffDelay = 2f, RainDelay = 1f;
            private const string RumbleRpc = "Overhaul_ThunderRumble";
            private static GameObject rumble;

            // The lightning's hit: the attack's damage (the staff's stats, skill and spell), its weapon, owner, the weapon wear
            // of the attack and the effects the weapon carries.
            private sealed class Bolt
            {
                internal Character Owner; internal ItemDrop.ItemData Weapon; internal HitData Hit;
                internal float Wrath; internal bool Explosive; internal AttackDurability.Attempt Attempt;
            }

            internal static void Call(Character owner, ItemDrop.ItemData item, HitData hit, Vector3 point)
            {
                var attempt = AttackDurability.Current;
                var bolt = new Bolt
                {
                    Owner = owner, Weapon = item, Hit = hit,
                    Wrath = item != null ? Rarity.Enchantments.MythicValue(item, "ZeusWrath") : 0,
                    Explosive = Rarity.Enchantments.ItemValue(item, "ExplosiveProjectile") >= 1 || Effects.Effects.Get(owner, "ExplosiveProjectile") >= 1,
                    Attempt = attempt != null && attempt.Player == owner ? attempt : null
                };
                Rumble(point);
                owner.StartCoroutine(Fall(bolt, point, StaffDelay));
            }

            // The staff's own lightning (staff_thunderblood_aoe) appears on the point: its areas deal the hit's damage (StaffMagic).
            private static System.Collections.IEnumerator Fall(Bolt bolt, Vector3 point, float delay)
            {
                yield return new WaitForSeconds(delay);
                if (!bolt.Owner || !ZNetScene.instance) yield break;
                var prefab = ZNetScene.instance.GetPrefab(LightningName);
                if (!prefab) yield break;
                var previous = Magic.StaffMagic.Spawning;
                Magic.StaffMagic.Spawning = new Magic.StaffMagic.ImpactSource { Owner = bolt.Owner, Weapon = bolt.Weapon, Damage = bolt.Hit.m_damage };
                GameObject lightning;
                try { lightning = Object.Instantiate(prefab, point, Quaternion.identity); }
                finally { Magic.StaffMagic.Spawning = previous; }
                foreach (var area in lightning.GetComponentsInChildren<Aoe>(true))
                {
                    area.Setup(bolt.Owner, Vector3.down, 0f, null, null, null);
                    // The attack's weapon wear goes with the areas' hits.
                    AttackDurability.Give(area, bolt.Attempt);
                }
                if (bolt.Explosive) Effects.EffectPatches.Explosive.Detonate(bolt.Owner, bolt.Weapon, bolt.Hit.m_skill, bolt.Hit.m_pushForce, point);
                if (bolt.Wrath > 0 && bolt.Owner is Player player && player == Player.m_localPlayer && Random.value * 100f < bolt.Wrath)
                    Rarity.MythicEffects.StartRain(player, point, bolt.Hit.m_damage.GetTotalDamage(), bolt.Hit);
            }

            internal static void Rumble(Vector3 point)
            {
                if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RumbleRpc, point);
                else Play(point);
            }

            private static void OnRumble(long sender, Vector3 point) => Play(point);

            private static void Play(Vector3 point)
            {
                if (!rumble)
                {
                    // The distant thunder is a child of the obliterator's lever effect (vfx_lever), next to its lever sound and sparks:
                    // only that child is played.
                    var incinerator = ZNetScene.instance ? ZNetScene.instance.GetPrefab("incinerator") : null;
                    var lever = incinerator ? incinerator.GetComponentInChildren<Incinerator>(true) : null;
                    if (lever)
                        foreach (var e in lever.m_leverEffects.m_effectPrefabs)
                            if (e.m_prefab)
                                foreach (var t in e.m_prefab.GetComponentsInChildren<ZSFX>(true))
                                    if (t.name.IndexOf("thunder", System.StringComparison.OrdinalIgnoreCase) >= 0) rumble = t.gameObject;
                    if (!rumble) { Utility.Log.LogWarning("Thunder rumble: the obliterator's distant thunder not found"); return; }
                }
                var sound = Object.Instantiate(rumble, point, Quaternion.identity);
                sound.SetActive(true);
                // Its own parent's timed destruction is not copied with it.
                Object.Destroy(sound, 8f);
            }

            [HarmonyPatch(typeof(ZRoutedRpc), MethodType.Constructor, typeof(bool))]
            private static class Register
            {
                private static void Postfix(ZRoutedRpc __instance) => __instance.Register<Vector3>(RumbleRpc, OnRumble);
            }
        }

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
