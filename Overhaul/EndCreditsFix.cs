using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Overhaul
{
    // Valheim 1.0.17 ("Fixed credits being hidden") can leave the end credits panel of a loaded
    // Valkyrie_End object open in the background. Its UI group (ContactInfo, priority 1) then
    // outranks every priority-0 window, which Valheim greys out (the portal window got stuck).
    // Keep the panel closed unless the end credits are actually playing.
    [HarmonyPatch(typeof(EndCredits), "Update")]
    internal static class EndCreditsFix
    {
        // Looked up by name: the field may change between game versions.
        private static readonly FieldInfo QueueLogout = AccessTools.Field(typeof(EndCredits), "m_queueLogout");
        private static readonly FieldInfo QueueCredits = AccessTools.Field(typeof(EndCredits), "m_queueCredits");

        private static void Postfix(EndCredits __instance)
        {
            var panel = __instance.m_creditsPanel;
            if (!panel || !panel.activeSelf || CinematicsManager.IsStartedPlaying()) return;
            if (QueueLogout != null && (bool)QueueLogout.GetValue(__instance)) return;
            if (QueueCredits != null && (bool)QueueCredits.GetValue(__instance)) return;
            panel.SetActive(false);
        }
    }
}
