using System;
using UnityEngine;
using UnityEngine.Rendering;
using Overhaul.Utility;

namespace Overhaul.Visuals
{
    // Weather, sky match: our sky and clouds follow the game's own sky instead of hand-set brightness. Twice a second a hidden
    // camera draws the game's sky (skybox only) into 32x32 targets toward the sun and away from it, read back without
    // stalling (AsyncGPUReadback), and our sky view table is read back the same way. From both: an exposure (the game's
    // brightness over ours) and a tint per side (the game's colour over ours), eased over about a second in log space.
    // Our sky, the aerial perspective and the cloud light use them, and the clouds take the game's sky colour as their
    // ambient: dark at night, pink at dusk, grey in rain, without tuning constants. Behaviour after the other weather mod's
    // sky meter, written for Overhaul.
    internal static partial class Weather
    {
        const int MeterSize = 32;
        static Camera meterCamera;
        static RenderTexture meterTarget;
        static float nextMeter;
        static int meterStep;
        static bool meterBusy;
        static Color vanillaToward = Color.gray, vanillaAway = Color.gray, oursToward = Color.gray, oursAway = Color.gray;
        static bool haveVanilla, haveOurs;
        internal static float AutoExposure = 1f;
        internal static Color TintToward = Color.white, TintAway = Color.white;
        // The game's sky colour away from the sun (what lights the clouds from around), eased.
        internal static Color GameSkyAway = Color.gray, GameSkyToward = Color.gray;

        static float Luminance(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

        static void TickMeter(Camera main, Vector3 sun)
        {
            if (Time.time < nextMeter || meterBusy) { EaseMatch(); return; }
            nextMeter = Time.time + 0.5f / 3f; // three steps per half second: toward, away, ours
            if (!meterCamera)
            {
                var go = new GameObject("Overhaul sky meter") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(go);
                meterCamera = go.AddComponent<Camera>();
                meterCamera.enabled = false;
                meterCamera.cullingMask = 0; // the skybox alone
                meterCamera.clearFlags = CameraClearFlags.Skybox;
                meterCamera.fieldOfView = 60f;
                meterCamera.allowHDR = true;
                meterTarget = new RenderTexture(MeterSize, MeterSize, 16, RenderTextureFormat.ARGBHalf) { name = "Overhaul sky meter" };
                meterTarget.Create();
                meterCamera.targetTexture = meterTarget;
            }
            var flat = new Vector3(sun.x, 0f, sun.z);
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.forward;
            flat.Normalize();
            int step = meterStep; meterStep = (meterStep + 1) % 3;
            if (step < 2)
            {
                // The game's sky, 15 degrees up, toward the sun or away from it.
                var look = (step == 0 ? flat : -flat) + Vector3.up * 0.27f;
                meterCamera.transform.SetPositionAndRotation(main.transform.position, Quaternion.LookRotation(look.normalized));
                meterCamera.Render();
                meterBusy = true;
                AsyncGPUReadback.Request(meterTarget, 0, TextureFormat.RGBAFloat, r =>
                {
                    meterBusy = false;
                    if (r.hasError) return;
                    var data = r.GetData<Color>();
                    Color sum = Color.black;
                    for (int i = 0; i < data.Length; i++) sum += data[i];
                    var avg = sum / Mathf.Max(1, data.Length);
                    if (step == 0) vanillaToward = avg; else vanillaAway = avg;
                    haveVanilla = true;
                });
            }
            else if (skyViewLut)
            {
                // Ours: the sky view table at 15 degrees up, azimuth 0 (toward the sun) and pi (away).
                meterBusy = true;
                AsyncGPUReadback.Request(skyViewLut, 0, TextureFormat.RGBAFloat, r =>
                {
                    meterBusy = false;
                    if (r.hasError) return;
                    var data = r.GetData<Color>();
                    int w = r.width, h = r.height;
                    // v for 15 degrees (same mapping as the shader, camera near the ground: horizon about 0).
                    float v = Mathf.Sqrt(Mathf.Clamp01(15f / 90f));
                    int row = Mathf.Clamp(Mathf.RoundToInt((v * 0.5f + 0.5f) * (h - 1)), 0, h - 1);
                    oursToward = data[row * w + 1] * SkyExposure;
                    oursAway = data[row * w + w - 2] * SkyExposure;
                    haveOurs = true;
                });
            }
            EaseMatch();
        }

        static void EaseMatch()
        {
            if (!haveVanilla || !haveOurs) return;
            float k = 1f - Mathf.Exp(-Time.deltaTime / 1f);
            GameSkyAway = Color.Lerp(GameSkyAway, vanillaAway, k);
            GameSkyToward = Color.Lerp(GameSkyToward, vanillaToward, k);
            float lv = Luminance(vanillaToward) + Luminance(vanillaAway), lo = Luminance(oursToward) + Luminance(oursAway);
            if (lv > 1e-5f && lo > 1e-5f)
            {
                float target = Mathf.Clamp(lv / lo, 0.05f, 50f);
                AutoExposure = Mathf.Exp(Mathf.Lerp(Mathf.Log(AutoExposure), Mathf.Log(target), k));
            }
            TintToward = Color.Lerp(TintToward, Tint(vanillaToward, oursToward), k);
            TintAway = Color.Lerp(TintAway, Tint(vanillaAway, oursAway), k);
        }

        // The game's hue over ours on one side, brightness taken out (the exposure carries it), kept within reason.
        static Color Tint(Color vanilla, Color ours)
        {
            float lv = Mathf.Max(Luminance(vanilla), 1e-5f), lo = Mathf.Max(Luminance(ours), 1e-5f);
            var t = new Color((vanilla.r / lv) / Mathf.Max(ours.r / lo, 1e-3f), (vanilla.g / lv) / Mathf.Max(ours.g / lo, 1e-3f), (vanilla.b / lv) / Mathf.Max(ours.b / lo, 1e-3f), 1f);
            return new Color(Mathf.Clamp(t.r, 0.3f, 3f), Mathf.Clamp(t.g, 0.3f, 3f), Mathf.Clamp(t.b, 0.3f, 3f), 1f);
        }

        static void StopMeter()
        {
            if (meterCamera) UnityEngine.Object.Destroy(meterCamera.gameObject);
            if (meterTarget) meterTarget.Release();
            meterCamera = null; meterTarget = null; haveVanilla = haveOurs = false; meterBusy = false;
            AutoExposure = 1f; TintToward = TintAway = Color.white;
        }
    }
}
