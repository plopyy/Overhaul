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
    // Mystic mage set: hat, robe and boots, three new items cloned from the Mistlands mage set (same armour and Eitr
    // bonuses), with our own models (overhaul_magemystic bundle: OverhaulLab MageSetBundle, pieces
    // skinned on the player's bones by PlayerFbxWork). No recipe and no loot yet: spawned by an admin.
    // The robe and the boots hide the body under them: while one is worn the player's body mesh is a copy without the faces
    // listed in the bundle (MageMystic_Data: per body model, the faces under the robe and under the boots).
    internal static class MageMysticSet
    {
        sealed class Piece { public string Prefab, Base, Model, Name; }
        static readonly Piece[] Pieces =
        {
            new Piece { Prefab = "Overhaul_MageMysticHat", Base = "HelmetMage", Model = "MageMystic_Hat", Name = "$overhaul_magemystic_hat" },
            new Piece { Prefab = "Overhaul_MageMysticRobe", Base = "ArmorMageChest", Model = "MageMystic_Robe", Name = "$overhaul_magemystic_robe" },
            new Piece { Prefab = "Overhaul_MageMysticBoots", Base = "ArmorMageLegs", Model = "MageMystic_Boots", Name = "$overhaul_magemystic_boots" },
        };
        const string SetName = "MageMystic";
        static readonly int RobeHash = "Overhaul_MageMysticRobe".GetStableHashCode(), BootsHash = "Overhaul_MageMysticBoots".GetStableHashCode();
        const int RobeBit = 1, BootsBit = 2;

        static AssetBundle bundle;
        internal static void Initialize() => PrefabManager.OnVanillaPrefabsAvailable += Register;
        internal static void Shutdown() => PrefabManager.OnVanillaPrefabsAvailable -= Register;

        static void Register()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= Register;
            if (!bundle) bundle = EmbeddedAssets.LoadBundle("Overhaul.Assets.overhaul_magemystic");
            ReadCuts(bundle.LoadAsset<TextAsset>("MageMystic_Data"));
            foreach (var p in Pieces)
            {
                if (PrefabManager.Instance.GetPrefab(p.Prefab)) continue;
                var model = bundle.LoadAsset<GameObject>(p.Model);
                if (!model) { Log.LogError("Mystic mage set: " + p.Model + " missing from overhaul_magemystic"); continue; }
                var item = new CustomItem(p.Prefab, p.Base, new ItemConfig { Name = p.Name, Description = p.Name + "_description" });
                var shared = item.ItemDrop.m_itemData.m_shared;
                shared.m_name = p.Name; shared.m_description = p.Name + "_description";
                // A set of its own when the base pieces carry a set effect (they have none in the current game: each piece
                // only brings its Eitr regeneration), so ours never complete the Mistlands set.
                if (shared.m_setStatusEffect) shared.m_setName = SetName;
                // The hat sits on the head: hair and beard stay.
                shared.m_helmetHideHair = ItemDrop.ItemData.HelmetHairType.Default; shared.m_helmetHideBeard = ItemDrop.ItemData.HelmetHairType.Default;
                // The robe paints nothing on the body (the Mistlands robe's sleeves would show on the forearms); the boots keep
                // the Mistlands trousers painted on the legs.
                if (shared.m_itemType == ItemDrop.ItemData.ItemType.Chest) shared.m_armorMaterial = null;
                var icon = bundle.LoadAsset<Sprite>(p.Model + "_Icon");
                if (icon) shared.m_icons = new[] { icon };
                Dress(item.ItemPrefab, model);
                ItemManager.Instance.AddItem(item);
            }
        }

        // Our worn skin and ground model in place of the base item's, on copies of the game's mage armour material carrying
        // our textures (its own normal and metal maps, made for its own UVs, left out).
        static readonly Dictionary<Texture, Material> Materials = new Dictionary<Texture, Material>();
        static void Dress(GameObject prefab, GameObject model)
        {
            var oldSkin = prefab.transform.Find("attach_skin");
            var gameRenderer = oldSkin ? oldSkin.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
            var gameMaterial = gameRenderer ? gameRenderer.sharedMaterial : null;
            if (!gameMaterial) { Log.LogError("Mystic mage set: " + prefab.name + " has no worn skin material to copy"); return; }
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
                    renderer.sharedMaterials = renderer.sharedMaterials.Select(m => GameMaterial(gameMaterial, m)).ToArray();
            }
        }
        static Material GameMaterial(Material game, Material ours)
        {
            var texture = ours ? ours.mainTexture : null;
            if (!texture) return game;
            if (Materials.TryGetValue(texture, out var made) && made) return made;
            made = new Material(game) { name = "MageMystic_" + texture.name };
            made.mainTexture = texture;
            made.SetTexture("_BumpMap", null); made.SetTexture("_MetallicGlossMap", null); made.SetFloat("_Metallic", 0f);
            return Materials[texture] = made;
        }

        // ---------------------------------------------------------------- body under the robe and the boots
        sealed class Cut { public int Triangles; public HashSet<int> Robe = new HashSet<int>(), Boots = new HashSet<int>(); }
        static readonly List<Cut> Cuts = new List<Cut>();
        static void ReadCuts(TextAsset data)
        {
            Cuts.Clear();
            if (!data) { Log.LogError("Mystic mage set: MageMystic_Data missing from overhaul_magemystic"); return; }
            var byModel = new Dictionary<string, Cut>();
            foreach (var line in data.text.Split('\n'))
            {
                var p = line.Trim().Split(' ');
                if (p.Length < 2 || p[0] != "body" && p[0] != "chest" && p[0] != "legs") continue;
                if (!byModel.TryGetValue(p[1], out var cut)) { byModel[p[1]] = cut = new Cut(); Cuts.Add(cut); }
                if (p[0] == "body") { cut.Triangles = int.Parse(p[2], CultureInfo.InvariantCulture); continue; }
                var set = p[0] == "chest" ? cut.Robe : cut.Boots;
                for (int i = 2; i < p.Length; i++) if (p[i].Length > 0) set.Add(int.Parse(p[i], CultureInfo.InvariantCulture));
            }
        }

        // Body mesh without the faces the worn pieces hide, per (game mesh, pieces). The game mesh itself when the list does
        // not fit it (another face count: the game's model changed).
        static readonly Dictionary<(int mesh, int mask), Mesh> Variants = new Dictionary<(int, int), Mesh>();
        static readonly HashSet<int> Warned = new HashSet<int>();
        static Mesh Variant(Mesh source, int mask)
        {
            if (!source || mask == 0) return source;
            var key = (source.GetInstanceID(), mask);
            if (Variants.TryGetValue(key, out var made) && made) return made;
            long total = 0; for (int s = 0; s < source.subMeshCount; s++) total += source.GetIndexCount(s) / 3;
            var cut = Cuts.FirstOrDefault(c => c.Triangles == total);
            if (cut == null || !source.isReadable)
            {
                if (Warned.Add(source.GetInstanceID())) Log.LogWarning("Mystic mage set: body mesh " + source.name + " (" + total + " faces" + (source.isReadable ? "" : ", not readable") + ") has no face list, it stays whole under the robe");
                return Variants[key] = source;
            }
            made = Object.Instantiate(source); made.name = source.name + "_magemystic" + mask;
            int index = 0;
            for (int s = 0; s < source.subMeshCount; s++)
            {
                var triangles = source.GetTriangles(s); var kept = new List<int>(triangles.Length);
                for (int i = 0; i < triangles.Length; i += 3, index++)
                {
                    if ((mask & RobeBit) != 0 && cut.Robe.Contains(index) || (mask & BootsBit) != 0 && cut.Boots.Contains(index)) continue;
                    kept.Add(triangles[i]); kept.Add(triangles[i + 1]); kept.Add(triangles[i + 2]);
                }
                made.SetTriangles(kept, s, false);
            }
            made.bounds = source.bounds;
            return Variants[key] = made;
        }

        // Per player visual: the pieces worn and the game's own body meshes (one per body model).
        sealed class State { public int Mask; public Mesh[] Originals; }
        static readonly ConditionalWeakTable<VisEquipment, State> States = new ConditionalWeakTable<VisEquipment, State>();

        // The game puts m_models[model].m_mesh back on the body whenever it differs (UpdateBaseModel, every frame): the
        // variant goes into this player's own m_models, and the game then applies it itself.
        [HarmonyPatch(typeof(VisEquipment), "UpdateEquipmentVisuals")]
        static class BodyUnderPieces
        {
            static void Postfix(VisEquipment __instance)
            {
                if (!__instance.m_isPlayer || __instance.m_isArmorStand || !__instance.m_bodyModel || __instance.m_models == null || __instance.m_models.Length == 0) return;
                int mask = (__instance.m_currentChestItemHash == RobeHash ? RobeBit : 0) | (__instance.m_currentLegItemHash == BootsHash ? BootsBit : 0);
                if (!States.TryGetValue(__instance, out var state))
                {
                    if (mask == 0) return;
                    States.Add(__instance, state = new State { Originals = __instance.m_models.Select(m => m.m_mesh).ToArray() });
                }
                if (state.Mask == mask) return;
                bool robeOn = (mask & RobeBit) != 0 && (state.Mask & RobeBit) == 0;
                state.Mask = mask;
                for (int i = 0; i < __instance.m_models.Length && i < state.Originals.Length; i++) __instance.m_models[i].m_mesh = Variant(state.Originals[i], mask);
                // The robe paints nothing: the bare chest instead of what the armour worn before left on the body.
                if (robeOn)
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
