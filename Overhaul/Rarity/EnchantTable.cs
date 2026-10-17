using System.Linq;
using Jotunn.Entities;
using Jotunn.Managers;
using Overhaul.Utility;
using UnityEngine;

namespace Overhaul.Rarity
{
    // Enchanting table: a hammer piece (Crafting tab, at a workbench) made from the game's table (network object, wear
    // and tear, build and destroy effects) wearing the overhaul_enchanttable model (OverhaulLab EnchantTable: Eikthyr's
    // altar holding the cartography table's maps, a rune table, crypt bones and eternal candles). Its parts wear the
    // game's own materials and the eternal candles burn with their game flames. No use yet: the enchanting comes later.
    internal static class EnchantTable
    {
        internal const string Name = "Overhaul_EnchantTable";
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
        // The eternal candles are the galdr table's, 0.3 times their size and turned 263° (fitted on the model's mesh):
        // their three flames and their light, at the same spots of the model (table top raised by 0.25 m on 2026-10-17).
        const float CandleScale = .3f;
        static readonly Vector3[] Flames = { new Vector3(-.296f, 1.145f, .787f), new Vector3(-.359f, 1.25f, .737f), new Vector3(-.387f, 1.202f, .815f) };
        static readonly Vector3 CandleLight = new Vector3(-.333f, 1.286f, .771f);

        internal static void Initialize() => PrefabManager.OnVanillaPrefabsAvailable += Register;
        internal static void Shutdown() => PrefabManager.OnVanillaPrefabsAvailable -= Register;

        static void Register()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= Register;
            if (PrefabManager.Instance.GetPrefab(Name)) return;
            if (!bundle) bundle = EmbeddedAssets.LoadBundle("Overhaul.Assets.overhaul_enchanttable");
            var model = bundle ? bundle.LoadAsset<GameObject>("EnchantTable_Piece") : null;
            if (!model) { Log.LogError("Enchanting table: model missing from overhaul_enchanttable"); return; }

            var custom = new CustomPiece(Name, "piece_table", "_HammerPieceTable");
            var prefab = custom.PiecePrefab;
            // The table's own model, colliders and LOD go; its network object, piece and wear and tear stay.
            foreach (var lod in prefab.GetComponents<LODGroup>()) Object.DestroyImmediate(lod);
            foreach (Transform child in prefab.transform.Cast<Transform>().ToArray()) Object.DestroyImmediate(child.gameObject);
            var wear = prefab.GetComponent<WearNTear>();
            if (wear) { wear.m_new = wear.m_worn = wear.m_broken = null; wear.m_materialType = WearNTear.MaterialType.Stone; wear.m_health = 800; }
            var visual = Object.Instantiate(model, prefab.transform, false); visual.name = "Model";
            Dress(visual);
            int layer = LayerMask.NameToLayer("piece");
            foreach (var t in visual.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;

            var piece = prefab.GetComponent<Piece>();
            piece.m_name = "$overhaul_enchanttable"; piece.m_description = "$overhaul_enchanttable_desc";
            piece.m_category = Piece.PieceCategory.Crafting;
            piece.m_craftingStation = PrefabManager.Instance.GetPrefab("piece_workbench")?.GetComponent<CraftingStation>();
            // Provisional recipe, until the enchanting itself is made.
            piece.m_resources = new[] { ("Stone", 10), ("FineWood", 6), ("BoneFragments", 4), ("SurtlingCore", 1) }
                .Select(r => new Piece.Requirement { m_resItem = PrefabManager.Instance.GetPrefab(r.Item1).GetComponent<ItemDrop>(), m_amount = r.Item2, m_recover = true }).ToArray();
            // The build menu icon, drawn from the dressed model.
            var icon = RenderManager.Instance.Render(new RenderManager.RenderRequest(visual)
            {
                Width = 128, Height = 128, Rotation = RenderManager.IsometricRotation,
                DistanceMultiplier = 1.1f, ParticleSimulationTime = -1f
            });
            if (icon) piece.m_icon = icon;
            PieceManager.Instance.AddPiece(custom);
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
            foreach (var position in Flames)
            {
                var copy = Object.Instantiate(flame, visual.transform, false); copy.name = "flame";
                copy.transform.localPosition = position; copy.transform.localRotation = Quaternion.identity; copy.transform.localScale = Vector3.one * CandleScale;
                foreach (var ps in copy.GetComponentsInChildren<ParticleSystem>(true)) { var main = ps.main; main.scalingMode = ParticleSystemScalingMode.Hierarchy; }
            }
            if (light)
            {
                var copy = Object.Instantiate(light, visual.transform, false); copy.name = "Point Light";
                copy.transform.localPosition = CandleLight;
                var l = copy.GetComponent<Light>(); l.range *= .5f;
            }
        }
    }
}
