using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Performance
{
    // Smooth frames, grass drawing: every grass patch asks the engine each frame for the camera's transform and position
    // and its own transform. The camera position is read once per frame and each patch's transform once; the draw is the
    // game's own (same frustum test, same fade with distance, same instanced call).
    internal static partial class SmoothFrames
    {
        internal static bool LeanGrass = true;

        static int grassFrame = -1;
        static Vector3 grassEye;
        static readonly Dictionary<InstanceRenderer, Transform> grassTransforms = new Dictionary<InstanceRenderer, Transform>();

        [HarmonyPatch(typeof(InstanceRenderer), nameof(InstanceRenderer.CustomUpdate))]
        private static class LeanDraw
        {
            private static bool Prefix(InstanceRenderer __instance, float deltaTime)
            {
                if (!On(LeanGrass)) return true;
                var r = __instance;
                if (grassFrame != Time.frameCount)
                {
                    var camera = Utils.GetMainCamera();
                    if (!camera) return true;
                    grassFrame = Time.frameCount;
                    grassEye = camera.transform.position;
                }
                if (r.m_instanceCount == 0) return false;
                if (r.m_frustumCull)
                {
                    if (r.m_dirtyBounds) r.UpdateBounds();
                    if (!Utils.InsideMainCamera(r.m_bounds)) return false;
                }
                if (!r.m_useLod)
                {
                    Graphics.DrawMeshInstanced(r.m_mesh, 0, r.m_material, r.m_instances, r.m_instanceCount, null, r.m_shadowCasting, true, InstanceRenderer.s_layer);
                    return false;
                }
                if (!grassTransforms.TryGetValue(r, out var own) || !own) grassTransforms[r] = own = r.transform;
                var position = own.position;
                float distance = r.m_useXZLodDistance ? Utils.DistanceXZ(grassEye, position) : Vector3.Distance(grassEye, position);
                int wanted = (int)((1f - Utils.LerpStep(r.m_lodMinDistance, r.m_lodMaxDistance, distance)) * r.m_instanceCount);
                r.m_lodCount = Mathf.MoveTowards(r.m_lodCount, wanted, deltaTime * r.m_instanceCount);
                if (r.m_firstFrame)
                {
                    if (distance < r.m_lodMinDistance) r.m_lodCount = wanted;
                    r.m_firstFrame = false;
                }
                r.m_lodCount = Mathf.Min(r.m_lodCount, r.m_instanceCount);
                int count = (int)r.m_lodCount;
                if (count > 0) Graphics.DrawMeshInstanced(r.m_mesh, 0, r.m_material, r.m_instances, count, null, r.m_shadowCasting, true, InstanceRenderer.s_layer);
                return false;
            }
        }

        [HarmonyPatch(typeof(InstanceRenderer), "OnDisable")]
        private static class ForgetGrassTransform { private static void Postfix(InstanceRenderer __instance) => grassTransforms.Remove(__instance); }
    }
}
