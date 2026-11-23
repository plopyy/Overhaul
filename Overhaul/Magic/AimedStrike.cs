using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Magic
{
    // How a strike falls: the effect that appears on the aimed point and deals the attack's hit, an optional warning played
    // there first, the delay before it falls (0 = at once) and how far from the player it can be aimed.
    internal sealed class StrikeSettings
    {
        internal string Effect;
        internal string Warning;
        internal float Delay;
        internal float Range = 30f;
    }

    // Aimed strike: an attack that fires no projectile. Where the game would create the attack's projectile, it gets a bare
    // strike call instead (no model, no network object), set up by the game like the projectile with the hit it computed
    // (the weapon's damage, quality, skill and modifiers). The point is traced from the camera through the crosshair (the
    // burst's others spread around it), the warning plays there, then the strike effect appears on it and its areas deal
    // that hit (StaffMagic), with the attack's weapon wear and the weapon's Zeus's Wrath and explosive effects.
    // Used by: weapons whose projectile is listed in Weapons, and grimoire spells cast as "Strike" (Spells.cfg).
    internal static class AimedStrike
    {
        // Weapon attacks turned into strikes, by the projectile the game would fire.
        private static readonly Dictionary<string, StrikeSettings> Weapons = new Dictionary<string, StrikeSettings>
        {
            // Lightning Strike staff: its own lightning, the obliterator's thunder first, 2 s to get out of the way.
            ["staff_thunderblood_projectile"] = new StrikeSettings { Effect = "staff_thunderblood_aoe", Warning = Warnings.Thunder, Delay = 2f, Range = 30f },
        };

        // The settings of the attack about to fire this projectile, if it is a strike: the spell being cast first, then the weapon.
        private static StrikeSettings For(Attack attack, GameObject projectile)
        {
            var owner = attack.m_character;
            if (!owner || owner != Player.m_localPlayer || !projectile) return null;
            var spell = SpellSystem.CastBy(owner);
            if (spell?.Strike != null) return spell.Strike;
            return Weapons.TryGetValue(Utils.GetPrefabName(projectile), out var settings) ? settings : null;
        }

        // The burst being fired: its attack (for the final projectile count) and the index of the next projectile.
        private static Attack burst;
        private static int index;
        private static StrikeSettings firing;

        [HarmonyPatch(typeof(Attack), "FireProjectileBurst")]
        private static class Burst
        {
            private static void Prefix(Attack __instance) { burst = __instance; index = 0; }
            private static void Finalizer() { burst = null; firing = null; }

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
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(AimedStrike), nameof(Spawn))).MoveLabelsFrom(code);
                        continue;
                    }
                    yield return code;
                }
                if (count != 1) throw new System.InvalidOperationException("Attack.FireProjectileBurst: projectile creation not found");
            }
        }

        private static GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation, Attack attack)
        {
            var settings = For(attack, prefab);
            if (settings == null) return Object.Instantiate(prefab, position, rotation);
            firing = settings;
            var call = new GameObject("Strike call");
            call.AddComponent<StrikeCall>().Settings = settings;
            return call;
        }

        // Takes the hit the attack computed for its projectile, finds the point and calls the strike there.
        private sealed class StrikeCall : MonoBehaviour, IProjectile
        {
            internal StrikeSettings Settings;

            public void Setup(Character owner, Vector3 velocity, float hitNoise, HitData hitData, ItemDrop.ItemData item, ItemDrop.ItemData ammo)
            {
                try
                {
                    // What a staff's projectile setup does with the hit (the spell turns the magic power into its element).
                    if (StaffMagic.IsStaff(item)) SpellSystem.OnHitSetup(owner, hitData);
                    if (Aim(owner, item, Settings.Range, out Vector3 point)) Call(Settings, owner, item, hitData, point);
                }
                finally { Destroy(gameObject); }
            }

            public string GetTooltipString(int itemQuality) => "";
        }

        // The point: traced from the camera through the crosshair - the first exactly on it, the others of the burst spread
        // around it (in a fan with Fan Volley, else at random within the weapon's accuracy) - to the first surface met within
        // range, or the ground under the far end of the line.
        private static bool Aim(Character owner, ItemDrop.ItemData item, float range, out Vector3 point)
        {
            point = default;
            var camera = Utils.GetMainCamera();
            if (!camera) return false;
            int count = burst != null ? Mathf.Max(1, burst.m_projectiles) : 1, i = index++;
            Vector3 origin = camera.transform.position, direction = camera.transform.forward;
            float reach = range + Vector3.Distance(origin, owner.transform.position);
            if (Rarity.MythicEffects.Extra(item) > 0) direction = Rarity.MythicEffects.Turn(direction, Rarity.MythicEffects.Angle(i % count, count));
            else if (i > 0 && burst != null)
            {
                float spread = burst.m_projectileAccuracy;
                direction = Quaternion.Euler(Random.Range(-spread, spread), Random.Range(-spread, spread), 0) * direction;
            }
            if (!Trace(origin, direction, reach, owner, out RaycastHit hit)
                && !Physics.Raycast(origin + direction * reach + Vector3.up * 50f, Vector3.down, out hit, 500f, Projectile.s_rayMaskSolids)) return false;
            point = hit.point;
            return true;
        }

        // First surface along the line, the shooter left out.
        private static bool Trace(Vector3 start, Vector3 direction, float reach, Character owner, out RaycastHit first)
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

        // The strike's hit: the attack's damage, its weapon, owner, the weapon wear of the attack and the weapon's effects.
        private sealed class Strike
        {
            internal StrikeSettings Settings;
            internal Character Owner; internal ItemDrop.ItemData Weapon; internal HitData Hit;
            internal float Wrath; internal bool Explosive; internal AttackDurability.Attempt Attempt;
        }

        private static void Call(StrikeSettings settings, Character owner, ItemDrop.ItemData item, HitData hit, Vector3 point)
        {
            var attempt = AttackDurability.Current;
            var strike = new Strike
            {
                Settings = settings, Owner = owner, Weapon = item, Hit = hit,
                Wrath = item != null ? Rarity.Enchantments.MythicValue(item, "ZeusWrath") : 0,
                Explosive = Rarity.Enchantments.ItemValue(item, "ExplosiveProjectile") >= 1 || Effects.Effects.Get(owner, "ExplosiveProjectile") >= 1,
                Attempt = attempt != null && attempt.Player == owner ? attempt : null
            };
            Warnings.Play(settings.Warning, point);
            if (settings.Delay <= 0f) Fall(strike, point);
            else owner.StartCoroutine(FallLater(strike, point));
        }

        private static System.Collections.IEnumerator FallLater(Strike strike, Vector3 point)
        {
            yield return new WaitForSeconds(strike.Settings.Delay);
            Fall(strike, point);
        }

        private static void Fall(Strike strike, Vector3 point)
        {
            if (!strike.Owner || !ZNetScene.instance) return;
            var prefab = ZNetScene.instance.GetPrefab(strike.Settings.Effect);
            if (!prefab) { Utility.Log.LogWarning("Aimed strike: effect " + strike.Settings.Effect + " not found"); return; }
            var previous = StaffMagic.Spawning;
            StaffMagic.Spawning = new StaffMagic.ImpactSource { Owner = strike.Owner, Weapon = strike.Weapon, Damage = strike.Hit.m_damage, Strike = true };
            GameObject effect;
            try { effect = Object.Instantiate(prefab, point, Quaternion.identity); }
            finally { StaffMagic.Spawning = previous; }
            foreach (var area in effect.GetComponentsInChildren<Aoe>(true))
            {
                area.Setup(strike.Owner, Vector3.down, 0f, null, null, null);
                // The attack's weapon wear goes with the areas' hits.
                AttackDurability.Give(area, strike.Attempt);
            }
            if (strike.Explosive) Effects.EffectPatches.Explosive.Detonate(strike.Owner, strike.Weapon, strike.Hit.m_skill, strike.Hit.m_pushForce, point);
            if (strike.Wrath > 0 && strike.Owner is Player player && player == Player.m_localPlayer && Random.value * 100f < strike.Wrath)
                Rarity.MythicEffects.StartRain(player, point, strike.Hit.m_damage.GetTotalDamage(), strike.Hit);
        }
    }

    // Warnings played where a strike will fall, heard by every player (routed RPC). Empty or unknown name: none.
    internal static class Warnings
    {
        // The obliterator's distant thunder (a child of its lever effect, next to the lever sound and sparks: only it is played).
        internal const string Thunder = "Thunder";
        private const string Rpc = "Overhaul_StrikeWarning";
        private static readonly Dictionary<string, GameObject> sounds = new Dictionary<string, GameObject>();

        internal static void Play(string warning, Vector3 point)
        {
            if (string.IsNullOrEmpty(warning)) return;
            if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, Rpc, warning, point);
            else Local(warning, point);
        }

        private static void Local(string warning, Vector3 point)
        {
            if (!sounds.TryGetValue(warning, out var sound) || !sound)
            {
                sound = Find(warning);
                if (!sound) { Utility.Log.LogWarning("Strike warning " + warning + " not found"); return; }
                sounds[warning] = sound;
            }
            var played = Object.Instantiate(sound, point, Quaternion.identity);
            played.SetActive(true);
            // Its own parent's timed destruction is not copied with it.
            Object.Destroy(played, 8f);
        }

        private static GameObject Find(string warning)
        {
            if (!string.Equals(warning, Thunder, System.StringComparison.OrdinalIgnoreCase) || !ZNetScene.instance) return null;
            var incinerator = ZNetScene.instance.GetPrefab("incinerator");
            var lever = incinerator ? incinerator.GetComponentInChildren<Incinerator>(true) : null;
            if (!lever) return null;
            foreach (var e in lever.m_leverEffects.m_effectPrefabs)
                if (e.m_prefab)
                    foreach (var t in e.m_prefab.GetComponentsInChildren<ZSFX>(true))
                        if (t.name.IndexOf("thunder", System.StringComparison.OrdinalIgnoreCase) >= 0) return t.gameObject;
            return null;
        }

        [HarmonyPatch(typeof(ZRoutedRpc), MethodType.Constructor, typeof(bool))]
        private static class Register
        {
            private static void Postfix(ZRoutedRpc __instance) => __instance.Register<string, Vector3>(Rpc, (sender, warning, point) => Local(warning, point));
        }
    }
}
