using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;

namespace Overhaul.Storage
{
    internal static class TroughPresentation
    {
        internal static void CreateIcon(GameObject prefab)
        {
            // Render the approved full visual only; no chest, inventory or network components.
            var visual = prefab.transform.Find("Overhaul trough").gameObject;
            var icon = RenderManager.Instance.Render(new RenderManager.RenderRequest(visual)
            {
                Width = 128, Height = 128, Rotation = RenderManager.IsometricRotation,
                DistanceMultiplier = 1.1f, ParticleSimulationTime = -1f
            });
            if (icon) prefab.GetComponent<Piece>().m_icon = icon;
        }

        internal static void ShowRange(GameObject ghost, GameObject markerSource)
        {
            if (!ghost || !ghost.GetComponent<TroughContainer>() || !markerSource) return;
            const string markerName = "Overhaul feeding range";
            if (ghost.transform.Find(markerName)) return;
            var marker = Object.Instantiate(markerSource, ghost.transform, false);
            marker.name = markerName;
            marker.transform.localPosition = Vector3.zero;
            marker.transform.localRotation = Quaternion.identity;
            marker.transform.localScale = Vector3.one;
            var circle = marker.GetComponent<CircleProjector>();
            if (circle) circle.m_radius = FeedingTrough.FeedRange;
            marker.SetActive(true);
        }

        [HarmonyPatch(typeof(Player), "SetupPlacementGhost")]
        internal static class Placement
        {
            private static void Postfix(Player __instance)
            {
                var ghost = __instance.m_placementGhost;
                if (!ghost || !ghost.GetComponent<TroughContainer>()) return;
                var station = PrefabManager.Instance.GetPrefab("piece_workbench")?.GetComponent<CraftingStation>();
                ShowRange(ghost, station ? station.m_areaMarker : null);
            }
        }
    }
}
