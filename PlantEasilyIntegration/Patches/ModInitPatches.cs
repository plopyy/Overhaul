namespace Advize_PlantEasily;

using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using static ModContext;

[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
static class ModInitPatches
{
    private static List<GameObject> unfilteredPrefabs;

    [HarmonyPriority(Priority.First)]
    static void Prefix(ZNetScene __instance)
    {
        unfilteredPrefabs = PrefabRefs.Count == 0 ? new(__instance.m_prefabs) : null;

        if (unfilteredPrefabs != null)
        {
            List<GameObject> filteredPrefabs = __instance.m_prefabs.Where(IsValidCrop).ToList();

            Dbgl($"({filteredPrefabs.Count}) vanilla crops detected");
            foreach (GameObject go in filteredPrefabs)
            {
                ReplantDB replantDB = new(go);
                ReplantDB.Registry.Add(replantDB.Pickable.name, replantDB);
            }
        }
    }

    [HarmonyPriority(Priority.Last)]
    static void Postfix(ZNetScene __instance)
    {
        if (unfilteredPrefabs == null) return;

        List<GameObject> plantablePickables = unfilteredPrefabs.Where(go => go.TryGetComponent<Pickable>(out _) && go.TryGetComponent<Piece>(out _)).ToList();

        plantablePickables.ForEach(go => PickableRefs.Add(new(go.name)));

        Dbgl($"({plantablePickables.Count}) plantable pickables detected: " + string.Join(", ", plantablePickables.Select(go => go.name)));

        HashSet<GameObject> unfilteredSet = new(unfilteredPrefabs);
        List<GameObject> moddedPrefabs = __instance.m_prefabs.Where(go => !unfilteredSet.Contains(go)).Where(IsValidCrop).ToList();

        Dbgl($"({moddedPrefabs.Count}) modded crops detected");

        foreach (GameObject go in moddedPrefabs)
            _ = new ReplantDB(go);

        InitPrefabRefs();
        unfilteredPrefabs = null;
    }

    static bool IsValidCrop(GameObject go)
    {
        if (!go.TryGetComponent(out Plant plant))
            return false;

        // Grown prefabs must be of type Pickable and not Vine/TreeBase
        return !plant.m_grownPrefabs.Any(gp => !gp.TryGetComponent<Pickable>(out _) || gp.TryGetComponent<Vine>(out _) || gp.TryGetComponent<TreeBase>(out _));
    }

    internal static void InitPrefabRefs()
    {
        Dbgl("InitPrefabRefs");

        foreach (KeyValuePair<string, ReplantDB> kvp in ReplantDB.Registry)
        {
            PrefabRefs[kvp.Key] = null;
            PrefabRefs[kvp.Value.PlantName] = null;
        }

        foreach (PickableDB entry in PickableRefs)
            PrefabRefs[entry.key] = null;

        ResolvePrefabRefs();
        InitLineRenderers();
        PickableDB.InitPickableSpacingConfig();
    }
    internal static void ResolvePrefabRefs()
    {
        foreach (string name in PrefabRefs.Keys.ToArray())
            PrefabRefs[name] = Jotunn.Managers.PrefabManager.Instance.GetPrefab(name);
    }

    // Make a dedicated Grid Direction Renderer class or something, this stuff is kind of scattered atm
    private static void InitLineRenderers()
    {
        Material material = Jotunn.Managers.PrefabManager.Cache.GetPrefab<Material>("Default-Line")
            ?? throw new System.InvalidOperationException("PlantEasily grid material Default-Line unavailable");
        GhostGrid.DirectionRenderer = new();
        Object.DontDestroyOnLoad(GhostGrid.DirectionRenderer);

        for (int i = 0; i < 3; i++)
        {
            GameObject child = new();
            child.transform.SetParent(GhostGrid.DirectionRenderer.transform);
            GhostGrid.LineRenderers.Add(child.AddComponent<LineRenderer>());
            GhostGrid.LineRenderers[i].material = material;
            GhostGrid.LineRenderers[i].widthMultiplier = 0.025f;
        }

        ConfigEventHandlers.GridColorChanged(null, null);
    }
}
