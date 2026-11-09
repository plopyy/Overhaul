using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Overhaul.Performance;
using Overhaul.Utility;

namespace Overhaul.Visuals
{
    // Far view air: the game's fog hides the land about a kilometre out, so while the far view is on the fog the game sets
    // each frame (EnvMan.SetEnv) is thinned to GameThin of itself and its linear fog pushed out, and a full-screen pass
    // (FarHaze shader, after the world and the water, so far sea pales too) adds a haze in the game's fog colour lightened by
    // HazeLight toward white, from HazeStart on (built up over 300 m), FarRate of the game's fog density, thinning with
    // height (base 30 m, scale 400 m). Compared in game with the other far view mods' air (Run-GameTest -IvestMods).
    // Rain, snow and foggy weather thicken the game's fog, so the haze with it. Kept as the game's in the Mistlands (short
    // view on purpose) and under water; the change in and out is eased over a few seconds.
    internal static partial class FarView
    {
        const float GameThin = 0.01f, FarRate = 0.03f, HazeStart = 80f, HazeLight = 0.25f, HazeBase = 30f, HazeHeight = 400f;
        static Shader farHazeShader;
        static Material hazeMaterial;
        static CommandBuffer hazeBuffer;
        static Camera hazeCamera;
        static float gameDensity, airMix, gameStart, gameEnd;
        static bool airWanted;
        internal static bool DebugNoHaze; // tests: the game's fog stays thinned but our haze is not drawn
        static float nextAirLog;

        [HarmonyPatch(typeof(EnvMan), nameof(EnvMan.SetEnv))]
        static class SetEnvAir
        {
            static void Postfix()
            {
                gameDensity = RenderSettings.fogDensity;
                if (gameEnd <= 0f) { gameStart = RenderSettings.fogStartDistance; gameEnd = RenderSettings.fogEndDistance; }
                if (airMix > 0f)
                {
                    RenderSettings.fogDensity = gameDensity * Mathf.Lerp(1f, GameThin, airMix);
                    // The game's shaders also fade to the fog colour by the linear fog distances: pushed out as far as the far
                    // view reaches.
                    RenderSettings.fogEndDistance = Mathf.Lerp(gameEnd, Mathf.Max(gameEnd, 20000f), airMix);
                    RenderSettings.fogStartDistance = Mathf.Lerp(gameStart, Mathf.Max(gameStart, 2000f), airMix);
                }
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
            if (Time.time >= nextAirLog) { nextAirLog = Time.time + 30f; Log.LogInfo("[FarView] air: game fog density " + gameDensity.ToString("0.00000") + ", " + RenderSettings.fogMode + ", linear " + gameStart + "-" + gameEnd + " now " + RenderSettings.fogStartDistance + "-" + RenderSettings.fogEndDistance + ", mix " + airMix.ToString("0.00")); }

            if (!hazeMaterial) { hazeMaterial = new Material(farHazeShader); LogSky(camera); }
            if (hazeCamera != camera) { DetachHaze(); AttachHaze(camera); }

            var t = camera.transform;
            float up = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad), right = up * camera.aspect;
            hazeMaterial.SetVector("_HazeCorner", t.forward - t.right * right - t.up * up);
            hazeMaterial.SetVector("_HazeRight", t.right * (2f * right));
            hazeMaterial.SetVector("_HazeUp", t.up * (2f * up));
            // Lighter than the game's fog colour: distant land fades toward the bright horizon, not into a dark wall.
            float dayLight = EnvMan.instance ? Mathf.Clamp01((SunDirection().y + 0.05f) / 0.15f) : 1f; // by night no lightening toward white
            hazeMaterial.SetColor("_HazeColor", Color.Lerp(RenderSettings.fogColor, Color.white, HazeLight * dayLight));
            var env = EnvMan.instance;
            hazeMaterial.SetColor("_HazeSunColor", env ? env.GetSunFogColor() : RenderSettings.fogColor);
            hazeMaterial.SetVector("_HazeSunDir", env ? SunDirection() : Vector3.up);
            hazeMaterial.SetFloat("_GameDensity", gameDensity);
            hazeMaterial.SetFloat("_Start", HazeStart);
            hazeMaterial.SetFloat("_FarRate", FarRate);
            hazeMaterial.SetFloat("_HazeBase", HazeBase);
            hazeMaterial.SetFloat("_HazeHeight", HazeHeight);
            hazeMaterial.SetFloat("_HazeStrength", DebugNoHaze ? 0f : airMix * (1f - Weather.AerialAmount)); // by day the weather's aerial perspective takes over
        }

        // Diagnosis (the far land vanished behind something sky coloured): the camera's reach and what draws the sky.
        static void LogSky(Camera camera)
        {
            Log.LogInfo("[FarView] air: camera far plane " + camera.farClipPlane + ", clear " + camera.clearFlags + ", path " + camera.actualRenderingPath + ", skybox " + (RenderSettings.skybox ? RenderSettings.skybox.shader.name : "none"));
            foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                var m = r.sharedMaterial;
                if (!m || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                string n = (r.name + " " + m.name + " " + m.shader.name).ToLowerInvariant();
                if (n.Contains("sky") || n.Contains("cloud") || n.Contains("dome") || r.bounds.extents.magnitude > 1500f)
                    Log.LogInfo("[FarView] air: big/sky renderer " + r.name + " (" + m.shader.name + ", queue " + m.renderQueue + ", layer " + r.gameObject.layer + ") extents " + r.bounds.extents + " scale " + r.transform.lossyScale);
            }
        }

        static void AttachHaze(Camera camera)
        {
            camera.depthTextureMode |= DepthTextureMode.Depth;
            hazeBuffer = new CommandBuffer { name = "Overhaul far haze" };
            hazeBuffer.DrawProcedural(Matrix4x4.identity, hazeMaterial, 0, MeshTopology.Triangles, 3);
            camera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, hazeBuffer);
            hazeCamera = camera;
        }

        static void DetachHaze()
        {
            if (hazeCamera && hazeBuffer != null) hazeCamera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, hazeBuffer);
            hazeBuffer?.Release();
            hazeBuffer = null; hazeCamera = null;
        }

        static void StopAir()
        {
            DetachHaze();
            airMix = 0f;
            if (gameDensity > 0f) RenderSettings.fogDensity = gameDensity;
            if (gameEnd > 0f) { RenderSettings.fogStartDistance = gameStart; RenderSettings.fogEndDistance = gameEnd; }
            if (hazeMaterial) Object.Destroy(hazeMaterial);
            hazeMaterial = null;
        }
    }
}
