using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

namespace Overhaul.Performance
{
    // "operf hud": a few lines of text on the left of the screen, half way down: frames per second, the slowest 1 % of the
    // last 5 s, and the main thread, render thread and GPU times of a frame, refreshed 4 times a second. A diagnostic
    // tool to see when and where frames are lost; off by default, its state is not saved.
    internal sealed class PerfHud : MonoBehaviour
    {
        static PerfHud instance;
        internal static bool Shown => instance;

        ProfilerRecorder main, render, gpu, draws;
        readonly Queue<float> recent = new Queue<float>();
        float recentTotal, sampleTime, frameTime; int sampleFrames;
        string text = "";
        GUIStyle style, shadow;
        string labelFps, labelLow, labelCpu, labelRender, labelGpu, labelDraws;

        internal static void Toggle()
        {
            if (instance) { Destroy(instance.gameObject); return; }
            var go = new GameObject("Overhaul performance hud");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            instance = go.AddComponent<PerfHud>();
        }

        void Awake()
        {
            main = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "CPU Main Thread Frame Time");
            render = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "CPU Render Thread Frame Time");
            gpu = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GPU Frame Time");
            draws = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            labelFps = PerfMonitor.L("overhaul_perf_hud_fps"); labelLow = PerfMonitor.L("overhaul_perf_hud_low");
            labelCpu = PerfMonitor.L("overhaul_perf_hud_cpu"); labelRender = PerfMonitor.L("overhaul_perf_hud_render");
            labelGpu = PerfMonitor.L("overhaul_perf_hud_gpu"); labelDraws = PerfMonitor.L("overhaul_perf_hud_draws");
        }

        void OnDestroy()
        {
            main.Dispose(); render.Dispose(); gpu.Dispose(); draws.Dispose();
            if (instance == this) instance = null;
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            recent.Enqueue(dt); recentTotal += dt;
            while (recentTotal > 5f && recent.Count > 1) recentTotal -= recent.Dequeue();
            sampleTime += dt; frameTime += dt; sampleFrames++;
            if (sampleTime < .25f) return;
            float fps = sampleFrames / Mathf.Max(frameTime, .0001f);
            var sorted = new List<float>(recent); sorted.Sort((a, b) => b.CompareTo(a));
            int count = Mathf.Max(1, sorted.Count / 100); float worst = 0; for (int i = 0; i < count; i++) worst += sorted[i];
            float low = count / Mathf.Max(worst, .0001f);
            string colour = fps < 30 ? "#ff5050" : fps < 60 ? "#ffd23c" : "#78ff78";
            text = "<b><color=" + colour + ">" + fps.ToString("0") + " " + labelFps + "</color></b>  (" + labelLow + " " + low.ToString("0") + ")\n"
                 + labelCpu + " " + Ms(main) + "\n" + labelRender + " " + Ms(render) + "\n" + labelGpu + " " + Ms(gpu) + "\n"
                 + labelDraws + " " + (draws.Valid && draws.Count > 0 ? draws.LastValue.ToString("N0") : "n/a");
            sampleTime = frameTime = 0; sampleFrames = 0;
        }

        static string Ms(ProfilerRecorder r) => r.Valid && r.Count > 0 ? (r.LastValue / 1e6).ToString("0.0") + " ms" : "n/a";

        void OnGUI()
        {
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(16 * Screen.height / 1080f), richText = true };
                style.normal.textColor = Color.white;
                shadow = new GUIStyle(style); shadow.normal.textColor = new Color(0, 0, 0, .85f);
            }
            float h = style.fontSize * 7f, y = Screen.height * .5f - h * .5f, x = 12f;
            string plain = System.Text.RegularExpressions.Regex.Replace(text, "<.*?>", "");
            GUI.Label(new Rect(x + 1.5f, y + 1.5f, 600, h), plain, shadow);
            GUI.Label(new Rect(x, y, 600, h), text, style);
        }
    }
}
