using System.Collections.Generic;
using System.Linq;
using Jotunn.Entities;
using Jotunn.Managers;
using Overhaul.Utility;
using UnityEngine;

namespace Overhaul.Magic
{
    // Grimoire stand: a hammer piece (Crafting tab, at a workbench) made from the game's table (network object,
    // wear and tear, build and destroy effects) wearing the overhaul_grimoirestand model (OverhaulLab GrimoireStand:
    // book stand, rigged grimoire, candle holder and candles). The book opens while any player stands nearby and
    // closes when none does; every machine sees the players around it, so everyone sees the same book without network
    // traffic. Using the stand opens the grimoire window. It can be moved like the other functional pieces.
    internal static class GrimoireStand
    {
        internal const string Name = "Overhaul_GrimoireStand";
        static AssetBundle bundle;

        internal static void Initialize() => PrefabManager.OnVanillaPrefabsAvailable += Register;
        internal static void Shutdown() => PrefabManager.OnVanillaPrefabsAvailable -= Register;

        static void Register()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= Register;
            if (PrefabManager.Instance.GetPrefab(Name)) return;
            if (!bundle) bundle = EmbeddedAssets.LoadBundle("Overhaul.Assets.overhaul_grimoirestand");
            var model = bundle.LoadAsset<GameObject>("GrimoireStand_Piece");
            // The icon's sprite, else one made from its texture (an icon imported as several sprites is named _0).
            var icon = bundle.LoadAssetWithSubAssets<Sprite>("GrimoireStand_Icon").FirstOrDefault();
            if (!icon) { var texture = bundle.LoadAsset<Texture2D>("GrimoireStand_Icon"); if (texture) icon = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f)); }
            if (!icon) Log.LogWarning("Grimoire stand: icon missing from overhaul_grimoirestand");
            if (!model) { Log.LogError("Grimoire stand: model missing from overhaul_grimoirestand"); return; }

            var custom = new CustomPiece(Name, "piece_table", "_HammerPieceTable");
            var prefab = custom.PiecePrefab;
            // The table's own model, colliders and LOD go; its network object, piece and wear and tear stay.
            foreach (var lod in prefab.GetComponents<LODGroup>()) Object.DestroyImmediate(lod);
            foreach (Transform child in prefab.transform.Cast<Transform>().ToArray()) Object.DestroyImmediate(child.gameObject);
            var wear = prefab.GetComponent<WearNTear>();
            if (wear) { wear.m_new = wear.m_worn = wear.m_broken = null; wear.m_materialType = WearNTear.MaterialType.Wood; wear.m_health = 400; }
            // The whole model (stand, book, candles, colliders) 20 % bigger than authored.
            var visual = Object.Instantiate(model, prefab.transform, false); visual.name = "Model"; visual.transform.localScale = Vector3.one * 1.2f;
            GameLook(visual);
            int layer = LayerMask.NameToLayer("piece");
            foreach (var t in visual.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;

            var piece = prefab.GetComponent<Piece>();
            piece.m_name = "$overhaul_grimoirestand"; piece.m_description = "$overhaul_grimoirestand_desc";
            if (icon) piece.m_icon = icon;
            piece.m_category = Piece.PieceCategory.Crafting;
            piece.m_craftingStation = PrefabManager.Instance.GetPrefab("piece_workbench")?.GetComponent<CraftingStation>();
            piece.m_resources = new[] { ("FineWood", 8), ("Bronze", 4), ("SurtlingCore", 1) }
                .Select(r => new Piece.Requirement { m_resItem = PrefabManager.Instance.GetPrefab(r.Item1).GetComponent<ItemDrop>(), m_amount = r.Item2, m_recover = true }).ToArray();
            prefab.AddComponent<GrimoireStandPiece>();
            PieceManager.Instance.AddPiece(custom);
        }

        // The pack's model in the game's look: the stand and the tall candle holder in the wood wall's planks (their post
        // turned along the grain in the bundle), the flat flame sprites (black edged in the game) replaced by the resin
        // candle's animated flame, and a dimmer, warmer candle light. The game prefabs borrowed are held (GamePrefabs).
        static void GameLook(GameObject visual)
        {
            var wall = GamePrefabs.Held("woodwall")?.transform.Find("New/_Combined Mesh [high]")?.GetComponent<MeshRenderer>()?.sharedMaterial;
            var woodParts = visual.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.name == "Magic Book-Stand 1" || r.name == "Tall Candle Holder 1").ToArray();
            if (wall && woodParts.Length > 0)
            {
                // Their textures are wood atlases over about half of their UV square: one plank texture per atlas half.
                var wood = new Material(wall) { name = "GrimoireStand_wood", mainTextureScale = new Vector2(2, 2) };
                foreach (var part in woodParts) part.sharedMaterials = part.sharedMaterials.Select(_ => wood).ToArray();
            }
            else Log.LogWarning("Grimoire stand: wood wall material not found, pack wood kept");

            var flame = GamePrefabs.Held("Candle_resin")?.transform.Find("high/vfx")?.gameObject;
            var sprites = visual.GetComponentsInChildren<SpriteRenderer>(true);
            if (flame && sprites.Length > 0)
            {
                // Each candle's flame is two crossed sprites: one game flame per wick, sized on the sprites.
                var wicks = new List<(Vector3 position, float height, Transform parent)>();
                foreach (var sprite in sprites)
                {
                    if (!sprite.sprite) { Object.DestroyImmediate(sprite.gameObject); continue; }
                    // From the sprite itself: renderer bounds are empty on the inactive prefab.
                    var b = sprite.sprite.bounds; var t = sprite.transform;
                    var center = t.TransformPoint(b.center); float h = b.size.y * t.lossyScale.y;
                    if (!wicks.Any(w => (w.position - center).sqrMagnitude < .02f * .02f)) wicks.Add((center, h, t.parent));
                    Object.DestroyImmediate(sprite.gameObject);
                }
                foreach (var (position, height, parent) in wicks)
                {
                    var copy = Object.Instantiate(flame, parent, false); copy.name = "flame";
                    // The game's flame burns from its root up: its root at the sprites' bottom.
                    copy.transform.position = position - Vector3.up * height * .5f;
                    copy.transform.rotation = Quaternion.identity;
                    copy.transform.localScale = Vector3.one * (height / .25f / parent.lossyScale.y);
                    foreach (var ps in copy.GetComponentsInChildren<ParticleSystem>(true)) { var main = ps.main; main.scalingMode = ParticleSystemScalingMode.Hierarchy; }
                }
            }
            else Log.LogWarning("Grimoire stand: resin candle flame not found, pack sprites kept");

            foreach (var light in visual.GetComponentsInChildren<Light>(true))
            {
                light.color = new Color(1f, .72f, .36f);
                light.intensity = 1.6f;
                light.range = 4f;
            }
        }
    }

    internal sealed class GrimoireStandPiece : MonoBehaviour, Hoverable, Interactable
    {
        // A player within this distance opens the book; the window closes past WindowRange.
        internal const float Range = 4f, WindowRange = 6f;
        ZNetView nview;
        Piece piece;
        Animator book;
        bool open;
        readonly List<(Light light, float intensity, float seed)> lights = new List<(Light, float, float)>();

        void Awake()
        {
            nview = GetComponent<ZNetView>(); piece = GetComponent<Piece>();
            book = GetComponentInChildren<Animator>(true);
            foreach (var l in GetComponentsInChildren<Light>(true)) lights.Add((l, l.intensity, Random.value * 100));
        }

        // A stand loaded again starts with its Animator reset: closed.
        void OnEnable() => open = false;

        void Update()
        {
            foreach (var (light, intensity, seed) in lights)
                if (light) light.intensity = intensity * (.85f + .3f * Mathf.PerlinNoise(seed, Time.time * 5));
            // The book's Animator (OverhaulLab GrimoireBookAnimator) plays its opening and closing on the "Open" bool.
            // Building ghosts and unloaded stands keep the book closed.
            bool near = nview && nview.IsValid() && Player.IsPlayerInRange(transform.position, Range);
            if (book && near != open) { open = near; book.SetBool("Open", open); }
        }

        public string GetHoverName() => piece ? piece.m_name : "";
        public float GetHoverOffset() => 0;
        public string GetHoverText() =>
            Localization.instance.Localize(GetHoverName() + "\n[<color=yellow><b>$KEY_Use</b></color>] $overhaul_grimoirestand_read");
        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold || user != Player.m_localPlayer || !nview || !nview.IsValid()) return false;
            GrimoireWindow.Open(transform);
            return true;
        }
        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;
    }
}
