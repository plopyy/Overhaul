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
        private static float sizeA, sizeB, alongLastA, alongLastB;
        private static Vector3 pieceMin, pieceMax;
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
                if (!__instance.InPlaceMode()) { Stop(); Handles(__instance, null); BuildRotation.Hide(); return; }
                if (Storage.RelocationClient.Active || !__instance.m_placementGhost) { Stop(); return; }
                if (!holding)
                {
                    // A row waiting for its chests is placed once they are leased.
                    if (Storage.BuildStorage.RunBatch(__instance)) return;
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
                if (__instance != Player.m_localPlayer) return;
                if (!holding || !__instance.m_placementGhost) { Handles(__instance, __instance.m_placementGhost); return; }
                Measure(__instance);
                var ghost = __instance.m_placementGhost;
                ghost.SetActive(true);
                ghost.transform.SetPositionAndRotation(anchor, turn);
                // The game tinted its ghost for the aimed spot (often away from the row): the row's colour instead.
                Highlight(ghost, valid);
                Show(__instance, ghost);
                Handles(__instance, ghost);
                // The gizmo on the row's last piece, the one at the aim.
                BuildRotation.ShowAt(anchor + turn * (axisA * sizeA * (toA > 0 ? toA : fromA) + axisB * sizeB * (toB > 0 ? toB : fromB)), turn);
            }
        }

        // The ghosts stay red when the row cannot be built, whatever the game decided for the first piece.
        [HarmonyPatch(typeof(Player), "UpdatePlacement")]
        private static class Tint
        {
            private static void Postfix(Player __instance)
            {
                if (!holding || __instance != Player.m_localPlayer || !__instance.m_placementGhost) return;
                Highlight(__instance.m_placementGhost, valid);
            }
        }

        private static bool Begin(Player player, Piece piece)
        {
            kind = KindOf(piece);
            if (kind == Kind.None) return false;
            var ghost = player.m_placementGhost;
            anchor = ghost.transform.position; turn = ghost.transform.rotation;
            if (!Axes(kind, ghost, out axisA, out sizeA, out axisB, out sizeB, out pieceMin, out pieceMax)) return false;
            alongLastA = alongLastB = 0;
            fromA = toA = fromB = toB = 0; valid = true;
            return true;
        }

        // The piece's box in its own axes, from its snap points (its colliders when it has none).
        private static void Extents(GameObject ghost, out Vector3 min, out Vector3 max)
        {
            snapPoints.Clear(); ghost.GetComponent<Piece>().GetSnapPoints(snapPoints);
            min = Vector3.positiveInfinity; max = Vector3.negativeInfinity;
            foreach (var point in snapPoints) { var p = ghost.transform.InverseTransformPoint(point.position); min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            if (snapPoints.Count > 1 && Vector3.Distance(min, max) > .1f) return;
            min = Vector3.positiveInfinity; max = Vector3.negativeInfinity;
            foreach (var collider in ghost.GetComponentsInChildren<Collider>())
            {
                var b = collider.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = ghost.transform.InverseTransformPoint(new Vector3(i % 2 == 0 ? b.min.x : b.max.x, i / 2 % 2 == 0 ? b.min.y : b.max.y, i / 4 == 0 ? b.min.z : b.max.z));
                    min = Vector3.Min(min, corner); max = Vector3.Max(max, corner);
                }
            }
            if (min.x > max.x) min = max = Vector3.zero;
        }

        // The axes a row of this kind stretches along, with the piece's size on each (B unused: zero).
        private static bool Axes(Kind kind, GameObject ghost, out Vector3 a, out float sizeA, out Vector3 b, out float sizeB, out Vector3 min, out Vector3 max)
        {
            Extents(ghost, out min, out max);
            var size = max - min;
            b = Vector3.zero; sizeB = 0;
            switch (kind)
            {
                case Kind.Wall: a = Vector3.right; sizeA = size.x; b = Vector3.up; sizeB = size.y; break;
                case Kind.Roof: a = Vector3.right; sizeA = size.x; break;
                default:
                    // Along its length, whichever axis that is (upright pole, lying beam).
                    if (size.y >= size.x && size.y >= size.z) { a = Vector3.up; sizeA = size.y; }
                    else if (size.x >= size.z) { a = Vector3.right; sizeA = size.x; }
                    else { a = Vector3.forward; sizeA = size.z; }
                    break;
            }
            if (sizeB < .1f) { b = Vector3.zero; sizeB = 0; }
            return sizeA >= .1f;
        }

        // A small green bar beyond each side the selected piece can be stretched to, shown until a row is started.
        private static readonly List<GameObject> handles = new List<GameObject>();
        private static GameObject handle;

        private static void Handles(Player player, GameObject ghost)
        {
            var piece = ghost && !holding && ghost.activeInHierarchy && !Storage.RelocationClient.Active ? player.GetSelectedPiece() : null;
            var shown = new List<(Vector3 at, Vector3 scale)>();
            var pieceKind = KindOf(piece);
            if (holding && ghost) RowHandles(shown);
            else if (pieceKind != Kind.None && Axes(pieceKind, ghost, out var a, out float sa, out var b, out float sb, out var min, out var max))
            {
                Vector3 centre = (min + max) / 2, half = (max - min) / 2;
                foreach (var axis in b == Vector3.zero ? new[] { a } : new[] { a, b })
                {
                    // Across the side: along the piece's other long axis, a third of its size.
                    var across = axis == a && b != Vector3.zero ? b : axis == a ? Longest(Vector3.Scale(Vector3.one - Abs(a), max - min)) : a;
                    float length = Mathf.Max(.2f, Vector3.Dot(Abs(across), max - min) / 3);
                    var scale = Vector3.one * .06f + Abs(across) * (length - .06f);
                    foreach (int sign in new[] { -1, 1 })
                        shown.Add((centre + axis * sign * (Vector3.Dot(Abs(axis), half) + .15f), scale));
                }
            }
            if (shown.Count > 0 && !handle) handle = BuildRotation.Asset("Overhaul_QuickBuildHandle");
            while (handle && handles.Count < shown.Count) { var h = Object.Instantiate(handle); Object.DontDestroyOnLoad(h); handles.Add(h); }
            for (int i = 0; i < handles.Count; i++)
            {
                bool used = i < shown.Count;
                if (handles[i].activeSelf != used) handles[i].SetActive(used);
                if (!used) continue;
                var t = handles[i].transform;
                t.SetPositionAndRotation(ghost.transform.TransformPoint(shown[i].at), ghost.transform.rotation);
                t.localScale = shown[i].scale;
            }
        }

        // While a row grows: a bar beyond its end on each side it grows or can still grow (a wall not stretched up or down
        // only shows the bar above it, the ground being below), stretching out as the aim moves towards the next piece.
        private static void RowHandles(List<(Vector3 at, Vector3 scale)> shown)
        {
            var size = pieceMax - pieceMin;
            var centre = (pieceMin + pieceMax) / 2 + axisA * ((fromA + toA) / 2f * sizeA) + axisB * ((fromB + toB) / 2f * sizeB);
            foreach (var (axis, step, from, to, along, upOnly) in new[]
                { (axisA, sizeA, fromA, toA, alongLastA, false), (axisB, sizeB, fromB, toB, alongLastB, kind == Kind.Wall) })
            {
                if (axis == Vector3.zero) continue;
                var across = axis == axisA && axisB != Vector3.zero ? axisB : axis == axisA ? Longest(Vector3.Scale(Vector3.one - Abs(axisA), size)) : axisA;
                float length = Mathf.Max(.2f, Vector3.Dot(Abs(across), size) / 3);
                var signs = to > 0 ? new[] { 1 } : from < 0 ? new[] { -1 } : upOnly ? new[] { 1 } : new[] { -1, 1 };
                foreach (int sign in signs)
                {
                    int end = sign > 0 ? to : from;
                    float edge = sign > 0 ? Vector3.Dot(axis, pieceMax) + to * step : Vector3.Dot(axis, pieceMin) + from * step;
                    // From 0 at the row's end to 1 halfway to the next piece, where it appears.
                    float progress = Mathf.Clamp01((along / step - end) * sign * 2);
                    float depth = .06f + progress * Mathf.Min(.6f, step * .3f);
                    var at = centre + axis * (edge - Vector3.Dot(axis, centre) + sign * (.15f + depth / 2));
                    shown.Add((at, Vector3.one * .06f + Abs(across) * (length - .06f) + Abs(axis) * (depth - .06f)));
                }
            }
        }

        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        // The unit axis of the vector's largest component.
        private static Vector3 Longest(Vector3 v) => v.x >= v.y && v.x >= v.z ? Vector3.right : v.y >= v.z ? Vector3.up : Vector3.forward;

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
            alongLastA = alongA; alongLastB = alongB;
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
        // Counted in the inventory, and with the nearby build chests too unless only the inventory is asked for.
        private static bool Affordable(Player player, bool chests = true)
        {
            var piece = player.GetSelectedPiece();
            if (piece == null) return false;
            if (player.m_noPlacementCost || ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey())) return true;
            var inventory = player.GetInventory();
            if (chests) Storage.BuildStorage.Reading++;
            try
            {
                foreach (var requirement in piece.m_resources)
                {
                    if (!requirement.m_resItem) continue;
                    int need = requirement.GetAmount(1) * Count;
                    string name = requirement.m_resItem.m_itemData.m_shared.m_name;
                    // While building chests are read, the inventory count includes them (StationStorage).
                    int have = inventory.CountItems(name);
                    if (need > 0 && have < need) return false;
                }
            }
            finally { if (chests) Storage.BuildStorage.Reading--; }
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
                Highlight(copies[i], valid);
            }
        }

        private static void Finish(Player player)
        {
            var piece = player.GetSelectedPiece();
            if (piece == null || !player.m_placementGhost) return;
            // The first piece was checked by the game when the row began, every spot while it grew.
            if (!valid)
            {
                player.Message(MessageHud.MessageType.Center, Affordable(player) ? "$msg_invalidplacement" : "$msg_missingrequirement");
                return;
            }
            var cells = Cells().ToList();
            var rotation = turn;
            // What the inventory lacks comes from the nearby build chests, once they are leased.
            if (Affordable(player, false)) Place(player, piece, cells, rotation);
            else if (!Storage.BuildStorage.BeginBatch(player, piece, () => Place(player, piece, cells, rotation)))
                player.Message(MessageHud.MessageType.Center, "$msg_missingrequirement");
        }

        private static void Place(Player player, Piece piece, List<Vector3> cells, Quaternion turn)
        {
            bool free = player.m_noPlacementCost || ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey());
            bool first = true;
            foreach (var cell in cells)
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

        // Every ghost of the row green (not built yet), red when the row cannot be built.
        private static void Highlight(GameObject ghost, bool ok)
        {
            var colour = ok ? new Color(.3f, 1f, .35f) : Color.red;
            MaterialMan.instance.SetValue<Color>(ghost, ShaderProps._Color, colour, false);
            MaterialMan.instance.SetValue<Color>(ghost, ShaderProps._EmissionColor, colour * .7f, false);
        }

        private static void Stop()
        {
            holding = false; kind = Kind.None;
            foreach (var copy in copies) if (copy) Object.Destroy(copy);
            copies.Clear();
        }
    }
}
