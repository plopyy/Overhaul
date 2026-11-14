using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using Overhaul.Utility;
using UnityEngine;

namespace Overhaul.Magic
{
    // Our own mage sets, new items cloned from the Mistlands mage set (same armour and Eitr bonuses) wearing our models:
    // - Mystic mage (overhaul_magemystic, OverhaulLab MageSetBundle): hat, robe, boots.
    // - Shadow mage, "Mage Ombral" (overhaul_shadowmage, OverhaulLab ShadowMageBundle): hat, chest, legs; its hanging cloths
    //   carry MagicaCloth, which the game sets up itself on any worn skin (VisEquipment.SetupCloth: body colliders, bones).
    // Pieces are skinned on the player's bones by PlayerFbxWork. No recipe and no loot yet: spawned by an admin.
    // The body pieces hide the body under them: while one is worn the player's body mesh is a copy without the faces listed
    // in the set's data file (per body model, the faces under the chest piece and under the legs piece).
    internal static class MageMysticSet
    {
        sealed class Piece { public string Prefab, Base, Model, Name; }
        sealed class GearSet
        {
            public string Bundle, Data, SetName, Chest, Legs;
            public Piece[] Pieces;
            public AssetBundle Loaded;
            public int ChestHash, LegsHash;
            public readonly List<Cut> Cuts = new List<Cut>();
        }
        static readonly GearSet[] Sets =
        {
            new GearSet
            {
                Bundle = "overhaul_magemystic", Data = "MageMystic_Data", SetName = "MageMystic", Chest = "Overhaul_MageMysticRobe", Legs = "Overhaul_MageMysticBoots",
                Pieces = new[]
                {
                    new Piece { Prefab = "Overhaul_MageMysticHat", Base = "HelmetMage", Model = "MageMystic_Hat", Name = "$overhaul_magemystic_hat" },
                    new Piece { Prefab = "Overhaul_MageMysticRobe", Base = "ArmorMageChest", Model = "MageMystic_Robe", Name = "$overhaul_magemystic_robe" },
                    new Piece { Prefab = "Overhaul_MageMysticBoots", Base = "ArmorMageLegs", Model = "MageMystic_Boots", Name = "$overhaul_magemystic_boots" },
                },
            },
            new GearSet
            {
                Bundle = "overhaul_shadowmage", Data = "ShadowMage_Data", SetName = "ShadowMage", Chest = "Overhaul_ShadowMageChest", Legs = "Overhaul_ShadowMageLegs",
                Pieces = new[]
                {
                    new Piece { Prefab = "Overhaul_ShadowMageHat", Base = "HelmetMage", Model = "ShadowMage_Hat", Name = "$overhaul_shadowmage_hat" },
                    new Piece { Prefab = "Overhaul_ShadowMageChest", Base = "ArmorMageChest", Model = "ShadowMage_Chest", Name = "$overhaul_shadowmage_chest" },
                    new Piece { Prefab = "Overhaul_ShadowMageLegs", Base = "ArmorMageLegs", Model = "ShadowMage_Legs", Name = "$overhaul_shadowmage_legs" },
                },
            },
        };

        internal static void Initialize() => PrefabManager.OnVanillaPrefabsAvailable += Register;
        internal static void Shutdown() => PrefabManager.OnVanillaPrefabsAvailable -= Register;

        static void Register()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= Register;
            foreach (var set in Sets)
            {
                set.ChestHash = set.Chest.GetStableHashCode(); set.LegsHash = set.Legs.GetStableHashCode();
                if (!set.Loaded) set.Loaded = EmbeddedAssets.LoadBundle("Overhaul.Assets." + set.Bundle);
                if (!set.Loaded) { Log.LogError("Mage sets: bundle " + set.Bundle + " missing"); continue; }
                ReadCuts(set, set.Loaded.LoadAsset<TextAsset>(set.Data));
                foreach (var p in set.Pieces)
                {
                    if (PrefabManager.Instance.GetPrefab(p.Prefab)) continue;
                    var model = set.Loaded.LoadAsset<GameObject>(p.Model);
                    if (!model) { Log.LogError("Mage sets: " + p.Model + " missing from " + set.Bundle); continue; }
                    var item = new CustomItem(p.Prefab, p.Base, new ItemConfig { Name = p.Name, Description = p.Name + "_description" });
                    var shared = item.ItemDrop.m_itemData.m_shared;
                    shared.m_name = p.Name; shared.m_description = p.Name + "_description";
                    // A set of its own when the base pieces carry a set effect (they have none in the current game: each piece
                    // only brings its Eitr regeneration), so ours never complete the Mistlands set.
                    if (shared.m_setStatusEffect) shared.m_setName = set.SetName;
                    // The hats sit on the head: hair and beard stay.
                    shared.m_helmetHideHair = ItemDrop.ItemData.HelmetHairType.Default; shared.m_helmetHideBeard = ItemDrop.ItemData.HelmetHairType.Default;
                    // The chest pieces paint nothing on the body (the Mistlands robe's sleeves would show on the forearms); the
                    // mystic boots keep the Mistlands trousers painted on the legs, the shadow legs model its own.
                    if (shared.m_itemType == ItemDrop.ItemData.ItemType.Chest) shared.m_armorMaterial = null;
                    if (shared.m_itemType == ItemDrop.ItemData.ItemType.Legs && set.SetName == "ShadowMage") shared.m_armorMaterial = null;
                    var icon = set.Loaded.LoadAsset<Sprite>(p.Model + "_Icon");
                    if (icon) shared.m_icons = new[] { icon };
                    Dress(item.ItemPrefab, model, set.SetName);
                    ItemManager.Instance.AddItem(item);
                }
            }
        }

        // Our worn skin and ground model in place of the base item's, on copies of the game's mage armour material carrying
        // our textures (its own normal and metal maps, made for its own UVs, left out).
        static readonly Dictionary<Texture, Material> Materials = new Dictionary<Texture, Material>();
        static void Dress(GameObject prefab, GameObject model, string setName)
        {
            var oldSkin = prefab.transform.Find("attach_skin");
            var gameRenderer = oldSkin ? oldSkin.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
            var gameMaterial = gameRenderer ? gameRenderer.sharedMaterial : null;
            if (!gameMaterial) { Log.LogError("Mage sets: " + prefab.name + " has no worn skin material to copy"); return; }
            int skinLayer = gameRenderer.gameObject.layer, groundLayer = prefab.layer;
            Object.DestroyImmediate(oldSkin.gameObject);
            foreach (var lod in prefab.GetComponentsInChildren<LODGroup>(true)) Object.DestroyImmediate(lod);
            // The base item's ground model goes, its colliders stay.
            foreach (var renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                groundLayer = renderer.gameObject.layer;
                var filter = renderer.GetComponent<MeshFilter>(); Object.DestroyImmediate(renderer); if (filter) Object.DestroyImmediate(filter);
            }
            foreach (Transform part in model.transform)
            {
                var copy = Object.Instantiate(part.gameObject, prefab.transform, false); copy.name = part.name;
                int layer = part.name == "attach_skin" ? skinLayer : groundLayer;
                foreach (var t in copy.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
                foreach (var renderer in copy.GetComponentsInChildren<Renderer>(true))
                    renderer.sharedMaterials = renderer.sharedMaterials.Select(m => GameMaterial(gameMaterial, m, setName)).ToArray();
            }
        }
        static Material GameMaterial(Material game, Material ours, string setName)
        {
            var texture = ours ? ours.mainTexture : null;
            if (!texture) return game;
            if (Materials.TryGetValue(texture, out var made) && made) return made;
            made = new Material(game) { name = setName + "_" + texture.name };
            made.mainTexture = texture;
            made.SetTexture("_BumpMap", null); made.SetTexture("_MetallicGlossMap", null); made.SetFloat("_Metallic", 0f);
            return Materials[texture] = made;
        }

        // ---------------------------------------------------------------- body under the chest and legs pieces
        sealed class Cut { public int Triangles; public HashSet<int> Chest = new HashSet<int>(), Legs = new HashSet<int>(); }
        static void ReadCuts(GearSet set, TextAsset data)
        {
            set.Cuts.Clear();
            if (!data) { Log.LogError("Mage sets: " + set.Data + " missing from " + set.Bundle); return; }
            var byModel = new Dictionary<string, Cut>();
            foreach (var line in data.text.Split('\n'))
            {
                var p = line.Trim().Split(' ');
                if (p.Length < 2 || p[0] != "body" && p[0] != "chest" && p[0] != "legs") continue;
                if (!byModel.TryGetValue(p[1], out var cut)) { byModel[p[1]] = cut = new Cut(); set.Cuts.Add(cut); }
                if (p[0] == "body") { cut.Triangles = int.Parse(p[2], CultureInfo.InvariantCulture); continue; }
                var faces = p[0] == "chest" ? cut.Chest : cut.Legs;
                for (int i = 2; i < p.Length; i++) if (p[i].Length > 0) faces.Add(int.Parse(p[i], CultureInfo.InvariantCulture));
            }
        }

        // Worn pieces as a key: chest set index + 1 and legs set index + 1 (0 = none of ours).
        static int Key(int chest, int legs) => chest * 16 + legs;

        // Body mesh without the faces the worn pieces hide, per (game mesh, worn pieces). The game mesh itself when a list
        // does not fit it (another face count: the game's model changed).
        static readonly Dictionary<(int mesh, int key), Mesh> Variants = new Dictionary<(int, int), Mesh>();
        static readonly HashSet<int> Warned = new HashSet<int>();
        static Mesh Variant(Mesh source, int chest, int legs)
        {
            int key = Key(chest, legs);
            if (!source || key == 0) return source;
            if (Variants.TryGetValue((source.GetInstanceID(), key), out var made) && made) return made;
            long total = 0; for (int s = 0; s < source.subMeshCount; s++) total += source.GetIndexCount(s) / 3;
            var chestCut = chest > 0 ? Sets[chest - 1].Cuts.FirstOrDefault(c => c.Triangles == total) : null;
            var legsCut = legs > 0 ? Sets[legs - 1].Cuts.FirstOrDefault(c => c.Triangles == total) : null;
            if (chestCut == null && legsCut == null || !source.isReadable)
            {
                if (Warned.Add(source.GetInstanceID())) Log.LogWarning("Mage sets: body mesh " + source.name + " (" + total + " faces" + (source.isReadable ? "" : ", not readable") + ") has no face list, it stays whole under the gear");
                return Variants[(source.GetInstanceID(), key)] = source;
            }
            made = Object.Instantiate(source); made.name = source.name + "_mageset" + key;
            int index = 0;
            for (int s = 0; s < source.subMeshCount; s++)
            {
                var triangles = source.GetTriangles(s); var kept = new List<int>(triangles.Length);
                for (int i = 0; i < triangles.Length; i += 3, index++)
                {
                    if (chestCut != null && chestCut.Chest.Contains(index) || legsCut != null && legsCut.Legs.Contains(index)) continue;
                    kept.Add(triangles[i]); kept.Add(triangles[i + 1]); kept.Add(triangles[i + 2]);
                }
                made.SetTriangles(kept, s, false);
            }
            made.bounds = source.bounds;
            return Variants[(source.GetInstanceID(), key)] = made;
        }

        // Per player visual: the pieces worn and the game's own body meshes (one per body model).
        sealed class State { public int Key; public Mesh[] Originals; }
        static readonly ConditionalWeakTable<VisEquipment, State> States = new ConditionalWeakTable<VisEquipment, State>();

        // The game puts m_models[model].m_mesh back on the body whenever it differs (UpdateBaseModel, every frame): the
        // variant goes into this player's own m_models, and the game then applies it itself.
        [HarmonyPatch(typeof(VisEquipment), "UpdateEquipmentVisuals")]
        static class BodyUnderPieces
        {
            static void Postfix(VisEquipment __instance)
            {
                if (!__instance.m_isPlayer || __instance.m_isArmorStand || !__instance.m_bodyModel || __instance.m_models == null || __instance.m_models.Length == 0) return;
                int chest = 0, legs = 0;
                for (int i = 0; i < Sets.Length; i++)
                {
                    if (Sets[i].ChestHash != 0 && __instance.m_currentChestItemHash == Sets[i].ChestHash) chest = i + 1;
                    if (Sets[i].LegsHash != 0 && __instance.m_currentLegItemHash == Sets[i].LegsHash) legs = i + 1;
                }
                int key = Key(chest, legs);
                if (!States.TryGetValue(__instance, out var state))
                {
                    if (key == 0) return;
                    States.Add(__instance, state = new State { Originals = __instance.m_models.Select(m => m.m_mesh).ToArray() });
                }
                if (state.Key == key) return;
                bool chestOn = chest != 0 && state.Key / 16 != chest;
                state.Key = key;
                for (int i = 0; i < __instance.m_models.Length && i < state.Originals.Length; i++) __instance.m_models[i].m_mesh = Variant(state.Originals[i], chest, legs);
                // Our chest pieces paint nothing: the bare chest instead of what the armour worn before left on the body.
                if (chestOn)
                {
                    var material = __instance.m_bodyModel.material;
                    material.SetTexture(VisEquipment.s_chestTex, __instance.m_emptyBodyTexture);
                    material.SetTexture(VisEquipment.s_chestBumpMap, __instance.m_emptyBodyBumpTexture);
                    material.SetTexture(VisEquipment.s_chestMetal, null);
                }
            }
        }
    }
}
