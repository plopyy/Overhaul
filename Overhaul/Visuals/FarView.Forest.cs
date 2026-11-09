using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;
using Overhaul.Performance;
using Overhaul.Utility;

namespace Overhaul.Visuals
{
    // Far view, forest: beyond the game's own trees, out to 1.5 / 2.5 / 3.5 km (Low / Medium / High), every tree the game
    // would grow there, drawn as an impostor (an image of the game's own tree model, taken on this machine).
    // Which trees: the vegetation the game places that has a TreeBase, or is a big plant (8 m and more), at most 32 kinds,
    // those the game places most of. Where: the game's own placement run again per zone on a background thread, with a
    // random generator working like Unity's and seeded as the game seeds it, and its checks (biome, altitude, slope,
    // vegetation mask, sea depth, forest factor...) worked out from the world generator; checks that need loaded objects
    // are left out, so a few trees can differ. Impostors: each kind is photographed once from 64 directions over the
    // upper hemisphere into an atlas (albedo + normal); every far tree of a kind is one camera-facing quad of one draw.
    internal static partial class FarView
    {
        const int Frames = 8, MaxKinds = 32, CellZones = 8;
        static float ForestReach(ClientVisuals.Level level) => level == ClientVisuals.Level.Low ? 1500f : level == ClientVisuals.Level.Medium ? 2500f : 3500f;

        // ---- Tree kinds and the game's vegetation rules, copied once (the worker never touches Unity objects) ----
        sealed class Kind
        {
            internal string Name; internal GameObject Prefab; internal bool Distant; internal int Layer;
            internal Vector4 Sphere; internal RenderTexture Albedo, Normal; internal Material Material;
            internal ComputeBuffer Trees, Yaws; internal int Count;
        }

        struct Rule
        {
            internal int Kind, Hash; internal Heightmap.Biome Biome;
            internal float Min, Max; internal bool Force; internal float ScaleMin, ScaleMax, RandTilt, GroundTiltChance;
            internal int GroupMin, GroupMax; internal float GroupRadius, MinAltitude, MaxAltitude, MinVegetation, MaxVegetation;
            internal float MinOcean, MaxOcean, CosMaxTilt, CosMinTilt, DeltaRadius, MinDelta, MaxDelta, MinCentre, MaxCentre;
            internal bool InForest; internal float ForestMin, ForestMax; internal bool SnapToWater; internal float GroundOffset;
        }

        static List<Kind> kinds;
        static Rule[] rules;
        static int seed;
        static Material capture;
        static Mesh quad;
        static bool forestFailed;

        static bool PrepareForest()
        {
            if (forestFailed) return false;
            if (kinds != null) return true;
            if (!ZoneSystem.instance || ZoneSystem.instance.m_vegetation == null || WorldGenerator.instance == null) return false;
            try
            {
                var bundle = EmbeddedAssets.LoadBundle("Overhaul.Assets.overhaul_farview");
                impostorShader = bundle.LoadAsset<Shader>("Assets/OverhaulFarView/FarImpostor.shader");
                var captureShader = bundle.LoadAsset<Shader>("Assets/OverhaulFarView/ImpostorCapture.shader");
                bundle.Unload(false);
                if (!impostorShader || !impostorShader.isSupported || !captureShader || !captureShader.isSupported) throw new Exception("far view shaders not supported here");
                capture = new Material(captureShader);
            }
            catch (Exception e) { forestFailed = true; Log.LogWarning("[FarView] far forest off: " + e.Message); return false; }

            var weight = new Dictionary<GameObject, float>();
            foreach (var veg in ZoneSystem.instance.m_vegetation)
            {
                if (!veg.m_enable || !veg.m_prefab || veg.AltBiomeParent != null || !IsTree(veg.m_prefab)) continue;
                weight.TryGetValue(veg.m_prefab, out float w);
                weight[veg.m_prefab] = w + Mathf.Max(veg.m_max < 1f ? veg.m_max : (veg.m_min + veg.m_max) * 0.5f, 0.01f) * (veg.m_groupSizeMin + veg.m_groupSizeMax) * 0.5f;
            }
            kinds = weight.OrderByDescending(p => p.Value).Take(MaxKinds).Select(p => new Kind { Name = p.Key.name, Prefab = p.Key }).ToList();
            var index = kinds.Select((k, i) => (k.Prefab, i)).ToDictionary(p => p.Prefab, p => p.i);
            var list = new List<Rule>();
            foreach (var veg in ZoneSystem.instance.m_vegetation)
            {
                if (!veg.m_enable || !veg.m_prefab || veg.AltBiomeParent != null || !index.TryGetValue(veg.m_prefab, out int kind)) continue;
                list.Add(new Rule
                {
                    Kind = kind, Hash = veg.m_prefab.name.GetStableHashCode(), Biome = veg.m_biome, Min = veg.m_min, Max = veg.m_max, Force = veg.m_forcePlacement,
                    ScaleMin = veg.m_scaleMin, ScaleMax = veg.m_scaleMax, RandTilt = veg.m_randTilt, GroundTiltChance = veg.m_chanceToUseGroundTilt,
                    GroupMin = veg.m_groupSizeMin, GroupMax = veg.m_groupSizeMax, GroupRadius = veg.m_groupRadius,
                    MinAltitude = veg.m_minAltitude, MaxAltitude = veg.m_maxAltitude, MinVegetation = veg.m_minVegetation, MaxVegetation = veg.m_maxVegetation,
                    MinOcean = veg.m_minOceanDepth, MaxOcean = veg.m_maxOceanDepth,
                    CosMaxTilt = Mathf.Cos(Mathf.Deg2Rad * veg.m_maxTilt), CosMinTilt = Mathf.Cos(Mathf.Deg2Rad * veg.m_minTilt),
                    DeltaRadius = veg.m_terrainDeltaRadius, MinDelta = veg.m_minTerrainDelta, MaxDelta = veg.m_maxTerrainDelta,
                    MinCentre = veg.m_minDistanceFromCenter, MaxCentre = veg.m_maxDistanceFromCenter,
                    InForest = veg.m_inForest, ForestMin = veg.m_forestTresholdMin, ForestMax = veg.m_forestTresholdMax,
                    SnapToWater = veg.m_snapToWater, GroundOffset = veg.m_groundOffset,
                });
            }
            // The game seeds per vegetation entry in list order; the order of our copy is the same.
            rules = list.ToArray();
            seed = WorldGenerator.instance.GetSeed();
            foreach (var kind in kinds) { var view = kind.Prefab.GetComponent<ZNetView>(); kind.Distant = view && view.m_distant; }
            quad = MakeQuad();
            Log.LogInfo("[FarView] far forest: " + kinds.Count + " kinds of tree (" + string.Join(", ", kinds.Select(k => k.Name)) + ")");
            return true;
        }

        static Shader impostorShader;

        static bool IsTree(GameObject prefab)
        {
            if (prefab.GetComponent<TreeBase>()) return true;
            if (prefab.GetComponentInChildren<Pickable>(true) || prefab.GetComponentInChildren<HoverText>(true)) return false;
            var renderers = FirstLod(prefab);
            if (renderers.Count == 0 || renderers.Any(r => r.sharedMaterials.Any(m => !m || !m.shader || m.shader.name == "Custom/StaticRock"))) return false;
            long tris = 0;
            foreach (var r in renderers) { var f = r.GetComponent<MeshFilter>(); if (f && f.sharedMesh) for (int s = 0; s < f.sharedMesh.subMeshCount; s++) tris += f.sharedMesh.GetIndexCount(s) / 3; }
            if (tris < 32) return false;
            return LocalBounds(prefab, renderers).size.y >= 8f;
        }

        // The renderers of the model's most detailed level (all mesh renderers when it has no LOD group).
        static List<MeshRenderer> FirstLod(GameObject prefab)
        {
            var group = prefab.GetComponentInChildren<LODGroup>(true);
            if (group && group.GetLODs().Length > 0) return group.GetLODs()[0].renderers.OfType<MeshRenderer>().Where(r => r).ToList();
            return prefab.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.GetComponent<MeshFilter>()).ToList();
        }

        static Bounds LocalBounds(GameObject prefab, List<MeshRenderer> renderers)
        {
            var toRoot = prefab.transform.worldToLocalMatrix;
            bool any = false; var bounds = new Bounds();
            foreach (var r in renderers)
            {
                var f = r.GetComponent<MeshFilter>(); if (!f || !f.sharedMesh) continue;
                var m = toRoot * r.transform.localToWorldMatrix; var b = f.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var p = m.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; } else bounds.Encapsulate(p);
                }
            }
            return bounds;
        }

        // ---- Random numbers as Unity's (xorshift128 seeded the same way), so the game's placement replays ----
        struct UnityRandom
        {
            uint x, y, z, w;
            internal UnityRandom(int seed) { x = (uint)seed; y = x * 1812433253u + 1; z = y * 1812433253u + 1; w = z * 1812433253u + 1; }
            uint Next() { uint t = x ^ (x << 11); x = y; y = z; z = w; return w = w ^ (w >> 19) ^ t ^ (t >> 8); }
            internal float Value => (Next() & 0x7FFFFF) * (1f / 8388607f);
            internal float Range(float min, float max) { float v = Value; return min + (max - min) * v; }
            internal int Range(int min, int max) => max <= min ? min : min + (int)(Next() % (uint)(max - min));
        }

        // ---- Placement, per zone, on the worker ----
        struct Tree { internal Vector3 Position; internal float Scale, Yaw; internal int Kind; }

        static void PlaceZone(WorldGenerator w, int zx, int zz, List<Tree> into)
        {
            float cx = zx * 64f, cz = zz * 64f;
            var zoneBiome = w.GetBiome(cx, cz, 0.02f, false) | w.GetBiome(cx - 32, cz - 32, 0.02f, false) | w.GetBiome(cx + 32, cz - 32, 0.02f, false)
                | w.GetBiome(cx - 32, cz + 32, 0.02f, false) | w.GetBiome(cx + 32, cz + 32, 0.02f, false);
            foreach (var rule in rules)
            {
                if ((rule.Biome & zoneBiome) == 0) continue;
                var rng = new UnityRandom(seed + zx * 4271 + zz * 9187 + rule.Hash);
                int count = 1;
                if (rule.Max < 1f) { if (rng.Value > rule.Max) continue; }
                else count = rng.Range((int)rule.Min, (int)rule.Max + 1);
                float half = 32f - rule.GroupRadius;
                int tries = rule.Force ? count * 50 : count, placed = 0;
                for (int i = 0; i < tries; i++)
                {
                    var spot = new Vector3(rng.Range(cx - half, cx + half), 0, rng.Range(cz - half, cz + half));
                    int group = rng.Range(rule.GroupMin, rule.GroupMax + 1);
                    for (int j = 0; j < group; j++)
                    {
                        var p = spot;
                        if (j > 0) { float a = rng.Value * Mathf.PI * 2f, d = rng.Range(0f, rule.GroupRadius); p += new Vector3(Mathf.Sin(a) * d, 0, Mathf.Cos(a) * d); }
                        float yaw = rng.Range(0, 360), scale = rng.Range(rule.ScaleMin, rule.ScaleMax);
                        rng.Range(-rule.RandTilt, rule.RandTilt); rng.Range(-rule.RandTilt, rule.RandTilt);
                        if (!Accept(w, rule, ref p)) continue;
                        if (rule.GroundTiltChance > 0f) rng.Value.GetHashCode();
                        into.Add(new Tree { Position = p, Scale = scale, Yaw = yaw * Mathf.Deg2Rad, Kind = rule.Kind });
                        placed++;
                    }
                    if (rule.Force && placed >= count) break;
                }
            }
        }

        static float Ground(WorldGenerator w, float x, float z, out Heightmap.Biome biome, out Color mask)
        {
            biome = w.GetBiome(x, z, 0.02f, false);
            return w.GetBiomeHeight(biome, x, z, out mask, false, true);
        }

        static bool Accept(WorldGenerator w, Rule rule, ref Vector3 p)
        {
            float h = Ground(w, p.x, p.z, out var biome, out var mask);
            if ((rule.Biome & biome) == 0) return false;
            p.y = h;
            float altitude = h - 30f;
            if (altitude < rule.MinAltitude || altitude > rule.MaxAltitude) return false;
            if (!rule.MinVegetation.Equals(rule.MaxVegetation) && (mask.a > rule.MaxVegetation || mask.a < rule.MinVegetation)) return false;
            if (!rule.MinOcean.Equals(rule.MaxOcean)) { float depth = Mathf.Max(0f, 30f - h); if (depth < rule.MinOcean || depth > rule.MaxOcean) return false; }
            const float step = 1f;
            float hx = Ground(w, p.x + step, p.z, out _, out _), hz = Ground(w, p.x, p.z + step, out _, out _);
            float up = new Vector3(h - hx, step, h - hz).normalized.y;
            if (up < rule.CosMaxTilt || up > rule.CosMinTilt) return false;
            if (rule.DeltaRadius > 0f)
            {
                float lo = h, hi = h;
                for (int i = 0; i < 8; i++) { float a = i * Mathf.PI / 4f; float s = Ground(w, p.x + Mathf.Cos(a) * rule.DeltaRadius, p.z + Mathf.Sin(a) * rule.DeltaRadius, out _, out _); lo = Mathf.Min(lo, s); hi = Mathf.Max(hi, s); }
                float delta = hi - lo;
                if (delta > rule.MaxDelta || delta < rule.MinDelta) return false;
            }
            if (rule.MinCentre > 0f || rule.MaxCentre > 0f)
            {
                float d = new Vector2(p.x, p.z).magnitude;
                if ((rule.MinCentre > 0f && d < rule.MinCentre) || (rule.MaxCentre > 0f && d > rule.MaxCentre)) return false;
            }
            if (rule.InForest) { float f = WorldGenerator.GetForestFactor(p); if (f < rule.ForestMin || f > rule.ForestMax) return false; }
            if (rule.SnapToWater) p.y = 30f;
            p.y += rule.GroundOffset;
            return true;
        }

        // ---- Cells of 8x8 zones: placed on the worker, nearest first, then all trees gathered per kind ----
        static readonly ConcurrentDictionary<(int, int), List<Tree>> forestCells = new ConcurrentDictionary<(int, int), List<Tree>>();
        static readonly BlockingCollection<(int X, int Z, int Epoch)> cellJobs = new BlockingCollection<(int, int, int)>();
        static Thread forestWorker;
        static int forestCentreX = int.MinValue, forestCentreZ = int.MinValue, forestEpoch, forestVersion, uploadedVersion = -1;
        static float forestReachShown;

        static void TickForest(Vector3 eye)
        {
            if (!PrepareForest()) return;
            if (!Bake()) return;
            float reach = ForestReach(ClientVisuals.FarViewInEffect);
            float cellSize = CellZones * 64f;
            int cx = Mathf.FloorToInt(eye.x / cellSize), cz = Mathf.FloorToInt(eye.z / cellSize);
            if (cx != forestCentreX || cz != forestCentreZ || reach != forestReachShown)
            {
                forestCentreX = cx; forestCentreZ = cz; forestReachShown = reach; forestEpoch++;
                if (forestWorker == null) { forestWorker = new Thread(ForestWork) { IsBackground = true, Name = "Overhaul far forest" }; forestWorker.Start(); }
                int r = Mathf.CeilToInt(reach / cellSize) + 1;
                var wanted = new List<(int, int)>();
                for (int x = cx - r; x <= cx + r; x++) for (int z = cz - r; z <= cz + r; z++) wanted.Add((x, z));
                foreach (var key in forestCells.Keys) if (!wanted.Contains(key)) forestCells.TryRemove(key, out _);
                foreach (var key in wanted.OrderBy(k => (k.Item1 - cx) * (k.Item1 - cx) + (k.Item2 - cz) * (k.Item2 - cz)))
                    if (!forestCells.ContainsKey(key)) cellJobs.Add((key.Item1, key.Item2, forestEpoch));
                forestVersion++;
            }
            if (uploadedVersion != forestVersion && Time.time >= nextGather) { nextGather = Time.time + 1f; Gather(); }
            DrawForest(eye, reach);
        }
        static float nextGather;

        static void ForestWork()
        {
            var trees = new List<Tree>();
            foreach (var job in cellJobs.GetConsumingEnumerable())
            {
                try
                {
                    if (job.Epoch != forestEpoch || forestCells.ContainsKey((job.X, job.Z))) continue;
                    var w = WorldGenerator.instance; if (w == null) continue;
                    trees.Clear();
                    for (int x = 0; x < CellZones; x++) for (int z = 0; z < CellZones; z++) PlaceZone(w, job.X * CellZones + x, job.Z * CellZones + z, trees);
                    forestCells[(job.X, job.Z)] = new List<Tree>(trees);
                    Interlocked.Increment(ref forestVersion);
                }
                catch (Exception e) { Log.LogWarning("[FarView] forest cell failed: " + e.Message); }
            }
        }

        // Every placed tree into its kind's buffers (position + scale, yaw).
        static void Gather()
        {
            uploadedVersion = forestVersion;
            var positions = kinds.Select(_ => new List<Vector4>()).ToArray();
            var yaws = kinds.Select(_ => new List<float>()).ToArray();
            foreach (var cell in forestCells.Values)
                foreach (var t in cell) { positions[t.Kind].Add(new Vector4(t.Position.x, t.Position.y, t.Position.z, t.Scale)); yaws[t.Kind].Add(t.Yaw); }
            for (int k = 0; k < kinds.Count; k++)
            {
                var kind = kinds[k]; int n = positions[k].Count;
                if (kind.Trees == null || kind.Trees.count < n)
                {
                    kind.Trees?.Release(); kind.Yaws?.Release();
                    int capacity = Mathf.Max(64, Mathf.NextPowerOfTwo(n));
                    kind.Trees = new ComputeBuffer(capacity, 16); kind.Yaws = new ComputeBuffer(capacity, 4);
                    if (kind.Material) { kind.Material.SetBuffer("_Trees", kind.Trees); kind.Material.SetBuffer("_Yaws", kind.Yaws); }
                }
                if (n > 0) { kind.Trees.SetData(positions[k], 0, 0, n); kind.Yaws.SetData(yaws[k], 0, 0, n); }
                kind.Count = n;
            }
        }

        // One draw per kind; the shader hides trees inside the game's own range and beyond the reach.
        static void DrawForest(Vector3 eye, float reach)
        {
            var bounds = new Bounds(eye, Vector3.one * (reach * 2f + 1000f));
            var sim = ZNet.instance.GetSyncedSimulationDistance();
            float near = (sim.NearSimulationDistance + 0.5f) * 64f, total = (sim.TotalSimulationDistance + 0.5f) * 64f;
            foreach (var kind in kinds)
            {
                if (kind.Count == 0 || !kind.Material) continue;
                kind.Material.SetVector("_Range", new Vector2(kind.Distant ? total : near, reach));
                Graphics.DrawMeshInstancedProcedural(quad, 0, kind.Material, bounds, kind.Count, null, ShadowCastingMode.Off, true, kind.Layer);
            }
        }

        // ---- Impostor baking: each kind photographed from 64 directions into its atlas, a little each frame ----
        static int bakeKind, bakeFrame;
        static Camera bakeCamera;
        static RenderTexture bakeTarget, bakeAlbedo, bakeNormal;
        static GameObject stage;
        static CommandBuffer bakeCommands;
        static int bakeLayer = -1;

        static bool Bake()
        {
            if (bakeKind >= kinds.Count) return true;
            int frameSize = ClientVisuals.FarViewInEffect == ClientVisuals.Level.Low ? 64 : 128;
            if (!bakeCamera)
            {
                bakeLayer = 31; for (int l = 31; l >= 8; l--) if (string.IsNullOrEmpty(LayerMask.LayerToName(l))) { bakeLayer = l; break; }
                var go = new GameObject("Overhaul impostor camera"); UnityEngine.Object.DontDestroyOnLoad(go);
                bakeCamera = go.AddComponent<Camera>();
                bakeCamera.enabled = false; bakeCamera.renderingPath = RenderingPath.DeferredShading; bakeCamera.orthographic = false; // deferred needs a perspective camera: the orthographic projection is set as a matrix below
                bakeCamera.clearFlags = CameraClearFlags.SolidColor; bakeCamera.backgroundColor = Color.clear; bakeCamera.cullingMask = 1 << bakeLayer;
                bakeCamera.allowHDR = false; bakeCamera.allowMSAA = false; bakeCamera.useOcclusionCulling = false;
                bakeTarget = new RenderTexture(frameSize, frameSize, 24, RenderTextureFormat.ARGB32);
                bakeAlbedo = new RenderTexture(frameSize, frameSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                bakeNormal = new RenderTexture(frameSize, frameSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                bakeCamera.targetTexture = bakeTarget;
                bakeCommands = new CommandBuffer { name = "Overhaul impostor bake" };
                bakeCommands.SetGlobalTexture("_BakeNormals", BuiltinRenderTextureType.GBuffer2); bakeCommands.SetGlobalTexture("_BakeAlbedo", BuiltinRenderTextureType.GBuffer0);
                bakeCommands.Blit(BuiltinRenderTextureType.GBuffer0, bakeAlbedo, capture, 0);
                bakeCommands.Blit(BuiltinRenderTextureType.GBuffer2, bakeNormal, capture, 1);
                bakeCamera.AddCommandBuffer(CameraEvent.AfterGBuffer, bakeCommands);
            }
            var watch = Stopwatch.StartNew();
            while (bakeKind < kinds.Count && watch.Elapsed.TotalMilliseconds < 2.0)
            {
                var kind = kinds[bakeKind];
                if (bakeFrame == 0 && !BeginKind(kind, frameSize)) { bakeKind++; continue; }
                int fx = bakeFrame % Frames, fy = bakeFrame / Frames;
                var dir = FrameDirection(fx, fy);
                var centre = stage.transform.position + (Vector3)kind.Sphere;
                float radius = kind.Sphere.w;
                bakeCamera.nearClipPlane = 0.1f; bakeCamera.farClipPlane = radius * 4f + 2f;
                bakeCamera.projectionMatrix = Matrix4x4.Ortho(-radius, radius, -radius, radius, bakeCamera.nearClipPlane, bakeCamera.farClipPlane);
                bakeCamera.transform.position = centre + dir * (radius * 2f + 1f);
                bakeCamera.transform.rotation = Quaternion.LookRotation(-dir, Mathf.Abs(dir.y) > 0.99f ? Vector3.forward : Vector3.up);
                bakeCamera.Render();
                Graphics.CopyTexture(bakeAlbedo, 0, 0, 0, 0, frameSize, frameSize, kind.Albedo, 0, 0, fx * frameSize, fy * frameSize);
                Graphics.CopyTexture(bakeNormal, 0, 0, 0, 0, frameSize, frameSize, kind.Normal, 0, 0, fx * frameSize, fy * frameSize);
                if (++bakeFrame == Frames * Frames) { EndKind(kind); bakeFrame = 0; bakeKind++; }
            }
            if (bakeKind >= kinds.Count) Log.LogInfo("[FarView] impostors baked for " + kinds.Count(k => k.Material) + " kinds of tree");
            return bakeKind >= kinds.Count;
        }

        // A copy of the model's most detailed level alone (meshes and materials only, no game components) on the bake layer.
        static bool BeginKind(Kind kind, int frameSize)
        {
            var renderers = FirstLod(kind.Prefab);
            if (renderers.Count == 0) return false;
            var bounds = LocalBounds(kind.Prefab, renderers);
            float radius = 0f;
            for (int i = 0; i < 8; i++) radius = Mathf.Max(radius, Vector3.Scale(bounds.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)).magnitude);
            kind.Sphere = new Vector4(bounds.center.x, bounds.center.y, bounds.center.z, radius);
            kind.Layer = renderers[0].gameObject.layer;
            stage = new GameObject("Overhaul impostor stage");
            stage.transform.position = new Vector3(0, -2000f, 0);
            var toRoot = kind.Prefab.transform.worldToLocalMatrix;
            foreach (var r in renderers)
            {
                var f = r.GetComponent<MeshFilter>(); if (!f || !f.sharedMesh) continue;
                var part = new GameObject("part") { layer = bakeLayer };
                part.transform.SetParent(stage.transform, false);
                var m = toRoot * r.transform.localToWorldMatrix;
                part.transform.localPosition = m.GetColumn(3); part.transform.localRotation = m.rotation; part.transform.localScale = m.lossyScale;
                part.AddComponent<MeshFilter>().sharedMesh = f.sharedMesh;
                var mr = part.AddComponent<MeshRenderer>(); mr.sharedMaterials = r.sharedMaterials; mr.shadowCastingMode = ShadowCastingMode.Off;
            }
            kind.Albedo = Atlas(frameSize, RenderTextureReadWrite.sRGB, kind.Name + " albedo");
            kind.Normal = Atlas(frameSize, RenderTextureReadWrite.Linear, kind.Name + " normal");
            return true;
        }

        static void EndKind(Kind kind)
        {
            kind.Albedo.GenerateMips(); kind.Normal.GenerateMips();
            if (stage) UnityEngine.Object.Destroy(stage);
            stage = null;
            kind.Material = new Material(impostorShader) { name = "Overhaul far " + kind.Name };
            kind.Material.SetTexture("_Albedo", kind.Albedo); kind.Material.SetTexture("_Normal", kind.Normal);
            kind.Material.SetFloat("_Frames", Frames); kind.Material.SetVector("_Sphere", kind.Sphere); kind.Material.SetFloat("_Cutoff", 0.5f);
            kind.Material.enableInstancing = true;
            if (kind.Trees != null) { kind.Material.SetBuffer("_Trees", kind.Trees); kind.Material.SetBuffer("_Yaws", kind.Yaws); }
        }

        static RenderTexture Atlas(int frameSize, RenderTextureReadWrite space, string name)
        {
            var rt = new RenderTexture(frameSize * Frames, frameSize * Frames, 0, RenderTextureFormat.ARGB32, space)
            { name = "Overhaul impostor " + name, useMipMap = true, autoGenerateMips = false, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            rt.Create();
            return rt;
        }

        // The view direction of atlas frame (fx, fy): the centre of its cell on the hemi-octahedral layout, as the shader reads it.
        static Vector3 FrameDirection(int fx, int fy)
        {
            float u = (fx + 0.5f) / Frames * 2f - 1f, v = (fy + 0.5f) / Frames * 2f - 1f;
            float x = (u + v) * 0.5f, z = (u - v) * 0.5f, y = 1f - Mathf.Abs(x) - Mathf.Abs(z);
            return new Vector3(x, Mathf.Max(y, 0f), z).normalized;
        }

        static Mesh MakeQuad()
        {
            var mesh = new Mesh { name = "Overhaul impostor quad" };
            mesh.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0) };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            mesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
            return mesh;
        }

        static void StopForest()
        {
            forestEpoch++; forestCentreX = forestCentreZ = int.MinValue; forestCells.Clear(); uploadedVersion = -1;
            if (kinds != null) foreach (var k in kinds) { k.Trees?.Release(); k.Yaws?.Release(); if (k.Albedo) k.Albedo.Release(); if (k.Normal) k.Normal.Release(); }
            kinds = null; rules = null; bakeKind = bakeFrame = 0;
            if (stage) UnityEngine.Object.Destroy(stage);
        }
    }
}
