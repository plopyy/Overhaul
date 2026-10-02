using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Overhaul
{
    internal static class CropFarming
    {
        internal static readonly int SourceKey = "overhaul_crop_source_v1".GetStableHashCode();
        internal static readonly int FinishedKey = "overhaul_crop_finished_v1".GetStableHashCode();
        internal sealed class Crop
        {
            internal GameObject Seedling;
            internal ItemDrop Ingredient;
            internal bool Replant;
            internal bool RequiresPlantingMarker;
        }
        private static readonly Dictionary<string, Crop> Crops = new Dictionary<string, Crop>(StringComparer.Ordinal);
        private static PieceTable table;
        private static int pieceCount;
        [ThreadStatic] private static Pickable harvesting;

        internal static void Register(PieceTable cultivator)
        {
            Crops.Clear();
            table = cultivator;
            pieceCount = cultivator.m_pieces.Count;
            foreach (var prefab in cultivator.m_pieces)
            {
                var plant = prefab ? prefab.GetComponent<Plant>() : null;
                var piece = prefab ? prefab.GetComponent<Piece>() : null;
                if (!plant || !piece || piece.m_resources.Length != 1 || !piece.m_resources[0].m_resItem) continue;
                // Trees and perennial climbing vines retain their native lifecycle.
                if (plant.m_grownPrefabs.Length == 0) continue;
                bool fieldCrop = true;
                foreach (var grown in plant.m_grownPrefabs)
                    if (!grown || !grown.GetComponent<Pickable>() || grown.GetComponent<Vine>()) fieldCrop = false;
                if (!fieldCrop) continue;
                var crop = new Crop { Seedling = prefab, Ingredient = piece.m_resources[0].m_resItem,
                    Replant = !prefab.name.StartsWith("sapling_seed", StringComparison.OrdinalIgnoreCase) };
                foreach (var grown in plant.m_grownPrefabs)
                {
                    var pick = grown.GetComponent<Pickable>();
                    if (pick.m_itemPrefab && pick.m_itemPrefab.name.EndsWith("Seeds", StringComparison.OrdinalIgnoreCase)
                        && pick.m_itemPrefab.name != crop.Ingredient.name) crop.Replant = false;
                    crop.RequiresPlantingMarker |= pick.m_respawnTimeMinutes > 0 || pick.m_hideWhenPicked
                        || (pick.m_itemPrefab && pick.m_itemPrefab.name.StartsWith("Mushroom", StringComparison.Ordinal));
                    Crops[grown.name] = crop;
                }
                Crops[prefab.name] = crop;
            }
        }

        internal static Crop Find(GameObject instance)
        {
            var cultivator = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab("Cultivator") : null;
            var pieces = cultivator ? cultivator.GetComponent<ItemDrop>()?.m_itemData.m_shared.m_buildPieces : null;
            if (pieces && (table != pieces || pieceCount != pieces.m_pieces.Count)) Register(pieces);
            if (!instance) return null;
            var zdo = instance.GetComponent<ZNetView>()?.GetZDO();
            string source = zdo?.GetString(SourceKey, "") ?? "";
            if (source.Length > 0 && Crops.TryGetValue(source, out var marked)) return marked;
            string name = instance.name.Replace("(Clone)", "");
            if (!Crops.TryGetValue(name, out var crop)) return null;
            // Wild mushrooms use the same mature prefab as cultivated mushrooms.
            if (crop.RequiresPlantingMarker && !instance.GetComponent<Plant>()) return null;
            return crop;
        }

        private static bool ClaimAction(ZNetView view)
        {
            if (!view || !view.IsValid() || !view.IsOwner()) return false;
            var zdo = view.GetZDO();
            if (zdo.GetBool(FinishedKey, false)) return false;
            zdo.Set(FinishedKey, true);
            return true;
        }

        internal static void Refund(GameObject plant, Crop crop)
        {
            var view = plant.GetComponent<ZNetView>();
            if (!ClaimAction(view)) return;
            // Direct single item creation: no harvest bonus, drop table, world multiplier or conversion.
            var drop = UnityEngine.Object.Instantiate(crop.Ingredient.gameObject, plant.transform.position + Vector3.up * .25f, Quaternion.identity);
            drop.GetComponent<ItemDrop>().m_itemData.m_stack = 1;
            ItemDrop.OnCreateNew(drop, view.GetZDO().GetBool(ZDOVars.s_cheated, false));
        }

        [HarmonyPatch(typeof(Plant), nameof(Plant.Grow))]
        private static class Growth
        {
            private static void Prefix(Plant __instance, out GrowthState __state)
            {
                __state = new GrowthState { Crop = Find(__instance.gameObject), Creator = __instance.GetComponent<Piece>()?.GetCreator() ?? 0, CreatorIndex = __instance.GetComponent<Piece>()?.GetCreatorPlatformUserIdIndex() ?? -1 };
            }
            private static void Postfix(GameObject __result, GrowthState __state)
            {
                var view = __result ? __result.GetComponent<ZNetView>() : null;
                if (__state.Crop == null || !view || !view.IsValid() || !view.IsOwner()) return;
                view.GetZDO().Set(SourceKey, __state.Crop.Seedling.name);
                view.GetZDO().Set(ZDOVars.s_creator, __state.Creator);
                view.GetZDO().Set(ZDOVars.s_creatorIndex, __state.CreatorIndex);
            }
        }
        private struct GrowthState { internal Crop Crop; internal long Creator; internal int CreatorIndex; }

        [HarmonyPatch(typeof(Pickable), "RPC_Pick")]
        private static class HarvestScope
        {
            private static void Prefix(Pickable __instance, out Pickable __state) { __state = harvesting; harvesting = __instance; }
            private static Exception Finalizer(Pickable __state, Exception __exception) { harvesting = __state; return __exception; }
        }
        [HarmonyPatch(typeof(Pickable), nameof(Pickable.SetPicked))]
        private static class Harvest
        {
            private static void Prefix(Pickable __instance, bool picked)
            {
                if (!picked || __instance.m_picked || harvesting != __instance) return;
                var crop = Find(__instance.gameObject);
                if (crop == null || !crop.Replant || !ClaimAction(__instance.m_nview)) return;
                var old = __instance.m_nview.GetZDO();
                var seedling = UnityEngine.Object.Instantiate(crop.Seedling, __instance.transform.position, __instance.transform.rotation);
                var view = seedling.GetComponent<ZNetView>();
                if (view && view.IsValid())
                {
                    view.GetZDO().Set(SourceKey, crop.Seedling.name);
                    view.GetZDO().Set(ZDOVars.s_cheated, old.GetBool(ZDOVars.s_cheated, false));
                    var piece = seedling.GetComponent<Piece>();
                    piece.m_creator = old.GetLong(ZDOVars.s_creator, 0);
                    piece.m_creatorPlatformUserIDIndex = old.GetInt(ZDOVars.s_creatorIndex, -1);
                    view.GetZDO().Set(ZDOVars.s_creator, piece.m_creator);
                    view.GetZDO().Set(ZDOVars.s_creatorIndex, piece.m_creatorPlatformUserIDIndex);
                }
                // Even cultivated mushrooms must disappear, rather than also respawning their old mature body.
                __instance.m_respawnTimeMinutes = 0;
                __instance.m_hideWhenPicked = null;
            }
        }
        [HarmonyPatch(typeof(Destructible), nameof(Destructible.Destroy))]
        private static class BreakPlant
        {
            private static void Prefix(Destructible __instance)
            {
                if (__instance.m_destroyed) return;
                var crop = Find(__instance.gameObject);
                if (crop != null) Refund(__instance.gameObject, crop);
            }
        }
        [HarmonyPatch(typeof(Piece), nameof(Piece.DropResources))]
        private static class RemovePlant
        {
            private static bool Prefix(Piece __instance)
            {
                var crop = Find(__instance.gameObject);
                if (crop == null) return true;
                Refund(__instance.gameObject, crop);
                return false;
            }
        }
        [HarmonyPatch(typeof(DropOnDestroyed), "OnDestroyed")]
        private static class NoExtraBreakLoot
        {
            private static bool Prefix(DropOnDestroyed __instance) => Find(__instance.gameObject) == null;
        }
    }
}


