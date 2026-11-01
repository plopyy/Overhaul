using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Storage
{
    // Signs hook onto the four side faces of a chest. The link is the pose itself: a sign sitting exactly on a
    // hook point belongs to the chest, so nothing is saved and a sign placed before this feature behaves as ever.
    // Native snap points (corners and side centres) are added too, so chests line up and take pieces on top.
    internal static class ChestSigns
    {
        const float SignHalfDepth = .05f, Reach = .25f;
        const string SnapName = "_overhaul_snap";
        static readonly Vector3[] Faces = { Vector3.forward, Vector3.back, Vector3.left, Vector3.right };
        static readonly Dictionary<int, Bounds> Shapes = new Dictionary<int, Bounds>();

        internal struct Hooked { internal Sign Sign; internal Vector3 Offset; internal Quaternion Turn; }

        static bool Hookable(Container c) => ChestInteractions.Eligible(c) && !c.GetComponent<TroughContainer>();
        static Bounds Shape(Container chest)
        {
            int prefab = chest.m_nview.GetZDO().GetPrefab();
            if (Shapes.TryGetValue(prefab, out var shape)) return shape;
            return Shapes[prefab] = Measure(chest.transform);
        }
        static Bounds Measure(Transform root)
        {
            var shape = new Bounds();
            bool first = true;
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
            {
                Bounds local;
                if (collider.isTrigger) continue;
                if (collider is BoxCollider box) local = new Bounds(box.center, box.size);
                else if (collider is MeshCollider mesh && mesh.sharedMesh) local = mesh.sharedMesh.bounds;
                else continue;
                for (int i = 0; i < 8; i++)
                {
                    var corner = local.center + Vector3.Scale(local.extents, new Vector3((i & 1) * 2 - 1, (i & 2) - 1, (i & 4) / 2 - 1));
                    corner = root.InverseTransformPoint(collider.transform.TransformPoint(corner));
                    if (first) { shape = new Bounds(corner, Vector3.zero); first = false; }
                    else shape.Encapsulate(corner);
                }
            }
            return shape;
        }
        static void AddSnap(Transform root, Vector3 local)
        {
            var point = new GameObject(SnapName);
            point.tag = "snappoint";
            point.SetActive(false);
            point.transform.SetParent(root, false);
            point.transform.localPosition = local;
        }
        static bool ChestPrefab(GameObject prefab) => prefab.GetComponent<Container>() && prefab.GetComponent<Piece>() &&
            !prefab.GetComponent<DeviceStore>() && !prefab.GetComponent<TroughContainer>() && !prefab.GetComponent<TombStone>() &&
            !prefab.GetComponent<Ship>() && !prefab.GetComponent<Vagon>();
        static void Setup(GameObject prefab)
        {
            var root = prefab.transform;
            bool sign = prefab.GetComponent<Sign>();
            if ((!sign && !ChestPrefab(prefab)) || root.Find(SnapName)) return;
            var shape = Measure(root);
            if (sign) { AddSnap(root, new Vector3(0, 0, shape.min.z)); return; }
            if (prefab.GetComponent<WearNTear>() is WearNTear wear) wear.m_supports = true;
            foreach (var face in Faces) AddSnap(root, shape.center + Vector3.Scale(face, shape.extents));
            foreach (float y in new[] { shape.min.y, shape.max.y })
                foreach (float x in new[] { shape.min.x, shape.max.x })
                    foreach (float z in new[] { shape.min.z, shape.max.z })
                        AddSnap(root, new Vector3(x, y, z));
        }
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class Prefabs
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(ZNetScene __instance)
            {
                foreach (var prefab in __instance.m_prefabs) if (prefab) Setup(prefab);
            }
        }
        static void Pose(Container chest, Vector3 face, out Vector3 position, out Quaternion rotation)
        {
            var shape = Shape(chest);
            var local = shape.center + Vector3.Scale(face, shape.extents + Vector3.one * SignHalfDepth);
            position = chest.transform.TransformPoint(local);
            rotation = chest.transform.rotation * Quaternion.LookRotation(face);
        }
        static bool Sits(Sign sign, Vector3 position, Quaternion rotation) =>
            Vector3.Distance(sign.transform.position, position) < .03f && Quaternion.Angle(sign.transform.rotation, rotation) < 3f;
        static Sign SignAt(Vector3 position, Quaternion rotation)
        {
            foreach (var hit in Physics.OverlapSphere(position, .1f, -1, QueryTriggerInteraction.Ignore))
            {
                var sign = hit.GetComponentInParent<Sign>();
                // A placement ghost has no valid network view.
                if (sign && sign.m_nview && sign.m_nview.IsValid() && Sits(sign, position, rotation)) return sign;
            }
            return null;
        }
        internal static List<Sign> SignsOn(Container chest)
        {
            var signs = new List<Sign>();
            if (!Hookable(chest) || !chest.m_nview.IsValid()) return signs;
            foreach (var face in Faces)
            {
                Pose(chest, face, out var position, out var rotation);
                var sign = SignAt(position, rotation);
                if (sign) signs.Add(sign);
            }
            return signs;
        }
        internal static Sign SignOn(Container chest)
        {
            var signs = SignsOn(chest);
            return signs.Count > 0 ? signs[0] : null;
        }
        internal static Container ChestOf(Sign sign)
        {
            if (!sign) return null;
            var behind = sign.transform.position - sign.transform.forward * .3f;
            foreach (var hit in Physics.OverlapSphere(behind, Reach, -1, QueryTriggerInteraction.Ignore))
            {
                var chest = hit.GetComponentInParent<Container>();
                if (!Hookable(chest) || !chest.m_nview.IsValid()) continue;
                foreach (var face in Faces)
                {
                    Pose(chest, face, out var position, out var rotation);
                    if (Sits(sign, position, rotation)) return chest;
                }
            }
            return null;
        }
        // The chest a hover target stands for: an aimed sign answers for the chest it hangs on.
        internal static Container ChestOf(GameObject hover) => hover ? ChestOf(hover.GetComponentInParent<Sign>()) : null;

        internal static List<Hooked> Capture(Piece chestPiece)
        {
            var hooked = new List<Hooked>();
            var chest = chestPiece.GetComponent<Container>();
            if (!chest) return hooked;
            var inverse = Quaternion.Inverse(chestPiece.transform.rotation);
            foreach (var sign in SignsOn(chest))
                hooked.Add(new Hooked { Sign = sign, Offset = inverse * (sign.transform.position - chestPiece.transform.position), Turn = inverse * sign.transform.rotation });
            return hooked;
        }
        // Called by the chest's owner right after the chest moved, with the signs captured before.
        internal static void Follow(List<Hooked> hooked, Vector3 point, Quaternion rotation)
        {
            foreach (var entry in hooked)
            {
                if (!entry.Sign || !entry.Sign.m_nview.IsValid()) continue;
                var piece = entry.Sign.GetComponent<Piece>();
                var data = entry.Sign.m_nview.GetZDO();
                Vector3 position = point + rotation * entry.Offset;
                Quaternion turn = rotation * entry.Turn;
                entry.Sign.m_nview.ClaimOwnership();
                data.SetPosition(position); data.SetRotation(turn);
                data.Set(PieceRelocation.RevisionKey, data.GetInt(PieceRelocation.RevisionKey, 0) + 1);
                PieceRelocation.Apply(piece, position, turn);
            }
        }

        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        private static class HookGhost
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Player __instance)
            {
                var ghost = __instance.m_placementGhost;
                if (__instance != Player.m_localPlayer || !ghost || !ghost.activeSelf || !ghost.GetComponent<Sign>()) return;
                if (!__instance.PieceRayTest(out var point, out _, out var piece, out _, out _, false) || !piece) return;
                var chest = piece.GetComponent<Container>();
                if (!Hookable(chest) || !chest.m_nview.IsValid()) return;
                float best = float.MaxValue;
                foreach (var face in Faces)
                {
                    Pose(chest, face, out var position, out var rotation);
                    float distance = Vector3.Distance(point, position);
                    if (distance >= best || SignAt(position, rotation)) continue;
                    best = distance;
                    ghost.transform.SetPositionAndRotation(position, rotation);
                }
            }
        }
        // Chests support nothing natively, but a hooked sign holds as long as its chest stands.
        [HarmonyPatch(typeof(WearNTear), "HaveSupport")]
        private static class Support
        {
            private static void Postfix(WearNTear __instance, ref bool __result)
            {
                if (!__result && __instance.GetComponent<Sign>() is Sign sign && ChestOf(sign)) __result = true;
            }
        }
        [HarmonyPatch(typeof(Sign), nameof(Sign.GetHoverText))]
        private static class HoverText
        {
            private static void Postfix(Sign __instance, ref string __result)
            {
                var chest = ChestOf(__instance);
                if (chest) __result = chest.GetHoverText();
            }
        }
        [HarmonyPatch(typeof(Sign), nameof(Sign.GetHoverName))]
        private static class HoverName
        {
            private static void Postfix(Sign __instance, ref string __result)
            {
                var chest = ChestOf(__instance);
                if (chest) __result = chest.GetHoverName();
            }
        }
        [HarmonyPatch(typeof(Sign), nameof(Sign.Interact))]
        private static class Interact
        {
            private static bool Prefix(Sign __instance, Humanoid character, bool hold, bool alt, ref bool __result)
            {
                if (ChestInteractions.EditingSign == __instance) return true;
                var chest = ChestOf(__instance);
                if (!chest) return true;
                __result = chest.Interact(character, hold, alt);
                return false;
            }
        }
    }
}
