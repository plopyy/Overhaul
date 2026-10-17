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
        // their three flames and their light, at the same spots of the model.
        const float CandleScale = .3f;
        static readonly Vector3[] Flames = { new Vector3(-.296f, .895f, .787f), new Vector3(-.359f, 1f, .737f), new Vector3(-.387f, .952f, .815f) };
        static readonly Vector3 CandleLight = new Vector3(-.333f, 1.036f, .771f);

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
            prefab.AddComponent<EnchantTableDiagnostic>();
            Log.LogInfo("Enchanting table diagnostic: prefab " + EnchantTableDiagnostic.Describe(prefab));
            PieceManager.Instance.AddPiece(custom);
        }

        static void Dress(GameObject visual)
        {
            foreach (var (part, source, name) in Materials)
            {
                var renderer = visual.transform.Find(part)?.GetComponent<MeshRenderer>();
                var material = PrefabManager.Instance.GetPrefab(source)?.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).FirstOrDefault(m => m && m.name == name);
                if (!renderer || !material) { Log.LogWarning("Enchanting table: " + part + " left without the " + name + " material of " + source); continue; }
                renderer.sharedMaterials = Enumerable.Repeat(material, renderer.sharedMaterials.Length).ToArray();
            }

            var candles = PrefabManager.Instance.GetPrefab("piece_magetable_ext2")?.transform.Find("new");
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

namespace Overhaul.Rarity
{
    // Temporary: the table and its ghost show nothing in game. Logs what the placed table and its ghost look like.
    internal sealed class EnchantTableDiagnostic : MonoBehaviour
    {
        internal static string Describe(GameObject go)
        {
            var t = go.transform;
            var parts = go.GetComponentsInChildren<Renderer>(true).Select(r => r.name + (r.enabled ? "" : " OFF") + (r.gameObject.activeInHierarchy ? "" : " INACTIVE")
                + " layer " + LayerMask.LayerToName(r.gameObject.layer) + " visible " + r.isVisible + " bounds " + r.bounds.center.ToString("F2") + "/" + r.bounds.size.ToString("F2")
                + " mats " + string.Join("+", r.sharedMaterials.Select(m => m ? m.name + ":" + m.shader.name : "null")));
            return go.name + " active " + go.activeInHierarchy + " pos " + t.position.ToString("F2") + " rot " + t.eulerAngles.ToString("F0") + " scale " + t.lossyScale.ToString("F3")
                + " | " + string.Join(" | ", parts);
        }
        void Start() { Log.LogInfo("Enchanting table diagnostic: start " + (Player.IsPlacementGhost(gameObject) ? "(ghost) " : "") + Describe(gameObject)); Invoke(nameof(Later), 2f); }
        int frames;
        void Update()
        {
            if (++frames > 6) return;
            Log.LogInfo("Enchanting table diagnostic: frame " + frames + (Player.IsPlacementGhost(gameObject) ? " (ghost) " : " ") + transform.position.ToString("F2")
                + " drawn " + string.Join(",", GetComponentsInChildren<MeshRenderer>().Select(r => r.name + "=" + r.isVisible)));
        }
        // Who removes the table: the game's network removal, with the call stack.
        [HarmonyLib.HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Destroy))]
        static class NetworkRemoval
        {
            static void Prefix(GameObject go)
            {
                if (go && go.name.StartsWith(EnchantTable.Name))
                    Log.LogInfo("Enchanting table diagnostic: ZNetScene.Destroy " + go.name + "\n" + System.Environment.StackTrace);
            }
        }
        void Later() => Log.LogInfo("Enchanting table diagnostic: 2 s later " + Describe(gameObject));
        void OnDestroy() => Log.LogInfo("Enchanting table diagnostic: destroyed " + name + " after " + frames + " frames\n" + System.Environment.StackTrace);
    }
}
