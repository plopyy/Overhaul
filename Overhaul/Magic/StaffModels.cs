using System.Collections.Generic;
using System.Linq;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using Overhaul.Utility;
using UnityEngine;

namespace Overhaul.Magic
{
    // Overhaul staffs (Design/SystemeMagie.md): one per biome from the Black Forest on, each a new item cloned from a
    // game staff (the game staffs are left as they are), crafted from a wood, the biome metal and a biome ingredient.
    // A staff with a Model gets it from the overhaul_staffs bundle (<Model>_Model prefabs), its pieces on the game's
    // own materials (a copy with a new diffuse when the model brings one), and the base staff's effects swapped for a
    // game object's; the others keep the base staff's model until theirs is made. Damage is replaced by the staff's
    // magic power (StaffMagic); attacks stay the base staff's until the grimoire spells are cast.
    internal static class StaffModels
    {
        internal sealed class Staff
        {
            public string Prefab, Base, Model, Name, Station;
            // Magic power at quality 1 and per upgrade level.
            public float Power, PowerPerLevel;
            // Model piece name -> (game prefab holding the material, its material name, diffuse texture of the bundle or null).
            public Dictionary<string, (string prefab, string material, string diffuse)> Materials;
            // Effects: the base staff's are removed and those around a game model are copied around one of ours (piece, game prefab).
            public (string piece, string prefab) Effects;
            // Wood, biome metal, biome ingredient: (prefab, amount, amount per upgrade level).
            public (string item, int amount, int perLevel)[] Recipe;
        }
        internal static readonly Staff[] Staffs =
        {
            // Black Forest: the staff of embers' model.
            new Staff { Prefab = "Overhaul_BlackForestStaff", Base = "StaffFireball", Name = "$overhaul_staff_blackforest", Station = "forge", Power = 20, PowerPerLevel = 5,
                Recipe = new[] { ("Wood", 10, 5), ("Bronze", 5, 3), ("SurtlingCore", 3, 1) } },
            // Swamp: staff of embers' model until its own is made.
            new Staff { Prefab = "Overhaul_SwampStaff", Base = "StaffFireball", Name = "$overhaul_staff_swamp", Station = "forge", Power = 35, PowerPerLevel = 6,
                Recipe = new[] { ("ElderBark", 10, 5), ("Iron", 5, 3), ("Ectoplasm", 5, 2) } },
            // Mountains: the frost staff's model.
            new Staff { Prefab = "Overhaul_MountainStaff", Base = "StaffIceShards", Name = "$overhaul_staff_mountain", Station = "forge", Power = 50, PowerPerLevel = 7,
                Recipe = new[] { ("RoundLog", 10, 5), ("Silver", 5, 3), ("FreezeGland", 5, 2) } },
            // Plains: the staff of embers' wood with its own diffuse, black metal rings, a fuling totem.
            new Staff { Prefab = "Overhaul_PlainsStaff", Base = "StaffFireball", Model = "PlainsStaff_Model", Name = "$overhaul_staff_plains", Station = "forge", Power = 65, PowerPerLevel = 8,
                Materials = new Dictionary<string, (string, string, string)> {
                    { "Wood", ("StaffFireball", "staffs_mat", "PlainsStaff_d") }, { "BlackMetal", ("BlackMetal", "blackmetal", null) }, { "Totem", ("GoblinTotem", "Idol_mat", null) } },
                Effects = ("Totem", "GoblinTotem"),
                Recipe = new[] { ("FineWood", 10, 5), ("BlackMetal", 5, 3), ("GoblinTotem", 2, 1) } },
            // Mistlands and Ashlands: frost staff's and staff of embers' models until theirs are made.
            new Staff { Prefab = "Overhaul_MistlandsStaff", Base = "StaffIceShards", Name = "$overhaul_staff_mistlands", Station = "piece_magetable", Power = 80, PowerPerLevel = 9,
                Recipe = new[] { ("YggdrasilWood", 10, 5), ("BlackMetal", 8, 4), ("BlackCore", 2, 1) } },
            new Staff { Prefab = "Overhaul_AshlandsStaff", Base = "StaffFireball", Name = "$overhaul_staff_ashlands", Station = "piece_magetable", Power = 100, PowerPerLevel = 10,
                Recipe = new[] { ("Blackwood", 10, 5), ("FlametalNew", 5, 3), ("MoltenCore", 2, 1) } },
        };
        internal static readonly Dictionary<string, Staff> ByPrefab = Staffs.ToDictionary(s => s.Prefab);

        static AssetBundle bundle;
        internal static void Initialize() => PrefabManager.OnVanillaPrefabsAvailable += Register;
        internal static void Shutdown() => PrefabManager.OnVanillaPrefabsAvailable -= Register;

        static void Register()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= Register;
            if (!bundle) bundle = Utility.EmbeddedAssets.LoadBundle("Overhaul.Assets.overhaul_staffs");
            foreach (var s in Staffs)
            {
                if (PrefabManager.Instance.GetPrefab(s.Prefab)) continue;
                GameObject model = null;
                if (s.Model != null && !(model = bundle.LoadAsset<GameObject>(s.Model))) { Log.LogError("Staff model " + s.Model + " missing from overhaul_staffs"); continue; }
                var config = new ItemConfig { CraftingStation = s.Station, MinStationLevel = 1,
                    Requirements = s.Recipe.Select(r => new RequirementConfig(r.item, r.amount, r.perLevel, true)).ToArray() };
                var item = new CustomItem(s.Prefab, s.Base, config);
                var shared = item.ItemDrop.m_itemData.m_shared;
                shared.m_name = s.Name; shared.m_description = s.Name + "_description";
                // Magic power replaces the damage: the base staff's damage only gives each damage type's share of it.
                StaffMagic.Shares[s.Prefab] = shared.m_damages;
                shared.m_damages = new HitData.DamageTypes(); shared.m_damagesPerLevel = new HitData.DamageTypes();
                if (model) Dress(item.ItemPrefab, model, s.Materials.ToDictionary(m => m.Key, m => Material(PrefabManager.Instance.GetPrefab(m.Value.prefab), m.Value.material, m.Value.diffuse == null ? null : bundle.LoadAsset<Texture2D>(m.Value.diffuse))));
                if (s.Effects.prefab != null) SwapEffects(item.ItemPrefab, s.Effects.piece, PrefabManager.Instance.GetPrefab(s.Effects.prefab));
                ItemManager.Instance.AddItem(item);
            }
        }
        static Material FindMaterial(GameObject prefab, string name) =>
            prefab ? prefab.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).FirstOrDefault(m => m && m.name == name) : null;
        // The game material, or a copy of it with another diffuse (normal, emission, metal and gloss kept); the new
        // texture samples like the one it replaces. Public for the Unity preview.
        public static Material Material(GameObject prefab, string name, Texture2D diffuse)
        {
            var game = FindMaterial(prefab, name);
            if (!game || !diffuse) return game;
            var copy = new Material(game) { name = name + "_" + diffuse.name };
            if (game.mainTexture) { diffuse.filterMode = game.mainTexture.filterMode; diffuse.wrapMode = game.mainTexture.wrapMode; }
            copy.mainTexture = diffuse;
            return copy;
        }

        // Puts the model in place of the base staff's mesh: same parent and local transform (the model is authored in
        // the base mesh's space), each piece with its game material; the LOD keeps the new renderers. Public for the
        // Unity preview.
        public static void Dress(GameObject staff, GameObject model, Dictionary<string, Material> materials)
        {
            var old = staff.GetComponentsInChildren<MeshFilter>(true).First(f => f.GetComponent<MeshRenderer>() && f.name.StartsWith("default"));
            var host = old.transform;
            var added = new List<Renderer>();
            foreach (var piece in model.GetComponentsInChildren<MeshFilter>(true))
            {
                var go = new GameObject(piece.name); go.transform.SetParent(host, false);
                go.transform.localPosition = piece.transform.localPosition; go.transform.localRotation = piece.transform.localRotation; go.transform.localScale = piece.transform.localScale;
                go.layer = host.gameObject.layer;
                go.AddComponent<MeshFilter>().sharedMesh = piece.sharedMesh;
                var r = go.AddComponent<MeshRenderer>();
                materials.TryGetValue(piece.name, out var mat);
                r.sharedMaterials = Enumerable.Repeat(mat, piece.sharedMesh.subMeshCount).ToArray();
                added.Add(r);
            }
            // The base mesh is hidden, not removed: its collider and the effects under it stay.
            var oldRenderer = old.GetComponent<MeshRenderer>(); oldRenderer.enabled = false;
            var lod = staff.GetComponent<LODGroup>();
            if (lod)
            {
                var lods = lod.GetLODs();
                for (int i = 0; i < lods.Length; i++) if (lods[i].renderers.Contains(oldRenderer)) lods[i].renderers = lods[i].renderers.Where(x => x != oldRenderer).Concat(added).ToArray();
                lod.SetLODs(lods);
            }
        }

        // Removes the base staff's effects (the flames, embers, glow and light of its head and of its equipped state)
        // and copies the effects that sit around the game model's mesh (its siblings: particles, lights) around our
        // piece, moved and scaled from the game mesh's bounds to ours. Public for the Unity preview.
        public static void SwapEffects(GameObject staff, string piece, GameObject source)
        {
            foreach (var t in staff.GetComponentsInChildren<Transform>(true).Where(t => t.name == "effects" || t.parent && t.parent.name == "equiped").ToArray()) Object.DestroyImmediate(t.gameObject);
            var ours = staff.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.name == piece);
            var theirs = source ? source.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.GetComponent<MeshRenderer>()) : null;
            if (!ours || !theirs) return;
            // Game model bounds in its parent's space, ours in the staff host's space.
            var tb = theirs.sharedMesh.bounds; var tm = Matrix4x4.TRS(theirs.transform.localPosition, theirs.transform.localRotation, theirs.transform.localScale);
            Vector3 tc = tm.MultiplyPoint3x4(tb.center); float ts = tb.size.y * theirs.transform.localScale.y;
            var ob = ours.sharedMesh.bounds; var host = ours.transform.parent;
            Vector3 oc = ours.transform.localPosition + ours.transform.localRotation * Vector3.Scale(ours.transform.localScale, ob.center);
            float scale = ob.size.y * ours.transform.localScale.y / ts;
            foreach (Transform fx in theirs.transform.parent)
            {
                if (fx == theirs.transform || !(fx.GetComponentInChildren<ParticleSystem>(true) || fx.GetComponentInChildren<Light>(true))) continue;
                var copy = Object.Instantiate(fx.gameObject, host, false); copy.name = fx.name;
                copy.transform.localPosition = oc + (fx.localPosition - tc) * scale; copy.transform.localRotation = fx.localRotation; copy.transform.localScale = fx.localScale * scale;
                foreach (var ps in copy.GetComponentsInChildren<ParticleSystem>(true)) { var main = ps.main; main.scalingMode = ParticleSystemScalingMode.Hierarchy; }
                foreach (var l in copy.GetComponentsInChildren<Light>(true)) l.range *= scale;
            }
        }
    }
}
