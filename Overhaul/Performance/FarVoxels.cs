using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using Overhaul.Utility;
using Object = UnityEngine.Object;

namespace Overhaul.Performance
{
    // Experimental far view (off by default): trees drawn as real voxels from 100 m on, and on to 1 km past the loaded area.
    //
    // Models: baked offline from the game's prefabs (Tools/3D/PlayerFbxWork VoxelBake, calibrated against the game's own
    // shaders) into FarVoxels/<prefab>.vox next to the DLL: 3D textures (colour + coverage, normal + leaf share) with one
    // mip per voxel size (0.125 m to 2 m). Each copy draws only its bounding box with the Overhaul/FarVoxel shader
    // (bundle overhaul_farvoxels), which marches the ray through the grid at the mip whose voxel stays under one screen
    // pixel, lit by the sun (with shadows, cast and received), the ambient light and the fog.
    // Where: inside the loaded area, Instanced props hands its tree copies beyond 100 m over to the voxels (the real tree
    // nearer); past the loaded area, the world's vegetation is placed again on the client like ZoneSystem.PlaceVegetation
    // (same seeds and rules, heights from WorldGenerator). Trees cut or rocks mined far away still show for now.
    internal static class FarVoxels
    {
        internal const float Start = 100f, MaxRange = 1000f, ShadowRange = 250f, SmallRange = 300f, SmallHeight = 8f;
        internal static float Range => FarRange;
        static float FarRange => OverhaulConfig.PerfFarVoxelsRange != null ? Mathf.Clamp(OverhaulConfig.PerfFarVoxelsRange.Value, Start, MaxRange) : 500f;
        const float ZoneSize = 64f, FogCut = .03f;
        const int Batch = 1023;

        sealed class Model
        {
            internal string Name; internal Material Material; internal Mesh Box; internal float MaxDistance, Height;
            internal readonly List<Matrix4x4> Draw = new List<Matrix4x4>(), DrawFar = new List<Matrix4x4>();
        }
        sealed class Zone { internal Vector2Int Id; internal Bounds Bounds; internal readonly List<(int Model, Matrix4x4 Matrix)> Items = new List<(int, Matrix4x4)>(); }
        sealed class Data { internal string Name; internal Vector3Int Dims; internal Vector3 BoundsMin; internal float VoxelSize; internal int Mips; internal byte[][] Colour, Normal; }

        static readonly List<Model> models = new List<Model>();
        static readonly Dictionary<string, int> modelIndex = new Dictionary<string, int>();
        static readonly Dictionary<Vector2Int, Zone> zones = new Dictionary<Vector2Int, Zone>();
        static readonly Queue<Data> loadedData = new Queue<Data>();
        static List<(ZoneSystem.ZoneVegetation Veg, int Model)> candidates;
        static Shader shader; static AssetBundle bundle; static Material template;
        static bool active, loading;
        static readonly Plane[] planes = new Plane[6];
        static readonly Matrix4x4[] batch = new Matrix4x4[Batch];
        static readonly int ColourId = Shader.PropertyToID("_ColorTex"), NormalId = Shader.PropertyToID("_NormalTex"), BoundsId = Shader.PropertyToID("_BoundsMin"),
            DimsId = Shader.PropertyToID("_Dims"), SizeId = Shader.PropertyToID("_VoxelSize"), MipsId = Shader.PropertyToID("_MipCount"), PixelId = Shader.PropertyToID("_PixelAngle");
        internal static int Zones, Copies, Drawn, Calls, ModelsReady, ModelsTotal, Handed;

        static bool On => PerfMonitor.On(OverhaulConfig.PerfFarVoxels);

        // ---- For Instanced props: a tree copy beyond Start is drawn as voxels instead of its own levels ----
        // Prefabs with a model file, known before the models are loaded (Instanced props asks when objects come in).
        static HashSet<string> known;
        internal static bool Known(string prefab)
        {
            if (known == null)
            {
                known = new HashSet<string>();
                string dir = Path.Combine(Path.GetDirectoryName(typeof(FarVoxels).Assembly.Location) ?? "", "FarVoxels");
                if (Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir, "*.vox")) known.Add(Path.GetFileNameWithoutExtension(f));
            }
            return prefab != null && known.Contains(prefab);
        }

        internal static float MaxDistance(int model) => Mathf.Min(models[model].MaxDistance, FarRange);
        internal static int ModelFor(string prefab) => active && prefab != null && modelIndex.TryGetValue(prefab, out int i) ? i : -1;
        // Only voxel trees within ShadowRange cast shadows: further away the fog drowns them, and the shadow passes cost most.
        static Vector3 lastEye;
        static float lastPixel = .001f, lastFar = MaxRange;
        internal static void Queue(int model, Matrix4x4 matrix)
        {
            var m = models[model]; Vector3 p = matrix.GetColumn(3);
            float d2 = (p - lastEye).sqrMagnitude;
            if (TooSmall(m, matrix, d2)) return;
            (d2 < ShadowRange * ShadowRange ? m.Draw : m.DrawFar).Add(matrix); Handed++;
        }

        // Screen size cut: not drawn once it would be under 2 pixels tall (small things far away cost as much as big ones
        // for nothing to see), nor past the camera's far plane.
        const float MinPixels = 2f;
        static bool TooSmall(Model m, Matrix4x4 matrix, float d2)
        {
            if (d2 > lastFar * lastFar) return true;
            float scale = ((Vector3)matrix.GetColumn(1)).magnitude;
            float px = m.Height * scale / (Mathf.Sqrt(d2) * lastPixel);
            return px < MinPixels;
        }

        internal static void Tick()
        {
            bool want = On && ZoneSystem.instance && WorldGenerator.instance != null && ZNetScene.instance && Player.m_localPlayer && !ZNet.instance.IsDedicated();
            if (!want) { if (active || loading) Clear(); return; }
            if (!active && !loading) StartLoading();
            lock (loadedData) while (loadedData.Count > 0) Upload(loadedData.Dequeue());
            if (!active) return;
            if (candidates == null) Prepare();
            Generate();
        }

        // Runs after Instanced props has queued its copies for this frame.
        internal static void LateTick()
        {
            if (!active) { Handed = 0; return; }
            Draw();
        }

        // ---- Loading: shader from the bundle, models read and unpacked on a worker thread, uploaded on the main thread ----
        static void StartLoading()
        {
            loading = true;
            // Loaded once per session and kept: loading the same bundle again while it is loaded fails.
            if (!shader)
            {
                if (!bundle) bundle = EmbeddedAssets.LoadBundle("Overhaul.Assets.overhaul_farvoxels");
                shader = bundle ? bundle.LoadAsset<Shader>("FarVoxel") : null;
                if (!shader && bundle) foreach (var s in bundle.LoadAllAssets<Shader>()) shader = s;
                // The bundle's instanced material (it keeps the instancing variants in the build): each model copies it.
                template = bundle ? bundle.LoadAsset<Material>("FarVoxel") : null;
            }
            if (!shader || !shader.isSupported) { Log.LogWarning("[Performance] Far voxels: shader missing or not supported, far view off"); return; }
            string dir = Path.Combine(Path.GetDirectoryName(typeof(FarVoxels).Assembly.Location) ?? "", "FarVoxels");
            if (!Directory.Exists(dir)) { Log.LogWarning("[Performance] Far voxels: no FarVoxels folder next to the DLL, far view off"); return; }
            var files = Directory.GetFiles(dir, "*.vox");
            ModelsTotal = files.Length;
            active = true;
            InstancedProps.Rescan();
            Task.Run(() =>
            {
                foreach (var file in files)
                {
                    try { var data = Read(file); lock (loadedData) loadedData.Enqueue(data); }
                    catch (Exception e) { Log.LogWarning("[Performance] Far voxels: " + Path.GetFileName(file) + ": " + e.Message); }
                }
            });
        }

        static Data Read(string file)
        {
            using (var gz = new GZipStream(File.OpenRead(file), CompressionMode.Decompress))
            using (var r = new BinaryReader(gz))
            {
                if (r.ReadInt32() != 0x3158564F) throw new InvalidDataException("not a far voxel model");
                var d = new Data { Name = Path.GetFileNameWithoutExtension(file), Dims = new Vector3Int(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()) };
                d.BoundsMin = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); d.VoxelSize = r.ReadSingle(); d.Mips = r.ReadInt32();
                d.Colour = new byte[d.Mips][]; d.Normal = new byte[d.Mips][];
                for (int m = 0; m < d.Mips; m++)
                {
                    int count = Math.Max(1, d.Dims.x >> m) * Math.Max(1, d.Dims.y >> m) * Math.Max(1, d.Dims.z >> m) * 4;
                    d.Colour[m] = r.ReadBytes(count); d.Normal[m] = r.ReadBytes(count);
                    if (d.Colour[m].Length != count || d.Normal[m].Length != count) throw new InvalidDataException("truncated");
                }
                return d;
            }
        }

        static void Upload(Data d)
        {
            if (!active) return;
            Texture3D Make(byte[][] mips)
            {
                var t = new Texture3D(d.Dims.x, d.Dims.y, d.Dims.z, TextureFormat.RGBA32, d.Mips) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Far voxels " + d.Name };
                for (int m = 0; m < d.Mips; m++) t.SetPixelData(mips[m], m);
                t.Apply(false, true);
                return t;
            }
            var mat = (template ? new Material(template) : new Material(shader)); mat.name = "Far voxels " + d.Name; mat.enableInstancing = true;
            mat.SetTexture(ColourId, Make(d.Colour)); mat.SetTexture(NormalId, Make(d.Normal));
            mat.SetVector(BoundsId, d.BoundsMin); mat.SetVector(DimsId, (Vector3)d.Dims); mat.SetFloat(SizeId, d.VoxelSize); mat.SetFloat(MipsId, d.Mips);
            modelIndex[d.Name] = models.Count; modelHashes[d.Name.GetStableHashCode()] = models.Count;
            // Small things (bushes, small rocks, young trees: under 8 m tall) stop at 300 m, the rest at the far view range.
            float height = d.Dims.y * d.VoxelSize;
            models.Add(new Model { Name = d.Name, Material = mat, Box = BoxMesh(d.BoundsMin, d.BoundsMin + (Vector3)d.Dims * d.VoxelSize), MaxDistance = height < SmallHeight ? SmallRange : MaxRange, Height = height });
            ModelsReady = models.Count;
            candidates = null; // vegetation entries are matched again with the new model
            zones.Clear();
        }

        static Mesh BoxMesh(Vector3 a, Vector3 b)
        {
            var v = new Vector3[8]; for (int i = 0; i < 8; i++) v[i] = new Vector3((i & 1) == 0 ? a.x : b.x, (i & 2) == 0 ? a.y : b.y, (i & 4) == 0 ? a.z : b.z);
            var m = new Mesh { name = "Far voxels box", vertices = v, triangles = new[] { 0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5 } };
            m.bounds = new Bounds((a + b) / 2, b - a);
            return m;
        }

        // ---- Placement past the loaded area: ZoneSystem.PlaceVegetation replayed for the trees that have a model ----
        static void Prepare()
        {
            candidates = new List<(ZoneSystem.ZoneVegetation, int)>();
            foreach (var veg in ZoneSystem.instance.m_vegetation)
            {
                if (!veg.m_enable || !veg.m_prefab || veg.m_max <= 0f || !string.IsNullOrEmpty(veg.AltBiomeParent)) continue;
                if (modelIndex.TryGetValue(veg.m_prefab.name, out int index)) candidates.Add((veg, index));
            }
        }

        static void Generate()
        {
            Vector3 eye = Player.m_localPlayer.transform.position;
            var center = ZoneSystem.GetZone(eye);
            int ring = Mathf.CeilToInt(FarRange / ZoneSize);
            var stop = System.Diagnostics.Stopwatch.StartNew();
            for (int r = 1; r <= ring && stop.Elapsed.TotalMilliseconds < 2.0; r++)
                for (int x = -r; x <= r && stop.Elapsed.TotalMilliseconds < 2.0; x++)
                    for (int y = -r; y <= r; y++)
                    {
                        if (Math.Abs(x) != r && Math.Abs(y) != r) continue;
                        var id = new Vector2Int(center.x + x, center.y + y);
                        if (zones.ContainsKey(id) || Loaded(id, center)) continue;
                        // The server's summary when it has come: the trees really standing there, or the original
                        // vegetation placed here when the zone was never generated. Nothing until the answer comes.
                        if (!FarTrees.TryGet(id, out var summary)) continue;
                        zones[id] = summary.Generated ? FromSummary(id, summary) : Place(id);
                        if (stop.Elapsed.TotalMilliseconds >= 2.0) break;
                    }
            // Zones whose summary changed (trees cut, grown, planted) are rebuilt.
            foreach (var id in FarTrees.Changed) zones.Remove(id);
            FarTrees.Changed.Clear();
            FarTrees.Tick(modelHashes.Keys);
            if (Time.frameCount % 120 == 0)
            {
                var gone = new List<Vector2Int>();
                foreach (var id in zones.Keys) if (Math.Abs(id.x - center.x) > ring + 2 || Math.Abs(id.y - center.y) > ring + 2) gone.Add(id);
                foreach (var id in gone) zones.Remove(id);
            }
        }

        static readonly Dictionary<int, int> modelHashes = new Dictionary<int, int>();

        static Zone FromSummary(Vector2Int id, FarTrees.Summary summary)
        {
            var zone = new Zone { Id = id };
            var zoneCenter = ZoneSystem.GetZonePos(new Vector2s(id.x, id.y));
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var t in summary.Trees)
            {
                if (!modelHashes.TryGetValue(t.Hash, out int model)) continue;
                zone.Items.Add((model, Matrix4x4.TRS(t.Position, t.Rotation, Vector3.one * t.Scale)));
                minY = Mathf.Min(minY, t.Position.y); maxY = Mathf.Max(maxY, t.Position.y);
            }
            if (zone.Items.Count == 0) { minY = 0f; maxY = 0f; }
            zone.Bounds = new Bounds(new Vector3(zoneCenter.x, (minY + maxY) * .5f + 20f, zoneCenter.z), new Vector3(ZoneSize + 40f, maxY - minY + 100f, ZoneSize + 40f));
            return zone;
        }

        static bool Loaded(Vector2Int id, Vector2s center) => ZNetScene.InActiveArea(ZoneSystem.GetZonePos(new Vector2s(id.x, id.y)), center);

        static readonly List<float> surround = new List<float>();

        static Zone Place(Vector2Int id)
        {
            var zone = new Zone { Id = id };
            var zoneCenter = ZoneSystem.GetZonePos(new Vector2s(id.x, id.y));
            var gen = WorldGenerator.instance;
            int seed = gen.GetSeed();
            var state = UnityEngine.Random.state;
            float minY = float.MaxValue, maxY = float.MinValue;
            try
            {
                foreach (var (veg, modelId) in candidates)
                {
                    UnityEngine.Random.InitState(seed + id.x * 4271 + id.y * 9187 + veg.m_prefab.name.GetStableHashCode());
                    int wanted = 1;
                    if (veg.m_max < 1f) { if (UnityEngine.Random.value > veg.m_max) continue; }
                    else wanted = UnityEngine.Random.Range((int)veg.m_min, (int)veg.m_max + 1);
                    float cosMax = Mathf.Cos(Mathf.Deg2Rad * veg.m_maxTilt), cosMin = Mathf.Cos(Mathf.Deg2Rad * veg.m_minTilt);
                    float half = 32f - veg.m_groupRadius;
                    int tries = veg.m_forcePlacement ? wanted * 50 : wanted, placed = 0;
                    surround.Clear();
                    for (int i = 0; i < tries; i++)
                    {
                        var start = new Vector3(UnityEngine.Random.Range(zoneCenter.x - half, zoneCenter.x + half), 0f, UnityEngine.Random.Range(zoneCenter.z - half, zoneCenter.z + half));
                        int group = UnityEngine.Random.Range(veg.m_groupSizeMin, veg.m_groupSizeMax + 1);
                        bool any = false;
                        for (int g = 0; g < group; g++)
                        {
                            Vector3 p = start;
                            if (g > 0) { float ang = UnityEngine.Random.value * Mathf.PI * 2f, dist = UnityEngine.Random.Range(0f, veg.m_groupRadius); p += new Vector3(Mathf.Sin(ang) * dist, 0f, Mathf.Cos(ang) * dist); }
                            float yaw = UnityEngine.Random.Range(0, 360), scale = UnityEngine.Random.Range(veg.m_scaleMin, veg.m_scaleMax);
                            float tiltX = UnityEngine.Random.Range(-veg.m_randTilt, veg.m_randTilt), tiltZ = UnityEngine.Random.Range(-veg.m_randTilt, veg.m_randTilt);
                            var biome = gen.GetBiome(p.x, p.z);
                            if ((veg.m_biome & biome) == 0 || (veg.m_biomeArea & gen.GetBiomeArea(p)) == 0) continue;
                            p.y = gen.GetHeight(p.x, p.z, out Color mask);
                            float altitude = p.y - 30f;
                            if (altitude < veg.m_minAltitude || altitude > veg.m_maxAltitude) continue;
                            if (!veg.m_minVegetation.Equals(veg.m_maxVegetation) && (mask.a > veg.m_maxVegetation || mask.a < veg.m_minVegetation)) continue;
                            if (!veg.m_minOceanDepth.Equals(veg.m_maxOceanDepth)) continue;
                            var normal = Normal(gen, p);
                            if (normal.y < cosMax || normal.y > cosMin) continue;
                            if (veg.m_minDistanceFromCenter > 0f || veg.m_maxDistanceFromCenter > 0f)
                            {
                                float d = Utils.LengthXZ(p);
                                if (veg.m_minDistanceFromCenter > 0f && d < veg.m_minDistanceFromCenter || veg.m_maxDistanceFromCenter > 0f && d > veg.m_maxDistanceFromCenter) continue;
                            }
                            if (veg.m_inForest) { float f = WorldGenerator.GetForestFactor(p); if (f < veg.m_forestTresholdMin || f > veg.m_forestTresholdMax) continue; }
                            if (veg.m_surroundCheckVegetation)
                            {
                                // As the game, with the vegetation mask from the world generator instead of the loaded heightmap.
                                float sum = 0f;
                                for (int layer = 0; layer < veg.m_surroundCheckLayers; layer++)
                                {
                                    float ringDist = (layer + 1f) / veg.m_surroundCheckLayers * veg.m_surroundCheckDistance;
                                    for (int k = 0; k < 6; k++)
                                    {
                                        float a = k / 6f * Mathf.PI * 2f;
                                        gen.GetHeight(p.x + Mathf.Sin(a) * ringDist, p.z + Mathf.Cos(a) * ringDist, out Color m2);
                                        sum += m2.a * ((1f - ringDist) / (veg.m_surroundCheckDistance * 2f));
                                    }
                                }
                                surround.Add(sum);
                                if (surround.Count < 10) continue;
                                float best = float.MinValue, total = 0f; foreach (var x in surround) { best = Mathf.Max(best, x); total += x; }
                                float average = total / surround.Count;
                                if (sum < average + (best - average) * veg.m_surroundBetterThanAverage) continue;
                            }
                            p.y += veg.m_groundOffset;
                            Quaternion rot;
                            if (veg.m_chanceToUseGroundTilt > 0f && UnityEngine.Random.value <= veg.m_chanceToUseGroundTilt)
                                rot = Quaternion.LookRotation(Vector3.Cross(normal, Quaternion.Euler(0f, yaw, 0f) * Vector3.forward), normal);
                            else rot = Quaternion.Euler(tiltX, yaw, tiltZ);
                            zone.Items.Add((modelId, Matrix4x4.TRS(p, rot, Vector3.one * scale)));
                            minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
                            any = true;
                        }
                        if (any) placed++;
                        if (placed >= wanted) break;
                    }
                }
            }
            finally { UnityEngine.Random.state = state; }
            if (zone.Items.Count == 0) { minY = 0f; maxY = 0f; }
            zone.Bounds = new Bounds(new Vector3(zoneCenter.x, (minY + maxY) * .5f + 20f, zoneCenter.z), new Vector3(ZoneSize + 40f, maxY - minY + 100f, ZoneSize + 40f));
            return zone;
        }

        static Vector3 Normal(WorldGenerator gen, Vector3 p)
        {
            float hx0 = gen.GetHeight(p.x - 1f, p.z), hx1 = gen.GetHeight(p.x + 1f, p.z), hz0 = gen.GetHeight(p.x, p.z - 1f), hz1 = gen.GetHeight(p.x, p.z + 1f);
            return new Vector3(hx0 - hx1, 2f, hz0 - hz1).normalized;
        }

        // ---- Drawing ----
        static void Draw()
        {
            var camera = Utils.GetMainCamera();
            if (!camera) { foreach (var m in models) { m.Draw.Clear(); m.DrawFar.Clear(); } Handed = 0; return; }
            // World size of one screen pixel at 1 m: the shader picks the voxel size that stays under a pixel.
            lastPixel = 2f * Mathf.Tan(.5f * camera.fieldOfView * Mathf.Deg2Rad) / Mathf.Max(1, Screen.height);
            lastFar = Mathf.Min(camera.farClipPlane, MaxRange);
            Shader.SetGlobalFloat(PixelId, lastPixel);
            GeometryUtility.CalculateFrustumPlanes(camera, planes);
            Vector3 eye = camera.transform.position;
            var center = ZoneSystem.GetZone(Player.m_localPlayer.transform.position);
            float fog = RenderSettings.fog ? RenderSettings.fogDensity : 0f;
            int copies = 0, drawn = Handed;
            foreach (var zone in zones.Values)
            {
                copies += zone.Items.Count;
                if (zone.Items.Count == 0 || Loaded(zone.Id, center) || !GeometryUtility.TestPlanesAABB(planes, zone.Bounds)) continue;
                float dist = Vector3.Distance(eye, zone.Bounds.center);
                if (dist > FarRange + ZoneSize) continue;
                if (fog > 0f && Mathf.Exp(-dist * fog) < FogCut) continue; // the game's fog hides it anyway
                bool shadows = dist < ShadowRange + ZoneSize;
                foreach (var (modelId, matrix) in zone.Items)
                {
                    var m = models[modelId]; if (dist > Mathf.Min(m.MaxDistance, FarRange) + ZoneSize * .5f) continue;
                    if (TooSmall(m, matrix, ((Vector3)matrix.GetColumn(3) - eye).sqrMagnitude)) continue;
                    (shadows ? m.Draw : m.DrawFar).Add(matrix); drawn++;
                }
            }
            int calls = 0;
            var world = new Bounds(eye, Vector3.one * (MaxRange * 2.5f));
            foreach (var model in models)
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    var list = pass == 0 ? model.Draw : model.DrawFar; if (list.Count == 0) continue;
                    var rp = new RenderParams(model.Material)
                    {
                        shadowCastingMode = pass == 0 ? ShadowCastingMode.On : ShadowCastingMode.Off, receiveShadows = true, layer = 0, worldBounds = world,
                        lightProbeUsage = LightProbeUsage.Off, reflectionProbeUsage = ReflectionProbeUsage.Off,
                    };
                    for (int s = 0; s < list.Count; s += Batch)
                    {
                        int take = Math.Min(Batch, list.Count - s);
                        list.CopyTo(s, batch, 0, take);
                        Graphics.RenderMeshInstanced(rp, model.Box, 0, batch, take);
                        calls++;
                    }
                    list.Clear();
                }
            }
            lastEye = eye;
            Zones = zones.Count; Copies = copies; Drawn = drawn; Calls = calls; Handed = 0;
        }

        static void Clear()
        {
            active = loading = false;
            foreach (var m in models)
            {
                if (m.Material) { Object.Destroy(m.Material.GetTexture(ColourId)); Object.Destroy(m.Material.GetTexture(NormalId)); Object.Destroy(m.Material); }
                if (m.Box) Object.Destroy(m.Box);
            }
            models.Clear(); modelIndex.Clear(); modelHashes.Clear(); zones.Clear(); candidates = null; FarTrees.Clear();
            lock (loadedData) loadedData.Clear();
            Zones = Copies = Drawn = Calls = ModelsReady = ModelsTotal = Handed = 0;
        }
    }
}
