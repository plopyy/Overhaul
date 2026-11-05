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
                Diagnose(__instance, camera);
                // Creatures gone from the game or from the bars are forgotten.
                Gone.Clear();
                foreach (var character in Sights.Keys) if (!character || !__instance.m_huds.ContainsKey(character)) Gone.Add(character);
                foreach (var character in Gone) Sights.Remove(character);
            }
        }

        // TEMPORARY DIAGNOSTIC (a player sees no health bars): every 10 s, what the game's bars are doing on this machine,
        // written to the log (bars made, shown, hidden here, the root and canvas, the first bar's place on the screen).
        private static float nextDiagnostic;
        private static void Diagnose(EnemyHud hud, Camera camera)
        {
            if (Time.time < nextDiagnostic) return;
            nextDiagnostic = Time.time + 10f;
            int shown = 0, hidden = 0; string first = "none";
            foreach (var pair in hud.m_huds)
            {
                var gui = pair.Value?.m_gui;
                if (gui && gui.activeSelf) shown++;
                if (pair.Key && Sights.TryGetValue(pair.Key, out var s) && s.Hidden) hidden++;
                if (first == "none" && gui && pair.Key)
                {
                    var rt = gui.transform as RectTransform; var canvas = gui.GetComponentInParent<Canvas>();
                    first = pair.Key.m_name + " active=" + gui.activeSelf + "/" + gui.activeInHierarchy + " pos=" + gui.transform.position.ToString("F0") + " screen=" + Screen.width + "x" + Screen.height
                        + " scale=" + gui.transform.lossyScale.ToString("F2") + " alpha=" + (gui.GetComponent<CanvasGroup>() ? gui.GetComponent<CanvasGroup>().alpha.ToString("F2") : "-")
                        + " canvas=" + (canvas ? canvas.name + " enabled=" + canvas.enabled + " mode=" + canvas.renderMode + " order=" + canvas.sortingOrder + " scale=" + canvas.scaleFactor.ToString("F2") + " cam=" + (canvas.worldCamera ? canvas.worldCamera.name : "-") : "none")
                        + " rect=" + (rt ? rt.rect.size.ToString("F0") : "-");
                }
            }
            var root = hud.m_hudRoot;
            Utility.Log.LogInfo("EnemyHud diagnostic: bars " + hud.m_huds.Count + ", shown " + shown + ", hidden behind the world " + hidden
                + ", root " + (root ? root.name + " active=" + root.activeSelf + "/" + root.activeInHierarchy + " children=" + root.transform.childCount : "missing")
                + ", base " + (hud.m_baseHud ? hud.m_baseHud.name : "missing") + "/" + (hud.m_baseHudPlayer ? hud.m_baseHudPlayer.name : "missing")
                + ", range " + hud.m_maxShowDistance + ", camera " + camera.name + ", hud hidden " + Hud.IsUserHidden() + ", first: " + first);
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
