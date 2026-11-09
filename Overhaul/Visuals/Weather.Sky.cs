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
        const float SkyBlend = 0.75f, SkyExposure = 12f;
        static Shader skyShader;
        static bool skyFailed, tablesMade;
        static Material skyMaterial;
        static RenderTexture transmittanceLut, multiLut, skyViewLut;
        static CommandBuffer skyBuffer;
        static Camera skyCamera;
        static float skyMix;

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
            if (skyMix <= 0f) { DetachSky(); TickClouds(camera, sun); return; }

            skyMaterial.SetVector("_SunDir", sun);
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
            skyMaterial.SetFloat("_SkyBlend", SkyBlend * skyMix);
            skyMaterial.SetFloat("_SkyExposure", SkyExposure);
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
                foreach (var m in mats) { Log.LogInfo("[Weather] Yggdrasil part " + r.name + " (" + m.shader.name + ", queue " + m.renderQueue + ")"); if (m.renderQueue < 2501) m.renderQueue = 2501; }
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
                bundle.Unload(false);
                if (!skyShader || !skyShader.isSupported) throw new Exception("sky shader not supported here");
            }
            catch (Exception e) { skyFailed = true; Log.LogWarning("[Weather] sky off: " + e.Message); return false; }
            skyMaterial = new Material(skyShader);
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
            skyCamera = camera;
        }

        static void DetachSky()
        {
            if (skyCamera && skyBuffer != null) skyCamera.RemoveCommandBuffer(CameraEvent.AfterSkybox, skyBuffer);
            skyBuffer?.Release();
            skyBuffer = null; skyCamera = null;
        }

        static void StopSky()
        {
            DetachSky();
            skyMix = 0f;
        }
    }
}
