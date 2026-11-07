using System.Collections.Generic;
using UnityEngine;
using Overhaul.Utility;

namespace Overhaul.Performance
{
    // Particle systems hidden behind solid things (a fire inside a closed house, torches behind a wall or a hill) are not drawn.
    // Far ones in plain sight stay: they help to find one's way. A few systems are checked each frame: rays from the camera to
    // the centre and to the top of each one; only when every ray is stopped by a wall, a piece or the ground does its renderer
    // switch off, and it comes back as soon as one ray gets through. Systems off screen are left to Unity (already culled),
    // close ones (under NearDistance) are always drawn. Only the renderer is touched: the simulation and the game's own
    // Play/Stop keep running, so nothing changes in what they do.
    internal static class HiddenParticles
    {
        const float NearDistance = 6f, RescanInterval = 2f;
        const int ChecksPerFrame = 40;
        static readonly List<ParticleSystemRenderer> systems = new List<ParticleSystemRenderer>();
        static readonly HashSet<ParticleSystemRenderer> hidden = new HashSet<ParticleSystemRenderer>();
        static int next, mask;
        static float nextScan;
        internal static int HiddenCount => hidden.Count;

        static int Mask() => mask != 0 ? mask : mask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain");

        internal static void Tick()
        {
            var camera = Utils.GetMainCamera();
            if (!PerfMonitor.On(OverhaulConfig.PerfHiddenParticles) || !camera) { ShowAll(); return; }
            if (Time.time >= nextScan)
            {
                nextScan = Time.time + RescanInterval;
                systems.Clear();
                systems.AddRange(Object.FindObjectsByType<ParticleSystemRenderer>(FindObjectsSortMode.None));
                hidden.RemoveWhere(r => !r);
                next = 0;
            }
            Vector3 eye = camera.transform.position;
            int layers = Mask();
            for (int n = 0; n < ChecksPerFrame && systems.Count > 0; n++)
            {
                if (next >= systems.Count) next = 0;
                var renderer = systems[next++];
                if (!renderer) continue;
                bool ours = hidden.Contains(renderer);
                // Turned off by the game, or not shown on screen: nothing to do.
                if (!ours && (!renderer.enabled || !renderer.gameObject.activeInHierarchy || !renderer.isVisible)) continue;
                var bounds = renderer.bounds;
                bool covered = Vector3.Distance(eye, bounds.center) > NearDistance
                    && Blocked(eye, bounds.center, layers) && Blocked(eye, bounds.center + Vector3.up * bounds.extents.y, layers);
                if (covered && !ours) { renderer.enabled = false; hidden.Add(renderer); }
                else if (!covered && ours) { renderer.enabled = true; hidden.Remove(renderer); }
            }
        }

        // Something solid between the eye and the point (a little short of it, so the object holding the effect does not count).
        static bool Blocked(Vector3 eye, Vector3 point, int layers)
        {
            Vector3 to = point - eye;
            float distance = to.magnitude - 1f;
            return distance > 0f && Physics.Raycast(eye, to / to.magnitude, distance, layers, QueryTriggerInteraction.Ignore);
        }

        static void ShowAll()
        {
            if (hidden.Count == 0) return;
            foreach (var renderer in hidden) if (renderer) renderer.enabled = true;
            hidden.Clear();
        }
    }
}
