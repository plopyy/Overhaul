using System.Collections.Generic;
using System.Linq;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace Overhaul.Rarity
{
    // The mythic enchantments' visual effects: the prefabs of overhaul_mythicvfx (OverhaulLab MythicVfxBundle). Those that
    // are network objects (removing themselves) are registered with the game, so a copy spawned by its owner shows for
    // every player; the others (Mythic_Souls) are shown by each machine on its own, attached to what they follow.
    internal static class MythicVfx
    {
        static AssetBundle bundle;
        static readonly Dictionary<string, GameObject> local = new Dictionary<string, GameObject>();

        internal static void Initialize() => PrefabManager.OnVanillaPrefabsAvailable += Register;
        internal static void Shutdown() => PrefabManager.OnVanillaPrefabsAvailable -= Register;

        static void Register()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= Register;
            if (!bundle) bundle = Utility.EmbeddedAssets.LoadBundle("Overhaul.Assets.overhaul_mythicvfx");
            if (!bundle) { Utility.Log.LogError("Mythic effects: overhaul_mythicvfx missing"); return; }
            foreach (var prefab in bundle.LoadAllAssets<GameObject>())
            {
                if (!prefab.GetComponent<ZNetView>()) { local[prefab.name] = prefab; continue; }
                // The spectral weapon builds its look from the item named in its ZDO, on every machine.
                if (prefab.name == "Mythic_SpectralWeapon" && !prefab.GetComponent<SpectralCopy>()) prefab.AddComponent<SpectralCopy>();
                if (!PrefabManager.Instance.GetPrefab(prefab.name)) PrefabManager.Instance.AddPrefab(new CustomPrefab(prefab, false));
            }
        }

        // A copy of the effect at position, facing rotation; it removes itself once played.
        internal static GameObject Spawn(string name, Vector3 position, Quaternion rotation)
        {
            var prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab(name) : null;
            if (!prefab) { Utility.Log.LogWarning("Mythic effect " + name + " not found"); return null; }
            return Object.Instantiate(prefab, position, rotation);
        }

        // A spectral copy of an item (its prefab name), owned by this machine: Mythic_SpectralWeapon with the item in its ZDO.
        internal static GameObject SpawnSpectral(string item, Vector3 position, Quaternion rotation)
        {
            var copy = Spawn("Mythic_SpectralWeapon", position, rotation);
            var view = copy ? copy.GetComponent<ZNetView>() : null;
            if (view && view.IsValid()) view.GetZDO().Set(SpectralCopy.ItemKey, item);
            return copy;
        }

        // A copy of a local (not networked) effect following parent, until destroyed.
        internal static GameObject Attach(string name, Transform parent)
        {
            if (!local.TryGetValue(name, out var prefab) || !prefab) { Utility.Log.LogWarning("Mythic effect " + name + " not found"); return null; }
            return Object.Instantiate(prefab, parent, false);
        }
    }

    // Look of Mythic_SpectralWeapon: the meshes of the held model ("attach") of the item named in its ZDO, placed as in the
    // model, on the spectral material of the prefab's Model slot.
    internal sealed class SpectralCopy : MonoBehaviour
    {
        internal static readonly int ItemKey = "overhaul_spectral_item".GetStableHashCode();
        bool built;

        void Update()
        {
            if (built) return;
            var view = GetComponent<ZNetView>();
            string name = view && view.IsValid() ? view.GetZDO().GetString(ItemKey, "") : "";
            var item = name.Length > 0 && ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(name) : null;
            if (!item) return;
            built = true;
            var slot = transform.Find("Model");
            var material = slot ? slot.GetComponent<MeshRenderer>()?.sharedMaterial : null;
            if (!material) return;
            var model = item.transform.Find("attach") ?? item.transform;
            foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!mf.sharedMesh || !mf.GetComponent<MeshRenderer>()) continue;
                var part = new GameObject(mf.name); part.transform.SetParent(slot, false);
                var m = model.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                part.transform.localPosition = m.GetColumn(3); part.transform.localRotation = m.rotation; part.transform.localScale = m.lossyScale;
                part.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                var renderer = part.AddComponent<MeshRenderer>(); renderer.sharedMaterials = Enumerable.Repeat(material, mf.sharedMesh.subMeshCount).ToArray();
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                part.layer = gameObject.layer;
            }
        }
    }
}
