using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Overhaul.Performance;
using Overhaul.Utility;

namespace Overhaul.Visuals
{
    // Weather, clouds (the Clouds checkbox of the Weather page): HDRP's volumetric clouds ported to the built-in pipeline
    // (shader Hidden/Overhaul/Clouds + ThirdParty/HDRP-VolumetricClouds, Unity Companion License), in place of the game's
    // painted cloud dome (its opacity set to 0 while ours show; its rain clouds kept for now). Traced at half resolution
    // each frame and laid over the sky pixels after our sky. The weather picks a mix of HDRP's four presets: fair days
    // between Sparse and Cloudy (per game day, from the world seed, same for every player), Overcast in fog, Stormy in rain.
    // The layer drifts with the game's wind.
    internal static partial class Weather
    {
        const float CloudSunScale = 6f, CloudAmbientScale = 1.5f;
        static Shader cloudShader;
        static Material cloudMaterial;
        static RenderTexture cloudTarget;
        static CommandBuffer cloudBuffer;
        static Camera cloudCamera;
        static Vector2 cloudWind;
        static float cloudMix;
        static Vector4 presetMix = new Vector4(1f, 0f, 0f, 0f);
        static Texture2D cloudLut;
        static readonly Color[] lutPixels = new Color[64];
        static Texture3D worley, perlin;

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
            if (cloudMix <= 0f) { DetachClouds(); HideFogClouds(false); return; }

            // Weather regime as weights of HDRP's four presets: fair days between Sparse and Cloudy (per game day, from the
            // world seed), Overcast in foggy air, Stormy in rain. Eased so a change of weather rolls in over about a minute.
            float rain = current != null ? current.m_rainCloudAlpha : 0f;
            float fog = Mathf.Clamp01(FogOfGame() * 40f);
            int day = (int)(ZNet.instance ? ZNet.instance.GetTimeSeconds() / Mathf.Max(1L, env.m_dayLengthSec) : 0);
            float broken = Mathf.Repeat(Mathf.Sin((WorldGenerator.instance != null ? WorldGenerator.instance.GetSeed() : 0) * 0.001f + day * 12.9898f) * 43758.5453f, 1f);
            var want = new Vector4((1f - broken) * (1f - rain) * (1f - fog), broken * (1f - rain) * (1f - fog), fog * (1f - rain), rain);
            // Eased so a change of weather rolls in, at once when the weather is forced (debug environment, tests).
            presetMix = string.IsNullOrEmpty(env.m_debugEnv) ? Vector4.MoveTowards(presetMix, want, Time.deltaTime * 0.02f) : want;
            ApplyPreset(presetMix);
            // Clear nights: thinner low layer, so the stars show between dark bands (fair weather only).
            float nightSky = Mathf.Clamp01((-sun.y + 0.02f) / 0.12f) * (presetMix.x + presetMix.y) / Mathf.Max(1e-3f, presetMix.x + presetMix.y + presetMix.z + presetMix.w);
            cloudMaterial.SetFloat("_Coverage", Mathf.Lerp(0.9f, 0.72f, nightSky));

            var wind = env.GetWindDir(); float speed = 10f + 15f * env.GetWindIntensity();
            var windDir = new Vector2(wind.x, wind.z).normalized;
            cloudWind += windDir * speed * Time.deltaTime;
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
            var m = cloudMaterial;
            m.SetVector("_CloudCorner", t.forward - t.right * right - t.up * up);
            m.SetVector("_CloudRight", t.right * (2f * right));
            m.SetVector("_CloudUp", t.up * (2f * up));
            m.SetVector("_CamPos", new Vector3(0f, Mathf.Max(1f, origin.y - 30f), 0f));
            m.SetVector("_CloudCameraXZ", new Vector2(origin.x, origin.z));
            // HDRP's planet and layer (bottom 1200 m, 2000 m thick).
            const float R = 6378100f;
            m.SetFloat("_EarthRadius", R);
            m.SetVector("_PlanetCenterPosition", new Vector3(0f, -R, 0f));
            m.SetFloat("_LowestCloudAltitude", R + 1200f);
            m.SetFloat("_HighestCloudAltitude", R + 3200f);
            m.SetFloat("_MaxStepSize", 2000f / 8f);
            m.SetFloat("_NumPrimarySteps", ClientVisuals.WeatherInEffect == ClientVisuals.Level.High ? 96 : ClientVisuals.WeatherInEffect == ClientVisuals.Level.Medium ? 64 : 48);
            m.SetFloat("_NumLightSteps", ClientVisuals.WeatherInEffect == ClientVisuals.Level.Low ? 4 : 6);
            m.SetFloat("_MultiScattering", 1f - 0.5f * 0.95f);
            m.SetFloat("_PowderEffectIntensity", 0.25f);
            m.SetFloat("_ErosionFactorCompensation", 0.75f); // Perlin32 erosion noise
            m.SetFloat("_ErosionOcclusion", 0.1f);
            m.SetFloat("_AltitudeDistortion", 0.25f * 0.25f);
            m.SetFloat("_MediumWindSpeed", 1f);
            m.SetFloat("_SmallWindSpeed", 0.25f);
            m.SetVector("_WindVector", cloudWind);
            m.SetVector("_WindDirection", windDir);
            m.SetFloat("_FadeInStart", 400f);
            m.SetFloat("_FadeInDistance", 600f);
            m.SetFloat("_CameraSpace", 1f);
            m.SetFloat("_CloudStretch", 1.3f);      // slightly longer along the wind (more smeared the erosion detail away)
            m.SetFloat("_CoverMapScale", 3f);       // bands about 30 km apart
            m.SetFloat("_CoverMapContrast", 1.8f);
            m.SetVector("_ScatteringTint", Vector3.one);
            m.SetVector("_SunDirection", sun.y > -0.1f ? sun : -sun);
            var light = env.m_dirLight;
            var sunColor = light ? light.color * light.intensity : Color.white;
            // By night the game's light is the moon: clouds lit faintly by it, the ambient of the night sky (they were as white
            // as by day).
            float daylight = Mathf.Clamp01((sun.y + 0.05f) / 0.15f);
            // Night: the moon (opposite the sun, as the game turns its light) in a cool silver, dark cores.
            m.SetVector("_SunLightColor", daylight > 0f ? (Vector4)(sunColor * CloudSunScale * Mathf.Lerp(0.3f, 1f, daylight)) : new Vector4(0.32f, 0.36f, 0.45f, 0f));
            var amb = RenderSettings.ambientLight;
            float ambScale = CloudAmbientScale * Mathf.Lerp(0.2f, 1f, daylight);
            m.SetVector("_AmbientTop", (Vector4)((amb * 1.2f + RenderSettings.fogColor * 0.5f) * ambScale));
            m.SetVector("_AmbientBottom", (Vector4)(amb * 0.6f * ambScale));
            m.SetFloat("_Fade", cloudMix);
            HideFogClouds(true);
            Graphics.Blit(null, cloudTarget, cloudMaterial, 0);
            cloudMaterial.SetTexture("_CloudTex", cloudTarget);
            if (cloudCamera != camera) { DetachClouds(); AttachClouds(camera); }
        }

        // HDRP's presets (shape factor/scale, erosion factor/scale, density multiplier, density curve), erosion and ambient
        // occlusion curves as in HDRP's defaults; mixed by weight and written as the 64 row height profile.
        static readonly AnimationCurve[] presetDensity =
        {
            new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.05f, 1f), new Keyframe(0.75f, 1f), new Keyframe(1f, 0f)),  // Sparse
            new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.15f, 1f), new Keyframe(1f, 0.1f)),                          // Cloudy
            new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.05f, 1f), new Keyframe(0.9f, 0f), new Keyframe(1f, 0f)),     // Overcast
            new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.037f, 1f), new Keyframe(0.6f, 1f), new Keyframe(1f, 0f)),   // Stormy
        };
        static readonly float[,] presetValues = { { 0.95f, 5f, 0.8f, 107f, 0.4f }, { 0.9f, 5f, 0.8f, 107f, 0.4f }, { 0.5f, 5f, 0.5f, 107f, 0.3f }, { 0.85f, 5f, 0.75f, 107f, 0.35f } };
        static readonly AnimationCurve erosionCurve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.1f, 0.9f), new Keyframe(1f, 1f));
        static readonly AnimationCurve occlusionCurve = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.25f, 0.4f), new Keyframe(1f, 0f));

        static void ApplyPreset(Vector4 weights)
        {
            float total = weights.x + weights.y + weights.z + weights.w;
            if (total <= 0f) { weights = new Vector4(1f, 0f, 0f, 0f); total = 1f; }
            var v = new float[5];
            for (int p = 0; p < 4; p++) for (int k = 0; k < 5; k++) v[k] += presetValues[p, k] * weights[p] / total;
            cloudMaterial.SetFloat("_ShapeFactor", v[0]);
            cloudMaterial.SetFloat("_ShapeScale", v[1] * 0.9f); // close to HDRP's scale (larger lost the cloud detail)
            cloudMaterial.SetFloat("_ErosionFactor", v[2]);
            cloudMaterial.SetFloat("_ErosionScale", v[3]);
            cloudMaterial.SetFloat("_DensityMultiplier", v[4] * v[4] * 2f);
            cloudMaterial.SetFloat("_Coverage", 0.9f);
            for (int i = 0; i < 64; i++)
            {
                float h = i / 63f, d = 0f;
                if (i > 0 && i < 63) for (int p = 0; p < 4; p++) d += Mathf.Clamp01(presetDensity[p].Evaluate(h)) * weights[p] / total;
                lutPixels[i] = new Color(d, Mathf.Clamp01(erosionCurve.Evaluate(h)), Mathf.Clamp01(occlusionCurve.Evaluate(h)), 1f);
            }
            cloudLut.SetPixels(lutPixels);
            cloudLut.Apply(false);
        }

        static float FogOfGame() => Mathf.Max(0f, (EnvMan.instance.GetCurrentEnvironment()?.m_fogDensityDay ?? 0f) - 0.01f);

        static bool PrepareClouds()
        {
            if (cloudShader) return cloudMaterial;
            try
            {
                var bundle = EmbeddedAssets.LoadBundle("Overhaul.Assets.overhaul_weather");
                cloudShader = bundle.LoadAsset<Shader>("Assets/OverhaulWeather/OverhaulClouds.shader");
                worley = bundle.LoadAsset<Texture3D>("Assets/OverhaulWeather/HDRP/Worley128.asset");
                perlin = bundle.LoadAsset<Texture3D>("Assets/OverhaulWeather/HDRP/Perlin32.asset");
                if (!skyShader) skyShader = bundle.LoadAsset<Shader>("Assets/OverhaulWeather/OverhaulSky.shader");
                bundle.Unload(false);
                if (!cloudShader || !cloudShader.isSupported) throw new Exception("cloud shader not supported here");
            }
            catch (Exception e) { Log.LogWarning("[Weather] clouds off: " + e.Message); cloudShader = Shader.Find("Hidden/InternalErrorShader"); return false; }
            if (!worley || !perlin) { Log.LogWarning("[Weather] clouds off: noise volumes missing"); return false; }
            cloudMaterial = new Material(cloudShader);
            cloudMaterial.SetTexture("_Worley128RGBA", worley);
            cloudMaterial.SetTexture("_ErosionNoise", perlin);
            cloudLut = new Texture2D(1, 64, TextureFormat.RGBAHalf, false, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "Overhaul cloud profile" };
            cloudMaterial.SetTexture("_CloudLutTexture", cloudLut);
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

        // The game's pink smoky clouds in the sky (renderers with the Custom/SkyObject shader, high above) are hidden while our
        // volumetric clouds show, given back otherwise. The ground mists stay (user's choice).
        static bool skyObjectsHidden;
        static float nextSkySearch;
        static readonly System.Collections.Generic.List<Renderer> skyObjects = new System.Collections.Generic.List<Renderer>();
        static void HideFogClouds(bool hide)
        {
            // Searched again every 5 s while none were found (the sky objects come after the first frames).
            if (hide == skyObjectsHidden && (!hide || skyObjects.Count > 0 || Time.time < nextSkySearch)) return;
            skyObjectsHidden = hide;
            if (hide)
            {
                nextSkySearch = Time.time + 5f;
                skyObjects.Clear();
                foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (r.enabled && r.sharedMaterial && r.sharedMaterial.shader && r.sharedMaterial.shader.name == "Custom/SkyObject")
                    { skyObjects.Add(r); r.enabled = false; Log.LogInfo("[Weather] game sky clouds hidden: " + r.name); }
            }
            else
            {
                foreach (var r in skyObjects) if (r) r.enabled = true;
                skyObjects.Clear();
            }
        }

        static void StopClouds()
        {
            HideFogClouds(false);
            DetachClouds();
            cloudMix = 0f;
        }
    }
}
