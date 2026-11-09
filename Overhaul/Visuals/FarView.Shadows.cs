using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using Overhaul.Performance;
using Overhaul.Utility;

namespace Overhaul.Visuals
{
    // Far view, terrain shadows: the game's shadows end a few hundred metres out, past that mountains cast none. For each
    // far land vertex a background thread walks toward the sun over the far tiles' own heights (steps growing from the
    // tile's spacing to 6 km) and keeps the steepest rise seen: higher than the sun = in shade, softened over a small angle.
    // The result goes in the tile mesh's uv1 and a second material on the tile (FarShadow) darkens the lit screen there,
    // only beyond the game's shadow distance and terrain. Worked out again when the sun moved 0.5 degree (at most every
    // 2 s) or the rings moved. Fades out at dusk and is off at night: nothing to work out then.
    internal static partial class FarView
    {
        const float ShadowStrength = 0.5f, ShadowSoftness = 0.02f, ShadowReach = 6000f;
        static Material shadowMaterial;
        static bool shadowsDirty; static volatile bool shadowRunning;
        static Vector3 shadowSun;
        static float nextShadow, shadowFade;
        static int shadowEpoch;
        static readonly ConcurrentQueue<(Tile Tile, Vector2[] Shade, int Epoch)> shadowDone = new ConcurrentQueue<(Tile, Vector2[], int)>();

        // Toward the sun, as the game turns its sun light (before it turns it around into the moon at night).
        internal static Vector3 SunDirection()
        {
            var env = EnvMan.instance;
            var current = env.GetCurrentEnvironment();
            float angle = current != null ? current.m_sunAngle : 60f;
            var rotation = Quaternion.Euler(-90f + angle, 0f, 0f) * Quaternion.Euler(0f, -90f, 0f) * Quaternion.Euler(-90f + 360f * env.GetDayFraction(), 0f, 0f);
            return -(rotation * Vector3.forward);
        }

        static void TickShadows()
        {
            bool on = ClientVisuals.FarShadows && EnvMan.instance;
            if (!shadowMaterial)
            {
                if (!on || !farShadowShader || !farShadowShader.isSupported) return;
                shadowMaterial = new Material(farShadowShader);
            }
            var sun = on ? SunDirection() : Vector3.down;
            // Day light: full with the sun 10 degrees up, none under 2 degrees (night: skipped altogether).
            float target = on ? Mathf.Clamp01((sun.y - 0.03f) / 0.15f) : 0f;
            shadowFade = Mathf.MoveTowards(shadowFade, target, Time.deltaTime * 0.5f);
            float hide = ZoneSystem.instance ? Heightmap.GetLodHideDistance() : 128f;
            shadowMaterial.SetFloat("_Strength", ShadowStrength * shadowFade);
            shadowMaterial.SetFloat("_Start", Mathf.Max(QualitySettings.shadowDistance, hide));
            SetShadowMaterial(shadowFade > 0f);

            ApplyShadows();
            if (target <= 0f || shadowRunning || Time.time < nextShadow) return;
            if (!shadowsDirty && Vector3.Angle(sun, shadowSun) < 0.5f) return;
            nextShadow = Time.time + 2f;
            shadowsDirty = false; shadowSun = sun; shadowRunning = true;
            // A snapshot of the shown tiles (finest first) for the worker.
            var tiles = new List<Tile>();
            foreach (var ring in rings) foreach (var t in ring.Shown.Values) if (t.Go && t.Heights != null) tiles.Add(t);
            int run = shadowEpoch;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { foreach (var t in tiles) shadowDone.Enqueue((t, Shade(t, tiles, sun), run)); }
                catch (Exception e) { Log.LogWarning("[FarView] far shadows: " + e.Message); }
                finally { shadowRunning = false; }
            });
        }

        // The tiles' second material, the shadow pass, while there is daylight.
        static void SetShadowMaterial(bool shown)
        {
            foreach (var ring in rings)
                foreach (var t in ring.Shown.Values)
                {
                    if (!t.Go || t.Shaded == shown) continue;
                    t.Shaded = shown;
                    t.Go.GetComponent<MeshRenderer>().sharedMaterials = shown ? new[] { material, shadowMaterial } : new[] { material };
                }
        }

        static void ApplyShadows()
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalMilliseconds < 1.0 && shadowDone.TryDequeue(out var r))
                if (r.Epoch == shadowEpoch && r.Tile.Mesh) r.Tile.Mesh.SetUVs(1, r.Shade);
        }

        // Height of the far land at a point: the finest shown tile holding it, bilinear between its points.
        static bool FarHeight(List<Tile> tiles, float x, float z, out float h)
        {
            foreach (var t in tiles)
            {
                float spacing = Spacing(t.Level), size = spacing * Quads;
                float u = (x - t.X * size) / spacing, v = (z - t.Z * size) / spacing;
                if (u < 0f || v < 0f || u >= Quads || v >= Quads) continue;
                int l = (int)u, k = (int)v; float fu = u - l, fv = v - k;
                var hs = t.Heights; int i = k * Verts + l;
                h = Mathf.Lerp(Mathf.Lerp(hs[i], hs[i + 1], fu), Mathf.Lerp(hs[i + Verts], hs[i + Verts + 1], fu), fv) - 2f * t.Level;
                return true;
            }
            h = 0f; return false;
        }

        static Vector2[] Shade(Tile tile, List<Tile> tiles, Vector3 sun)
        {
            var shade = new Vector2[Verts * Verts + SkirtVerts];
            var flat = new Vector2(sun.x, sun.z);
            float run = flat.magnitude;
            if (run < 1e-4f) return shade;
            var dir = flat / run;
            float rise = sun.y / run; // the sun's height per metre toward it
            float spacing = Spacing(tile.Level), size = TileSize(tile.Level), y0 = -2f * tile.Level;
            for (int k = 0; k < Verts; k++)
                for (int l = 0; l < Verts; l++)
                {
                    int i = k * Verts + l;
                    float px = tile.X * size + l * spacing, pz = tile.Z * size + k * spacing, py = tile.Heights[i] + y0 + 1f;
                    float steepest = -1f;
                    for (float d = spacing; d < ShadowReach; d *= 1.15f)
                    {
                        if (!FarHeight(tiles, px + dir.x * d, pz + dir.y * d, out float h)) break;
                        float slope = (h - py) / d;
                        if (slope > steepest) { steepest = slope; if (steepest > rise + ShadowSoftness) break; }
                    }
                    shade[i] = new Vector2(Mathf.Clamp01((steepest - rise + ShadowSoftness) / (2f * ShadowSoftness)), 0f);
                }
            for (int s = 0; s < SkirtVerts; s++) shade[Verts * Verts + s] = shade[SkirtSource(s)];
            return shade;
        }

        static void StopShadows()
        {
            shadowEpoch++;
            while (shadowDone.TryDequeue(out _)) { }
            shadowsDirty = true; shadowFade = 0f;
            if (shadowMaterial) UnityEngine.Object.Destroy(shadowMaterial);
            shadowMaterial = null;
        }
    }
}
