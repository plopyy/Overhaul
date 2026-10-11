using HarmonyLib;
using UnityEngine;

namespace Overhaul
{
    // Lightning Strike staff (StaffThunderBlood): its lightning falls at once where the player aims instead of after a
    // flying projectile. The game still fires its projectiles (count, spread, Fan Volley, damage untouched); each one is
    // traced straight along its direction, moved to the first surface it meets and hits there at once, so the game
    // spawns its own lightning (staff_thunderblood_aoe). The lightning's red parts are recoloured blue-white.
    internal static class ThunderStaff
    {
        private const string ProjectileName = "staff_thunderblood_projectile", LightningName = "staff_thunderblood_aoe";
        private const float MaxDistance = 100f;

        [HarmonyPatch(typeof(Projectile), nameof(Projectile.Setup))]
        private static class Instant
        {
            // After the other Setup patches (explosive projectiles mark it there).
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Projectile __instance, Character owner, Vector3 velocity)
            {
                if (!owner || owner != Player.m_localPlayer || !__instance.name.StartsWith(ProjectileName, System.StringComparison.Ordinal)) return;
                if (velocity.sqrMagnitude < 1e-6f) return;
                Vector3 start = __instance.transform.position, direction = velocity.normalized;
                if (!Trace(start, direction, owner, out RaycastHit hit))
                {
                    // Nothing within reach: the lightning falls on the ground under the far end of the line.
                    if (!Physics.Raycast(start + direction * MaxDistance + Vector3.up * 50f, Vector3.down, out hit, 500f, Projectile.s_rayMaskSolids)) return;
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

        // Blue-white lightning: the lights, the bolt and rod particles and the sparks of the lightning prefab.
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class Colours
        {
            private static readonly Color Light = new Color(.6f, .85f, 1f), Pale = new Color(.85f, .95f, 1f), Blue = new Color(.35f, .7f, 1f);

            private static void Postfix(ZNetScene __instance)
            {
                GameObject lightning = __instance.GetPrefab(LightningName);
                if (!lightning) { Utility.Log.LogWarning("Lightning Strike staff: " + LightningName + " not found, colours unchanged"); return; }
                foreach (var light in lightning.GetComponentsInChildren<Light>(true)) light.color = Light;
                foreach (var system in lightning.GetComponentsInChildren<ParticleSystem>(true))
                {
                    var main = system.main;
                    switch (system.name)
                    {
                        case "Lighting":
                            main.startColor = Pale;
                            break;
                        case "Lighting_rod":
                            main.startColor = new ParticleSystem.MinMaxGradient(Pale, Blue);
                            break;
                        case "Sparcs":
                            main.startColor = Pale;
                            // The red sparks material is shared with other effects: this prefab gets its own blue copy.
                            var renderer = system.GetComponent<ParticleSystemRenderer>();
                            if (renderer && renderer.sharedMaterial && renderer.sharedMaterial.HasProperty("_EmissionColor"))
                            {
                                var material = new Material(renderer.sharedMaterial) { name = "lightning_sparcs_blue" };
                                material.SetColor("_EmissionColor", new Color(.4f, 1.2f, 2.5f));
                                renderer.sharedMaterial = material;
                            }
                            break;
                    }
                }
            }
        }
    }
}
