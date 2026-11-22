using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Visuals
{
    // Building view while the hammer is in hand (user, 2026-11-22, replaces the forced first person of 2026-11-21): the
    // camera keeps its distance, but the local character is not drawn on this client (other players see it as usual), so
    // nothing blocks the view. The wheel zooms from the game's first person (distance 0) to a bit farther than the usual
    // maximum. Putting the hammer away gives back the distance the player had before taking it. The zoom sensitivity is
    // left alone (other camera patches save and restore it; one kept a 0 for good once, 2.4.0.645-647).
    internal static class BuildFirstPerson
    {
        const float ExtraZoomOut = 1.5f;   // max distance x1.5 while building
        static bool active;
        static float savedDistance;
        static readonly List<Renderer> hidden = new List<Renderer>();

        // The circlet turns its own meshes on and off every frame (under a helmet): it asks here so it stays off too.
        internal static bool Hides(VisEquipment v) => active && v && Player.m_localPlayer && v.gameObject == Player.m_localPlayer.gameObject;

        static bool Wanted(Player p) => p && p == Player.m_localPlayer && !p.IsDead() && !p.InIntro() && !p.IsAttached()
            && !p.InBed() && Storage.RelocationClient.HasHammer(p);

        [HarmonyPatch(typeof(GameCamera), "UpdateCamera")]
        static class Camera
        {
            static void Prefix(GameCamera __instance, out (float min, float max, float boat, bool tilt) __state)
            {
                __state = (__instance.m_minDistance, __instance.m_maxDistance, __instance.m_maxDistanceBoat, __instance.m_smoothYTilt);
                var player = Player.m_localPlayer;
                bool want = !__instance.m_freeFly && Wanted(player);
                if (want && !active) { active = true; savedDistance = __instance.m_distance; }
                else if (!want && active) { active = false; __instance.m_distance = savedDistance; }
                if (active)
                {
                    __instance.m_minDistance = 0f;
                    __instance.m_maxDistance *= ExtraZoomOut; __instance.m_maxDistanceBoat *= ExtraZoomOut;
                    // Looking up, the game pulls the camera towards 1.5 m (smooth Y tilt): close in, it jumped behind the head.
                    if (__instance.m_distance < 1.5f) __instance.m_smoothYTilt = false;
                }
                Hide(player, active);
            }
            static void Finalizer(GameCamera __instance, (float min, float max, float boat, bool tilt) __state)
            {
                __instance.m_minDistance = __state.min; __instance.m_maxDistance = __state.max; __instance.m_maxDistanceBoat = __state.boat; __instance.m_smoothYTilt = __state.tilt;
            }
        }

        // Every renderer of the local character (body, gear, held items, which change with equipment) skipped while
        // building; the ones turned off are turned back on after. Shadows go with them.
        static void Hide(Player player, bool hide)
        {
            if (hide && player)
            {
                Off(player.gameObject);
                // The thief raven (catalyst on a jewel) is its own object flying with the character.
                var raven = Rarity.RavenThief.Shown(player); if (raven) Off(raven);
            }
            else Restore();
        }
        static void Off(GameObject go)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                if (!r.forceRenderingOff) { r.forceRenderingOff = true; hidden.Add(r); }
        }
        static void Restore()
        {
            foreach (var r in hidden) if (r) r.forceRenderingOff = false;
            hidden.Clear();
        }
    }
}
