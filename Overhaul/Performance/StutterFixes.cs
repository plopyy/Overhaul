using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Emit;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;
using Overhaul.Utility;

namespace Overhaul.Performance
{
    // Four sources of stutter (frames much longer than the others), each with its own [Performance] option.
    internal static class StutterFixes
    {
        static bool On(PerfSwitch entry) => PerfMonitor.On(entry);

        // ---- Grass: rebuilds spread over frames ----
        // The game builds one grass patch per frame, except after a terrain change or a reset (m_forceRebuild), when it builds
        // every patch around the player in the same frame. Those rebuilds now go on within a time budget per frame; the old
        // grass of a patch stays until its new one is ready. A first build (no patch at all yet, as at load) stays whole.
        const double GrassBudgetMs = 1.5;
        static long grassStart;

        [HarmonyPatch(typeof(ClutterSystem), nameof(ClutterSystem.UpdateGrass))]
        private static class GrassSpread
        {
            private static void Prefix(ClutterSystem __instance, ref bool rebuildAll)
            {
                grassStart = Stopwatch.GetTimestamp();
                if (rebuildAll && __instance.m_patches.Count > 0 && On(OverhaulConfig.PerfGrassSpread)) rebuildAll = false;
            }
        }

        // Within the budget, the "one patch per frame" gate lets the next patch through as well.
        [HarmonyPatch(typeof(ClutterSystem), nameof(ClutterSystem.GeneratePatch))]
        private static class GrassBudget
        {
            private static void Prefix(ref bool generated)
            {
                if (generated && On(OverhaulConfig.PerfGrassSpread) && (Stopwatch.GetTimestamp() - grassStart) * 1000.0 / Stopwatch.Frequency < GrassBudgetMs) generated = false;
            }
        }

        // ---- Terrain: queued rebuilds spread over frames ----
        // Terrain tiles waiting for a rebuild (after digging, raising, loading) are all rebuilt in the same frame. They are now
        // rebuilt within a time budget per frame (at least one, the closest to the player first by the game's own order of
        // updates); the others wait for the next frame with their current look.
        const double TerrainBudgetMs = 3;
        static int terrainFrame = -1; static long terrainStart; static int terrainDone;

        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.CustomLateUpdate))]
        private static class TerrainSpread
        {
            private static bool Prefix(Heightmap __instance)
            {
                if (__instance.m_doLateUpdate != 2 || !On(OverhaulConfig.PerfTerrainSpread)) return true;
                if (terrainFrame != Time.frameCount) { terrainFrame = Time.frameCount; terrainStart = Stopwatch.GetTimestamp(); terrainDone = 0; }
                // Under the player: always now (the ground under the feet must follow at once).
                var player = Player.m_localPlayer;
                bool underPlayer = player && Utils.DistanceXZ(player.transform.position, __instance.transform.position) < __instance.m_width * __instance.m_scale;
                if (!underPlayer && terrainDone > 0 && (Stopwatch.GetTimestamp() - terrainStart) * 1000.0 / Stopwatch.Frequency >= TerrainBudgetMs) return false;
                terrainDone++;
                return true;
            }
        }

        // ---- Creatures: cheap range check first ----
        // Before the game's checks (global keys, ghost mode, interiors, stealth...), a target farther than both the hearing and
        // the seeing range is refused straight away: the result is the same, only sooner.
        [HarmonyPatch(typeof(BaseAI), nameof(BaseAI.CanSenseTarget), typeof(Transform), typeof(Vector3), typeof(float), typeof(float), typeof(float), typeof(bool), typeof(bool), typeof(Character), typeof(bool), typeof(bool))]
        private static class SenseRange
        {
            private static bool Prefix(Transform me, float hearRange, float viewRange, Character target, ref bool __result)
            {
                if (!me || !target || !On(OverhaulConfig.PerfAiSense)) return true;
                float range = Mathf.Max(hearRange, viewRange);
                if ((target.transform.position - me.position).sqrMagnitude <= range * range) return true;
                __result = false;
                return false;
            }
        }

        // ---- Terrain collision: cooked on a worker thread ----
        // A rebuilt terrain tile hands its new mesh to its collider, which makes the physics engine cook it on the main thread.
        // For tiles away from the player the mesh is cooked first on a worker thread (Physics.BakeMesh) and handed over once
        // done, which is then quick; the old collision stays meanwhile. Tiles under the player, and a tile's first collision,
        // are handed over at once as before.
        sealed class Bake { internal MeshCollider Collider; internal Mesh Mesh; internal Task Task; }
        static readonly Dictionary<MeshCollider, Bake> bakes = new Dictionary<MeshCollider, Bake>();
        static readonly List<MeshCollider> done = new List<MeshCollider>();

        static void Assign(MeshCollider collider, Mesh mesh)
        {
            if (bakes.TryGetValue(collider, out var pending)) { pending.Task.Wait(); bakes.Remove(collider); }
            var player = Player.m_localPlayer;
            bool near = !player || Vector3.Distance(player.transform.position, collider.bounds.center) < 80f;
            if (!On(OverhaulConfig.PerfAsyncCollision) || !collider.sharedMesh || near) { collider.sharedMesh = mesh; return; }
            int id = mesh.GetInstanceID(); var options = collider.cookingOptions;
            bakes[collider] = new Bake { Collider = collider, Mesh = mesh, Task = Task.Run(() => Physics.BakeMesh(id, false, options)) };
        }

        // Before a tile changes its mesh again, its bake must be over (the worker reads that mesh).
        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.RebuildCollisionMesh))]
        private static class CollisionAsync
        {
            private static void Prefix(Heightmap __instance)
            {
                if (__instance.m_collider && bakes.TryGetValue(__instance.m_collider, out var pending)) { pending.Task.Wait(); pending.Collider.sharedMesh = pending.Mesh; bakes.Remove(__instance.m_collider); }
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                var setter = AccessTools.PropertySetter(typeof(MeshCollider), nameof(MeshCollider.sharedMesh));
                int replaced = 0;
                foreach (var instruction in code)
                {
                    if (instruction.Calls(setter)) { replaced++; yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(StutterFixes), nameof(Assign))).MoveLabelsFrom(instruction); }
                    else yield return instruction;
                }
                if (replaced != 1) Log.LogWarning("[Performance] Async terrain collision: expected one collider assignment in Heightmap.RebuildCollisionMesh, replaced " + replaced);
            }
        }

        // Called every frame: finished bakes are handed to their collider (quick, the cooked mesh is ready).
        internal static void Tick()
        {
            if (bakes.Count == 0) return;
            done.Clear();
            foreach (var pair in bakes) if (pair.Value.Task.IsCompleted) done.Add(pair.Key);
            foreach (var collider in done)
            {
                var bake = bakes[collider]; bakes.Remove(collider);
                if (collider && bake.Mesh) collider.sharedMesh = bake.Mesh;
            }
        }
    }
}
