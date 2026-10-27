using HarmonyLib;
using UnityEngine;

namespace Overhaul
{
    // A creature's health bar only shows while the creature can be seen: after the game places its bars, the bar of a
    // creature hidden behind the world (terrain, rocks, trees, buildings) is turned off for this frame. Lines are traced
    // from the camera to the creature's head and centre: seen if either is clear. Boss bars (top of the screen) and
    // players' bars are left alone.
    internal static class EnemyHudOcclusion
    {
        private static int mask;
        // What can hide a creature: the world's solid layers, never characters.
        private static int Mask => mask != 0 ? mask : mask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");

        [HarmonyPatch(typeof(EnemyHud), nameof(EnemyHud.UpdateHuds))]
        [HarmonyPriority(Priority.Low)]
        private static class Hide
        {
            private static void Postfix(EnemyHud __instance)
            {
                var camera = Utils.GetMainCamera();
                if (!camera || __instance.m_huds == null) return;
                Vector3 eye = camera.transform.position;
                foreach (var pair in __instance.m_huds)
                {
                    var character = pair.Key; var hud = pair.Value;
                    if (!character || hud?.m_gui == null || !hud.m_gui.activeSelf || character.IsBoss() || character.IsPlayer()) continue;
                    if (!Seen(eye, Head(character)) && !Seen(eye, character.GetCenterPoint())) hud.m_gui.SetActive(false);
                    else if (Time.time >= nextLog) Probe(eye, character);
                }
                if (Time.time >= nextLog) nextLog = Time.time + 2f;
            }
        }

        // TEMPORARY (bars still shown through rocks in the Mistlands): what the lines to a creature whose bar stays shown
        // hit first, every 2 s, in the client log.
        private static float nextLog;
        private static void Probe(Vector3 eye, Character character)
        {
            string Line(Vector3 point)
            {
                Vector3 to = point - eye;
                var hits = Physics.RaycastAll(eye, to.normalized, to.magnitude + 1f, ~0, QueryTriggerInteraction.Ignore);
                System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                return to.magnitude.ToString("F1") + " m: " + string.Join(", ", System.Linq.Enumerable.Select(System.Linq.Enumerable.Take(hits, 4),
                    h => h.collider.name + "/" + LayerMask.LayerToName(h.collider.gameObject.layer) + "@" + h.distance.ToString("F1")));
            }
            Utility.Log.LogInfo("HUD occlusion " + character.m_name + " head " + Line(Head(character)) + " | centre " + Line(character.GetCenterPoint()));
        }

        // The head, or half a metre above the centre for creatures without a head bone (GetHeadPoint throws for them).
        private static Vector3 Head(Character character) => character.m_head ? character.GetHeadPoint() : character.GetCenterPoint() + Vector3.up * .5f;

        // Clear line from the eye to a point, the last half metre left out (the creature's own surroundings).
        private static bool Seen(Vector3 eye, Vector3 point)
        {
            Vector3 to = point - eye; float distance = to.magnitude - .5f;
            return distance <= 0 || !Physics.Raycast(eye, to / to.magnitude, distance, Mask, QueryTriggerInteraction.Ignore);
        }
    }
}
