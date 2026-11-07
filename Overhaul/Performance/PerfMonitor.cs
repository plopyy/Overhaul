using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using Overhaul.Utility;

namespace Overhaul.Performance
{
    // Player tool behind the "operf" console command: measures frame times over a few seconds,
    // compares the client optimisations off and on, and switches each of them live.
    // It needs no admin rights (the command does not start with o_).
    internal static class PerfMonitor
    {
        internal const string Command = "operf";
        private const float MeasureSeconds = 10f, SettleSeconds = 2f;

        internal static readonly (string Id, Func<ConfigEntry<bool>> Entry)[] Options =
        {
            ("streaming", () => OverhaulConfig.PerfStreamingSkip),
            ("water", () => OverhaulConfig.PerfWaterMaterials),
            ("floaters", () => OverhaulConfig.PerfFastFloaters),
            ("terrain", () => OverhaulConfig.PerfTerrainCache),
            ("shadows", () => OverhaulConfig.PerfDecorShadows),
            ("particles", () => OverhaulConfig.PerfHiddenParticles),
            ("merge", () => OverhaulConfig.PerfMergePieces),
        };

        // Set by "operf ab" while it measures the game without the optimisations; the saved settings stay as they are.
        internal static volatile bool AllOff;
        internal static int StreamingRuns, StreamingSkips, FloaterPasses, TerrainPromoted;

        private static Sampler sampler;

        internal static bool On(ConfigEntry<bool> entry) => !AllOff && entry != null && entry.Value;

        internal static void Initialize()
        {
            var runner = new GameObject("Overhaul performance");
            UnityEngine.Object.DontDestroyOnLoad(runner);
            runner.hideFlags = HideFlags.HideAndDontSave;
            runner.AddComponent<Runner>();
            new Terminal.ConsoleCommand(Command, "Overhaul performance: operf [ab|profile|hud|record [minutes|stop]|status|<option> on|off]",
                (Terminal.ConsoleEvent)Run, optionsFetcher: () => new List<string> { "ab", "profile", "hud", "record", "status" }.Concat(Options.Select(o => o.Id)).ToList());
        }

        internal static string L(string key, params string[] words) =>
            Localization.instance != null ? Localization.instance.Localize("$" + key, words) : key;

        private static void Run(Terminal.ConsoleEventArgs args)
        {
            string first = args.Length > 1 ? args.Args[1].ToLowerInvariant() : "";
            if (first == "" || first == "ab")
            {
                if (sampler != null || PerfProfile.Running) { args.Context.AddString(L("overhaul_perf_busy")); return; }
                var go = new GameObject("Overhaul performance sampler");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                sampler = go.AddComponent<Sampler>();
                sampler.Begin(first == "ab");
                return;
            }
            if (first == "record")
            {
                if (args.Length > 2 && args.Args[2].ToLowerInvariant() == "stop") { if (PerfRecorder.Recording) PerfRecorder.Stop(); else args.Context.AddString(L("overhaul_perf_record_none")); return; }
                float minutes = args.Length > 2 && float.TryParse(args.Args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float m) ? m : 0f;
                PerfRecorder.Start(args.Context, minutes);
                return;
            }
            if (first == "hud")
            {
                PerfHud.Toggle();
                return;
            }
            if (first == "profile")
            {
                if (sampler != null) { args.Context.AddString(L("overhaul_perf_busy")); return; }
                PerfProfile.Start(args.Context);
                return;
            }
            if (first == "status")
            {
                foreach (var option in Options)
                    args.Context.AddString(L("overhaul_perf_status", option.Id, L(option.Entry().Value ? "overhaul_perf_on" : "overhaul_perf_off")));
                args.Context.AddString(Counters(StreamingRuns, StreamingSkips, FloaterPasses, TerrainPromoted));
                return;
            }
            var match = Options.FirstOrDefault(o => o.Id == first);
            if (match.Id == null || args.Length < 3 || (args.Args[2] != "on" && args.Args[2] != "off"))
            {
                args.Context.AddString(match.Id == null && first != "help" ? L("overhaul_perf_unknown", first) : "");
                args.Context.AddString(L("overhaul_perf_usage"));
                return;
            }
            match.Entry().Value = args.Args[2] == "on";
            args.Context.AddString(L("overhaul_perf_set", match.Id, L(match.Entry().Value ? "overhaul_perf_on" : "overhaul_perf_off")));
        }

        private static string Counters(int runs, int skips, int floaters, int terrain) =>
            L("overhaul_perf_counters", runs.ToString(), skips.ToString(), floaters.ToString(), terrain.ToString());

        internal static void Print(string text)
        {
            Log.LogInfo("[Performance] " + text);
            if (Console.instance) Console.instance.AddString(text);
        }

        // Per-frame work of the optimisations that follow the scene (and their option) live.
        private sealed class Runner : MonoBehaviour
        {
            private void Update()
            {
                try { ServerStats.Tick(); DecorShadows.Tick(); HiddenParticles.Tick(); PieceBatcher.Tick(); }
                catch (Exception e) { Log.LogWarning("[Performance] " + e); enabled = false; }
            }
        }

        private struct Result
        {
            internal float Average, Low, AverageMs, WorstMs;
            internal int Runs, Skips, Floaters, Terrain;
        }

        private sealed class Sampler : MonoBehaviour
        {
            private readonly List<float> frames = new List<float>(4096);
            private readonly List<(string Label, Result Value)> results = new List<(string, Result)>();
            private bool compare, measuring;
            private float phaseTime;
            private int runs, skips, floaters, terrain;

            internal void Begin(bool ab)
            {
                compare = ab;
                AllOff = ab;
                StartPhase();
            }

            private void StartPhase()
            {
                frames.Clear();
                measuring = false;
                phaseTime = 0f;
                Print(L(compare ? (AllOff ? "overhaul_perf_ab_off" : "overhaul_perf_ab_on") : "overhaul_perf_measuring", MeasureSeconds.ToString("0")));
            }

            private void Update()
            {
                float dt = Time.unscaledDeltaTime;
                phaseTime += dt;
                if (!measuring)
                {
                    // Lets the game settle after a switch before frames count.
                    if (phaseTime < (compare ? SettleSeconds : 0.5f)) return;
                    measuring = true;
                    phaseTime = 0f;
                    runs = StreamingRuns; skips = StreamingSkips; floaters = FloaterPasses; terrain = TerrainPromoted;
                    return;
                }
                frames.Add(dt);
                if (phaseTime < MeasureSeconds) return;
                results.Add((L(compare ? (AllOff ? "overhaul_perf_label_off" : "overhaul_perf_label_on") : "overhaul_perf_label_now"), Summarise()));
                if (compare && AllOff) { AllOff = false; StartPhase(); return; }
                Finish();
            }

            private Result Summarise()
            {
                var sorted = frames.OrderByDescending(f => f).ToList();
                float total = frames.Sum();
                int lowCount = Mathf.Max(1, sorted.Count / 100);
                float lowFrame = sorted.Take(lowCount).Average();
                return new Result
                {
                    Average = frames.Count / Mathf.Max(total, 0.0001f),
                    Low = 1f / Mathf.Max(lowFrame, 0.0001f),
                    AverageMs = total / Mathf.Max(1, frames.Count) * 1000f,
                    WorstMs = sorted.Count > 0 ? sorted[0] * 1000f : 0f,
                    Runs = StreamingRuns - runs, Skips = StreamingSkips - skips,
                    Floaters = FloaterPasses - floaters, Terrain = TerrainPromoted - terrain,
                };
            }

            private void Finish()
            {
                foreach (var (label, r) in results)
                {
                    Print(L("overhaul_perf_result", label, r.Average.ToString("0.0"), r.Low.ToString("0.0"), r.AverageMs.ToString("0.00"), r.WorstMs.ToString("0.0")));
                    Print(Counters(r.Runs, r.Skips, r.Floaters, r.Terrain));
                }
                if (results.Count == 2)
                {
                    var off = results[0].Value; var on = results[1].Value;
                    float percent = off.Average > 0f ? (on.Average / off.Average - 1f) * 100f : 0f;
                    Print(L("overhaul_perf_gain", (on.Average - off.Average).ToString("+0.0;-0.0"), percent.ToString("+0.0;-0.0"), (on.Low - off.Low).ToString("+0.0;-0.0")));
                }
                Destroy(gameObject);
            }

            private void OnDestroy()
            {
                AllOff = false;
                if (sampler == this) sampler = null;
            }
        }
    }
}
