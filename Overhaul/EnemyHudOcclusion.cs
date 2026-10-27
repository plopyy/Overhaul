using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Overhaul
{
    // A creature's health bar only shows while the creature can be seen: after the game places its bars, the bar of a
    // creature hidden behind the world (terrain, rocks, trees, buildings) is turned off. Lines are traced from the camera
    // to the creature's head and centre: seen if either is clear. Boss bars (top of the screen) and players' bars are left
    // alone. Cheap with many creatures: each one is checked again every Interval seconds (a random offset spreads them over
    // the frames), at most Budget checks a frame, and the last answer is kept in between.
    internal static class EnemyHudOcclusion
    {
        private const float Interval = 1f;
        private const int Budget = 8;
        private static int mask;
        // What can hide a creature: the world's solid layers, never characters.
        private static int Mask => mask != 0 ? mask : mask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");

        private sealed class Sight { internal bool Hidden; internal float Next; }
        private static readonly Dictionary<Character, Sight> Sights = new Dictionary<Character, Sight>();
        private static readonly List<Character> Gone = new List<Character>();

        [HarmonyPatch(typeof(EnemyHud), nameof(EnemyHud.UpdateHuds))]
        [HarmonyPriority(Priority.Low)]
        private static class Hide
        {
            private static void Postfix(EnemyHud __instance)
            {
                var camera = Utils.GetMainCamera();
                if (!camera || __instance.m_huds == null) return;
                Vector3 eye = camera.transform.position; float now = Time.time; int checks = 0;
                foreach (var pair in __instance.m_huds)
                {
                    var character = pair.Key; var hud = pair.Value;
                    if (!character || hud?.m_gui == null || !hud.m_gui.activeSelf || character.IsBoss() || character.IsPlayer()) continue;
                    if (!Sights.TryGetValue(character, out var sight)) Sights[character] = sight = new Sight { Next = now };
                    if (now >= sight.Next && checks < Budget)
                    {
                        sight.Hidden = !Seen(eye, Head(character)) && !Seen(eye, character.GetCenterPoint());
                        sight.Next = now + Interval * Random.Range(.8f, 1.2f); checks++;
                    }
                    if (sight.Hidden) hud.m_gui.SetActive(false);
                }
                // Creatures gone from the game or from the bars are forgotten.
                Gone.Clear();
                foreach (var character in Sights.Keys) if (!character || !__instance.m_huds.ContainsKey(character)) Gone.Add(character);
                foreach (var character in Gone) Sights.Remove(character);
            }
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
