using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Overhaul.Utility;
using Object = UnityEngine.Object;

namespace Overhaul.Performance
{
    // Repeated still objects of the world (trees, rocks, bushes, location props) drawn by Overhaul instead of one renderer
    // each: every copy of a model with a material is drawn in one instanced call (Graphics.RenderMeshInstanced), shadows
    // included, and which copies are seen, at which level of detail, is worked out each frame on several cores.
    // The objects stay as they are in the game (colliders, network, interaction); only their renderers are hidden
    // (Renderer.forceRenderingOff, the game's own enabled flags untouched).
    //
    // Taken: objects coming into the world (networked ones on creation, locations' visuals once spawned), split per LODGroup
    // (each a copy with its levels) and per renderer outside any LODGroup. Left alone: pieces (merged by PieceBatcher), creatures,
    // items, anything with physics, animation, a ship or cart, things that change look by themselves (pickables, mined rocks,
    // doors, fires), renderers with values of their own (property blocks), skinned meshes and particles, and materials without
    // instancing. A copy that moves, is hidden, changes or goes away is given back to its own renderers at once (a slice of
    // the copies is checked each frame, and an object going inactive gives its copies back immediately).
    internal static class InstancedProps
    {
        const int Batch = 1023, ChecksPerFrame = 400;
        const float ShadowMargin = 40f, RegisterBudgetMs = 1f;

        // A model drawn instanced: mesh, sub-mesh, material and render settings.
        sealed class Proto
        {
            internal Mesh Mesh; internal int Sub; internal Material Material; internal ShadowCastingMode Shadow; internal bool Receive; internal int Layer;
            internal readonly List<Matrix4x4> Matrices = new List<Matrix4x4>(256);
            internal Bounds Bounds; internal bool HasBounds;
        }
        internal struct Part { internal int Proto; internal Matrix4x4 Matrix; internal Renderer Renderer; }
        // One copy: an object, or one of its LODGroups, with its parts per level and the screen heights where levels change.
        internal sealed class Copy
        {
            internal Tracker Owner; internal Vector3 Center; internal float Radius, Size;
            internal Part[][] Levels; internal float[] Heights; internal bool Dead;
        }

        static readonly Dictionary<(Mesh, int, Material, ShadowCastingMode, bool, int), int> protoIndex = new Dictionary<(Mesh, int, Material, ShadowCastingMode, bool, int), int>();
        static readonly List<Proto> protos = new List<Proto>();
        static readonly List<Copy> copies = new List<Copy>();
        static int[] chosen = new int[0];
        static readonly Queue<GameObject> queue = new Queue<GameObject>();
        static readonly Plane[] planes = new Plane[6];
        static readonly Matrix4x4[] batch = new Matrix4x4[Batch];
        static int checkIndex;
        static bool active;
        internal static int Copies, Drawn, Calls;

        static bool On => PerfMonitor.On(OverhaulConfig.PerfInstancedProps);

        // Removes the copies of an object as soon as it goes inactive or away.
        internal sealed class Tracker : MonoBehaviour
        {
            internal readonly List<Copy> Copies = new List<Copy>();
            void OnDisable() { foreach (var c in Copies) Release(c); Copies.Clear(); Destroy(this); }
        }

        // ---- Registration ----
        [HarmonyPatch(typeof(ZNetView), "Awake")]
        private static class OnNetworkObject { private static void Postfix(ZNetView __instance) { if (active && __instance && __instance.GetZDO() != null) queue.Enqueue(__instance.gameObject); } }

        [HarmonyPatch(typeof(LocationProxy), nameof(LocationProxy.SpawnLocation))]
        private static class OnLocation { private static void Postfix(LocationProxy __instance, bool __result) { if (active && __result && __instance.m_instance) queue.Enqueue(__instance.m_instance); } }

        static readonly Type[] refused =
        {
            typeof(Piece), typeof(Character), typeof(ItemDrop), typeof(Animator), typeof(Animation), typeof(Pickable), typeof(MineRock5), typeof(Ship), typeof(Vagon),
            typeof(Door), typeof(Fireplace), typeof(Smelter), typeof(CookingStation), typeof(Beehive), typeof(Plant), typeof(Container), typeof(Projectile), typeof(LocationProxy),
        };

        static bool Refused(GameObject go)
        {
            foreach (var t in refused) if (go.GetComponentInChildren(t, true)) return true;
            foreach (var body in go.GetComponentsInChildren<Rigidbody>(true)) if (!body.isKinematic) return true;
            return false;
        }

        static readonly List<LODGroup> lodGroups = new List<LODGroup>();
        static readonly List<MeshRenderer> renderers = new List<MeshRenderer>();
        static readonly HashSet<Renderer> inGroups = new HashSet<Renderer>();

        static void Register(GameObject go)
        {
            if (!go || !go.activeInHierarchy || go.GetComponent<Tracker>() || Refused(go)) return;
            Tracker tracker = null;
            lodGroups.Clear(); go.GetComponentsInChildren(false, lodGroups);
            inGroups.Clear();
            foreach (var group in lodGroups)
            {
                var lods = group.GetLODs();
                foreach (var lod in lods) foreach (var r in lod.renderers) if (r) inGroups.Add(r);
                if (!group.enabled) continue;
                var levels = new Part[lods.Length][]; bool ok = true;
                for (int i = 0; i < lods.Length && ok; i++) ok = Parts(lods[i].renderers, out levels[i]);
                if (!ok) continue;
                var t = group.transform; float scale = MaxScale(t);
                var copy = new Copy
                {
                    Center = t.TransformPoint(group.localReferencePoint), Size = group.size * scale, Levels = levels, Heights = new float[lods.Length],
                };
                for (int i = 0; i < lods.Length; i++) copy.Heights[i] = lods[i].screenRelativeTransitionHeight;
                copy.Radius = Radius(levels, copy.Center);
                Add(ref tracker, go, copy);
            }
            renderers.Clear(); go.GetComponentsInChildren(false, renderers);
            foreach (var r in renderers)
            {
                if (inGroups.Contains(r) || !Parts(new Renderer[] { r }, out var parts)) continue;
                var bounds = r.bounds;
                var copy = new Copy { Center = bounds.center, Radius = bounds.extents.magnitude, Size = 0f, Levels = new[] { parts }, Heights = new[] { 0f } };
                Add(ref tracker, go, copy);
            }
        }

        static float MaxScale(Transform t) { var s = t.lossyScale; return Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)); }

        static float Radius(Part[][] levels, Vector3 center)
        {
            float radius = 0f;
            foreach (var level in levels) foreach (var p in level) { var b = p.Renderer.bounds; radius = Mathf.Max(radius, Vector3.Distance(center, b.center) + b.extents.magnitude); }
            return radius;
        }

        static readonly List<Part> partList = new List<Part>();
        // The parts of a set of renderers, or false when one of them cannot be drawn instanced (the whole copy then stays as it is).
        static bool Parts(Renderer[] set, out Part[] parts)
        {
            partList.Clear(); parts = null;
            foreach (var r in set)
            {
                if (!r) continue;
                if (!(r is MeshRenderer mr) || !r.enabled || !r.gameObject.activeInHierarchy || r.forceRenderingOff || r.HasPropertyBlock()) return false;
                var filter = r.GetComponent<MeshFilter>(); var mesh = filter ? filter.sharedMesh : null;
                var materials = r.sharedMaterials;
                if (!mesh || materials.Length == 0 || materials.Length > mesh.subMeshCount) return false;
                for (int i = 0; i < materials.Length; i++)
                {
                    var m = materials[i];
                    if (!m || !m.shader || !m.enableInstancing) return false;
                    var key = (mesh, i, m, r.shadowCastingMode, r.receiveShadows, r.gameObject.layer);
                    if (!protoIndex.TryGetValue(key, out int index))
                    {
                        protoIndex[key] = index = protos.Count;
                        protos.Add(new Proto { Mesh = mesh, Sub = i, Material = m, Shadow = r.shadowCastingMode, Receive = r.receiveShadows, Layer = r.gameObject.layer });
                    }
                    partList.Add(new Part { Proto = index, Matrix = r.localToWorldMatrix, Renderer = r });
                }
            }
            if (partList.Count == 0) return false;
            parts = partList.ToArray();
            return true;
        }

        static void Add(ref Tracker tracker, GameObject go, Copy copy)
        {
            if (!tracker) tracker = go.AddComponent<Tracker>();
            copy.Owner = tracker;
            tracker.Copies.Add(copy);
            foreach (var level in copy.Levels) foreach (var p in level) { p.Renderer.forceRenderingOff = true; p.Renderer.transform.hasChanged = false; }
            copies.Add(copy);
        }

        static void Release(Copy copy)
        {
            if (copy.Dead) return;
            copy.Dead = true;
            foreach (var level in copy.Levels) foreach (var p in level) if (p.Renderer) p.Renderer.forceRenderingOff = false;
        }

        // A copy whose renderers moved, were hidden or changed goes back to them.
        static bool StillSame(Copy copy)
        {
            foreach (var level in copy.Levels)
                foreach (var p in level)
                {
                    var r = p.Renderer;
                    if (!r || !r.enabled || !r.gameObject.activeInHierarchy || r.transform.hasChanged || r.HasPropertyBlock()) return false;
                }
            return true;
        }

        // ---- Per frame ----
        internal static void Tick()
        {
            bool on = On;
            if (on != active)
            {
                active = on;
                if (!on) Clear();
                else foreach (var view in Object.FindObjectsByType<ZNetView>(FindObjectsSortMode.None)) if (view.GetZDO() != null) queue.Enqueue(view.gameObject);
            }
            if (!on) return;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            while (queue.Count > 0 && (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency < RegisterBudgetMs)
            {
                try { Register(queue.Dequeue()); } catch (Exception e) { Log.LogWarning("[Performance] Instanced props: " + e.Message); }
            }
            Check();
            Draw();
        }

        static void Check()
        {
            int n = Math.Min(ChecksPerFrame, copies.Count);
            for (int i = 0; i < n; i++)
            {
                if (checkIndex >= copies.Count) checkIndex = 0;
                var copy = copies[checkIndex];
                if (copy.Dead || !copy.Owner) { Release(copy); copies.RemoveAt(checkIndex); continue; }
                if (!StillSame(copy)) { Release(copy); copy.Owner.Copies.Remove(copy); copies.RemoveAt(checkIndex); continue; }
                checkIndex++;
            }
        }

        static void Draw()
        {
            var camera = Utils.GetMainCamera();
            if (!camera || copies.Count == 0) { Copies = copies.Count; Drawn = Calls = 0; return; }
            GeometryUtility.CalculateFrustumPlanes(camera, planes);
            // Planes pushed out a little: copies just off screen still cast their shadows into it.
            var p0 = planes[0]; var p1 = planes[1]; var p2 = planes[2]; var p3 = planes[3]; var p4 = planes[4];
            Vector3 eye = camera.transform.position;
            float k = 1f / (2f * Mathf.Tan(.5f * camera.fieldOfView * Mathf.Deg2Rad)) * QualitySettings.lodBias;
            int count = copies.Count;
            if (chosen.Length < count) chosen = new int[Mathf.NextPowerOfTwo(count)];
            var list = copies; var picks = chosen;
            // Which copies are seen and at which level: worked out on several cores (plain arithmetic, no Unity call).
            Parallel.For(0, count, i =>
            {
                var c = list[i];
                if (c.Dead) { picks[i] = -1; return; }
                Vector3 center = c.Center; float r = c.Radius + ShadowMargin;
                // Sides and near plane only: how far a copy is drawn is the levels of detail's business.
                if (p0.GetDistanceToPoint(center) < -r || p1.GetDistanceToPoint(center) < -r || p2.GetDistanceToPoint(center) < -r
                    || p3.GetDistanceToPoint(center) < -r || p4.GetDistanceToPoint(center) < -r)
                { picks[i] = -1; return; }
                if (c.Size <= 0f) { picks[i] = 0; return; }
                float distance = Mathf.Max(.01f, (center - eye).magnitude);
                float height = c.Size * k / distance;
                int level = -1;
                for (int l = 0; l < c.Heights.Length; l++) if (height >= c.Heights[l]) { level = l; break; }
                picks[i] = level;
            });
            foreach (var proto in protos) { proto.Matrices.Clear(); proto.HasBounds = false; }
            int drawn = 0;
            for (int i = 0; i < count; i++)
            {
                int level = picks[i]; if (level < 0) continue;
                var c = list[i]; drawn++;
                var b = new Bounds(c.Center, Vector3.one * (c.Radius * 2f));
                foreach (var p in c.Levels[level])
                {
                    var proto = protos[p.Proto];
                    proto.Matrices.Add(p.Matrix);
                    if (proto.HasBounds) proto.Bounds.Encapsulate(b); else { proto.Bounds = b; proto.HasBounds = true; }
                }
            }
            int calls = 0;
            foreach (var proto in protos)
            {
                int n = proto.Matrices.Count; if (n == 0 || !proto.Mesh || !proto.Material) continue;
                var rp = new RenderParams(proto.Material)
                {
                    shadowCastingMode = proto.Shadow, receiveShadows = proto.Receive, layer = proto.Layer, worldBounds = proto.Bounds,
                    lightProbeUsage = LightProbeUsage.Off, reflectionProbeUsage = ReflectionProbeUsage.Off,
                };
                for (int s = 0; s < n; s += Batch)
                {
                    int take = Math.Min(Batch, n - s);
                    proto.Matrices.CopyTo(s, batch, 0, take);
                    Graphics.RenderMeshInstanced(rp, proto.Mesh, proto.Sub, batch, take);
                    calls++;
                }
            }
            Copies = count; Drawn = drawn; Calls = calls;
        }

        static void Clear()
        {
            foreach (var copy in copies) Release(copy);
            foreach (var copy in copies) if (copy.Owner) Object.Destroy(copy.Owner);
            copies.Clear(); protos.Clear(); protoIndex.Clear(); queue.Clear();
            Copies = Drawn = Calls = 0;
        }
    }
}
