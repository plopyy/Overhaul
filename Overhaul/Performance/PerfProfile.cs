using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Unity.Profiling;
using UnityEngine;
using Overhaul.Utility;
using Debug = UnityEngine.Debug;

namespace Overhaul.Performance
{
    // "operf profile": where the frame time goes. For 10 s it times the game's update groups (the MonoUpdaters categories the
    // game names itself: Character, BaseAI, ZSyncTransform... and the big managers: building wear, network scene, minimap...),
    // reads Unity's render counters (draw calls, batches, triangles, shadow casters; CPU and GPU frame times when the build
    // exposes them) and counts what is loaded around the player (pieces, creatures, lights, particles, renderers).
    // Its timing patches are added for the measure only and removed afterwards (own Harmony id), so nothing costs outside it.
    // Times are inclusive: a manager's time contains the groups it calls.
    internal static class PerfProfile
    {
        const float Seconds = 10f;
        const string HarmonyId = "plopyy.valheim.Overhaul.perfprofile";
        static Harmony harmony;
        static readonly Dictionary<string, long> ticks = new Dictionary<string, long>();
        static readonly Dictionary<string, int> calls = new Dictionary<string, int>();
        internal static bool Running => harmony != null;

        // The game's own update groups (MonoUpdatersExtra: the scope string names the group).
        static readonly string[] Scoped = { "UpdateAI", "CustomFixedUpdate", "CustomUpdate", "CustomLateUpdate" };
        // Managers timed whole, by type and method.
        static readonly (string Type, string Method)[] Managers =
        {
            ("MonoUpdaters", "Update"), ("MonoUpdaters", "FixedUpdate"), ("MonoUpdaters", "LateUpdate"),
            ("WearNTearUpdater", "Update"), ("ZNetScene", "Update"), ("ZNet", "Update"), ("ZNet", "LateUpdate"),
            ("ZoneSystem", "Update"), ("Minimap", "Update"), ("EnvMan", "Update"), ("EnvMan", "FixedUpdate"),
            ("Player", "Update"), ("Player", "FixedUpdate"), ("Player", "LateUpdate"), ("GameCamera", "LateUpdate"),
            ("Hud", "Update"), ("Hud", "LateUpdate"), ("InventoryGui", "Update"), ("ClutterSystem", "LateUpdate"),
            ("Game", "Update"), ("Game", "FixedUpdate"), ("SpawnSystem", "UpdateSpawning"), ("Heightmap", "CustomLateUpdate"),
            ("EnemyHud", "LateUpdate"), ("MessageHud", "Update"), ("AudioMan", "Update"), ("MusicMan", "Update"),
            ("LightLod", "Update"), ("Fireplace", "UpdateFireplace"), ("Smelter", "UpdateSmelter"),
        };
        static readonly (string Name, ProfilerCategory Category)[] Counters =
        {
            ("Draw Calls Count", ProfilerCategory.Render), ("Batches Count", ProfilerCategory.Render), ("SetPass Calls Count", ProfilerCategory.Render),
            ("Triangles Count", ProfilerCategory.Render), ("Shadow Casters Count", ProfilerCategory.Render), ("Visible Skinned Meshes Count", ProfilerCategory.Render),
            ("CPU Main Thread Frame Time", ProfilerCategory.Internal), ("CPU Render Thread Frame Time", ProfilerCategory.Internal), ("GPU Frame Time", ProfilerCategory.Internal),
            ("GC Allocated In Frame", ProfilerCategory.Memory),
        };

        internal static void Start(Terminal context)
        {
            if (harmony != null) { context.AddString(PerfMonitor.L("overhaul_perf_busy")); return; }
            ticks.Clear(); calls.Clear();
            harmony = new Harmony(HarmonyId);
            var begin = new HarmonyMethod(typeof(PerfProfile), nameof(Begin)) { priority = Priority.First };
            int patched = 0;
            foreach (var name in Scoped)
            {
                var method = AccessTools.Method(typeof(MonoUpdatersExtra), name);
                if (method == null) continue;
                harmony.Patch(method, begin, new HarmonyMethod(typeof(PerfProfile), nameof(EndScoped)) { priority = Priority.Last }); patched++;
            }
            foreach (var (type, name) in Managers)
            {
                var t = AccessTools.TypeByName(type); var method = t != null ? AccessTools.DeclaredMethod(t, name) : null;
                if (method == null || method.IsAbstract) continue;
                harmony.Patch(method, begin, new HarmonyMethod(typeof(PerfProfile), nameof(EndMethod)) { priority = Priority.Last }); patched++;
            }
            var go = new GameObject("Overhaul performance profile");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Sampler>();
            PerfMonitor.Print(PerfMonitor.L("overhaul_perf_profile_start", Seconds.ToString("0")));
            Log.LogInfo("[Performance] Profile: " + patched + " methods timed");
        }

        static void Begin(out long __state) => __state = Stopwatch.GetTimestamp();

        static void EndScoped(long __state, string profileScope) => Add(profileScope != null && profileScope.StartsWith("MonoUpdaters.") ? profileScope.Substring(13) : profileScope ?? "?", __state);

        static void EndMethod(long __state, MethodBase __originalMethod) => Add(__originalMethod.DeclaringType.Name + "." + __originalMethod.Name, __state);

        static void Add(string label, long start)
        {
            long spent = Stopwatch.GetTimestamp() - start;
            ticks.TryGetValue(label, out long total); ticks[label] = total + spent;
            calls.TryGetValue(label, out int count); calls[label] = count + 1;
        }

        static void Stop()
        {
            harmony?.UnpatchSelf();
            harmony = null;
        }

        sealed class Sampler : MonoBehaviour
        {
            readonly List<ProfilerRecorder> recorders = new List<ProfilerRecorder>();
            readonly double[] sums = new double[Counters.Length];
            readonly int[] samples = new int[Counters.Length];
            float elapsed, frameTotal; int frames;

            void Start()
            {
                foreach (var (name, category) in Counters) recorders.Add(ProfilerRecorder.StartNew(category, name));
            }

            void Update()
            {
                float dt = Time.unscaledDeltaTime;
                // The first frame is skipped: the patches were added during it.
                if (elapsed == 0f && frames == 0) { elapsed = 0.0001f; ticks.Clear(); calls.Clear(); return; }
                elapsed += dt; frameTotal += dt; frames++;
                for (int i = 0; i < recorders.Count; i++)
                    if (recorders[i].Valid && recorders[i].Count > 0) { sums[i] += recorders[i].LastValue; samples[i]++; }
                if (elapsed < Seconds) return;
                Report();
                Destroy(gameObject);
            }

            void OnDestroy()
            {
                foreach (var r in recorders) r.Dispose();
                Stop();
            }

            void Report()
            {
                double perFrame = 1000.0 / Stopwatch.Frequency / Math.Max(1, frames);
                PerfMonitor.Print(PerfMonitor.L("overhaul_perf_profile_frame", (frameTotal / Math.Max(1, frames) * 1000f).ToString("0.00"), (frames / Math.Max(frameTotal, .0001f)).ToString("0.0")));
                PerfMonitor.Print(PerfMonitor.L("overhaul_perf_profile_scripts"));
                foreach (var pair in ticks.OrderByDescending(p => p.Value).Take(20))
                    PerfMonitor.Print("  " + pair.Key + " : " + (pair.Value * perFrame).ToString("0.000") + " ms (" + (calls[pair.Key] / (double)Math.Max(1, frames)).ToString("0.#") + "/frame)");
                PerfMonitor.Print(PerfMonitor.L("overhaul_perf_profile_render"));
                for (int i = 0; i < Counters.Length; i++)
                {
                    string name = Counters[i].Name;
                    if (samples[i] == 0) { PerfMonitor.Print("  " + name + " : n/a"); continue; }
                    double average = sums[i] / samples[i];
                    string value = name.EndsWith("Time") ? (average / 1e6).ToString("0.00") + " ms"
                        : name.StartsWith("GC") ? (average / 1024.0).ToString("0.0") + " KB"
                        : average.ToString("N0");
                    PerfMonitor.Print("  " + name + " : " + value);
                }
                PerfMonitor.Print(PerfMonitor.L("overhaul_perf_profile_scene"));
                foreach (var line in Scene()) PerfMonitor.Print("  " + line);
            }

            // What is loaded around the player right now.
            static IEnumerable<string> Scene()
            {
                yield return "Pieces (WearNTear) : " + WearNTear.s_allInstances.Count;
                yield return "Characters : " + Character.s_characters.Count;
                yield return "Network objects : " + (ZNetScene.instance ? ZNetScene.instance.m_instances.Count : 0);
                var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
                yield return "Lights : " + lights.Count(l => l.enabled && l.gameObject.activeInHierarchy) + " on (" + lights.Count(l => l.enabled && l.gameObject.activeInHierarchy && l.shadows != LightShadows.None) + " with shadows)";
                var particles = UnityEngine.Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None);
                yield return "Particle systems playing : " + particles.Count(p => p.isPlaying) + " (" + HiddenParticles.HiddenCount + " hidden behind walls, not drawn)";
                var renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
                yield return "Renderers : " + renderers.Length + " (" + renderers.Count(r => r.isVisible) + " visible)";
            }
        }
    }
}
