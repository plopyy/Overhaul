using HarmonyLib;
using UnityEngine;

namespace Overhaul.Visuals
{
    // First-person view while the building hammer is in hand (user, 2026-11-21): a clear view to build. Taking the hammer
    // switches the camera to the game's own first-person mode (camera distance 0, eye position + m_fpsOffset, normally out
    // of the zoom's reach); putting it away gives back the distance the player had before. The wheel no longer zooms
    // meanwhile: the distance's bounds are both 0 (the zoom sensitivity is left alone: other camera patches save and
    // restore it, and one of them kept the 0 for good, no more zoom at all, 2.4.0.645-647). The local player's head bone is shrunk to nothing on this client only, so the head, hair, beard and
    // helmet (all on that bone) do not block the view; other players see the character as usual.
    internal static class BuildFirstPerson
    {
        static bool active;
        static float savedDistance;
        static Transform head;
        static Vector3 headScale;

        static bool Wanted(Player p) => p && p == Player.m_localPlayer && !p.IsDead() && !p.InIntro() && !p.IsAttached()
            && !p.InBed() && Storage.RelocationClient.HasHammer(p);

        [HarmonyPatch(typeof(GameCamera), "UpdateCamera")]
        static class Camera
        {
            static void Prefix(GameCamera __instance, out (float min, float max, float boat) __state)
            {
                __state = (__instance.m_minDistance, __instance.m_maxDistance, __instance.m_maxDistanceBoat);
                var player = Player.m_localPlayer;
                bool want = !__instance.m_freeFly && Wanted(player);
                if (want && !active) { active = true; savedDistance = __instance.m_distance; }
                else if (!want && active) { active = false; __instance.m_distance = savedDistance; }
                if (active)
                {
                    __instance.m_minDistance = __instance.m_maxDistance = __instance.m_maxDistanceBoat = 0f; __instance.m_distance = 0f;
                }
                Head(player, active);
            }
            static void Finalizer(GameCamera __instance, (float min, float max, float boat) __state)
            {
                __instance.m_minDistance = __state.min; __instance.m_maxDistance = __state.max; __instance.m_maxDistanceBoat = __state.boat;
            }
        }

        // The head bone (and everything attached under it) shrunk while in first person, restored after.
        static void Head(Player player, bool hide)
        {
            if (hide)
            {
                var bone = player ? player.GetComponentInChildren<Animator>()?.GetBoneTransform(HumanBodyBones.Head) : null;
                if (bone != head) { Restore(); head = bone; if (head) headScale = head.localScale; }
                if (head) head.localScale = Vector3.one * 0.0001f;
            }
            else Restore();
        }
        static void Restore()
        {
            if (head) head.localScale = headScale;
            head = null;
        }
    }
}
