using HarmonyLib;
using UnityEngine;
using Overhaul.Performance;

namespace Overhaul.Visuals
{
    // Weather, first parts (client only, everything derived from the game's own weather, so all players see the same):
    // - Night light: at night the ambient light is lifted a quarter of the way toward the environment's day ambient, so the
    //   world reads at night without lamps everywhere (not in dungeons and other interiors).
    // - Biome border hold: the game picks weather and music from the biome under the camera; walking along a border flips
    //   it back and forth. The previous biome is kept while it is still within 4 m of the camera (8 points around).
    internal static partial class Weather
    {
        const float NightBoost = 0.25f, BorderHold = 4f;
        static BiomeSector held;

        static bool On => ClientVisuals.WeatherInEffect != ClientVisuals.Level.Off;

        [HarmonyPatch(typeof(EnvMan), nameof(EnvMan.SetEnv))]
        static class NightLight
        {
            [HarmonyPriority(Priority.Low)]
            static void Postfix(EnvSetup env, float dayInt, float nightInt)
            {
                if (!On || env == null || nightInt <= 0f) return;
                var player = Player.m_localPlayer;
                if (player && player.InInterior()) return;
                var lifted = Color.Lerp(RenderSettings.ambientLight, env.m_ambColorDay, nightInt * NightBoost);
                RenderSettings.ambientLight = lifted;
                Shader.SetGlobalColor("_AmbientColor", lifted);
            }
        }

        [HarmonyPatch(typeof(EnvMan), nameof(EnvMan.GetBiome))]
        static class BorderHoldPatch
        {
            static void Postfix(ref BiomeSector __result)
            {
                if (!On || __result == null || WorldGenerator.instance == null) { held = __result; return; }
                if (held == null || held.Biome == __result.Biome) { held = __result; return; }
                var camera = Utils.GetMainCamera();
                if (!camera) { held = __result; return; }
                var p = camera.transform.position;
                for (int i = 0; i < 8; i++)
                {
                    float a = i * Mathf.PI / 4f;
                    if (WorldGenerator.instance.GetBiome(p.x + Mathf.Cos(a) * BorderHold, p.z + Mathf.Sin(a) * BorderHold) == held.Biome)
                    {
                        __result = held; // still at the border: the weather stays as it was
                        return;
                    }
                }
                held = __result;
            }
        }
    }
}
