using UnityEngine;

namespace Overhaul
{
    // Ground auras of the rarities (from the "Unique Loot Drops Vol. 1" pack, Vertical02 style; the 7th an iridescent
    // crystal version of the 6th). Each rarity names its aura (AuraAssetName). The bundle is embedded in Overhaul.dll.
    public static class LootAura
    {
        // The pack is made for large scenes: shrunk to item size and lifted so the ground ring is not buried.
        private const float Scale = .35f;
        // The pack places its ground ring 0.701 below the prefab origin: lift it back to the ground, plus 3 cm.
        private const float Lift = .701f * Scale + .03f;
        private static AssetBundle bundle;
        private static readonly System.Collections.Generic.Dictionary<string, GameObject> prefabs = new System.Collections.Generic.Dictionary<string, GameObject>(System.StringComparer.OrdinalIgnoreCase);

        private static bool Ready()
        {
            if (bundle) return true;
            // Owned bytes: a bundle loaded from a stream needs that stream alive for its whole life.
            try { bundle = Utility.EmbeddedAssets.LoadBundle("Overhaul.Assets.LootAura.overhaul_lootaura"); }
            catch (System.Exception e) { Utility.Log.LogWarning("Loot auras unavailable: " + e.Message); return false; }
            if (!bundle) return false;
            foreach (GameObject prefab in bundle.LoadAllAssets<GameObject>()) prefabs[prefab.name] = prefab;
            return true;
        }

        internal static bool Has(string name) => Ready() && prefabs.ContainsKey(name);

        // The aura follows its parent's position but always stays upright.
        public static GameObject Spawn(string name, Vector3 position, Transform follow = null)
        {
            if (string.IsNullOrEmpty(name) || !Ready() || !prefabs.TryGetValue(name, out GameObject prefab)) return null;
            GameObject aura = Object.Instantiate(prefab, (follow ? Ground(position) : position) + Vector3.up * Lift, Quaternion.identity);
            aura.transform.localScale = Vector3.one * Scale;
            foreach (var system in aura.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = system.main;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            }
            if (follow) aura.AddComponent<Upright>().Target = follow;
            aura.AddComponent<Daylight>();
            return aura;
        }

        private static int groundMask;
        // The surface under the item (terrain, floors, rocks), or the item itself when nothing is below.
        private static Vector3 Ground(Vector3 position)
        {
            if (groundMask == 0) groundMask = LayerMask.GetMask("terrain", "Default", "static_solid", "piece");
            return Physics.Raycast(position + Vector3.up * .5f, Vector3.down, out RaycastHit hit, 3f, groundMask) ? hit.point : position;
        }

        // Auras read well at night but fade in daylight: their dark backing layer grows more opaque
        // with the light of the day, which gives the colours contrast.
        private class Daylight : MonoBehaviour
        {
            private const float DayBacking = 2.1f;
            private ParticleSystem[] systems;
            private ParticleSystem.MinMaxGradient[] colors;
            private float factor = -1, next;

            private void Awake()
            {
                systems = GetComponentsInChildren<ParticleSystem>(true);
                colors = new ParticleSystem.MinMaxGradient[systems.Length];
                for (int i = 0; i < systems.Length; i++) colors[i] = systems[i].main.startColor;
            }

            private void Update()
            {
                if (Time.time < next || !EnvMan.instance) return;
                next = Time.time + 1f;
                // 0 at night, 1 at noon, following the sun.
                float light = Mathf.Clamp01(Mathf.Sin((EnvMan.instance.GetDayFraction() - .25f) * 2f * Mathf.PI) * 1.5f + .3f);
                float wanted = light;
                if (Mathf.Abs(wanted - factor) < .02f) return;
                factor = wanted;
                for (int i = 0; i < systems.Length; i++)
                {
                    if (!systems[i]) continue;
                    // The coloured layers are already fully opaque: only the dark backing layer changes.
                    if (!systems[i].name.StartsWith("Black")) continue;
                    var main = systems[i].main;
                    main.startColor = Backing(colors[i], factor);
                }
            }

            private ParticleSystem.MinMaxGradient Backing(ParticleSystem.MinMaxGradient c, float light)
            {
                float boost = Mathf.Lerp(1f, DayBacking, light);
                if (c.mode == ParticleSystemGradientMode.Color) { Color k = c.color; k.a = Mathf.Clamp01(k.a * boost); return k; }
                if (c.mode == ParticleSystemGradientMode.TwoColors) { Color a = c.colorMin, b = c.colorMax; a.a = Mathf.Clamp01(a.a * boost); b.a = Mathf.Clamp01(b.a * boost); return new ParticleSystem.MinMaxGradient(a, b); }
                return c;
            }

        }

        private class Upright : MonoBehaviour
        {
            public Transform Target;
            private void LateUpdate()
            {
                if (!Target) { Destroy(gameObject); return; }
                transform.SetPositionAndRotation(Ground(Target.position) + Vector3.up * Lift, Quaternion.identity);
            }
        }
    }
}
