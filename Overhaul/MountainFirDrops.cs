using System;
using HarmonyLib;
using UnityEngine;

namespace Overhaul
{
    internal static class MountainFirDrops
    {
        internal static readonly int OriginKey = "Overhaul.MountainFir".GetStableHashCode();
        [ThreadStatic] internal static bool? SpawningMountainFir;
        static string PrefabName(GameObject obj) => obj.name.Replace("(Clone)", "").Trim();
        internal static bool IsFirLog(GameObject obj) => PrefabName(obj) == "FirTree_log" || PrefabName(obj) == "FirTree_log_half";
        internal static bool ResolveOrigin(int saved, bool? spawning, Heightmap.Biome biome) =>
            saved >= 0 ? saved == 1 : spawning ?? biome == Heightmap.Biome.Mountain;

        internal static void Configure(TreeLog log)
        {
            if (!IsFirLog(log.gameObject)) return;
            var view = log.GetComponent<ZNetView>();
            if (!view || !view.IsValid()) return;
            var data = view.GetZDO();
            int saved = data.GetInt(OriginKey, -1);
            bool mountain = ResolveOrigin(saved, SpawningMountainFir, saved < 0 && !SpawningMountainFir.HasValue ? Heightmap.FindBiome(log.transform.position) : Heightmap.Biome.None);
            if (saved < 0 && view.IsOwner()) data.Set(OriginKey, mountain ? 1 : 0);
            if (PrefabName(log.gameObject) != "FirTree_log_half" || !ZNetScene.instance) return;
            // Re-evaluate on destruction as the owner's origin marker can arrive after Awake.
            var prefab = ZNetScene.instance.GetPrefab(mountain ? "PineTree_log_half" : "FirTree_log_half");
            if (prefab && prefab.TryGetComponent<TreeLog>(out var donor))
                log.m_dropWhenDestroyed = donor.m_dropWhenDestroyed;
        }

        [HarmonyPatch(typeof(TreeLog), "Awake")]
        internal static class LogAwake
        {
            static void Postfix(TreeLog __instance) => Configure(__instance);
        }

        [HarmonyPatch(typeof(TreeBase), "SpawnLog")]
        internal static class TreeOrigin
        {
            static void Prefix(TreeBase __instance, out bool? __state)
            {
                __state = SpawningMountainFir;
                SpawningMountainFir = PrefabName(__instance.gameObject) == "FirTree" &&
                    Heightmap.FindBiome(__instance.transform.position) == Heightmap.Biome.Mountain;
            }
            static Exception Finalizer(bool? __state, Exception __exception)
            {
                SpawningMountainFir = __state;
                return __exception;
            }
        }

        [HarmonyPatch(typeof(TreeLog), "Destroy")]
        internal static class SplitOrigin
        {
            static void Prefix(TreeLog __instance, out bool? __state)
            {
                __state = SpawningMountainFir;
                if (!IsFirLog(__instance.gameObject)) { SpawningMountainFir = false; return; }
                // Retry at destruction if the prefab registry was unavailable during Awake.
                Configure(__instance);
                var view = __instance.GetComponent<ZNetView>();
                int saved = view && view.IsValid() ? view.GetZDO().GetInt(OriginKey, -1) : -1;
                SpawningMountainFir = ResolveOrigin(saved, __state, saved < 0 && !__state.HasValue ? Heightmap.FindBiome(__instance.transform.position) : Heightmap.Biome.None);
            }
            static Exception Finalizer(bool? __state, Exception __exception)
            {
                SpawningMountainFir = __state;
                return __exception;
            }
        }
    }
}
