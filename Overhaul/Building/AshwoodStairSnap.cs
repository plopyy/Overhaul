using HarmonyLib;
using UnityEngine;

namespace Overhaul.Building
{
    // The ashwood stair is 1 m wide, but its corner snap points sit at x 0 and 0.5 instead of its edges (-0.5 and 0.5), so two
    // of them could not be set side by side. The corners are moved to the edges, top and bottom; the centre points stay.
    internal static class AshwoodStairSnap
    {
        private const string Stair = "ashwood_stair";

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class Fix
        {
            private static void Postfix(ZNetScene __instance)
            {
                var prefab = __instance.GetPrefab(Stair);
                if (!prefab) return;
                var root = prefab.transform;
                foreach (var child in prefab.GetComponentsInChildren<Transform>(true))
                {
                    if (!child.CompareTag("snappoint") || child.name.Contains("center")) continue;
                    var position = root.InverseTransformPoint(child.position);
                    if (child.name.EndsWith(" 1")) position.x = -.5f;
                    else if (child.name.EndsWith(" 2")) position.x = .5f;
                    else continue;
                    child.position = root.TransformPoint(position);
                }
            }
        }
    }
}
