using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Storage
{
    internal static class BurningFoodDrops
    {
        private static ZNetScene cachedScene;
        private static readonly Dictionary<GameObject, GameObject> cooked = new Dictionary<GameObject, GameObject>();

        internal static void Rebuild(ZNetScene scene)
        {
            cooked.Clear();cachedScene = scene;
            if (!scene) return;
            foreach (var prefab in scene.m_namedPrefabs.Values)
            {
                if (!prefab) continue;
                foreach (var station in prefab.GetComponentsInChildren<CookingStation>(true))
                    foreach (var recipe in station.m_conversion)
                    {
                        if (!recipe.m_from || !recipe.m_to || recipe.m_to.m_itemData.m_shared.m_food <= 0) continue;
                        // Use real cooking recipes, never item names or translated labels.
                        // Non-food transformations (e.g. burning to coal) are excluded.
                        if (!cooked.ContainsKey(recipe.m_from.gameObject))
                            cooked.Add(recipe.m_from.gameObject, recipe.m_to.gameObject);
                    }
            }
        }

        internal static void Convert(Character character, List<KeyValuePair<GameObject, int>> drops)
        {
            if (!character || character.IsPlayer() || drops == null ||
                character.GetSEMan() == null || !character.GetSEMan().HaveStatusEffect(SEMan.s_statusEffectBurning)) return;
            var scene = ZNetScene.instance;
            if (!scene) return;
            if (cachedScene != scene) Rebuild(scene);
            for (int i = 0; i < drops.Count; i++)
                if (drops[i].Key && cooked.TryGetValue(drops[i].Key, out var result))
                    drops[i] = new KeyValuePair<GameObject, int>(result, drops[i].Value);
        }
    }

    // This runs while the dying creature still has its status effects, both for
    // direct drops and before Ragdoll.SaveLootList persists the delayed loot.
    [HarmonyPatch(typeof(CharacterDrop), nameof(CharacterDrop.GenerateDropList))]
    internal static class BurningFoodDropsPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(CharacterDrop __instance, List<KeyValuePair<GameObject, int>> __result) =>
            BurningFoodDrops.Convert(__instance.m_character, __result);
    }
}
