using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Building
{
    // Quick build: holding the place button on a wall, a floor, a pillar/beam or a roof stretches it into a row of the same piece,
    // one piece size at a time (measured from its snap points), towards where the camera aims:
    //   - walls in their own plane: sideways, up or down, or both (a rectangle);
    //   - pillars and beams end to end, along their length;
    //   - floors flat on their own level: sideways, forwards or backwards, or both (a rectangle); wood floors may be laid
    //     as a checkerboard, every other one turned 90 degrees about its centre (building options box, KeyHintPanel);
    //   - roofs side by side only.
    // The piece is turned before (no turning while stretching). Every copy shows as a ghost; the resources of the whole
    // row are counted while it grows, and the ghosts turn red when they are short or a spot is not allowed (warded, no
    // build zone). Releasing the button places and pays every piece, or nothing at all when red. Right click cancels.
    // Other pieces are placed by the game as usual.
    internal static class QuickBuild
    {
        private enum Kind { None, Wall, Floor, Pillar, Roof }
        private const int MaxPerAxis = 30;
        // Seconds the place button is held before a row starts growing.
        private const float HoldDelay = .25f;
        private static float began;

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
            if (name.Contains("floor")) return Kind.Floor;
            return Kind.None;
        }

        // The game's place and remove buttons inside UpdatePlacement: not seen by it while a row is held, on the press
        // that starts one.
        private static bool Down(string name) => !Owned(name) && (ZInput.GetButtonDown(name) || name == "Attack" && (LevelingHeld() || RepairHeld()));

        // Quick repair: with the hammer's repair selected, holding the place button repairs whatever damaged piece is aimed at,
        // one repair every RepairInterval seconds (the game's repair keeps no time of its own); an undamaged piece is skipped, so
        // the "does not need repair" message is not repeated.
        private const float RepairInterval = .3f;
        private static float lastRepair;
        private static bool RepairHeld()
        {
            var player = Player.m_localPlayer;
            if (!player || !ZInput.GetButton("Attack") || ZInput.GetButtonDown("Attack") || Hud.InRadial() || Hud.IsPieceSelectionVisible()) return false;
            var piece = player.GetSelectedPiece();
            if (!piece || !piece.m_repairPiece || Time.time - lastRepair < RepairInterval) return false;
            var aimed = player.GetHoveringPiece();
            var wear = aimed ? aimed.GetComponent<WearNTear>() : null;
            if (!wear || wear.GetHealthPercentage() >= 1f) return false;
            lastRepair = Time.time;
            return true;
        }

        // The hoe's level ground keeps levelling while the place button is held, at the tool's own pace (raising and
        // paths still take a click each).
        private static bool LevelingHeld()
        {
            var player = Player.m_localPlayer;
            if (!player || !ZInput.GetButton("Attack") || Hud.InRadial() || Hud.IsPieceSelectionVisible()) return false;
            var piece = player.GetSelectedPiece();
            return piece && piece.m_name == "$piece_levelground" && Time.time - player.m_lastToolUseTime >= player.m_placeDelay;
        }
        private static bool Up(string name) => !Owned(name) && ZInput.GetButtonUp(name);
        // Also on the frame a row ends: its release must not place or remove one more piece.
        private static bool Owned(string name) => (holding || ended == Time.frameCount) && (name == "Attack" || name == "Remove");
        private static int ended = -1;

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
                    if (!takeInput || Hud.InRadial() || Hud.IsPieceSelectionVisible()) return;
                    if (ZInput.GetButtonDown("Remove") && BeginDestroy(__instance)) { holding = destroying = true; return; }
                    if (!ZInput.GetButtonDown("Attack")) return;
                    var piece = __instance.GetSelectedPiece();
                    if (piece == null || piece.m_repairPiece || piece.m_removePiece || __instance.m_placementStatus != Player.PlacementStatus.Valid) return;
                    if (Time.time - __instance.m_lastToolUseTime < __instance.m_placeDelay) return;
                    // The first piece as the game checks it (station, artisan, build chests): the game answers otherwise.
                    if (!__instance.m_noPlacementCost && !__instance.HaveRequirements(piece, Player.RequirementMode.CanBuild)) return;
                    if (!Begin(__instance, piece)) return;
                    holding = true;
                    return;
                }
                if (destroying)
                {
                    if (ZInput.GetButtonDown("Attack") || !takeInput) { Stop(); return; }
                    if (!ZInput.GetButton("Remove")) { DestroyRow(__instance); Stop(); }
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
                if (destroying)
                {
                    // Removing: the game's ghost and the gizmo stay out of the way.
                    __instance.m_placementGhost.SetActive(false);
                    BuildRotation.Hide();
                    Mark();
                    Handles(__instance, __instance.m_placementGhost);
                    return;
                }
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
                if (!holding || destroying || __instance != Player.m_localPlayer || !__instance.m_placementGhost) return;
                Highlight(__instance.m_placementGhost, valid);
            }
        }

        // Quick removal: holding the remove button on a wall, a floor, a pillar/beam or a roof stretches, in the same directions,
        // over the identical pieces lined up with it (same piece, same turn, one piece size apart); they turn red, and
        // releasing removes each of them as the game would (refund, ward, station, removable). Left click cancels.
        private static bool destroying;
        private static readonly Dictionary<(int, int), Piece> candidates = new Dictionary<(int, int), Piece>();
        private static HashSet<Piece> marked = new HashSet<Piece>();

        private static bool BeginDestroy(Player player)
        {
            if (!GameCamera.instance) return false;
            var camera = GameCamera.instance.transform;
            // The nearest piece aimed at, through the selected piece's ghost (it sits where the camera aims, often in front of
            // the piece to remove).
            Piece piece = null;
            float nearest = float.MaxValue;
            var ghost = player.m_placementGhost ? player.m_placementGhost.transform : null;
            foreach (var hit in Physics.RaycastAll(camera.position, camera.forward, 50f, player.m_removeRayMask))
            {
                if (hit.distance >= nearest || ghost && hit.collider.transform.IsChildOf(ghost)) continue;
                nearest = hit.distance;
                piece = hit.collider.GetComponentInParent<Piece>();
                if (Vector3.Distance(hit.point, player.m_eye.position) >= player.m_maxPlaceDistance) piece = null;
            }
            kind = KindOf(piece);
            if (kind == Kind.None || !piece.IsPlacedByPlayer()) return false;
            anchor = piece.transform.position; turn = piece.transform.rotation;
            if (!Axes(kind, piece.gameObject, out axisA, out sizeA, out axisB, out sizeB, out pieceMin, out pieceMax)) return false;
            // The same pieces, turned the same way, sitting on the row's grid.
            string name = Utils.GetPrefabName(piece.gameObject);
            candidates.Clear();
            // Square floors also by quarter turns (a checkerboard floor), matched by their centres.
            bool quarters = kind == Kind.Floor && Mathf.Abs(sizeA - sizeB) < .05f;
            var centre = (pieceMin + pieceMax) / 2;
            foreach (var other in Piece.s_allPieces)
            {
                if (!other || Utils.GetPrefabName(other.gameObject) != name) continue;
                var relative = Quaternion.Inverse(turn) * other.transform.rotation;
                if (Quaternion.Angle(relative, Quaternion.identity) > 1f &&
                    !(quarters && Quaternion.Angle(relative, Quaternion.Euler(0, Mathf.Round(relative.eulerAngles.y / 90) * 90, 0)) <= 1f)) continue;
                var local = Quaternion.Inverse(turn) * (other.transform.position + other.transform.rotation * centre - anchor - turn * centre);
                int i = Mathf.RoundToInt(Vector3.Dot(local, axisA) / sizeA), j = axisB == Vector3.zero ? 0 : Mathf.RoundToInt(Vector3.Dot(local, axisB) / sizeB);
                if (Mathf.Abs(i) > MaxPerAxis || Mathf.Abs(j) > MaxPerAxis || (local - axisA * sizeA * i - axisB * sizeB * j).magnitude > .1f) continue;
                candidates[(i, j)] = other;
            }
            alongLastA = alongLastB = 0;
            fromA = toA = fromB = toB = 0; valid = true;
            return true;
        }

        // The pieces of the row turn red; those it no longer covers get their colour back.
        private static void Mark()
        {
            var now = new HashSet<Piece>();
            for (int j = fromB; j <= toB; j++)
                for (int i = fromA; i <= toA; i++)
                    if (candidates.TryGetValue((i, j), out var piece) && piece) now.Add(piece);
            foreach (var piece in marked) if (piece && !now.Contains(piece)) Unmark(piece);
            foreach (var piece in now) Highlight(piece.gameObject, false);
            marked = now;
        }

        private static void Unmark(Piece piece)
        {
            MaterialMan.instance.ResetValue(piece.gameObject, ShaderProps._Color);
            MaterialMan.instance.ResetValue(piece.gameObject, ShaderProps._EmissionColor);
        }

        private static void DestroyRow(Player player)
        {
            int removed = 0;
            foreach (var piece in marked.ToList()) if (piece && Remove(player, piece)) removed++;
            var tool = player.GetRightItem();
            if (removed == 0 || tool == null) return;
            player.FaceLookDirection();
            player.m_zanim.SetTrigger(tool.m_shared.m_attack.m_attackAnimation);
        }

        // The game's removal of one piece (Player.RemovePiece), without its own aiming.
        private static bool Remove(Player player, Piece piece)
        {
            if (!piece.m_canBeRemoved || Location.IsInsideNoBuildLocation(piece.transform.position) ||
                !PrivateArea.CheckAccess(piece.transform.position, 0, false, false) || !player.CheckCanRemovePiece(piece) ||
                !piece.GetComponent<ZNetView>() || !piece.CanBeRemoved()) return false;
            piece.GetComponent<IRemoved>()?.OnRemoved();
            var wear = piece.GetComponent<WearNTear>();
            if (!wear) return false;
            wear.Remove(false);
            return true;
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
            began = Time.time;
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
                case Kind.Floor: a = Vector3.right; sizeA = size.x; b = Vector3.forward; sizeB = size.z; break;
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
                // A row (built or removed) is drawn in its own frame, a lone piece on the game's ghost.
                if (holding) t.SetPositionAndRotation(anchor + turn * shown[i].at, turn);
                else t.SetPositionAndRotation(ghost.transform.TransformPoint(shown[i].at), ghost.transform.rotation);
                t.localScale = shown[i].scale;
            }
        }

        // While a row grows: a bar beyond its end on each side it grows or can still grow (a wall not stretched up or down
        // only shows the bar above it, the ground being below), stretching out and growing as the aim moves towards the next
        // piece.
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
                    // It also grows along the side (taller beside, wider above or below).
                    float grown = length * (1 + .6f * progress);
                    shown.Add((at, Vector3.one * .06f + Abs(across) * (grown - .06f) + Abs(axis) * (depth - .06f)));
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
                // Aiming at the ground before the plane: the spot aimed at, not the plane under the ground.
                var point = Physics.Raycast(ray, out var hit, distance, player.m_placeRayMask) ? hit.point : ray.GetPoint(distance);
                var offset = point - anchor;
                alongA = Vector3.Dot(offset, a); alongB = Vector3.Dot(offset, b);
            }
            else alongA = Along(anchor, a, camera.position, camera.forward);
            alongLastA = alongA; alongLastB = alongB;
            int na = Mathf.Clamp(Mathf.RoundToInt(alongA / sizeA), -MaxPerAxis, MaxPerAxis);
            int nb = axisB == Vector3.zero ? 0 : Mathf.Clamp(Mathf.RoundToInt(alongB / sizeB), -MaxPerAxis, MaxPerAxis);
            // A short click places the lone piece: the row only grows once the button is held.
            if (Time.time - began < HoldDelay) na = nb = 0;
            fromA = Mathf.Min(0, na); toA = Mathf.Max(0, na); fromB = Mathf.Min(0, nb); toB = Mathf.Max(0, nb);
            valid = destroying || Affordable(player) && Cells().All(Allowed);
        }

        // Distance along the line (origin, direction) to its nearest point to the camera ray.
        private static float Along(Vector3 origin, Vector3 direction, Vector3 eye, Vector3 look)
        {
            Vector3 w = origin - eye;
            float b = Vector3.Dot(direction, look), d = Vector3.Dot(direction, w), e = Vector3.Dot(look, w), denominator = 1 - b * b;
            return denominator < 1e-4f ? 0 : (b * e - d) / denominator;
        }

        private static IEnumerable<Vector3> Cells() => Spots().Select(s => s.at);

        // Each piece of the row with its turn: the row's own, or turned 90 degrees about the piece's centre on every
        // other cell of a checkerboard floor.
        private static IEnumerable<(Vector3 at, Quaternion turn)> Spots()
        {
            Vector3 a = turn * axisA * sizeA, b = turn * axisB * sizeB;
            bool checker = Checkerboard;
            var quarter = turn * Quaternion.Euler(0, 90, 0);
            var centre = (pieceMin + pieceMax) / 2;
            var shift = turn * centre - quarter * centre;
            for (int j = fromB; j <= toB; j++)
                for (int i = fromA; i <= toA; i++)
                {
                    var at = anchor + a * i + b * j;
                    yield return checker && ((i + j) & 1) != 0 ? (at + shift, quarter) : (at, turn);
                }
        }

        private static bool Checkerboard => kind == Kind.Floor && Utility.OverhaulConfig.BuildCheckerboardFloors.Value &&
            Mathf.Abs(sizeA - sizeB) < .05f && IsWoodFloor(Player.m_localPlayer ? Player.m_localPlayer.GetSelectedPiece() : null);

        // The wood floors, the pieces the checkerboard option is for: wood_floor, wood_floor_1x1, ashwood_floor_2x2,
        // ashwood_floor_1x1, ashwood_deco_floor.
        internal static bool IsWoodFloor(Piece piece)
        {
            if (!piece) return false;
            string name = Utils.GetPrefabName(piece.gameObject).ToLowerInvariant();
            return name.Contains("wood") && name.Contains("floor");
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
            var cells = Spots().Where(s => (s.at - anchor).sqrMagnitude > .0001f).ToList();
            while (copies.Count < cells.Count)
            {
                copies.Add(Copy(ghost));
            }
            for (int i = 0; i < copies.Count; i++)
            {
                bool used = i < cells.Count;
                copies[i].SetActive(used);
                if (!used) continue;
                copies[i].transform.SetPositionAndRotation(cells[i].at, cells[i].turn);
                Highlight(copies[i], valid);
            }
        }

        // A copy of the game's ghost for show only: made under an inactive parent so none of its scripts wakes (the
        // ghost's network view is gone, WearNTear.Awake would throw on it), its scripts removed, then let out.
        private static GameObject Copy(GameObject ghost)
        {
            if (!holder) { holder = new GameObject("Overhaul_QuickBuildCopies"); holder.SetActive(false); Object.DontDestroyOnLoad(holder); }
            var copy = Object.Instantiate(ghost, holder.transform);
            // Last first: a script another one requires goes after it.
            foreach (var script in copy.GetComponentsInChildren<MonoBehaviour>(true).Reverse()) Object.DestroyImmediate(script);
            copy.transform.SetParent(null);
            return copy;
        }
        private static GameObject holder;

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
            var cells = Spots().ToList();
            // What the inventory lacks comes from the nearby build chests, once they are leased.
            if (Affordable(player, false)) Place(player, piece, cells);
            else if (!Storage.BuildStorage.BeginBatch(player, piece, () => Place(player, piece, cells)))
                player.Message(MessageHud.MessageType.Center, "$msg_missingrequirement");
        }

        private static void Place(Player player, Piece piece, List<(Vector3 at, Quaternion turn)> cells)
        {
            bool free = player.m_noPlacementCost || ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey());
            bool first = true;
            foreach (var cell in cells)
            {
                bool cheated = (player.m_inventory.ItemCheated(piece.m_resources, -1, true) || player.NoCostCheat()) && !PlayerProfile.s_bypassCheatChecks;
                Game.instance.IncrementPlayerStat(PlayerStatType.Builds, 1f, cheated);
                Game.instance.GetPlayerProfile().IncrementStatBuildPiecePlaced(piece.m_name, 1f, cheated);
                player.PlacePiece(piece, cell.at, cell.turn, first, cheated);
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
            if (holding) ended = Time.frameCount;
            holding = false; kind = Kind.None;
            foreach (var piece in marked) if (piece) Unmark(piece);
            marked.Clear(); candidates.Clear(); destroying = false;
            foreach (var copy in copies) if (copy) Object.Destroy(copy);
            copies.Clear();
        }
    }
}
