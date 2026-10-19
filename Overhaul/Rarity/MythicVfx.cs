using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace Overhaul.Rarity
{
    // The mythic enchantments' visual effects: the prefabs of overhaul_mythicvfx (OverhaulLab MythicVfxBundle), each a
    // network object that removes itself, registered with the game so a copy spawned by its owner shows for every player.
    internal static class MythicVfx
    {
        static AssetBundle bundle;

        internal static void Initialize() => PrefabManager.OnVanillaPrefabsAvailable += Register;
        internal static void Shutdown() => PrefabManager.OnVanillaPrefabsAvailable -= Register;

        static void Register()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= Register;
            if (!bundle) bundle = Utility.EmbeddedAssets.LoadBundle("Overhaul.Assets.overhaul_mythicvfx");
            if (!bundle) { Utility.Log.LogError("Mythic effects: overhaul_mythicvfx missing"); return; }
            foreach (var prefab in bundle.LoadAllAssets<GameObject>())
                if (!PrefabManager.Instance.GetPrefab(prefab.name)) PrefabManager.Instance.AddPrefab(new CustomPrefab(prefab, false));
        }

        // A copy of the effect at position, facing rotation; it removes itself once played.
        internal static GameObject Spawn(string name, Vector3 position, Quaternion rotation)
        {
            var prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab(name) : null;
            if (!prefab) { Utility.Log.LogWarning("Mythic effect " + name + " not found"); return null; }
            return Object.Instantiate(prefab, position, rotation);
        }
    }
}
