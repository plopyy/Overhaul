using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using Overhaul.Utility;

namespace Overhaul.Performance
{
    // "o_opti record [minutes]" / "o_opti record stop": records the performance in the background while playing, to track down
    // fps losses afterwards. Two files in BepInEx/OverhaulPerf (CSV, ";" separated, opens in a spreadsheet):
    // - record-<date>.csv, one line per second: fps, average and worst frame, main thread, render thread and GPU times, draw
    //   calls, shadow casters, triangles, what is loaded (pieces and how many merged, characters, particle systems and hidden ones),
    //   memory collections, position and biome, and the 3 costliest game script groups of that second;
    // - spikes-<date>.csv, one line per slow frame (over 2.5 times the running median and over 25 ms): its time, where it was,
    //   whether a memory collection ran, and the 5 costliest groups of that very frame.
    // The cost is the timing patches of "o_opti profile" (a stopwatch around about 30 update methods) and one file write
    // per second. A summary goes to the console and the log when it stops.
    internal sealed class PerfRecorder : MonoBehaviour
    {
        const float SpikeFactor = 2.5f, SpikeMinMs = 25f;
        static PerfRecorder instance;
        internal static bool Recording => instance;

        ProfilerRecorder main, render, gpu, draws, shadows, triangles;
        StreamWriter seconds, spikes;
        string folder, stamp;
        float until, second, worst, total; int frames, gcAtSecond, gcAtFrame;
        readonly Dictionary<string, long> secondTicks = new Dictionary<string, long>();
        readonly Dictionary<string, long> secondAllocs = new Dictionary<string, long>();
        long secondAlloc;
        readonly float[] recent = new float[120]; int recentCount, recentIndex;
        long memory;
        int spikeCount, secondCount; float worstSpike; float fpsSum, fpsMin = float.MaxValue;
        readonly Dictionary<string, int> spikeBlame = new Dictionary<string, int>();
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        internal static void Start(Terminal context, float minutes)
        {
            if (instance) { context.AddString(PerfMonitor.L("overhaul_perf_record_already")); return; }
            var go = new GameObject("Overhaul performance recorder");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            instance = go.AddComponent<PerfRecorder>();
            instance.Begin(minutes);
        }

        internal static void Stop() { if (instance) Destroy(instance.gameObject); }

        void Begin(float minutes)
        {
            until = minutes > 0 ? Time.realtimeSinceStartup + minutes * 60f : float.MaxValue;
            folder = Path.Combine(BepInEx.Paths.BepInExRootPath, "OverhaulPerf");
            Directory.CreateDirectory(folder);
            stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv);
            seconds = new StreamWriter(Path.Combine(folder, "record-" + stamp + ".csv"), false, new UTF8Encoding(false));
            spikes = new StreamWriter(Path.Combine(folder, "spikes-" + stamp + ".csv"), false, new UTF8Encoding(false));
            seconds.WriteLine("time;fps;avg_ms;worst_ms;cpu_main_ms;cpu_render_ms;gpu_ms;draw_calls;shadow_casters;triangles;pieces;merged_pieces;characters;particles;particles_hidden;gc;alloc_kb;x;z;biome;top_scripts;top_allocators_kb");
            spikes.WriteLine("time;frame_ms;median_ms;untimed_ms;cpu_render_ms;gpu_ms;alloc_kb;gc;objects_created;zones_spawned;x;z;biome;top_scripts;top_allocators_kb");
            main = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "CPU Main Thread Frame Time");
            render = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "CPU Render Thread Frame Time");
            gpu = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GPU Frame Time");
            draws = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            shadows = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Shadow Casters Count");
            triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            PerfProfile.Install();
            PerfProfile.MeasureAllocations = true;
            PerfProfile.FrameTicks.Clear();
            gcAtSecond = gcAtFrame = GC.CollectionCount(0);
            memory = GC.GetTotalMemory(false);
            PerfMonitor.Print(PerfMonitor.L("overhaul_perf_record_start", minutes > 0 ? minutes.ToString("0.#", Inv) + " min" : "∞", folder));
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime, ms = dt * 1000f;
            // The groups timed since the last frame belong to this one (close enough to find who made a frame slow).
            foreach (var pair in PerfProfile.FrameTicks) { secondTicks.TryGetValue(pair.Key, out long t); secondTicks[pair.Key] = t + pair.Value; }
            foreach (var pair in PerfProfile.FrameAllocs) { secondAllocs.TryGetValue(pair.Key, out long a); secondAllocs[pair.Key] = a + pair.Value; }
            int gc = GC.CollectionCount(0);
            long now = GC.GetTotalMemory(false); long alloc = Math.Max(0, now - memory); memory = now; secondAlloc += alloc;
            float median = Median();
            if (recentCount >= 30 && ms > SpikeMinMs && ms > median * SpikeFactor) Spike(ms, median, gc != gcAtFrame, alloc);
            gcAtFrame = gc;
            PerfProfile.FrameTicks.Clear(); PerfProfile.FrameCalls.Clear(); PerfProfile.FrameAllocs.Clear();
            recent[recentIndex] = ms; recentIndex = (recentIndex + 1) % recent.Length; recentCount = Math.Min(recentCount + 1, recent.Length);
            second += dt; total += dt; frames++; worst = Mathf.Max(worst, ms);
            if (second >= 1f) WriteSecond();
            if (Time.realtimeSinceStartup >= until) Destroy(gameObject);
        }

        float Median()
        {
            if (recentCount == 0) return 0f;
            var copy = new float[recentCount]; Array.Copy(recent, copy, recentCount); Array.Sort(copy);
            return copy[recentCount / 2];
        }

        static (float X, float Z, string Biome) Where()
        {
            var player = Player.m_localPlayer;
            if (!player) return (0, 0, "-");
            var p = player.transform.position;
            return (p.x, p.z, player.GetCurrentBiome().ToString());
        }

        static string TopKb(Dictionary<string, long> bytes, int count) =>
            string.Join(" | ", bytes.OrderByDescending(p => p.Value).Take(count).Select(p => p.Key + " " + (p.Value / 1024).ToString(Inv)));

        static string Top(Dictionary<string, long> ticks, int count, double toMs) =>
            string.Join(" | ", ticks.OrderByDescending(p => p.Value).Take(count).Select(p => p.Key + " " + (p.Value * toMs).ToString("0.00", Inv)));

        void Spike(float ms, float median, bool gc, long alloc)
        {
            spikeCount++; worstSpike = Mathf.Max(worstSpike, ms);
            var (x, z, biome) = Where();
            double toMs = 1000.0 / Stopwatch.Frequency;
            var first = PerfProfile.FrameTicks.OrderByDescending(p => p.Value).FirstOrDefault();
            string blame = gc ? "GC" : first.Key ?? "unknown";
            spikeBlame.TryGetValue(blame, out int n); spikeBlame[blame] = n + 1;
            // What the timing does not cover: the engine's own work (loading, instancing, physics, rendering) in that frame.
            double timed = 0; foreach (var pair in PerfProfile.FrameTicks) if (PerfProfile.Roots.Contains(pair.Key)) timed += pair.Value * toMs;
            PerfProfile.FrameCalls.TryGetValue("ZNetScene.CreateObject", out int created);
            PerfProfile.FrameCalls.TryGetValue("ZoneSystem.SpawnZone", out int zones);
            spikes.WriteLine(string.Join(";", DateTime.Now.ToString("HH:mm:ss.fff", Inv), ms.ToString("0.0", Inv), median.ToString("0.0", Inv), Math.Max(0, ms - timed).ToString("0.0", Inv),
                Ns(render), Ns(gpu), (alloc / 1024).ToString(Inv), gc ? "1" : "0", created.ToString(Inv), zones.ToString(Inv),
                x.ToString("0", Inv), z.ToString("0", Inv), biome, Top(PerfProfile.FrameTicks, 8, toMs), TopKb(PerfProfile.FrameAllocs, 5)));
        }

        void WriteSecond()
        {
            float fps = frames / Mathf.Max(second, .0001f);
            secondCount++; fpsSum += fps; fpsMin = Mathf.Min(fpsMin, fps);
            var (x, z, biome) = Where();
            int gc = GC.CollectionCount(0);
            double toMs = 1000.0 / Stopwatch.Frequency / Math.Max(1, frames);
            // Particle systems in the scene, from the hidden particle scan (a scene search every second would itself make frames slow).
            int particles = HiddenParticles.SystemCount;
            seconds.WriteLine(string.Join(";",
                DateTime.Now.ToString("HH:mm:ss", Inv), fps.ToString("0.0", Inv), (second / frames * 1000f).ToString("0.00", Inv), worst.ToString("0.0", Inv),
                Ns(main), Ns(render), Ns(gpu), Count(draws), Count(shadows), Count(triangles),
                WearNTear.s_allInstances.Count, PieceBatcher.MergedPieces, Character.s_characters.Count, particles, HiddenParticles.HiddenCount, gc - gcAtSecond,
                (secondAlloc / 1024).ToString(Inv), x.ToString("0", Inv), z.ToString("0", Inv), biome, Top(secondTicks, 3, toMs), TopKb(secondAllocs, 5)));
            seconds.Flush(); spikes.Flush();
            gcAtSecond = gc; secondTicks.Clear(); secondAllocs.Clear(); secondAlloc = 0;
            second = 0f; frames = 0; worst = 0f;
        }

        static string Ns(ProfilerRecorder r) => r.Valid && r.Count > 0 ? (r.LastValue / 1e6).ToString("0.00", Inv) : "";
        static string Count(ProfilerRecorder r) => r.Valid && r.Count > 0 ? r.LastValue.ToString(Inv) : "";

        void OnDestroy()
        {
            try
            {
                seconds?.Dispose(); spikes?.Dispose();
                main.Dispose(); render.Dispose(); gpu.Dispose(); draws.Dispose(); shadows.Dispose(); triangles.Dispose();
                PerfProfile.MeasureAllocations = false;
                PerfProfile.Uninstall();
                float minutes = total / 60f;
                string blame = string.Join(", ", spikeBlame.OrderByDescending(p => p.Value).Take(3).Select(p => p.Key + " x" + p.Value));
                PerfMonitor.Print(PerfMonitor.L("overhaul_perf_record_stop", minutes.ToString("0.0", Inv), (secondCount > 0 ? fpsSum / secondCount : 0).ToString("0", Inv),
                    (secondCount > 0 ? fpsMin : 0).ToString("0", Inv), spikeCount.ToString(Inv), worstSpike.ToString("0", Inv), blame.Length > 0 ? blame : "-"));
                PerfMonitor.Print(Path.Combine(folder, "record-" + stamp + ".csv"));
            }
            catch (Exception e) { Log.LogWarning("[Performance] Recorder: " + e.Message); }
            if (instance == this) instance = null;
        }
    }
}
