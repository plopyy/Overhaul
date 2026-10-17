using System.Collections.Generic;
using Jotunn.Managers;
using SoftReferenceableAssets;
using UnityEngine;

namespace Overhaul.Utility
{
    // Game prefabs whose materials, meshes or effects Overhaul borrows. The game loads many prefabs on demand (location
    // props, pickables, station extensions, some pieces) and unloads their asset bundle once nobody holds them: what was
    // borrowed would lose its textures and stop drawing. Each prefab asked for here is held for the whole session.
    internal static class GamePrefabs
    {
        static readonly Dictionary<string, SoftReference<GameObject>> held = new Dictionary<string, SoftReference<GameObject>>();

        internal static GameObject Held(string name)
        {
            if (!held.TryGetValue(name, out var reference))
            {
                reference = AssetManager.Instance.GetSoftReference<GameObject>(name);
                if (reference.IsValid) { reference.LoadAsync(); reference.WaitForLoadToComplete(); held[name] = reference; }
                if (reference.IsValid && !reference.Asset) Log.LogWarning("Game prefab " + name + " could not be loaded");
            }
            return reference.IsValid && reference.Asset ? reference.Asset : PrefabManager.Instance.GetPrefab(name);
        }
    }
}
