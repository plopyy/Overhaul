using System.Collections.Generic;
using System.Linq;
using Jotunn.Entities;
using Jotunn.Managers;
using Overhaul.Utility;
using UnityEngine;

namespace Overhaul.Magic
{
    // Overhaul staffs made from a game staff with a new model (overhaul_staffs bundle, <Model>_Model prefabs). The
    // model's pieces take the game's own materials, named after the piece; the effects, attack and stats of the base
    // staff stay until the magic system gives each staff its own. No recipe yet: spawn with the console.
    internal static class StaffModels
    {
        internal sealed class Staff
        {
            public string Prefab, Base, Model, Name;
            // Model piece name -> (game prefab holding the material, its material name).
            public Dictionary<string, (string prefab, string material)> Materials;
        }
        internal static readonly Staff[] Staffs =
        {
            new Staff { Prefab = "Overhaul_MistlandStaff", Base = "StaffFireball", Model = "MistlandStaff_Model", Name = "$overhaul_staff_mistland",
                Materials = new Dictionary<string, (string, string)> { { "Staff", ("StaffFireball", "staffs_mat") }, { "Totem_de_fuling", ("GoblinTotem", "Idol_mat") } } },
        };

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
                var model = bundle.LoadAsset<GameObject>(s.Model);
                if (!model) { Log.LogError("Staff model " + s.Model + " missing from overhaul_staffs"); continue; }
                var item = new CustomItem(s.Prefab, s.Base);
                var shared = item.ItemDrop.m_itemData.m_shared;
                shared.m_name = s.Name; shared.m_description = s.Name + "_description";
                Dress(item.ItemPrefab, model, s.Materials.ToDictionary(m => m.Key, m => FindMaterial(PrefabManager.Instance.GetPrefab(m.Value.prefab), m.Value.material)));
                ItemManager.Instance.AddItem(item);
            }
        }
        static Material FindMaterial(GameObject prefab, string name) =>
            prefab ? prefab.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).FirstOrDefault(m => m && m.name == name) : null;

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
    }
}
