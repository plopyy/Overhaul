using System.Linq;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using Overhaul.Utility;
using UnityEngine;

namespace Overhaul.Rarity
{
    // Enchanting table: the EnchantTable_Piece prefab of overhaul_enchanttable (OverhaulLab EnchantTableBundle), a complete
    // crafting station of its own with no build range: network object, piece (hammer Crafting tab, at a workbench), wear and
    // tear, model (Eikthyr's altar holding the cartography table's maps, a rune table, crypt bones and eternal candles) and
    // colliders. What a bundle cannot hold is added here: the game's materials on its parts, the galdr table's eternal candle
    // flames and light on the model's anchors, the stone build and break effects, and the build menu icon. Using it opens the
    // enchanting window; it can be moved like the other functional pieces. The candle glow swells while the player is near.
    internal static class EnchantTable
    {
        internal const string Name = "Overhaul_EnchantTable";
        internal const string StationName = "$overhaul_enchanttable";
        static AssetBundle bundle;

        // Model part -> game prefab and material it wears.
        static readonly (string part, string prefab, string material)[] Materials =
        {
            ("Autel", "offeraltar_deer", "altar"),
            ("Skull", "Pickable_ForestCryptRemains03", "fi_village_catacombs"),
            ("Candles", "piece_magetable_ext2", "MageCandle_MAT"),
            ("Carto", "piece_cartographytable", "Cartographer_mat"),
            ("Rune", "piece_magetable_ext", "runetable_mat"),
        };
        // The eternal candles are the galdr table's, 0.3 times their size.
        const float CandleScale = .3f;

        internal static void Initialize() => PrefabManager.OnVanillaPrefabsAvailable += Register;
        internal static void Shutdown() => PrefabManager.OnVanillaPrefabsAvailable -= Register;

        static void Register()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= Register;
            if (PrefabManager.Instance.GetPrefab(Name)) return;
            if (!bundle) bundle = EmbeddedAssets.LoadBundle("Overhaul.Assets.overhaul_enchanttable");
            var source = bundle ? bundle.LoadAsset<GameObject>("EnchantTable_Piece") : null;
            if (!source) { Log.LogError("Enchanting table: EnchantTable_Piece missing from overhaul_enchanttable"); return; }

            var prefab = PrefabManager.Instance.CreateClonedPrefab(Name, source);
            var visual = prefab.transform.Find("Model").gameObject;
            Dress(visual);
            // Stone sounds and debris when built, hit and destroyed: the stone wall's.
            var stone = PrefabManager.Instance.GetPrefab("stone_wall_1x1");
            var piece = prefab.GetComponent<Piece>(); var wear = prefab.GetComponent<WearNTear>();
            if (stone)
            {
                piece.m_placeEffect = stone.GetComponent<Piece>().m_placeEffect;
                var stoneWear = stone.GetComponent<WearNTear>();
                wear.m_destroyedEffect = stoneWear.m_destroyedEffect; wear.m_hitEffect = stoneWear.m_hitEffect;
            }
            prefab.AddComponent<EnchantTableGlow>();
            // The build menu icon, drawn from the dressed model.
            var icon = RenderManager.Instance.Render(new RenderManager.RenderRequest(visual)
            {
                Width = 128, Height = 128, Rotation = RenderManager.IsometricRotation,
                DistanceMultiplier = 1.1f, ParticleSimulationTime = -1f
            });
            // Provisional recipe, until the enchanting itself is made.
            var config = new PieceConfig
            {
                PieceTable = "Hammer", Category = "Crafting", CraftingStation = "piece_workbench", Icon = icon,
                Requirements = new[] { ("Stone", 10), ("FineWood", 6), ("BoneFragments", 4), ("SurtlingCore", 1) }
                    .Select(r => new RequirementConfig(r.Item1, r.Item2, 0, true)).ToArray()
            };
            PieceManager.Instance.AddPiece(new CustomPiece(prefab, false, config));
        }

        static void Dress(GameObject visual)
        {
            foreach (var (part, source, name) in Materials)
            {
                var renderer = visual.transform.Find(part)?.GetComponent<MeshRenderer>();
                var material = GamePrefabs.Held(source)?.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).FirstOrDefault(m => m && m.name == name);
                if (!renderer || !material) { Log.LogWarning("Enchanting table: " + part + " left without the " + name + " material of " + source); continue; }
                renderer.sharedMaterials = Enumerable.Repeat(material, renderer.sharedMaterials.Length).ToArray();
            }

            var candles = GamePrefabs.Held("piece_magetable_ext2")?.transform.Find("new");
            var flame = candles?.Find("flames")?.gameObject;
            var light = candles?.GetComponentInChildren<Light>(true)?.gameObject;
            if (!flame) { Log.LogWarning("Enchanting table: eternal candle flames not found"); return; }
            int layer = LayerMask.NameToLayer("piece");
            foreach (Transform anchor in visual.transform)
            {
                if (!anchor.name.StartsWith("FlameAnchor")) continue;
                var copy = Object.Instantiate(flame, anchor, false); copy.name = "flame";
                copy.transform.localPosition = Vector3.zero; copy.transform.localRotation = Quaternion.identity; copy.transform.localScale = Vector3.one * CandleScale;
                foreach (var ps in copy.GetComponentsInChildren<ParticleSystem>(true)) { var main = ps.main; main.scalingMode = ParticleSystemScalingMode.Hierarchy; }
                foreach (var t in copy.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
            }
            var lightAnchor = visual.transform.Find("LightAnchor");
            if (light && lightAnchor)
            {
                var copy = Object.Instantiate(light, lightAnchor, false); copy.name = "Point Light";
                copy.transform.localPosition = Vector3.zero;
                var l = copy.GetComponent<Light>(); l.range *= .5f;
            }
        }

        // Using the table opens the enchanting window instead of the crafting one.
        [HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.Interact))]
        private static class Use
        {
            private static bool Prefix(CraftingStation __instance, Humanoid user, bool repeat, ref bool __result)
            {
                if (__instance.m_name != StationName) return true;
                __result = false;
                if (repeat || user != Player.m_localPlayer || !__instance.InUseDistance(user)) return false;
                // The enchanting window comes with the enchanting itself.
                ((Player)user).Message(MessageHud.MessageType.Center, "$overhaul_enchant_soon");
                return false;
            }
        }
    }

    // The violet glow of the eternal candles swells while the local player is within reach of the table, and fades back
    // when they leave. Seen by each player for themself: no network traffic.
    internal sealed class EnchantTableGlow : MonoBehaviour
    {
        const float Reach = 5f, Boost = 2.5f, RangeBoost = 1.4f, FlameBoost = 1.3f, Speed = 2f, FlameScale = .3f;
        Light glow; LightFlicker flicker; LightLod lod;
        float intensity, range, level;
        Transform[] flames;

        void Start()
        {
            glow = GetComponentInChildren<Light>(true);
            flames = GetComponentsInChildren<Transform>(true).Where(t => t.name == "flame").ToArray();
            if (!glow) return;
            // The game's flicker and light distance scripts rewrite intensity and range from their own base every frame:
            // the glow changes those bases.
            flicker = glow.GetComponent<LightFlicker>(); lod = glow.GetComponent<LightLod>();
            intensity = flicker ? flicker.m_baseIntensity : glow.intensity;
            range = lod ? lod.m_baseRange : glow.range;
        }

        void Update()
        {
            var player = Player.m_localPlayer;
            float target = player && Vector3.Distance(player.transform.position, transform.position) <= Reach ? 1f : 0f;
            if (level == target) return;
            level = Mathf.MoveTowards(level, target, Speed * Time.deltaTime);
            float t = Mathf.SmoothStep(0, 1, level);
            if (glow)
            {
                float i = intensity * Mathf.Lerp(1, Boost, t), r = range * Mathf.Lerp(1, RangeBoost, t);
                if (flicker) flicker.m_baseIntensity = i; else glow.intensity = i;
                if (lod) { lod.m_baseRange = r; if (glow.range > 0) glow.range = r; } else glow.range = r;
            }
            foreach (var flame in flames) if (flame) flame.localScale = Vector3.one * FlameScale * Mathf.Lerp(1, FlameBoost, t);
        }
    }
}
