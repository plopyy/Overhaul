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
    // Objects with physics (logs left on the ground) are taken while their bodies sleep and given back in the frame they wake;
    // objects synchronised over the network (simulated by the server or another player) are checked every frame as well, a
    // move being a real change of position or rotation (the sync rewrites the same position each frame).
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
            // Physics bodies of the object (logs on the ground): taken only while they sleep, checked every frame.
            internal Rigidbody[] Bodies;
            internal Part[][] Levels; internal float[] Heights; internal bool Dead;
            // Far voxels: the object's prefab and placement; the first copy of an object draws its voxel model.
            internal string Prefab; internal Matrix4x4 Root; internal bool VoxelOwner, InVoxel;
        }

        static readonly Dictionary<(Mesh, int, Material, ShadowCastingMode, bool, int), int> protoIndex = new Dictionary<(Mesh, int, Material, ShadowCastingMode, bool, int), int>();
        static readonly List<Proto> protos = new List<Proto>();
        static readonly List<Copy> copies = new List<Copy>();
        static int[] chosen = new int[0];
        static readonly Queue<GameObject> queue = new Queue<GameObject>();
        // Copies with physics bodies, checked every frame; objects whose bodies are awake, checked twice a second.
        static readonly List<Copy> physical = new List<Copy>();
        static readonly List<GameObject> awake = new List<GameObject>();
        static float nextAwakeCheck;

        static void Watch(GameObject go) { if (!awake.Contains(go)) awake.Add(go); }
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
            return false;
        }

        // The object's moving physics bodies, or null; false while one of them is awake (the object is then watched and taken
        // once they all sleep).
        static bool Asleep(GameObject go, out Rigidbody[] bodies)
        {
            bodies = null;
            var all = go.GetComponentsInChildren<Rigidbody>(true);
            List<Rigidbody> moving = null;
            foreach (var body in all) if (!body.isKinematic) (moving ?? (moving = new List<Rigidbody>())).Add(body);
            if (moving == null) return true;
            bodies = moving.ToArray();
            foreach (var body in bodies) if (!body.IsSleeping()) return false;
            return true;
        }

        static readonly List<LODGroup> lodGroups = new List<LODGroup>();
        static readonly List<MeshRenderer> renderers = new List<MeshRenderer>();
        static readonly HashSet<Renderer> inGroups = new HashSet<Renderer>();

        static void Register(GameObject go)
        {
            if (!go || !go.activeInHierarchy || go.GetComponent<Tracker>() || Refused(go)) return;
            if (!Asleep(go, out var bodies)) { Watch(go); return; }
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
                    Center = Centre(levels), Size = group.size * scale, Levels = levels, Heights = new float[lods.Length],
                };
                for (int i = 0; i < lods.Length; i++) copy.Heights[i] = lods[i].screenRelativeTransitionHeight;
                copy.Radius = Radius(levels, copy.Center);
                Add(ref tracker, go, copy);
            }
            renderers.Clear(); go.GetComponentsInChildren(false, renderers);
            foreach (var r in renderers)
            {
                if (inGroups.Contains(r) || !Parts(new Renderer[] { r }, out var parts)) continue;
                var bounds = WorldBounds(r);
                var copy = new Copy { Center = bounds.center, Radius = bounds.extents.magnitude, Size = 0f, Levels = new[] { parts }, Heights = new[] { 0f } };
                Add(ref tracker, go, copy);
            }
            if (tracker && (bodies != null || go.GetComponent<ZSyncTransform>()))
                foreach (var c in tracker.Copies) { c.Bodies = bodies; physical.Add(c); }
        }

        // A renderer's world box from its mesh and its transform: Renderer.bounds of an object created this very frame can still be
        // empty or at the world's origin, which made freshly spawned copies (a location's trees and rocks) culled for good.
        static Bounds WorldBounds(Renderer r)
        {
            var filter = r.GetComponent<MeshFilter>(); var mesh = filter ? filter.sharedMesh : null;
            var m = r.transform.localToWorldMatrix;
            if (!mesh) return new Bounds(m.MultiplyPoint3x4(Vector3.zero), Vector3.one);
            var local = mesh.bounds;
            var b = new Bounds(m.MultiplyPoint3x4(local.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
                b.Encapsulate(m.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
            return b;
        }

        // Centre of the copy: middle of its meshes' boxes. Not the LODGroup's reference point: some game trees (AshlandsTree*)
        // carry a broken one, millions of metres away, which kept them culled and invisible.
        static Vector3 Centre(Part[][] levels)
        {
            bool any = false; var b = new Bounds();
            foreach (var level in levels) foreach (var p in level) { var w = WorldBounds(p.Renderer); if (any) b.Encapsulate(w); else { b = w; any = true; } }
            return b.center;
        }

        static float MaxScale(Transform t) { var s = t.lossyScale; return Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)); }

        static float Radius(Part[][] levels, Vector3 center)
        {
            float radius = 0f;
            foreach (var level in levels) foreach (var p in level) { var b = WorldBounds(p.Renderer); radius = Mathf.Max(radius, Vector3.Distance(center, b.center) + b.extents.magnitude); }
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
            copy.Prefab = Utils.GetPrefabName(go); copy.Root = go.transform.localToWorldMatrix; copy.VoxelOwner = tracker.Copies.Count == 0;
            tracker.Copies.Add(copy);
            foreach (var level in copy.Levels) foreach (var p in level) p.Renderer.forceRenderingOff = true;
            copies.Add(copy);
        }

        static void Release(Copy copy)
        {
            if (copy.Dead) return;
            copy.Dead = true;
            foreach (var level in copy.Levels) foreach (var p in level) if (p.Renderer) p.Renderer.forceRenderingOff = false;
        }

        // A copy whose renderers moved, were hidden or changed goes back to them.
        // Moved for real (more than 1 cm or about half a degree): a position rewritten unchanged each frame, as the network
        // sync of an object simulated elsewhere does, is no move.
        static bool Moved(Part p)
        {
            var now = p.Renderer.localToWorldMatrix; var was = p.Matrix;
            float dx = now.m03 - was.m03, dy = now.m13 - was.m13, dz = now.m23 - was.m23;
            if (dx * dx + dy * dy + dz * dz > 1e-4f) return true;
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) if (Mathf.Abs(now[i, j] - was[i, j]) > .01f) return true;
            return false;
        }

        static bool StillSame(Copy copy)
        {
            foreach (var level in copy.Levels)
                foreach (var p in level)
                {
                    var r = p.Renderer;
                    if (!r || !r.enabled || !r.gameObject.activeInHierarchy || r.HasPropertyBlock() || Moved(p)) return false;
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
            CheckPhysical();
            Check();
            Draw();
        }

        // A body waking up (hit, pushed) gives its copy back in the same frame, and the object is watched until it sleeps again.
        static void CheckPhysical()
        {
            for (int i = physical.Count - 1; i >= 0; i--)
            {
                var copy = physical[i];
                if (copy.Dead || !copy.Owner) { physical.RemoveAt(i); continue; }
                bool moving = false;
                if (copy.Bodies != null) foreach (var body in copy.Bodies) if (!body || !body.isKinematic && !body.IsSleeping()) { moving = true; break; }
                if (!moving) foreach (var level in copy.Levels) { foreach (var p in level) if (!p.Renderer || Moved(p)) { moving = true; break; } if (moving) break; }
                if (!moving) continue;
                var go = copy.Owner.gameObject;
                Release(copy); copy.Owner.Copies.Remove(copy); physical.RemoveAt(i);
                if (copy.Owner.Copies.Count == 0) Object.Destroy(copy.Owner);
                if (go) Watch(go);
            }
            if (Time.time < nextAwakeCheck) return;
            nextAwakeCheck = Time.time + .5f;
            for (int i = awake.Count - 1; i >= 0; i--)
            {
                var go = awake[i];
                if (!go) { awake.RemoveAt(i); continue; }
                if (go.GetComponent<Tracker>() || !go.activeInHierarchy) continue;
                if (Asleep(go, out _)) { awake.RemoveAt(i); queue.Enqueue(go); }
            }
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
                if (c.Dead) { picks[i] = -2; return; }
                Vector3 center = c.Center; float r = c.Radius + ShadowMargin;
                // Sides and near plane only: how far a copy is drawn is the levels of detail's business.
                if (p0.GetDistanceToPoint(center) < -r || p1.GetDistanceToPoint(center) < -r || p2.GetDistanceToPoint(center) < -r
                    || p3.GetDistanceToPoint(center) < -r || p4.GetDistanceToPoint(center) < -r)
                { picks[i] = -2; return; } // off screen (-1: too small for any level)
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
                int level = picks[i]; if (level == -2) continue;
                var c = list[i];
                // Beyond Far voxels' start, a tree with a voxel model is drawn as voxels (even past its own last level).
                int voxel = FarVoxels.ModelFor(c.Prefab);
                // Hysteresis (5 m each way) so a tree near the switch does not flip between its model and voxels as the camera sways.
                if (voxel >= 0)
                {
                    // Voxels past 100 m, or as soon as the game's levels would hide it (small things vanish at 50-100 m),
                    // up to the model's own range; 5 m hysteresis at 100 m so it does not flip as the camera sways.
                    float edge = c.InVoxel ? FarVoxels.Start - 5f : FarVoxels.Start + 5f;
                    float d2 = (c.Center - eye).sqrMagnitude, max = FarVoxels.MaxDistance(voxel);
                    c.InVoxel = (d2 >= edge * edge || level == -1) && d2 <= max * max;
                    if (c.InVoxel) { if (c.VoxelOwner) FarVoxels.Queue(voxel, c.Root); continue; }
                }
                else c.InVoxel = false;
                if (level < 0) continue;
                drawn++;
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

        // "operf look": what Instanced props does with the object under the crosshair (to find why a copy would not show).
        internal static void Look(Terminal context)
        {
            var camera = Utils.GetMainCamera(); if (!camera) return;
            if (!Physics.Raycast(camera.transform.position, camera.transform.forward, out var hit, 300f, ~0, QueryTriggerInteraction.Ignore)) { PerfMonitor.Print("look: nothing under the crosshair"); return; }
            // Every network object with renderers within 15 m of the aimed point (trees, rocks), closest first, up to 6.
            var found = new List<GameObject>();
            foreach (var view in Object.FindObjectsByType<ZNetView>(FindObjectsSortMode.None))
                if (view && view.GetComponentInChildren<MeshRenderer>(true) && (view.transform.position - hit.point).sqrMagnitude < 225f && !view.GetComponent<Character>()) found.Add(view.gameObject);
            found.Sort((a, b) => (a.transform.position - hit.point).sqrMagnitude.CompareTo((b.transform.position - hit.point).sqrMagnitude));
            PerfMonitor.Print($"look: aimed at {hit.collider.name} {hit.distance:0.0} m away, {found.Count} objects within 15 m");
            for (int i = 0; i < found.Count && i < 6; i++) LookAt(found[i], camera);
        }

        static void LookAt(GameObject go, Camera camera)
        {
            var tracker = go.GetComponent<Tracker>();
            PerfMonitor.Print($"object {go.name} at {(go.transform.position - camera.transform.position).magnitude:0.0} m, active={active}, tracked={(tracker ? tracker.Copies.Count + " copies" : "no")}, queued={queue.Contains(go)}, refused={Refused(go)}, watched={awake.Contains(go)}");
            float k = 1f / (2f * Mathf.Tan(.5f * camera.fieldOfView * Mathf.Deg2Rad)) * QualitySettings.lodBias;
            if (tracker)
                foreach (var c in tracker.Copies)
                {
                    float distance = Mathf.Max(.01f, (c.Center - camera.transform.position).magnitude), height = c.Size * k / distance;
                    int level = -1; for (int l = 0; l < c.Heights.Length; l++) if (height >= c.Heights[l]) { level = l; break; }
                    PerfMonitor.Print($"  copy dead={c.Dead} center={c.Center} radius={c.Radius:0.0} size={c.Size:0.0} height={height:0.000} heights=[{string.Join(" ", c.Heights)}] level={level} inList={copies.Contains(c)}");
                    for (int l = 0; l < c.Levels.Length; l++)
                        foreach (var p in c.Levels[l])
                        {
                            var proto = protos[p.Proto]; var r = p.Renderer;
                            PerfMonitor.Print($"    L{l} {(r ? r.name : "gone")} off={(r && r.forceRenderingOff)} enabled={(r && r.enabled)} activeInHierarchy={(r && r.gameObject.activeInHierarchy)} block={(r && r.HasPropertyBlock())} moved={(r && Moved(p))} mesh={(proto.Mesh ? proto.Mesh.name + " v" + proto.Mesh.vertexCount : "null")} mat={(proto.Material ? proto.Material.name + "/" + proto.Material.shader.name : "null")} drawnThisFrame={proto.Matrices.Count}");
                        }
                }
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                PerfMonitor.Print($"  renderer {r.name} type={r.GetType().Name} enabled={r.enabled} active={r.gameObject.activeInHierarchy} off={r.forceRenderingOff} visible={r.isVisible} block={r.HasPropertyBlock()} bounds={r.bounds.size}");
            foreach (var g in go.GetComponentsInChildren<LODGroup>(true))
                PerfMonitor.Print($"  LODGroup {g.name} enabled={g.enabled} size={g.size} lossyScale={g.transform.lossyScale}");
        }

        static void Clear()
        {
            foreach (var copy in copies) Release(copy);
            foreach (var copy in copies) if (copy.Owner) Object.Destroy(copy.Owner);
            copies.Clear(); protos.Clear(); protoIndex.Clear(); queue.Clear(); physical.Clear(); awake.Clear();
            Copies = Drawn = Calls = 0;
        }
    }
}
