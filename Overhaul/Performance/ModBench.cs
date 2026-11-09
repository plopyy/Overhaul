using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using Unity.Profiling;
using UnityEngine;
using Overhaul.Utility;

namespace Overhaul.Performance
{
    // "o_opti mods": what the other mods cost (SteadyFrame against Overhaul's own optimisations, WeatherOverhaul, FarLands), measured in one go without leaving the game. WeatherOverhaul and
    // FarLands apply their settings while the game runs, so their settings are switched step by step (current, weather at
    // Medium, weather off, far land at Low, far land off), each step measured for a few seconds after settling, then
    // everything is put back as it was and a table printed. Do not move the camera meanwhile.
    internal static class ModBench
    {
        const string Weather = "ru.ivest.weatheroverhaul", Far = "ru.ivest.farlands", Steady = "ru.ivest.steadyframe";
        const float Settle = 4f, Measure = 8f;
        static Runner running;

        internal static void Start(Terminal context)
        {
            if (running) { PerfMonitor.Print("o_opti mods: already running"); return; }
            var weather = Config(Weather); var far = Config(Far); var steady = Config(Steady);
            if (weather == null && far == null && steady == null) { PerfMonitor.Print("o_opti mods: none of SteadyFrame, WeatherOverhaul, FarLands is installed"); return; }
            var go = new GameObject("Overhaul mod bench"); UnityEngine.Object.DontDestroyOnLoad(go); go.hideFlags = HideFlags.HideAndDontSave;
            running = go.AddComponent<Runner>(); running.Begin(weather, far, steady);
        }

        static ConfigFile Config(string guid) => Chainloader.PluginInfos.TryGetValue(guid, out var info) && info.Instance ? info.Instance.Config : null;

        static ConfigEntryBase Entry(ConfigFile file, string section, string key)
        {
            if (file == null) return null;
            foreach (var def in file.Keys) if (def.Section == section && def.Key == key) return file[def];
            return null;
        }

        sealed class Runner : MonoBehaviour
        {
            readonly List<(string Label, Action Apply)> steps = new List<(string, Action)>();
            readonly Dictionary<ConfigEntryBase, object> saved = new Dictionary<ConfigEntryBase, object>();
            readonly List<(string Label, float Fps, float Low, double Main, double Gpu)> results = new List<(string, float, float, double, double)>();

            void Set(ConfigEntryBase e, object value)
            {
                if (e == null) return;
                if (!saved.ContainsKey(e)) saved[e] = e.BoxedValue;
                e.BoxedValue = value is string s && e.SettingType.IsEnum ? Enum.Parse(e.SettingType, s) : value;
            }

            internal void Begin(ConfigFile weather, ConfigFile far, ConfigFile steady)
            {
                // Every WeatherOverhaul part has its own Enabled switch: all off gives the game's own weather back.
                var weatherParts = weather?.Keys.Where(k => k.Key == "Enabled").Select(k => weather[k]).ToList() ?? new List<ConfigEntryBase>();
                var farOn = Entry(far, "General", "Enabled");
                var steadyOn = Entry(steady, "1 - General", "Enabled");
                // Overhaul's own versions of what SteadyFrame does (grass, object streaming, terrain).
                var ours = new ConfigEntryBase[] { OverhaulConfig.PerfGrassSpread, OverhaulConfig.PerfStreamingSkip, OverhaulConfig.PerfTerrainCache };
                steps.Add(("as set now", () => { }));
                if (steadyOn != null)
                {
                    steps.Add(("SteadyFrame off", () => { Set(steadyOn, false); foreach (var e in ours) Set(e, false); }));
                    steps.Add(("Overhaul's own", () => { Set(steadyOn, false); foreach (var e in ours) Set(e, true); }));
                    steps.Add(("SteadyFrame back", () => { Set(steadyOn, saved[steadyOn]); foreach (var e in ours) Set(e, saved.TryGetValue(e, out var v) ? v : e.BoxedValue); }));
                }
                if (weatherParts.Count > 0) steps.Add(("weather off", () => { foreach (var e in weatherParts) Set(e, false); }));
                if (farOn != null) steps.Add(("+ far land off", () => Set(farOn, false)));
                StartCoroutine(Run());
            }

            IEnumerator Run()
            {
                PerfMonitor.Print("o_opti mods: " + steps.Count + " steps of " + (Settle + Measure).ToString("0") + " s, do not move the camera...");
                var main = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "CPU Main Thread Frame Time");
                var gpu = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GPU Frame Time");
                try
                {
                    foreach (var (label, apply) in steps)
                    {
                        try { apply(); } catch (Exception e) { Log.LogWarning("[Performance] o_opti mods: " + label + ": " + e.Message); }
                        PerfMonitor.Print("o_opti mods: " + label + "...");
                        yield return new WaitForSecondsRealtime(Settle);
                        var frames = new List<float>(1024); double mainSum = 0, gpuSum = 0; int mainN = 0, gpuN = 0; float t = 0f;
                        while (t < Measure)
                        {
                            yield return null;
                            float dt = Time.unscaledDeltaTime; t += dt; frames.Add(dt);
                            if (main.Valid && main.Count > 0) { mainSum += main.LastValue; mainN++; }
                            if (gpu.Valid && gpu.Count > 0) { gpuSum += gpu.LastValue; gpuN++; }
                        }
                        var sorted = frames.OrderByDescending(f => f).ToList();
                        float low = sorted.Take(Math.Max(1, sorted.Count / 100)).Average();
                        results.Add((label, frames.Count / Mathf.Max(frames.Sum(), .0001f), 1f / Mathf.Max(low, .0001f), mainN > 0 ? mainSum / mainN / 1e6 : 0, gpuN > 0 ? gpuSum / gpuN / 1e6 : 0));
                    }
                }
                finally { main.Dispose(); gpu.Dispose(); Restore(); }
                PerfMonitor.Print("o_opti mods: results (fps, 1% low, main thread, GPU):");
                foreach (var r in results)
                    PerfMonitor.Print("  " + r.Label.PadRight(16) + " " + r.Fps.ToString("0.0").PadLeft(6) + " fps  " + r.Low.ToString("0.0").PadLeft(6) + "  main " + r.Main.ToString("0.00") + " ms  GPU " + r.Gpu.ToString("0.00") + " ms");
                PerfMonitor.Print("o_opti mods: settings put back as they were.");
                Destroy(gameObject);
            }

            // Back to the settings found at the start, whatever happened (also on leaving the game mid-run).
            void Restore()
            {
                foreach (var kv in saved) { try { kv.Key.BoxedValue = kv.Value; } catch (Exception e) { Log.LogWarning("[Performance] o_opti mods: restore " + kv.Key.Definition + ": " + e.Message); } }
                saved.Clear();
            }

            void OnDestroy() { Restore(); if (running == this) running = null; }
        }
    }
}
