using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Overhaul.Performance;
using Overhaul.Utility;

namespace Overhaul.Visuals
{
    // Far view air: the game's fog hides the land about a kilometre out, so while the far view is on the fog the game sets
    // each frame (EnvMan.SetEnv) is thinned to GameThin of itself, and a full-screen pass (FarHaze shader, after the opaque
    // world, before water and transparents) puts back a haze in the game's fog colours: as thick as the game's fog times
    // NearHaze within NearDistance of the camera, FarRate of it farther, thinning with height (base 30 m, scale 400 m).
    // Rain, snow and foggy weather thicken the game's fog, so the haze with it. Kept as the game's in the Mistlands (short
    // view on purpose) and under water; the change in and out is eased over a few seconds.
    internal static partial class FarView
    {
        const float GameThin = 0.12f, NearHaze = 0.6f, NearDistance = 180f, FarRate = 0.17f, HazeBase = 30f, HazeHeight = 400f;
        static Shader farHazeShader;
        static Material hazeMaterial;
        static CommandBuffer hazeBuffer;
        static Camera hazeCamera;
        static float gameDensity, airMix;
        static bool airWanted, fogModeLogged;

        [HarmonyPatch(typeof(EnvMan), nameof(EnvMan.SetEnv))]
        static class SetEnvAir
        {
            static void Postfix()
            {
                gameDensity = RenderSettings.fogDensity;
                if (airMix > 0f) RenderSettings.fogDensity = gameDensity * Mathf.Lerp(1f, GameThin, airMix);
            }
        }

        static bool AirFits(Camera camera)
        {
            if (rings.Length == 0 || !farHazeShader || !farHazeShader.isSupported) return false;
            if (ZoneSystem.instance && camera.transform.position.y < ZoneSystem.instance.m_waterLevel) return false;
            var player = Player.m_localPlayer;
            return !player || player.GetCurrentBiome() != Heightmap.Biome.Mistlands;
        }

        static void TickAir()
        {
            var camera = Utils.GetMainCamera();
            airWanted = camera && AirFits(camera);
            airMix = Mathf.MoveTowards(airMix, airWanted ? 1f : 0f, Time.deltaTime / 3f);
            if (airMix <= 0f) { DetachHaze(); return; }
            if (!fogModeLogged) { fogModeLogged = true; Log.LogInfo("[FarView] game fog mode " + RenderSettings.fogMode); }

            if (!hazeMaterial) hazeMaterial = new Material(farHazeShader);
            if (hazeCamera != camera) { DetachHaze(); AttachHaze(camera); }

            var t = camera.transform;
            float up = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad), right = up * camera.aspect;
            hazeMaterial.SetVector("_HazeCorner", t.forward - t.right * right - t.up * up);
            hazeMaterial.SetVector("_HazeRight", t.right * (2f * right));
            hazeMaterial.SetVector("_HazeUp", t.up * (2f * up));
            hazeMaterial.SetColor("_HazeColor", RenderSettings.fogColor);
            var env = EnvMan.instance;
            hazeMaterial.SetColor("_HazeSunColor", env ? env.GetSunFogColor() : RenderSettings.fogColor);
            hazeMaterial.SetVector("_HazeSunDir", env ? SunDirection() : Vector3.up);
            hazeMaterial.SetFloat("_GameDensity", gameDensity);
            hazeMaterial.SetFloat("_GameThin", gameDensity * Mathf.Lerp(1f, GameThin, airMix));
            hazeMaterial.SetFloat("_NearHaze", NearHaze);
            hazeMaterial.SetFloat("_NearDistance", NearDistance);
            hazeMaterial.SetFloat("_FarRate", FarRate);
            hazeMaterial.SetFloat("_HazeBase", HazeBase);
            hazeMaterial.SetFloat("_HazeHeight", HazeHeight);
            hazeMaterial.SetFloat("_HazeStrength", airMix);
        }

        static void AttachHaze(Camera camera)
        {
            camera.depthTextureMode |= DepthTextureMode.Depth;
            hazeBuffer = new CommandBuffer { name = "Overhaul far haze" };
            hazeBuffer.DrawProcedural(Matrix4x4.identity, hazeMaterial, 0, MeshTopology.Triangles, 3);
            camera.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, hazeBuffer);
            hazeCamera = camera;
        }

        static void DetachHaze()
        {
            if (hazeCamera && hazeBuffer != null) hazeCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, hazeBuffer);
            hazeBuffer?.Release();
            hazeBuffer = null; hazeCamera = null;
        }

        static void StopAir()
        {
            DetachHaze();
            airMix = 0f;
            if (gameDensity > 0f) RenderSettings.fogDensity = gameDensity;
            if (hazeMaterial) Object.Destroy(hazeMaterial);
            hazeMaterial = null;
        }
    }
}
