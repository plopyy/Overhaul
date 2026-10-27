using HarmonyLib;
using UnityEngine;

namespace Overhaul.Building
{
    // Double doors: opening or closing a door also opens or closes the identical door set right beside it (same piece, side by
    // side in the same wall, facing the same way or mirrored), when that one is in the same state. The other door goes through
    // the game's own Interact, so it checks its own key and ward and swings away from the player like the first one.
    [HarmonyPatch(typeof(Door), nameof(Door.Interact))]
    internal static class DoubleDoors
    {
        private static bool busy;

        private static void Prefix(Door __instance, bool hold, out int __state) =>
            __state = !hold && !busy && __instance.m_nview && __instance.m_nview.IsValid() ? __instance.m_nview.GetZDO().GetInt(ZDOVars.s_state) : int.MinValue;

        private static void Postfix(Door __instance, Humanoid character, bool __result, int __state)
        {
            if (!__result || __state == int.MinValue || !character) return;
            var other = Partner(__instance, __state == 0);
            if (!other) return;
            busy = true;
            try { other.Interact(character, false, false); }
            finally { busy = false; }
        }

        // The nearest identical door beside this one, closed like it was (or open like it was).
        private static Door Partner(Door door, bool closed)
        {
            string name = Utils.GetPrefabName(door.gameObject);
            var t = door.transform;
            float width = Width(door), best = float.MaxValue;
            Door found = null;
            // By position, not by colliders: an open door's leaf has swung away from its frame.
            var near = new System.Collections.Generic.List<Piece>();
            Piece.GetAllPiecesInRadius(t.position, width * 1.25f, near);
            foreach (var piece in near)
            {
                var other = piece ? piece.GetComponentInChildren<Door>() : null;
                if (!other || other == door || Utils.GetPrefabName(other.gameObject) != name || !other.m_nview || !other.m_nview.IsValid()) continue;
                if ((other.m_nview.GetZDO().GetInt(ZDOVars.s_state) == 0) != closed) continue;
                // The same wall: facing the same way or mirrored, side by side along the door's width, at the same height.
                var o = other.transform;
                if (Mathf.Abs(Vector3.Dot(t.forward, o.forward)) < .98f) continue;
                var offset = o.position - t.position;
                if (Mathf.Abs(Vector3.Dot(offset, t.forward)) > .3f || Mathf.Abs(offset.y) > .3f) continue;
                float side = Mathf.Abs(Vector3.Dot(offset, t.right));
                if (side < width * .5f || side > width * 1.25f || side >= best) continue;
                best = side; found = other;
            }
            return found;
        }

        // The door's width: the spread of its snap points along its own width, 2 m when it has none.
        private static float Width(Door door)
        {
            var piece = door.GetComponent<Piece>();
            if (!piece) return 2f;
            var points = new System.Collections.Generic.List<Transform>();
            piece.GetSnapPoints(points);
            float min = float.MaxValue, max = float.MinValue;
            foreach (var p in points) { float x = door.transform.InverseTransformPoint(p.position).x; min = Mathf.Min(min, x); max = Mathf.Max(max, x); }
            return max - min > .3f ? max - min : 2f;
        }
    }
}
