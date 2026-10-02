using HarmonyLib;
using Overhaul.Utility;
using UnityEngine;

namespace Overhaul
{
    internal static class ShoulderCamera
    {
        static readonly int AimMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece",
            "piece_nonsolid", "terrain", "character", "character_net", "character_ghost", "hitbox",
            "character_noenv", "vehicle");

        internal static float Distance(float zoom)
        {
            float value = OverhaulConfig.RightShoulderOffset?.Value ?? 0f;
            return float.IsNaN(value) || float.IsInfinity(value) ? 0f :
                Mathf.Clamp(value, 0f, 1.5f) * Mathf.Clamp01(zoom);
        }

        static bool Active(GameCamera camera, Player player) => camera && player && player == Player.m_localPlayer
            && Distance(camera.m_distance) > 0f && !camera.m_freeFly && !player.InIntro()
            && !player.IsDead() && !player.InBed() && !player.IsAttached();

        internal static Vector3 Offset(Vector3 origin, Vector3 right, float distance, float radius, int mask)
        {
            // Check the sideways movement too, before the native camera sweeps backwards.
            if (Physics.CheckSphere(origin, radius, mask, QueryTriggerInteraction.Ignore)) return Vector3.zero;
            if (Physics.SphereCast(origin, radius, right, out var hit, distance, mask, QueryTriggerInteraction.Ignore))
                distance = Mathf.Max(0f, hit.distance - 0.02f);
            return right * distance;
        }

        [HarmonyPatch(typeof(GameCamera), "GetCameraOffset")]
        internal static class CameraOffset
        {
            static void Postfix(GameCamera __instance, Player player, ref Vector3 __result)
            {
                if (!Active(__instance, player)) return;
                __result += Offset(player.m_eye.position + __result, player.m_eye.right,
                    Distance(__instance.m_distance), Mathf.Max(0.05f, __instance.m_raycastWidth), __instance.m_blockCameraMask);
            }
        }

        internal static Vector3 Aim(Ray ray, Vector3 spawn, Transform owner, Vector3 fallback)
        {
            float nearest = 1000f;
            // Ignore the firing player's colliders without ignoring other characters.
            foreach (var hit in Physics.RaycastAll(ray, nearest, AimMask, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider.transform.IsChildOf(owner)) continue;
                if (hit.distance < nearest) nearest = hit.distance;
            }
            Vector3 delta = ray.GetPoint(nearest) - spawn;
            // A wall between camera and player must not make the shot turn backwards.
            return Vector3.Dot(delta, ray.direction) > 0.01f ? delta.normalized : fallback;
        }

        [HarmonyPatch(typeof(Attack), "GetProjectileSpawnPoint")]
        internal static class ProjectileAim
        {
            static void Postfix(Attack __instance, Vector3 spawnPoint, ref Vector3 aimDir)
            {
                var player = __instance.m_character as Player;
                var camera = GameCamera.instance;
                if (!Active(camera, player) || __instance.m_useCharacterFacing || !camera.m_camera) return;
                var ray = camera.m_camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
                aimDir = Aim(ray, spawnPoint, player.transform, aimDir);
            }
        }
    }
}
