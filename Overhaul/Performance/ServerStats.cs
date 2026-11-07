using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Performance
{
    // Server side of "operf hud". The server measures each second its frames per second, its average and worst frame
    // time and the CPU its process used (percent of the whole machine, all cores); a client showing the hud asks once a
    // second and gets them back. The answer echoes the client's send time, so the client also measures the round trip
    // through the server (network plus the server's own delay) and how much it varies over the last 10 answers: a steady
    // connection keeps that spread small. Nothing is sent while no hud is shown.
    internal static class ServerStats
    {
        const string AskRpc = "Overhaul_PerfAsk", StatsRpc = "Overhaul_PerfStats";

        // Measured where the world runs (dedicated server, or the host's own game).
        static float second, worst; static int frames;
        static TimeSpan lastCpu; static float lastCpuAt;
        internal static float Fps, AverageMs, WorstMs, Cpu = -1f;

        // Received by a client.
        internal static float ServerFps, ServerAverageMs, ServerWorstMs, ServerCpu = -1f, ReceivedAt = -100f;
        static readonly Queue<float> trips = new Queue<float>();
        internal static float RoundTrip = -1f, Spread;

        internal static void Tick()
        {
            float dt = Time.unscaledDeltaTime;
            second += dt; frames++; worst = Mathf.Max(worst, dt);
            if (second < 1f) return;
            Fps = frames / second; AverageMs = second / frames * 1000f; WorstMs = worst * 1000f;
            try
            {
                var used = Process.GetCurrentProcess().TotalProcessorTime; float now = Time.realtimeSinceStartup;
                if (lastCpuAt > 0f) Cpu = (float)(used - lastCpu).TotalSeconds / Mathf.Max(.001f, now - lastCpuAt) / Environment.ProcessorCount * 100f;
                lastCpu = used; lastCpuAt = now;
            }
            catch { Cpu = -1f; }
            second = 0f; frames = 0; worst = 0f;
            // The hud asks the server once a second.
            if (PerfHud.Shown && ZNet.instance && !ZNet.instance.IsServer())
                ZNet.instance.GetServerRPC()?.Invoke(AskRpc, (double)Time.realtimeSinceStartup);
        }

        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        private static class Connection
        {
            private static void Postfix(ZNet __instance, ZNetPeer peer)
            {
                if (__instance.IsServer())
                    peer.m_rpc.Register<double>(AskRpc, (rpc, sent) =>
                    {
                        var pkg = new ZPackage();
                        pkg.Write(sent); pkg.Write(Fps); pkg.Write(AverageMs); pkg.Write(WorstMs); pkg.Write(Cpu);
                        rpc.Invoke(StatsRpc, pkg);
                    });
                else
                    peer.m_rpc.Register<ZPackage>(StatsRpc, (rpc, pkg) =>
                    {
                        double sent = pkg.ReadDouble();
                        ServerFps = pkg.ReadSingle(); ServerAverageMs = pkg.ReadSingle(); ServerWorstMs = pkg.ReadSingle(); ServerCpu = pkg.ReadSingle();
                        ReceivedAt = Time.realtimeSinceStartup;
                        float trip = (float)((Time.realtimeSinceStartup - sent) * 1000.0);
                        if (trip < 0f || trip > 60000f) return;
                        trips.Enqueue(trip); while (trips.Count > 10) trips.Dequeue();
                        float min = float.MaxValue, max = 0f, total = 0f;
                        foreach (float t in trips) { min = Mathf.Min(min, t); max = Mathf.Max(max, t); total += t; }
                        RoundTrip = total / trips.Count; Spread = max - min;
                    });
            }
        }
    }
}
