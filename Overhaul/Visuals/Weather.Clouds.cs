using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Overhaul.Performance;
using Overhaul.Utility;

namespace Overhaul.Visuals
{
    // Weather, clouds (the Clouds checkbox of the Weather page): our own raymarched cloud layer, 1500 to 3500 m, in place of
    // the game's painted cloud dome (its opacity set to 0 while ours show; its rain clouds kept for now). Drawn at half
    // resolution each frame (Hidden/Overhaul/Clouds pass 0) and laid over the sky pixels after our sky (pass 1).
    // Cover follows the game's weather: rain clouds alpha and foggy air thicken and darken it; in fair weather each game day
    // has its own cover between 0.30 and 0.55 from the world seed (same for every player). Drift with the game's wind.
    internal static partial class Weather
    {
        const float CloudBottom = 1500f, CloudTop = 3500f, CloudScale = 1f / 2600f;
        static Shader cloudShader;
        static Material cloudMaterial;
        static RenderTexture cloudTarget;
        static CommandBuffer cloudBuffer;
        static Camera cloudCamera;
        static Vector3 cloudWind;
        static float cloudMix, cover = 0.4f, darkness;

        static bool CloudsOn => On && ClientVisuals.Clouds && cloudMaterial;

        // The game's cloud dome is hidden while ours are shown.
        [HarmonyPatch(typeof(EnvMan), nameof(EnvMan.SetEnv))]
        static class HideGameClouds
        {
            [HarmonyPriority(Priority.Low)]
            static void Postfix(EnvMan __instance)
            {
                if (cloudMix > 0f && __instance.m_clouds) __instance.m_clouds.material.SetFloat("_Opacity", 0f);
            }
        }

        static void TickClouds(Camera camera, Vector3 sun)
        {
            if (!On || !ClientVisuals.Clouds) { StopClouds(); return; }
            if (!cloudMaterial && !PrepareClouds()) return;
            var env = EnvMan.instance;
            var current = env.GetCurrentEnvironment();
            var player = Player.m_localPlayer;
            bool inside = player && player.InInterior();
            cloudMix = Mathf.MoveTowards(cloudMix, inside ? 0f : 1f, Time.deltaTime / 2f);
            if (cloudMix <= 0f) { DetachClouds(); return; }

            // Cover and darkness from the weather, eased so a change of weather rolls in.
            float rain = current != null ? current.m_rainCloudAlpha : 0f;
            float fog = RenderSettings.fogDensity > 0f ? FogOfGame() : 0f;
            int day = (int)(ZNet.instance ? ZNet.instance.GetTimeSeconds() / Mathf.Max(1L, env.m_dayLengthSec) : 0);
            float fair = 0.30f + 0.25f * Mathf.Repeat(Mathf.Sin((WorldGenerator.instance != null ? WorldGenerator.instance.GetSeed() : 0) * 0.001f + day * 12.9898f) * 43758.5453f, 1f);
            float wantCover = Mathf.Clamp01(Mathf.Lerp(fair, 0.92f, rain) + fog * 8f);
            float wantDark = Mathf.Clamp01(rain * 0.75f + fog * 5f);
            cover = Mathf.MoveTowards(cover, wantCover, Time.deltaTime * 0.02f);
            darkness = Mathf.MoveTowards(darkness, wantDark, Time.deltaTime * 0.02f);

            var wind = env.GetWindDir(); float speed = 10f + 15f * env.GetWindIntensity();
            cloudWind -= new Vector3(wind.x, 0f, wind.z) * speed * Time.deltaTime;
            var origin = camera.transform.position;

            int w = Mathf.Max(64, camera.pixelWidth / 2), h = Mathf.Max(64, camera.pixelHeight / 2);
            if (!cloudTarget || cloudTarget.width != w || cloudTarget.height != h)
            {
                if (cloudTarget) cloudTarget.Release();
                cloudTarget = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf) { name = "Overhaul clouds", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                cloudTarget.Create();
            }
            var t = camera.transform;
            float up = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad), right = up * camera.aspect;
            cloudMaterial.SetVector("_CloudCorner", t.forward - t.right * right - t.up * up);
            cloudMaterial.SetVector("_CloudRight", t.right * (2f * right));
            cloudMaterial.SetVector("_CloudUp", t.up * (2f * up));
            cloudMaterial.SetVector("_CamPos", origin);
            // The wind offset, kept relative to the camera so the layer stays put under a moving player.
            cloudMaterial.SetVector("_CloudWind", cloudWind + new Vector3(origin.x, 0f, origin.z));
            cloudMaterial.SetVector("_SunDir", sun.y > -0.1f ? sun : -sun);
            var light = env.m_dirLight;
            var sunColor = light ? light.color * light.intensity : Color.white;
            cloudMaterial.SetVector("_SunColor", (Vector4)(sunColor * 1.6f));
            var amb = RenderSettings.ambientLight;
            cloudMaterial.SetVector("_AmbientTop", (Vector4)(amb * 1.3f + RenderSettings.fogColor * 0.4f));
            cloudMaterial.SetVector("_AmbientBottom", (Vector4)(amb * 0.7f));
            cloudMaterial.SetFloat("_CloudBottom", CloudBottom);
            cloudMaterial.SetFloat("_CloudTop", CloudTop);
            cloudMaterial.SetFloat("_Cover", cover);
            cloudMaterial.SetFloat("_Density", 0.012f);
            cloudMaterial.SetFloat("_Scale", CloudScale);
            cloudMaterial.SetFloat("_Darkness", darkness);
            cloudMaterial.SetFloat("_Steps", ClientVisuals.WeatherInEffect == ClientVisuals.Level.High ? 64 : ClientVisuals.WeatherInEffect == ClientVisuals.Level.Medium ? 48 : 32);
            cloudMaterial.SetFloat("_Fade", cloudMix);
            Graphics.Blit(null, cloudTarget, cloudMaterial, 0);
            cloudMaterial.SetTexture("_CloudTex", cloudTarget);
            if (cloudCamera != camera) { DetachClouds(); AttachClouds(camera); }
        }

        static float FogOfGame() => Mathf.Max(0f, (EnvMan.instance.GetCurrentEnvironment()?.m_fogDensityDay ?? 0f) - 0.01f);

        static bool PrepareClouds()
        {
            if (cloudShader) return cloudMaterial;
            try
            {
                var bundle = EmbeddedAssets.LoadBundle("Overhaul.Assets.overhaul_weather");
                cloudShader = bundle.LoadAsset<Shader>("Assets/OverhaulWeather/OverhaulClouds.shader");
                if (!skyShader) skyShader = bundle.LoadAsset<Shader>("Assets/OverhaulWeather/OverhaulSky.shader");
                bundle.Unload(false);
                if (!cloudShader || !cloudShader.isSupported) throw new Exception("cloud shader not supported here");
            }
            catch (Exception e) { Log.LogWarning("[Weather] clouds off: " + e.Message); cloudShader = Shader.Find("Hidden/InternalErrorShader"); return false; }
            cloudMaterial = new Material(cloudShader);
            return true;
        }

        static void AttachClouds(Camera camera)
        {
            camera.depthTextureMode |= DepthTextureMode.Depth;
            cloudBuffer = new CommandBuffer { name = "Overhaul clouds" };
            cloudBuffer.DrawProcedural(Matrix4x4.identity, cloudMaterial, 1, MeshTopology.Triangles, 3);
            camera.AddCommandBuffer(CameraEvent.AfterSkybox, cloudBuffer);
            cloudCamera = camera;
        }

        static void DetachClouds()
        {
            if (cloudCamera && cloudBuffer != null) cloudCamera.RemoveCommandBuffer(CameraEvent.AfterSkybox, cloudBuffer);
            cloudBuffer?.Release();
            cloudBuffer = null; cloudCamera = null;
        }

        static void StopClouds()
        {
            DetachClouds();
            cloudMix = 0f;
        }
    }
}
