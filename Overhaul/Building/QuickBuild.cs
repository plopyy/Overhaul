using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Building
{
    // Quick build: holding the place button on a wall, a pillar/beam or a roof stretches it into a row of the same piece,
    // one piece size at a time (measured from its snap points), towards where the camera aims:
    //   - walls in their own plane: sideways, up or down, or both (a rectangle);
    //   - pillars and beams end to end, along their length;
    //   - roofs side by side only.
    // The piece is turned before (no turning while stretching). Every copy shows as a ghost; the resources of the whole
    // row are counted while it grows, and the ghosts turn red when they are short or a spot is not allowed (warded, no
    // build zone). Releasing the button places and pays every piece, or nothing at all when red. Right click cancels.
    // Other pieces are placed by the game as usual.
    internal static class QuickBuild
    {
        private enum Kind { None, Wall, Pillar, Roof }
        private const int MaxPerAxis = 30;

        private static Kind kind;
        private static bool holding, valid;
        private static Vector3 anchor, axisA, axisB;
        private static Quaternion turn;
        private static float sizeA, sizeB;
        private static int fromA, toA, fromB, toB;
        private static readonly List<GameObject> copies = new List<GameObject>();
        private static readonly List<Transform> snapPoints = new List<Transform>();

        internal static bool Active => holding;

        private static Kind KindOf(Piece piece)
        {
            if (!piece || piece.m_groundPiece || piece.m_groundOnly || piece.m_cultivatedGroundOnly) return Kind.None;
            string name = Utils.GetPrefabName(piece.gameObject).ToLowerInvariant();
            if (name.Contains("roof")) return Kind.Roof;
            if (name.Contains("pole") || name.Contains("beam") || name.Contains("pillar")) return Kind.Pillar;
            if (name.Contains("wall")) return Kind.Wall;
            return Kind.None;
        }

        // The game's place and remove buttons inside UpdatePlacement: not seen by it while a row is held, nor on the press
        // that starts one.
        private static bool Down(string name) => !holding || name != "Attack" && name != "Remove" ? ZInput.GetButtonDown(name) : false;
        private static bool Up(string name) => !holding || name != "Attack" && name != "Remove" ? ZInput.GetButtonUp(name) : false;

        [HarmonyPatch(typeof(Player), "UpdatePlacement")]
        private static class Input
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => instructions
                .MethodReplacer(AccessTools.Method(typeof(ZInput), nameof(ZInput.GetButtonDown)), AccessTools.Method(typeof(QuickBuild), nameof(Down)))
                .MethodReplacer(AccessTools.Method(typeof(ZInput), nameof(ZInput.GetButtonUp)), AccessTools.Method(typeof(QuickBuild), nameof(Up)));

            private static void Prefix(Player __instance, bool takeInput)
            {
                if (__instance != Player.m_localPlayer) return;
                if (!__instance.InPlaceMode() || Storage.RelocationClient.Active || !__instance.m_placementGhost) { Stop(); return; }
                if (!holding)
                {
                    if (!takeInput || Hud.InRadial() || Hud.IsPieceSelectionVisible() || !ZInput.GetButtonDown("Attack")) return;
                    var piece = __instance.GetSelectedPiece();
                    if (piece == null || piece.m_repairPiece || piece.m_removePiece || __instance.m_placementStatus != Player.PlacementStatus.Valid) return;
                    if (Time.time - __instance.m_lastToolUseTime < __instance.m_placeDelay) return;
                    // The first piece as the game checks it (station, artisan, build chests): the game answers otherwise.
                    if (!__instance.m_noPlacementCost && !__instance.HaveRequirements(piece, Player.RequirementMode.CanBuild)) return;
                    if (!Begin(__instance, piece)) return;
                    holding = true;
                    return;
                }
                if (ZInput.GetButtonDown("Remove") || !takeInput) { Stop(); return; }
                if (!ZInput.GetButton("Attack")) { Finish(__instance); Stop(); }
            }
        }

        // While held the game's ghost follows the aim as usual; the row is measured from the aim, then the ghost goes back to
        // the row's first piece.
        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        private static class Row
        {
            [HarmonyPriority(Priority.Low)]
            private static void Postfix(Player __instance)
            {
                if (!holding || __instance != Player.m_localPlayer || !__instance.m_placementGhost) return;
                Measure(__instance);
                var ghost = __instance.m_placementGhost;
                ghost.SetActive(true);
                ghost.transform.SetPositionAndRotation(anchor, turn);
                Show(__instance, ghost);
            }
        }

        // The ghosts stay red when the row cannot be built, whatever the game decided for the first piece.
        [HarmonyPatch(typeof(Player), "UpdatePlacement")]
        private static class Tint
        {
            private static void Postfix(Player __instance)
            {
                if (!holding || __instance != Player.m_localPlayer || !__instance.m_placementGhost) return;
                __instance.m_placementGhost.GetComponent<Piece>()?.SetInvalidPlacementHeightlight(!valid);
            }
        }

        private static bool Begin(Player player, Piece piece)
        {
            kind = KindOf(piece);
            if (kind == Kind.None) return false;
            var ghost = player.m_placementGhost;
            anchor = ghost.transform.position; turn = ghost.transform.rotation;
            // The piece's size along its own axes, from its snap points (its collider when it has none).
            snapPoints.Clear(); ghost.GetComponent<Piece>().GetSnapPoints(snapPoints);
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            foreach (var point in snapPoints) { var p = ghost.transform.InverseTransformPoint(point.position); min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            Vector3 size = snapPoints.Count > 1 ? max - min : Vector3.zero;
            if (size.x < .1f && size.y < .1f && size.z < .1f)
                foreach (var collider in ghost.GetComponentsInChildren<Collider>())
                {
                    var box = collider.bounds; var extent = Quaternion.Inverse(turn) * box.size;
                    size = Vector3.Max(size, new Vector3(Mathf.Abs(extent.x), Mathf.Abs(extent.y), Mathf.Abs(extent.z)));
                }
            switch (kind)
            {
                case Kind.Wall: axisA = Vector3.right; sizeA = size.x; axisB = Vector3.up; sizeB = size.y; break;
                case Kind.Roof: axisA = Vector3.right; sizeA = size.x; axisB = Vector3.zero; sizeB = 0; break;
                default:
                    // Along its length, whichever axis that is (upright pole, lying beam).
                    if (size.y >= size.x && size.y >= size.z) { axisA = Vector3.up; sizeA = size.y; }
                    else if (size.x >= size.z) { axisA = Vector3.right; sizeA = size.x; }
                    else { axisA = Vector3.forward; sizeA = size.z; }
                    axisB = Vector3.zero; sizeB = 0; break;
            }
            if (sizeA < .1f) return false;
            if (sizeB < .1f) axisB = Vector3.zero;
            fromA = toA = fromB = toB = 0; valid = true;
            return true;
        }

        // The aim, as a camera ray, read in the row's own axes: on the wall's plane, or nearest to the row's line.
        private static void Measure(Player player)
        {
            if (!GameCamera.instance) return;
            var camera = GameCamera.instance.transform;
            Vector3 a = turn * axisA, b = turn * axisB;
            float alongA, alongB = 0;
            if (axisB != Vector3.zero)
            {
                var plane = new Plane(Vector3.Cross(a, b).normalized, anchor);
                var ray = new Ray(camera.position, camera.forward);
                if (!plane.Raycast(ray, out float distance) || distance > 60f) return;
                var offset = ray.GetPoint(distance) - anchor;
                alongA = Vector3.Dot(offset, a); alongB = Vector3.Dot(offset, b);
            }
            else alongA = Along(anchor, a, camera.position, camera.forward);
            int na = Mathf.Clamp(Mathf.RoundToInt(alongA / sizeA), -MaxPerAxis, MaxPerAxis);
            int nb = axisB == Vector3.zero ? 0 : Mathf.Clamp(Mathf.RoundToInt(alongB / sizeB), -MaxPerAxis, MaxPerAxis);
            fromA = Mathf.Min(0, na); toA = Mathf.Max(0, na); fromB = Mathf.Min(0, nb); toB = Mathf.Max(0, nb);
            valid = Affordable(player) && Cells().All(Allowed);
        }

        // Distance along the line (origin, direction) to its nearest point to the camera ray.
        private static float Along(Vector3 origin, Vector3 direction, Vector3 eye, Vector3 look)
        {
            Vector3 w = origin - eye;
            float b = Vector3.Dot(direction, look), d = Vector3.Dot(direction, w), e = Vector3.Dot(look, w), denominator = 1 - b * b;
            return denominator < 1e-4f ? 0 : (b * e - d) / denominator;
        }

        private static IEnumerable<Vector3> Cells()
        {
            Vector3 a = turn * axisA * sizeA, b = turn * axisB * sizeB;
            for (int j = fromB; j <= toB; j++)
                for (int i = fromA; i <= toA; i++)
                    yield return anchor + a * i + b * j;
        }

        private static int Count => (toA - fromA + 1) * (toB - fromB + 1);

        private static bool Allowed(Vector3 point) => !Location.IsInsideNoBuildLocation(point) && PrivateArea.CheckAccess(point, 0, false, false);

        // Resources of the whole row in the inventory (none needed in no-cost mode or with the world's free build key).
        private static bool Affordable(Player player)
        {
            var piece = player.GetSelectedPiece();
            if (piece == null) return false;
            if (player.m_noPlacementCost || ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey())) return true;
            var inventory = player.GetInventory();
            foreach (var requirement in piece.m_resources)
            {
                if (!requirement.m_resItem) continue;
                int need = requirement.GetAmount(1) * Count;
                if (need > 0 && inventory.CountItems(requirement.m_resItem.m_itemData.m_shared.m_name) < need) return false;
            }
            return true;
        }

        // One ghost per piece of the row past the first (the game's ghost).
        private static void Show(Player player, GameObject ghost)
        {
            var cells = Cells().Where(p => (p - anchor).sqrMagnitude > .0001f).ToList();
            while (copies.Count < cells.Count)
            {
                bool init = ZNetView.m_forceDisableInit; ZNetView.m_forceDisableInit = true;
                try { copies.Add(Object.Instantiate(ghost)); } finally { ZNetView.m_forceDisableInit = init; }
            }
            for (int i = 0; i < copies.Count; i++)
            {
                bool used = i < cells.Count;
                copies[i].SetActive(used);
                if (!used) continue;
                copies[i].transform.SetPositionAndRotation(cells[i], turn);
                copies[i].GetComponent<Piece>()?.SetInvalidPlacementHeightlight(!valid);
            }
        }

        private static void Finish(Player player)
        {
            var piece = player.GetSelectedPiece();
            if (piece == null || !player.m_placementGhost) return;
            if (!valid || player.m_placementStatus != Player.PlacementStatus.Valid && player.m_placementStatus != Player.PlacementStatus.NoRayHits)
            {
                player.Message(MessageHud.MessageType.Center, Affordable(player) ? "$msg_invalidplacement" : "$msg_missingrequirement");
                return;
            }
            bool free = player.m_noPlacementCost || ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey());
            bool first = true;
            foreach (var cell in Cells().ToList())
            {
                bool cheated = (player.m_inventory.ItemCheated(piece.m_resources, -1, true) || player.NoCostCheat()) && !PlayerProfile.s_bypassCheatChecks;
                Game.instance.IncrementPlayerStat(PlayerStatType.Builds, 1f, cheated);
                Game.instance.GetPlayerProfile().IncrementStatBuildPiecePlaced(piece.m_name, 1f, cheated);
                player.PlacePiece(piece, cell, turn, first, cheated);
                if (!free) player.ConsumeResources(piece.m_resources, 0, -1, 1);
                first = false;
            }
            Hud.instance.m_buildUi.AddRecentPiece(piece);
            player.m_lastToolUseTime = Time.time;
        }

        private static void Stop()
        {
            holding = false; kind = Kind.None;
            foreach (var copy in copies) if (copy) Object.Destroy(copy);
            copies.Clear();
        }
    }
}
