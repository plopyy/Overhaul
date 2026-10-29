using System.Linq;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn;
using Jotunn.Managers;
using Overhaul.Utility;
using UnityEngine;

namespace Overhaul.Rarity
{
    // Enchanting table: the EnchantTable_Piece prefab of overhaul_enchanttable (OverhaulLab EnchantTableBundle), a complete
    // crafting station of its own with no build range: network object, piece (hammer Crafting tab, at a workbench), wear and
    // tear, model (Eikthyr's altar holding the cartography table's maps, a rune table, crypt bones and eternal candles) and
    // colliders; its parts wear the game's own materials through Jotunn mocks (JVLmock_<material>, swapped by Jotunn).
    // What a bundle cannot hold is added here: the galdr table's eternal candle flames and light on the model's anchors, the stone build and break effects, and the build menu icon. Using it opens the
    // enchanting window (Rarity.EnchantInventory, opened like a chest); it can be moved like the other functional pieces. The candle glow swells while the player is near.
    internal static class EnchantTable
    {
        internal const string Name = "Overhaul_EnchantTable";
        internal const string StationName = "$overhaul_enchanttable";
        static AssetBundle bundle;

        // The game prefabs whose materials the model wears (JVLmock_ materials in the bundle, swapped by Jotunn): altar,
        // crypt bones, eternal candles, maps, runes. Held for the session so the game keeps their textures loaded.
        static readonly string[] MaterialSources =
            { "offeraltar_deer", "Pickable_ForestCryptRemains03", "piece_magetable_ext2", "piece_cartographytable", "piece_magetable_ext" };
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
            // The bundle's JVLmock_ materials become the game's now, before the icon is drawn from the model.
            foreach (var held in MaterialSources) GamePrefabs.Held(held);
            prefab.FixReferences(true);
            Dress(visual);
            // Stone sounds and debris when built, hit and destroyed: the stone wall's.
            var stone = PrefabManager.Instance.GetPrefab("stone_wall_1x1");
            var piece = prefab.GetComponent<Piece>(); var wear = prefab.GetComponent<WearNTear>();
            // The build menu sorts by usage tags: without one the table only shows under "All".
            piece.m_usage = Piece.UsageTagFlags.Crafting;
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
            var config = new PieceConfig
            {
                PieceTable = "Hammer", Category = "Crafting", CraftingStation = "piece_workbench", Icon = icon,
                Requirements = new[] { ("Stone", 10), ("FineWood", 6), ("BoneFragments", 10), ("SurtlingCore", 2) }
                    .Select(r => new RequirementConfig(r.Item1, r.Item2, 0, true)).ToArray()
            };
            PieceManager.Instance.AddPiece(new CustomPiece(prefab, false, config));
        }

        static void Dress(GameObject visual)
        {
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
    }

    // The wooden runes float above the table while any player is near. The violet glow of the eternal candles swells
    // while the local player is within reach of the table, and fades back
    // when they leave. Seen by each player for themself: no network traffic.
    internal sealed class EnchantTableGlow : MonoBehaviour
    {
        const float Reach = 5f, Boost = 1.5f, RangeBoost = 1.15f, FlameBoost = 1.2f, Speed = 2f, FlameScale = .3f;
        Light glow; LightFlicker flicker; LightLod lod;
        float intensity, range, level;
        Transform[] flames;
        ZNetView nview; Animator runes; bool open;

        // A table loaded again starts with its Animator reset: runes down.
        void OnEnable() => open = false;

        void Start()
        {
            nview = GetComponent<ZNetView>(); runes = GetComponentInChildren<Animator>(true);
            // The candles' light (the runes' own glow light is theirs, driven by their Animator).
            glow = transform.Find("Model/LightAnchor")?.GetComponentInChildren<Light>(true);
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
            // The wooden runes (OverhaulLab FloatingPieces) rise off the table, float and settle back on the "Open" bool
            // while any player is within reach, like the grimoire stand's candles: every machine sees the same.
            // Building ghosts keep them down.
            bool near = nview && nview.IsValid() && Player.IsPlayerInRange(transform.position, Reach);
            if (runes && near != open) { open = near; runes.SetBool("Open", open); }
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
