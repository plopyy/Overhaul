using System;
using System.Collections.Generic;
using HarmonyLib;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace Overhaul
{
    internal static class TarDrain
    {
        internal const string Name = "Overhaul_HoeDrainTar", Rpc = "Overhaul_DrainTar";
        internal const float Radius = 2f, DepthPerUse = 2f;
        internal static readonly HashSet<LiquidVolume> Loaded = new HashSet<LiquidVolume>();
        private static Sprite drainIcon;
        internal static Sprite GetIcon()
        {
            if (drainIcon) return drainIcon;
            using (var stream = typeof(TarDrain).Assembly.GetManifestResourceStream("Overhaul.Assets.hoe_drain_tar.png"))
            using (var memory = new System.IO.MemoryStream())
            {
                stream.CopyTo(memory);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                texture.name = Name + "_icon";
                ImageConversion.LoadImage(texture, memory.ToArray());
                texture.wrapMode = TextureWrapMode.Clamp;
                drainIcon = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f,.5f), 100);
                drainIcon.name = Name + "_icon";
                return drainIcon;
            }
        }
        internal static void Initialize() => PrefabManager.OnVanillaPrefabsAvailable += Register;
        internal static void Shutdown() { PrefabManager.OnVanillaPrefabsAvailable -= Register; Loaded.Clear(); }
        private static void Register()
        {
            if (PrefabManager.Instance.GetPrefab(Name)) return;
            var custom = new CustomPiece(Name, "mud_road_v2", "_HoePieceTable");
            Configure(custom.PiecePrefab, PrefabManager.Instance.GetPrefab("Stone").GetComponent<ItemDrop>(),
                GetIcon());
            PieceManager.Instance.AddPiece(custom);
        }
        internal static void Configure(GameObject prefab, ItemDrop stone, Sprite icon)
        {
            // No TerrainOp/Modifier survives: neither placement nor the preview can edit terrain or spawn loot.
            foreach (var op in prefab.GetComponentsInChildren<TerrainOp>(true)) UnityEngine.Object.DestroyImmediate(op);
            foreach (var modifier in prefab.GetComponentsInChildren<TerrainModifier>(true)) UnityEngine.Object.DestroyImmediate(modifier);
            var piece = prefab.GetComponent<Piece>();
            piece.m_name = "$overhaul_hoe_drain_tar";
            piece.m_description = "$overhaul_hoe_drain_tar_description";
            piece.m_icon = icon;
            piece.m_craftingStation = null;
            piece.m_allowAltGroundPlacement = false;
            piece.m_resources = new[] { new Piece.Requirement { m_resItem = stone, m_amount = 5, m_recover = false } };
            if (!prefab.GetComponent<TarDrainPlacement>()) prefab.AddComponent<TarDrainPlacement>();
        }
        internal static bool IsAction(Piece piece) => piece && piece.GetComponent<TarDrainPlacement>();
        internal static bool CanUse(Player player)
        {
            var tool = player ? player.GetRightItem() : null;
            return tool != null && tool.m_quality >= 3 && tool.m_shared?.m_buildPieces &&
                tool.m_shared.m_buildPieces.m_pieces.Exists(prefab => prefab && IsAction(prefab.GetComponent<Piece>()));
        }
        [HarmonyPatch(typeof(PieceTable), nameof(PieceTable.UpdateAvailable))]
        private static class FilterActions
        {
            private static void Postfix(PieceTable __instance, Player player)
            {
                if (CanUse(player)) return;
                __instance.m_availablePieces.RemoveWhere(IsAction);
                __instance.m_enabledPieces.RemoveWhere(IsAction);
                foreach (var category in __instance.m_availablePiecesByCategory) category.RemoveAll(IsAction);
            }
        }
        internal static bool Finite(Vector3 point) => !float.IsNaN(point.x) && !float.IsInfinity(point.x)
            && !float.IsNaN(point.y) && !float.IsInfinity(point.y) && !float.IsNaN(point.z) && !float.IsInfinity(point.z);
        internal static bool ValidVolume(LiquidVolume liquid, Vector3 point)
        {
            return liquid && liquid.m_liquidType == LiquidType.Tar && Finite(point) && liquid.m_scale > 0
                && liquid.m_depths != null && liquid.m_depths.Count == (liquid.m_width + 1) * (liquid.m_width + 1)
                && Mathf.Abs(point.y - liquid.transform.position.y) <= liquid.m_maxDepth + Radius;
        }
        // Only depth cells are edited, under the same lock used by native simulation and serialization.
        internal static float ChangeDepths(LiquidVolume liquid, Vector3 point, bool apply)
        {
            if (!ValidVolume(liquid, point)) return 0;
            var center = liquid.WorldToLocal(point);
            float radius = Radius / liquid.m_scale;
            int x0 = Mathf.Max(0, Mathf.CeilToInt(center.x - radius));
            int x1 = Mathf.Min(liquid.m_width, Mathf.FloorToInt(center.x + radius));
            int z0 = Mathf.Max(0, Mathf.CeilToInt(center.y - radius));
            int z1 = Mathf.Min(liquid.m_width, Mathf.FloorToInt(center.y + radius));
            float removed = 0;
            lock (liquid.m_meshDataLock)
            {
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        if ((new Vector2(x, z) - center).sqrMagnitude > radius * radius) continue;
                        int index = z * (liquid.m_width + 1) + x;
                        float amount = Mathf.Min(DepthPerUse, Mathf.Max(0, liquid.m_depths[index]));
                        removed += amount;
                        if (apply) liquid.m_depths[index] -= amount;
                    }
                if (apply && removed > 0) { liquid.m_dirty = true; liquid.m_needsSaving = true; }
            }
            return removed;
        }
        internal static bool HasTar(Vector3 point)
        {
            Loaded.RemoveWhere(v => !v);
            foreach (var liquid in Loaded)
                if (liquid.m_nview && liquid.m_nview.IsValid() && ChangeDepths(liquid, point, false) > .001f) return true;
            return false;
        }
        internal static bool WardAccess(Vector3 point, long playerId)
        {
            foreach (var area in PrivateArea.m_allAreas)
                if (area && area.IsEnabled() && area.IsInside(point, Radius) && area.m_piece.GetCreator() != playerId && !area.IsPermitted(playerId)) return false;
            return true;
        }
        internal static void Receive(LiquidVolume liquid, long sender, ZDOID character, Vector3 point)
        {
            if (!ValidVolume(liquid, point) || !liquid.m_nview || !liquid.m_nview.IsValid() || !liquid.m_nview.IsOwner()) return;
            var actor = Storage.ChestAccess.Actor(sender, character);
            if (actor == null || Vector3.Distance(actor.GetPosition(), point) > 12f || !WardAccess(point, actor.GetLong(ZDOVars.s_playerID, 0))) return;
            if (ChangeDepths(liquid, point, true) > 0)
            {
                // Publish immediately; clients and future sessions receive the native saved liquid state.
                liquid.Save();

            }
        }
        internal static void Request(Vector3 point)
        {
            var player = Player.m_localPlayer;
            if (!CanUse(player) || player.GetZDOID().Equals(ZDOID.None) || !Finite(point)) return;
            foreach (var liquid in Loaded)
                if (liquid && liquid.m_nview && liquid.m_nview.IsValid() && ChangeDepths(liquid, point, false) > .001f)
                    liquid.m_nview.InvokeRPC(Rpc, player.GetZDOID(), point);
        }
        [HarmonyPatch(typeof(LiquidVolume), "Awake")]
        private static class RegisterLiquid
        {
            private static void Postfix(LiquidVolume __instance)
            {
                if (__instance.m_liquidType != LiquidType.Tar || !__instance.m_nview || !__instance.m_nview.IsValid()) return;
                Loaded.Add(__instance);
                var liquid = __instance;
                liquid.m_nview.Register<ZDOID, Vector3>(Rpc, (sender, actor, point) => Receive(liquid, sender, actor, point));
            }
        }
        [HarmonyPatch(typeof(LiquidVolume), "OnDestroy")]
        private static class RemoveLiquid
        {
            private static void Prefix(LiquidVolume __instance) => Loaded.Remove(__instance);
        }
        [HarmonyPatch(typeof(LiquidVolume), "Load")]
        private static class RefreshLiquid
        {
            private static void Postfix(LiquidVolume __instance)
            {
                if (__instance.m_liquidType == LiquidType.Tar)
                    lock (__instance.m_meshDataLock) __instance.m_dirty = true;
            }
        }
        [HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
        private static class ValidatePlacement
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(Player __instance, Piece piece, ref bool __result)
            {
                if (!IsAction(piece)) return true;
                if (!CanUse(__instance))
                {
                    __instance.Message(MessageHud.MessageType.Center, "$overhaul_hoe_tar_quality");
                    __result = false;
                    return false;
                }
                if (__instance.m_placementGhost && HasTar(__instance.m_placementGhost.transform.position))
                {
                    if (WardAccess(__instance.m_placementGhost.transform.position, __instance.GetPlayerID())) return true;
                    __instance.Message(MessageHud.MessageType.Center, "$msg_privatezone");
                    __result = false;
                    return false;
                }
                __instance.Message(MessageHud.MessageType.Center, "$overhaul_hoe_no_tar");
                __result = false;
                return false;
            }
        }
    }
    internal sealed class TarDrainPlacement : MonoBehaviour, IPlaced
    {
        public void OnPlaced()
        {
            if (Player.IsPlacementGhost(gameObject)) return;
            TarDrain.Request(transform.position);
            Destroy(gameObject);
        }
    }
}



