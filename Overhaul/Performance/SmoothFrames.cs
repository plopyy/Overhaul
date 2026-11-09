using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Overhaul.Utility;

namespace Overhaul.Performance
{
    // Smooth frames: client optimisations that keep the game's own result while doing less work per frame (terrain that is
    // not rebuilt when nothing changed, a terrain builder that does not nap while it has work, finished terrain kept for
    // when you come back, grass arrays reused, buildings not judged while their floors stream in, a few needless copies and
    // allocations removed). One switch in the settings window (saved like the game's own graphics settings, no config
    // entry); `o_opti <name> on|off` switches each part live for measuring.
    internal static partial class SmoothFrames
    {
        private const string PrefKey = "Overhaul_SmoothFrames";

        // Parts, each switchable on its own with o_opti (session only).
        internal static bool UnchangedGround = true, PaintOnly = true, BuilderNoNap = true, TerrainMemory = true,
            GrassArrays = true, SettlingBuildings = true, Housekeeping = true;

        static int enabled = -1;
        internal static bool Enabled
        {
            get { if (enabled < 0) enabled = PlatformPrefs.GetInt(PrefKey, 1) == 1 ? 1 : 0; return enabled == 1; }
            set { enabled = value ? 1 : 0; PlatformPrefs.SetInt(PrefKey, enabled); ApplyHousekeeping(); }
        }

        static bool On(bool part) => part && !PerfMonitor.AllOff && Enabled;

        internal static readonly (string Id, Func<bool> Get, Action<bool> Set)[] Parts =
        {
            ("ground", () => UnchangedGround, v => UnchangedGround = v),
            ("paint", () => PaintOnly, v => PaintOnly = v),
            ("builder", () => BuilderNoNap, v => BuilderNoNap = v),
            ("terrainmemory", () => TerrainMemory, v => TerrainMemory = v),
            ("grassarrays", () => GrassArrays, v => GrassArrays = v),
            ("settling", () => SettlingBuildings, v => SettlingBuildings = v),
            ("housekeeping", () => Housekeeping, v => { Housekeeping = v; ApplyHousekeeping(); }),
            ("transitions", () => Transitions, v => Transitions = v),
            ("dungeons", () => DungeonSpread, v => DungeonSpread = v),
        };

        internal static long GroundKept, TerrainHits, TerrainMisses, ArraysReused, BuildingsHeld;

        internal static void Tick()
        {
            ApplyHousekeeping();
            GrassPool.Tick();
            TickTransitions();
            TickDungeons();
        }

        // ---- Ground meshes kept when nothing changed ----
        // A piece streaming in or out asks the terrain tile around it to rebuild, which rebuilds its collision (the physics
        // engine cooks it again) and render meshes even when no height changed. The heights of each tile's last build are
        // remembered (exact bits) with its position; an identical rebuild keeps both meshes. Paint changes do not use these
        // meshes (they go through the paint texture), so they still show.
        sealed class LastBuild { internal float[] Heights; internal Vector3 Position; internal bool RenderDone; }
        static readonly ConditionalWeakTable<Heightmap, LastBuild> builds = new ConditionalWeakTable<Heightmap, LastBuild>();
        static Heightmap keptCollision;

        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.RebuildCollisionMesh))]
        private static class KeepCollision
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(Heightmap __instance)
            {
                keptCollision = null;
                if (!On(UnchangedGround) || !__instance.m_collisionMesh || !builds.TryGetValue(__instance, out var last)) return true;
                if (__instance.m_collider && __instance.m_collider.sharedMesh != __instance.m_collisionMesh) return true;
                if (last.Position != __instance.transform.position || !SameHeights(last.Heights, __instance.m_heights)) return true;
                keptCollision = __instance;
                GroundKept++;
                return false;
            }

            private static void Postfix(Heightmap __instance, bool __runOriginal)
            {
                if (!__runOriginal) return;
                var last = builds.GetOrCreateValue(__instance);
                var heights = __instance.m_heights;
                if (last.Heights == null || last.Heights.Length != heights.Count) last.Heights = new float[heights.Count];
                heights.CopyTo(last.Heights);
                last.Position = __instance.transform.position;
                last.RenderDone = false;
            }
        }

        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.RebuildRenderMesh))]
        private static class KeepRender
        {
            private static bool Prefix(Heightmap __instance)
            {
                bool keep = keptCollision == __instance && builds.TryGetValue(__instance, out var last) && last.RenderDone
                    && __instance.m_renderMesh && __instance.m_meshFilter && __instance.m_meshFilter.sharedMesh == __instance.m_renderMesh;
                keptCollision = null;
                return !keep;
            }

            private static void Postfix(Heightmap __instance, bool __runOriginal)
            {
                if (__runOriginal && builds.TryGetValue(__instance, out var last)) last.RenderDone = true;
            }
        }

        static bool SameHeights(float[] last, List<float> now)
        {
            if (last == null || last.Length != now.Count) return false;
            for (int i = 0; i < last.Length; i++)
                if (BitConverter.SingleToInt32Bits(last[i]) != BitConverter.SingleToInt32Bits(now[i])) return false;
            return true;
        }

        // ---- Paint-only terrain modifiers ----
        // A terrain modifier that only paints (no levelling, no smoothing: paths, cultivated ground) asks the tiles for a
        // full rebuild like any other; they are asked for the paint-only update the game has for this case.
        [HarmonyPatch(typeof(TerrainModifier), nameof(TerrainModifier.PokeHeightmaps))]
        private static class PaintOnlyPoke
        {
            private static bool Prefix(TerrainModifier __instance, bool forcedDelay)
            {
                if (!On(PaintOnly) || __instance.m_level || __instance.m_smooth || !__instance.m_paintCleared) return true;
                bool delayed = !TerrainModifier.m_triggerOnPlaced || forcedDelay;
                foreach (var heightmap in Heightmap.GetAllHeightmaps())
                    if (heightmap.TerrainVSModifier(__instance)) heightmap.Poke(delayed ? 2 : 0, true);
                if (ClutterSystem.instance) ClutterSystem.instance.ResetGrass(__instance.transform.position, __instance.GetRadius());
                return false;
            }
        }

        // ---- Terrain builder: no nap while tiles wait ----
        // The background terrain builder sleeps 10 ms after each pass, even with tiles queued; it only sleeps now when the
        // queue is empty.
        public static int BuilderNap()
        {
            if (!On(BuilderNoNap)) return 10;
            var builder = HeightmapBuilder.instance;
            if (builder == null) return 10;
            lock (builder.m_lock) return builder.m_toBuild.Count > 0 ? 0 : 10;
        }

        [HarmonyPatch(typeof(HeightmapBuilder), "BuildThread")]
        private static class NoNap
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                var sleep = AccessTools.Method(typeof(System.Threading.Thread), nameof(System.Threading.Thread.Sleep), new[] { typeof(int) });
                var list = new List<CodeInstruction>(code);
                int replaced = 0;
                for (int i = 0; i + 1 < list.Count; i++)
                    if ((list[i].opcode == OpCodes.Ldc_I4_S || list[i].opcode == OpCodes.Ldc_I4) && Convert.ToInt32(list[i].operand) == 10 && list[i + 1].Calls(sleep))
                    {
                        list[i] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SmoothFrames), nameof(BuilderNap))).MoveLabelsFrom(list[i]);
                        replaced++;
                    }
                if (replaced != 1) Log.LogWarning("[Performance] Terrain builder: expected one 10 ms sleep in HeightmapBuilder.BuildThread, replaced " + replaced);
                return list;
            }
        }

        // ---- Terrain memory ----
        // Finished terrain (the heights the world generator gives a tile, before player edits) is kept for the session,
        // least recently used first out, so walking back over land you crossed does not generate it again. The game only
        // reads that data, so one copy can serve again. Size from the machine's memory.
        readonly struct TileKey : IEquatable<TileKey>
        {
            readonly int x, z, width; readonly float scale; readonly bool distant;
            internal TileKey(Vector3 c, int w, float s, bool d) { x = Mathf.RoundToInt(c.x); z = Mathf.RoundToInt(c.z); width = w; scale = s; distant = d; }
            public bool Equals(TileKey o) => x == o.x && z == o.z && width == o.width && scale == o.scale && distant == o.distant;
            public override bool Equals(object o) => o is TileKey k && Equals(k);
            public override int GetHashCode() => ((x * 397 ^ z) * 397 ^ width) * 397 ^ (distant ? 1 : 0);
        }
        static readonly Dictionary<TileKey, LinkedListNode<(TileKey Key, HeightmapBuilder.HMBuildData Data)>> memory = new Dictionary<TileKey, LinkedListNode<(TileKey, HeightmapBuilder.HMBuildData)>>();
        static readonly LinkedList<(TileKey Key, HeightmapBuilder.HMBuildData Data)> recency = new LinkedList<(TileKey, HeightmapBuilder.HMBuildData)>();
        static WorldGenerator memoryWorld;
        static int MemoryLimit => SystemInfo.systemMemorySize >= 30000 ? 2048 : SystemInfo.systemMemorySize >= 15000 ? 1024 : SystemInfo.systemMemorySize >= 11000 ? 512 : 256;

        static HeightmapBuilder.HMBuildData Remembered(Vector3 center, int width, float scale, bool distant, WorldGenerator world)
        {
            if (!On(TerrainMemory) || world == null) return null;
            if (world != memoryWorld) { memory.Clear(); recency.Clear(); memoryWorld = world; return null; }
            if (!memory.TryGetValue(new TileKey(center, width, scale, distant), out var node)) { TerrainMisses++; return null; }
            recency.Remove(node); recency.AddFirst(node);
            TerrainHits++;
            return node.Value.Data;
        }

        static void Remember(HeightmapBuilder.HMBuildData data)
        {
            if (data == null || !On(TerrainMemory) || data.m_worldGen != memoryWorld) return;
            var key = new TileKey(data.m_center, data.m_width, data.m_scale, data.m_distantLod);
            if (memory.TryGetValue(key, out var old)) { recency.Remove(old); memory.Remove(key); }
            memory[key] = recency.AddFirst((key, data));
            while (memory.Count > MemoryLimit) { var last = recency.Last; recency.RemoveLast(); memory.Remove(last.Value.Key); }
        }

        [HarmonyPatch(typeof(HeightmapBuilder), nameof(HeightmapBuilder.IsTerrainReady))]
        private static class ReadyFromMemory
        {
            private static bool Prefix(Vector3 center, int width, float scale, bool distantLod, WorldGenerator worldGen, ref bool __result)
            {
                if (Remembered(center, width, scale, distantLod, worldGen) == null) return true;
                __result = true;
                return false;
            }
        }

        [HarmonyPatch(typeof(HeightmapBuilder), nameof(HeightmapBuilder.RequestTerrain))]
        private static class RequestFromMemory
        {
            private static bool Prefix(Vector3 center, int width, float scale, bool distantLod, WorldGenerator worldGen, ref HeightmapBuilder.HMBuildData __result)
            {
                var data = Remembered(center, width, scale, distantLod, worldGen);
                if (data == null) return true;
                __result = data;
                return false;
            }

            private static void Postfix(HeightmapBuilder.HMBuildData __result, bool __runOriginal) { if (__runOriginal) Remember(__result); }
        }

        [HarmonyPatch(typeof(ZoneSystem), "OnDestroy")]
        private static class ForgetTerrain
        {
            private static void Postfix() { memory.Clear(); recency.Clear(); memoryWorld = null; }
        }

        // ---- Grass arrays reused ----
        // Each grass patch allocates a 1024-matrix array (64 KB) per kind of grass, and patches come and go as you move, which
        // feeds the garbage collector. Arrays of destroyed patches are cleared and handed to the next ones.
        internal static class GrassPool
        {
            const int Size = 1024, MaxKept = 512;
            static readonly Stack<Matrix4x4[]> kept = new Stack<Matrix4x4[]>();
            static readonly Dictionary<InstanceRenderer, Matrix4x4[]> parked = new Dictionary<InstanceRenderer, Matrix4x4[]>();
            static readonly List<InstanceRenderer> gone = new List<InstanceRenderer>();
            static int mainThread = -1;

            public static Matrix4x4[] Rent()
            {
                if (kept.Count > 0 && System.Threading.Thread.CurrentThread.ManagedThreadId == mainThread && On(GrassArrays))
                {
                    var array = kept.Pop();
                    Array.Clear(array, 0, array.Length);
                    ArraysReused++;
                    return array;
                }
                return new Matrix4x4[Size];
            }

            internal static void Tick()
            {
                if (mainThread < 0) mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                if (parked.Count == 0) return;
                gone.Clear();
                foreach (var pair in parked) if (!pair.Key) gone.Add(pair.Key);
                foreach (var renderer in gone)
                {
                    var array = parked[renderer];
                    parked.Remove(renderer);
                    if (On(GrassArrays) && kept.Count < MaxKept) kept.Push(array);
                }
            }

            [HarmonyPatch(typeof(InstanceRenderer), MethodType.Constructor)]
            private static class Allocate
            {
                private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
                {
                    var field = AccessTools.Field(typeof(InstanceRenderer), "m_instances");
                    var list = new List<CodeInstruction>(code);
                    int found = 0;
                    for (int i = 2; i < list.Count; i++)
                        if (list[i].StoresField(field) && list[i - 1].opcode == OpCodes.Newarr && Equals(list[i - 1].operand, typeof(Matrix4x4)) && list[i - 2].LoadsConstant(Size))
                        {
                            list[i - 2] = new CodeInstruction(OpCodes.Nop).MoveLabelsFrom(list[i - 2]);
                            list[i - 1] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GrassPool), nameof(Rent)));
                            found++;
                        }
                    if (found != 1) Log.LogWarning("[Performance] Grass arrays: expected one instance array in InstanceRenderer, found " + found);
                    return list;
                }
            }

            [HarmonyPatch(typeof(InstanceRenderer), "OnDisable")]
            private static class Park { private static void Postfix(InstanceRenderer __instance) { var a = __instance.m_instances; if (a != null && a.Length == Size) parked[__instance] = a; } }

            [HarmonyPatch(typeof(InstanceRenderer), "OnEnable")]
            private static class Unpark { private static void Postfix(InstanceRenderer __instance) => parked.Remove(__instance); }
        }

        // ---- Buildings settling ----
        // A building piece is not judged for support while the objects under it may still be appearing: for up to two minutes
        // after it was created, while an object waiting to be created lies in its zone or one around it. Stops bases from
        // breaking while their floors stream in.
        static int pendingFrame = -1;
        static readonly HashSet<int> pendingZones = new HashSet<int>();
        static int ZoneKey(int x, int y) => (x << 16) ^ (y & 0xFFFF);

        [HarmonyPatch(typeof(WearNTear), "ShouldUpdate")]
        private static class Settling
        {
            private static void Postfix(WearNTear __instance, float time, ref bool __result)
            {
                if (!__result || !On(SettlingBuildings) || !ZNetScene.instance) return;
                float created = __instance.m_createTime;
                if (created < 0f || time - created > 120f) return;
                var scene = ZNetScene.instance;
                if (pendingFrame != Time.frameCount)
                {
                    pendingFrame = Time.frameCount;
                    pendingZones.Clear();
                    foreach (var zdo in scene.m_tempCurrentObjects2)
                        if (zdo != null && !zdo.Created && scene.GetPrefab(zdo.GetPrefab())) { var s = zdo.GetSector(); pendingZones.Add(ZoneKey(s.x, s.y)); }
                }
                if (pendingZones.Count == 0) return;
                var zone = ZoneSystem.GetZone(__instance.transform.position);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        if (pendingZones.Contains(ZoneKey(zone.x + dx, zone.y + dy))) { __result = false; BuildingsHeld++; return; }
            }
        }

        // ---- Housekeeping: same results, less waste ----
        static bool collisionReuseSet, collisionReuseWas;
        static void ApplyHousekeeping()
        {
            bool want = On(Housekeeping);
            if (want && !collisionReuseSet) { collisionReuseWas = Physics.reuseCollisionCallbacks; Physics.reuseCollisionCallbacks = true; collisionReuseSet = true; }
            else if (!want && collisionReuseSet) { Physics.reuseCollisionCallbacks = collisionReuseWas; collisionReuseSet = false; }
        }

        // Collision contacts read into reused arrays (one per contact count) instead of a new array per callback.
        static readonly ContactPoint[][] contactArrays = new ContactPoint[65][];
        static readonly ContactPoint[] noContacts = new ContactPoint[0];
        public static ContactPoint[] Contacts(Collision collision)
        {
            if (!On(Housekeeping)) return collision.contacts;
            int count = collision.contactCount;
            if (count <= 0) return noContacts;
            if (count >= contactArrays.Length) return collision.contacts;
            var array = contactArrays[count] ?? (contactArrays[count] = new ContactPoint[count]);
            collision.GetContacts(array);
            return array;
        }

        static IEnumerable<CodeInstruction> ReuseContacts(IEnumerable<CodeInstruction> code, string where)
        {
            var getter = AccessTools.PropertyGetter(typeof(Collision), nameof(Collision.contacts));
            int replaced = 0;
            foreach (var instruction in code)
            {
                if (instruction.Calls(getter)) { replaced++; yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SmoothFrames), nameof(Contacts))).MoveLabelsFrom(instruction); }
                else yield return instruction;
            }
            if (replaced == 0) Log.LogWarning("[Performance] Collision contacts: none found in " + where);
        }

        [HarmonyPatch(typeof(Character), "OnCollisionStay")]
        private static class CharacterContacts { private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> c) => ReuseContacts(c, "Character.OnCollisionStay"); }
        [HarmonyPatch(typeof(ImpactEffect), nameof(ImpactEffect.OnCollisionEnter))]
        private static class ImpactContacts { private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> c) => ReuseContacts(c, "ImpactEffect.OnCollisionEnter"); }
        [HarmonyPatch(typeof(FloatingTerrain), "OnCollisionStay")]
        private static class FloatingContacts { private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> c) => ReuseContacts(c, "FloatingTerrain.OnCollisionStay"); }

        // A package written into another: its bytes go straight from its buffer instead of through a copied array.
        [HarmonyPatch(typeof(ZPackage), nameof(ZPackage.Write), typeof(ZPackage))]
        private static class PackageInPackage
        {
            private static bool Prefix(ZPackage __instance, ZPackage pkg)
            {
                if (!On(Housekeeping)) return true;
                pkg.m_writer.Flush();
                pkg.m_stream.Flush();
                int length = (int)pkg.m_stream.Length;
                __instance.m_writer.Write(length);
                __instance.m_writer.Write(pkg.m_stream.GetBuffer(), 0, length);
                return false;
            }
        }

        // A projectile standing still asked Unity for a rotation looking nowhere, which logs a warning each physics step.
        public static Quaternion ProjectileLook(Vector3 forward) =>
            forward == Vector3.zero && On(Housekeeping) ? Quaternion.identity : Quaternion.LookRotation(forward);

        [HarmonyPatch(typeof(Projectile), "FixedUpdate")]
        private static class ProjectileStill
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                var look = AccessTools.Method(typeof(Quaternion), nameof(Quaternion.LookRotation), new[] { typeof(Vector3) });
                foreach (var instruction in code)
                    yield return instruction.Calls(look) ? new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SmoothFrames), nameof(ProjectileLook))).MoveLabelsFrom(instruction) : instruction;
            }
        }

        // Endless loading screen: the area counts as ready when the only objects it still waits for are instances whose
        // game object is already destroyed (they would never come).
        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.IsAreaReady))]
        private static class StuckLoading
        {
            static float nextWarning;
            private static void Postfix(ZNetScene __instance, Vector3 point, ref bool __result)
            {
                if (__result || !On(Housekeeping) || !ZoneSystem.instance || !ZoneSystem.instance.IsZoneLoaded(ZoneSystem.GetZone(point))) return;
                string stuck = null;
                foreach (var zdo in __instance.m_tempCurrentObjects)
                {
                    if (!__instance.IsPrefabZDOValid(zdo)) continue;
                    if (!__instance.m_instances.TryGetValue(zdo, out var view)) return;
                    if (!view) stuck = stuck ?? zdo.GetPrefab() + " at " + zdo.GetPosition();
                }
                if (stuck == null) return;
                __result = true;
                if (Time.realtimeSinceStartup >= nextWarning) { nextWarning = Time.realtimeSinceStartup + 10f; Log.LogWarning("[Performance] Loading went on although an object was gone: " + stuck); }
            }
        }
    }
}
