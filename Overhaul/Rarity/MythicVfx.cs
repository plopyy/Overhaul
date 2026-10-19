using System.Collections.Generic;
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

        // A copy of a local (not networked) effect following parent, until destroyed.
        internal static GameObject Attach(string name, Transform parent)
        {
            if (!local.TryGetValue(name, out var prefab) || !prefab) { Utility.Log.LogWarning("Mythic effect " + name + " not found"); return null; }
            return Object.Instantiate(prefab, parent, false);
        }
    }
}
