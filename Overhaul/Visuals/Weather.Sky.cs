using System;
using UnityEngine;
using UnityEngine.Rendering;
using Overhaul.Performance;
using Overhaul.Utility;

namespace Overhaul.Visuals
{
    // Weather, sky: a physical atmosphere (Hidden/Overhaul/Sky, after Hillaire 2020) drawn over the game's sky. Its tables:
    // transmittance (256x64) and multiple scattering (32x32) are worked out once, the sky view around the camera (192x108)
    // every frame (camera height and sun). The composite pass, right after the game's sky (Yggdrasil and the rest drawn later stay
    // over it), covers only the
    // sky pixels and mixes ours in by SkyBlend, faded out at dusk: at night the game's own sky, stars and aurora. Style
    // "North": ozone x1.3, haze (Mie) x0.6, after the other weather mod's default.
    internal static partial class Weather
    {
        const float SkyBlend = 0.9f, SkyExposure = 8f;
        static Shader skyShader;
        static bool skyFailed, tablesMade;
        static Material skyMaterial;
        static RenderTexture transmittanceLut, multiLut, skyViewLut, aerialLut;
        static CommandBuffer aerialBuffer;
        // How much of the air the aerial perspective covers now (0 at night): the far view's haze gives way to it.
        internal static float AerialAmount;
        const float AerialScale = 4f; // Valheim's distances are short: the air thickened so a few km read as far
        static CommandBuffer skyBuffer;
        static Camera skyCamera;
        static float skyMix, gameSunDisk = -1f, gameMoonSize = -1f;
        // High layer: the day's photographed sky among eight (weights eased from one day's to the next), turned with the wind.
        static Texture2D highMapA, highMapB;
        static Vector4 highWeightA = new Vector4(1f, 0f, 0f, 0f), highWeightB;
        static float highTurn;
        // Test hook (Tools/GameTest): >= 0 shows that one of the eight skies, < 0 the day's own.
        internal static int highSkyOverride = -1;
        internal static bool DebugNoSky; // tests: the game's own sky under our clouds

        internal static void Tick()
        {
            var camera = Utils.GetMainCamera();
            bool want = On && camera && EnvMan.instance && ZNet.instance && !ZNet.instance.IsDedicated() && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
            if (!want) { StopSky(); StopClouds(); return; }
            if (!PrepareSky()) return;

            var sun = FarView.SunDirection();
            var player = Player.m_localPlayer;
            bool inside = player && player.InInterior();
            // Day: full with the sun 6 degrees up, none 3 degrees under the horizon.
            float target = inside ? 0f : Mathf.Clamp01((sun.y + 0.05f) / 0.15f);
            skyMix = Mathf.MoveTowards(skyMix, target, Time.deltaTime / 2f);
            // Kept attached at night too: the moon is drawn by the same pass (our sky's own colour faded out).
            float night = inside ? 0f : Mathf.Clamp01((-sun.y + 0.02f) / 0.12f);
            if (skyMix <= 0f && night <= 0f) { DetachSky(); TickClouds(camera, sun); return; }
            skyMaterial.SetVector("_MoonDir", -sun);
            skyMaterial.SetVector("_MoonColor", new Vector3(0.9f, 0.92f, 0.95f));
            // The game's own sun/moon disc off while ours shows (two discs made a ring).
            var skybox = RenderSettings.skybox;
            if (skybox && skybox.HasProperty("_SunDisk")) { if (gameSunDisk < 0f) gameSunDisk = skybox.GetFloat("_SunDisk"); skybox.SetFloat("_SunDisk", night > 0f ? 0f : gameSunDisk); }
            // Its own moon too (a bluish disc larger than ours: a blue ring around our white disc).
            if (skybox && skybox.HasProperty("_MoonSize")) { if (gameMoonSize < 0f) gameMoonSize = skybox.GetFloat("_MoonSize"); skybox.SetFloat("_MoonSize", 0f); }
            skyMaterial.SetFloat("_MoonStrength", night);

            skyMaterial.SetVector("_SunDir", sun);
            TickMeter(camera, sun);
            skyMaterial.SetFloat("_AutoExposure", AutoExposure);
            skyMaterial.SetVector("_TintToward", (Vector4)TintToward);
            skyMaterial.SetVector("_TintAway", (Vector4)TintAway);
            skyMaterial.SetFloat("_CameraKm", Mathf.Clamp(camera.transform.position.y - 30f, 0.01f, 9000f) / 1000f + 0.001f);
            if (!tablesMade)
            {
                Graphics.Blit(null, transmittanceLut, skyMaterial, 0);
                skyMaterial.SetTexture("_TransmittanceLut", transmittanceLut);
                Graphics.Blit(null, multiLut, skyMaterial, 1);
                skyMaterial.SetTexture("_MultiLut", multiLut);
                tablesMade = true;
            }
            Graphics.Blit(null, skyViewLut, skyMaterial, 2);
            skyMaterial.SetTexture("_SkyViewLut", skyViewLut);

            var t = camera.transform;
            float up = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad), right = up * camera.aspect;
            skyMaterial.SetVector("_SkyCorner", t.forward - t.right * right - t.up * up);
            skyMaterial.SetVector("_SkyRight", t.right * (2f * right));
            skyMaterial.SetVector("_SkyUp", t.up * (2f * up));
            skyMaterial.SetFloat("_SkyBlend", DebugNoSky ? 0f : SkyBlend * skyMix);
            skyMaterial.SetFloat("_SkyExposure", SkyExposure);
            skyMaterial.SetFloat("_AerialScale", AerialScale);
            skyMaterial.SetFloat("_AerialStrength", DebugNoSky ? 0f : skyMix);
            AerialAmount = DebugNoSky ? 0f : skyMix;
            // Cirrus: present in fair weather (more on some days), gone under rain and fog; drifting slowly with the wind.
            float fairWeight = presetMix.x + presetMix.y, total = presetMix.x + presetMix.y + presetMix.z + presetMix.w;
            int cirrusDay = (int)(ZNet.instance ? ZNet.instance.GetTimeSeconds() / Mathf.Max(1L, EnvMan.instance.m_dayLengthSec) : 0);
            float cirrusDayAmount = 0.45f + 0.4f * Mathf.Repeat(Mathf.Sin(cirrusDay * 78.233f + 1.7f) * 43758.5453f, 1f);
            skyMaterial.SetFloat("_CirrusAmount", total > 0f ? fairWeight / total * cirrusDayAmount : 0f);
            skyMaterial.SetVector("_CirrusOffset", cloudWind * 0.0004f);
            // The day's sky among the eight maps (per game day, from the world seed, same for every player), faded in over
            // about a minute when the day changes; turned a full circle in about 2 game hours of average wind.
            int highSky = (int)(Mathf.Repeat(Mathf.Sin((WorldGenerator.instance != null ? WorldGenerator.instance.GetSeed() : 0) * 0.0017f + cirrusDay * 39.346f) * 11234.5678f, 1f) * 8f) & 7;
            if (highSkyOverride >= 0) { highSky = highSkyOverride & 7; cirrusDayAmount = 0.8f; }
            var wantA = Vector4.zero; var wantB = Vector4.zero;
            if (highSky < 4) wantA[highSky] = 1f; else wantB[highSky - 4] = 1f;
            float ease = string.IsNullOrEmpty(EnvMan.instance.m_debugEnv) ? Time.deltaTime / 60f : 1f;
            highWeightA = Vector4.MoveTowards(highWeightA, wantA, ease); highWeightB = Vector4.MoveTowards(highWeightB, wantB, ease);
            skyMaterial.SetVector("_HighWeightA", highWeightA);
            skyMaterial.SetVector("_HighWeightB", highWeightB);
            highTurn = Mathf.Repeat(highTurn + Time.deltaTime * (0.3f + EnvMan.instance.GetWindIntensity()) / 4000f, 1f);
            skyMaterial.SetFloat("_HighTurn", highTurn);
            // The older painted mid-level decks are off: the high maps and the altocumulus layer take their place.
            skyMaterial.SetFloat("_AltoAmount", 0f);
            skyMaterial.SetVector("_CirrusWind", cloudWind.sqrMagnitude > 0f ? cloudWind.normalized : Vector2.right);
            var dirLight = EnvMan.instance.m_dirLight;
            var sunCol = dirLight ? dirLight.color * dirLight.intensity : Color.white;
            // By night the high layers stay, dark, faintly lit by the moon.
            float dayK = Mathf.Clamp01((sun.y + 0.05f) / 0.15f);
            // Lit by the sun by day, by the game's measured sky (lighter than it) and the moon by night, so the streaks stay
            // visible over the dark sky.
            skyMaterial.SetVector("_CirrusSun", dayK > 0f ? (Vector4)(sunCol * 0.55f * dayK) : new Vector4(0.05f, 0.055f, 0.07f, 0f));
            var skyGame = Color.Lerp(GameSkyAway, GameSkyToward, 0.5f);
            skyMaterial.SetVector("_CirrusAmbient", (Vector4)Color.Lerp(skyGame * 2.6f + new Color(0.02f, 0.025f, 0.035f), RenderSettings.fogColor * 0.6f + RenderSettings.ambientLight * 0.4f, dayK));
            skyMaterial.SetVector("_GlowColor", (Vector4)(EnvMan.instance.GetSunFogColor() * 0.6f + sunCol * 0.4f));
            skyMaterial.SetFloat("_GlowStrength", Mathf.Lerp(1.4f, 0.6f, Mathf.Clamp01(sun.y * 2f))); // stronger with the sun low
            skyMaterial.SetVector("_HorizonTint", (Vector4)RenderSettings.fogColor);
            skyMaterial.SetFloat("_HorizonPale", 0.35f);
            Graphics.Blit(null, aerialLut, skyMaterial, 4);
            skyMaterial.SetTexture("_AerialLut", aerialLut);
            if (skyCamera != camera) { DetachSky(); DetachClouds(); AttachSky(camera); KeepYggdrasilOver(); }
            TickClouds(camera, sun); // after the sky: its pass comes after ours
        }

        // Yggdrasil (the world tree in the sky) is drawn before the sky would be covered by ours: its materials are moved
        // after the sky pass (copies, the game's own materials untouched).
        static bool yggdrasilDone;
        static void KeepYggdrasilOver()
        {
            if (yggdrasilDone) return;
            int moved = 0;
            foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (r.name.IndexOf("yggdrasil", StringComparison.OrdinalIgnoreCase) < 0 && (!r.transform.parent || r.transform.parent.name.IndexOf("yggdrasil", StringComparison.OrdinalIgnoreCase) < 0)) continue;
                var mats = r.materials; // instanced copies
                foreach (var m in mats)
                {
                    var sh = m.shader; var props = new System.Text.StringBuilder();
                    for (int i = 0; i < sh.GetPropertyCount(); i++)
                    {
                        string n = sh.GetPropertyName(i); var t = sh.GetPropertyType(i);
                        props.Append(n).Append('=');
                        if (t == UnityEngine.Rendering.ShaderPropertyType.Color) props.Append(m.GetColor(n));
                        else if (t == UnityEngine.Rendering.ShaderPropertyType.Float || t == UnityEngine.Rendering.ShaderPropertyType.Range) props.Append(m.GetFloat(n));
                        else if (t == UnityEngine.Rendering.ShaderPropertyType.Vector) props.Append(m.GetVector(n));
                        else props.Append(m.GetTexture(n) ? m.GetTexture(n).name : "none");
                        props.Append("; ");
                    }
                    Log.LogInfo("[Weather] Yggdrasil part " + r.name + " (" + sh.name + ", queue " + m.renderQueue + "): " + props);
                    if (m.renderQueue < 2501) m.renderQueue = 2501;
                    // Its own haze (fog to the game's fog colour out to 15 km) made its trunk a flat blue against our sky:
                    // half of it kept, as the other weather mod does.
                    if (m.HasProperty("_FogAmount")) m.SetFloat("_FogAmount", 0.5f);
                    // Its green glow halved (it stood out against our darker night).
                    if (m.HasProperty("_EmissBlend")) m.SetFloat("_EmissBlend", m.GetFloat("_EmissBlend") * 0.5f);
                    if (m.HasProperty("_MossEmissive")) m.SetFloat("_MossEmissive", m.GetFloat("_MossEmissive") * 0.5f);
                }
                r.materials = mats; moved++;
            }
            if (moved > 0) yggdrasilDone = true;
            // What else draws in the sky: the environments' particle systems and renderers far up.
            var envMan = EnvMan.instance;
            if (envMan)
            {
                foreach (var e in envMan.m_environments)
                {
                    if (e.m_psystems == null) continue;
                    foreach (var ps in e.m_psystems) if (ps) Log.LogInfo("[Weather] env " + e.m_name + " particles " + ps.name + " active " + ps.activeInHierarchy);
                }
                Log.LogInfo("[Weather] game clouds " + (envMan.m_clouds ? envMan.m_clouds.name + " " + envMan.m_clouds.sharedMaterial.shader.name : "none") + ", rain clouds " + (envMan.m_rainClouds ? envMan.m_rainClouds.name : "none") + ", downside " + (envMan.m_rainCloudsDownside ? envMan.m_rainCloudsDownside.name : "none"));
            }
            var seen = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var pr in UnityEngine.Object.FindObjectsByType<ParticleSystemRenderer>(FindObjectsSortMode.None))
            {
                if (!pr.enabled || !pr.gameObject.activeInHierarchy) continue;
                string key = pr.name + " / " + (pr.transform.parent ? pr.transform.parent.name : "-") + " / " + (pr.sharedMaterial ? pr.sharedMaterial.name + " " + pr.sharedMaterial.shader.name : "-");
                seen[key] = seen.TryGetValue(key, out int c) ? c + 1 : 1;
            }
            foreach (var kv in seen) Log.LogInfo("[Weather] particles " + kv.Key + " x" + kv.Value);
            foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                if (r.enabled && r.gameObject.activeInHierarchy && r.bounds.center.y - (Camera.main ? Camera.main.transform.position.y : 0f) > 300f)
                    Log.LogInfo("[Weather] high renderer " + r.name + " (" + r.GetType().Name + ", " + (r.sharedMaterial ? r.sharedMaterial.shader.name : "-") + ") at " + r.bounds.center);
            // What the game's sky material offers (its clouds are painted in it).
            var sky = RenderSettings.skybox;
            if (sky)
            {
                var sh = sky.shader; var list = new System.Text.StringBuilder();
                for (int i = 0; i < sh.GetPropertyCount(); i++)
                {
                    string n = sh.GetPropertyName(i); var t = sh.GetPropertyType(i);
                    list.Append(n).Append('=');
                    if (t == UnityEngine.Rendering.ShaderPropertyType.Float || t == UnityEngine.Rendering.ShaderPropertyType.Range) list.Append(sky.GetFloat(n));
                    else if (t == UnityEngine.Rendering.ShaderPropertyType.Color) list.Append(sky.GetColor(n));
                    else if (t == UnityEngine.Rendering.ShaderPropertyType.Texture) list.Append(sky.GetTexture(n) ? sky.GetTexture(n).name : "none");
                    else list.Append(sky.GetVector(n));
                    list.Append("; ");
                }
                Log.LogInfo("[Weather] game sky " + sh.name + ": " + list);
            }
        }

        static bool PrepareSky()
        {
            if (skyFailed) return false;
            if (skyMaterial) return true;
            try
            {
                var bundle = EmbeddedAssets.LoadBundle("Overhaul.Assets.overhaul_weather");
                skyShader = bundle.LoadAsset<Shader>("Assets/OverhaulWeather/OverhaulSky.shader");
                highMapA = bundle.LoadAsset<Texture2D>("Assets/OverhaulWeather/Textures/HighCloudMapA.png");
                highMapB = bundle.LoadAsset<Texture2D>("Assets/OverhaulWeather/Textures/HighCloudMapB.png");
                bundle.Unload(false);
                if (!skyShader || !skyShader.isSupported) throw new Exception("sky shader not supported here");
                if (!highMapA || !highMapB) throw new Exception("high cloud maps missing");
            }
            catch (Exception e) { skyFailed = true; Log.LogWarning("[Weather] sky off: " + e.Message); return false; }
            skyMaterial = new Material(skyShader);
            skyMaterial.SetTexture("_HighMapA", highMapA);
            skyMaterial.SetTexture("_HighMapB", highMapB);
            // Earth's air (per km), "North" style.
            skyMaterial.SetVector("_RayleighScatter", new Vector3(5.802e-3f, 13.558e-3f, 33.1e-3f));
            skyMaterial.SetFloat("_RayleighHeight", 8f);
            skyMaterial.SetFloat("_MieScatter", 3.996e-3f * 0.6f);
            skyMaterial.SetFloat("_MieExtinct", 4.44e-3f * 0.6f);
            skyMaterial.SetFloat("_MieHeight", 1.2f);
            skyMaterial.SetFloat("_MieG", 0.8f);
            skyMaterial.SetVector("_OzoneAbsorb", new Vector3(0.65e-3f, 1.881e-3f, 0.085e-3f) * 1.3f);
            transmittanceLut = Lut("transmittance", 256, 64);
            multiLut = Lut("multiple scattering", 32, 32);
            skyViewLut = Lut("sky view", 192, 108);
            aerialLut = Lut("aerial perspective", 32 * 32, 32);
            tablesMade = false;
            return true;
        }

        static RenderTexture Lut(string name, int w, int h)
        {
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear) { name = "Overhaul sky " + name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            rt.Create();
            return rt;
        }

        static void AttachSky(Camera camera)
        {
            camera.depthTextureMode |= DepthTextureMode.Depth;
            skyBuffer = new CommandBuffer { name = "Overhaul sky" };
            skyBuffer.DrawProcedural(Matrix4x4.identity, skyMaterial, 3, MeshTopology.Triangles, 3);
            camera.AddCommandBuffer(CameraEvent.AfterSkybox, skyBuffer);
            // The aerial perspective after the water and transparents, like the far view's haze it replaces by day.
            aerialBuffer = new CommandBuffer { name = "Overhaul aerial perspective" };
            aerialBuffer.DrawProcedural(Matrix4x4.identity, skyMaterial, 5, MeshTopology.Triangles, 3);
            camera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, aerialBuffer);
            skyCamera = camera;
        }

        static void DetachSky()
        {
            if (skyCamera && skyBuffer != null) skyCamera.RemoveCommandBuffer(CameraEvent.AfterSkybox, skyBuffer);
            if (skyCamera && aerialBuffer != null) skyCamera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, aerialBuffer);
            skyBuffer?.Release(); aerialBuffer?.Release();
            skyBuffer = null; aerialBuffer = null; skyCamera = null;
            AerialAmount = 0f;
        }

        static void StopSky()
        {
            DetachSky();
            StopMeter();
            if (gameSunDisk >= 0f && RenderSettings.skybox && RenderSettings.skybox.HasProperty("_SunDisk")) RenderSettings.skybox.SetFloat("_SunDisk", gameSunDisk);
            if (gameMoonSize >= 0f && RenderSettings.skybox && RenderSettings.skybox.HasProperty("_MoonSize")) RenderSettings.skybox.SetFloat("_MoonSize", gameMoonSize);
            skyMix = 0f;
        }
    }
}
