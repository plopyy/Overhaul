using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;
using Overhaul.Performance;
using Overhaul.Utility;

namespace Overhaul.Visuals
{
    // Far view, terrain: the game draws its distant land as a 2.4 km square around you (TerrainLod). In its place, square
    // rings of terrain tiles reach out to 2.5 / 5 / 10 km (Low / Medium / High): 64x64 quads per tile, 10 m between points
    // in the first ring then twice as far apart in each next one. Heights, biome colours and the ground mask come from the
    // world generator exactly as the game makes its own distant land (same functions, same smoothing), worked out on a
    // background thread. Tiles are drawn with the game's distant terrain material, which already hides them where the real
    // terrain is drawn; each ring sits a little lower than the finer one inside it and each tile has a skirt, so rings meet
    // without cracks. A ring moves (recentres) once you went half a tile; its new tiles replace the old ones all at once.
    internal static partial class FarView
    {
        const int Quads = 64, Verts = Quads + 1, SkirtVerts = 4 * Verts;
        const float FirstSpacing = 10f;
        const double UploadBudgetMs = 1.5;

        sealed class Tile
        {
            internal int Level, X, Z;
            internal GameObject Go; internal Mesh Mesh; internal Texture2D Mask; internal MaterialPropertyBlock Props;
        }

        sealed class Job
        {
            internal int Level, X, Z, Epoch; internal WorldGenerator World;
            internal float[] Heights; internal Vector3[] Normals; internal Color32[] Colours; internal Color[] Mask;
        }

        sealed class Ring
        {
            internal int CentreX = int.MinValue, CentreZ = int.MinValue, FinerX = int.MinValue, FinerZ = int.MinValue;
            internal readonly Dictionary<(int, int), Tile> Shown = new Dictionary<(int, int), Tile>();
            internal readonly Dictionary<(int, int), Tile> Coming = new Dictionary<(int, int), Tile>();
            internal readonly HashSet<(int, int)> Wanted = new HashSet<(int, int)>();
        }

        static Ring[] rings = new Ring[0];
        static TerrainLod vanilla;
        static Material material;
        static int layer, epoch;
        static WorldGenerator world;
        static GameObject root;
        static Thread worker;
        static readonly BlockingCollection<Job> jobs = new BlockingCollection<Job>();
        static readonly ConcurrentQueue<Job> done = new ConcurrentQueue<Job>();
        static int[] indices;
        static readonly Vector2[] uvs = MakeUvs();

        static int RingCount(ClientVisuals.Level level) => level == ClientVisuals.Level.Low ? 2 : level == ClientVisuals.Level.Medium ? 3 : level == ClientVisuals.Level.High ? 4 : 0;
        static float Spacing(int level) => FirstSpacing * (1 << level);
        static float TileSize(int level) => Spacing(level) * Quads;

        internal static void Tick()
        {
            var wanted = RingCount(ClientVisuals.FarViewInEffect);
            var camera = Utils.GetMainCamera();
            bool canRun = wanted > 0 && camera && WorldGenerator.instance != null && ZNet.instance && !ZNet.instance.IsDedicated() && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
            if (!canRun || WorldGenerator.instance != world) { if (rings.Length > 0 || root) Stop(); if (!canRun) return; }
            if (!Prepare()) return;
            if (rings.Length != wanted) { Stop(); Prepare(); rings = new Ring[wanted]; for (int i = 0; i < wanted; i++) rings[i] = new Ring(); }

            var eye = camera.transform.position;
            for (int level = 0; level < rings.Length; level++) Plan(level, eye);
            Upload();
            for (int level = 0; level < rings.Length; level++) Swap(level);
            UpdateProperties();
        }

        // The game's distant land: its material and layer are reused, the land itself is switched off while ours is drawn.
        static bool Prepare()
        {
            if (!vanilla) vanilla = UnityEngine.Object.FindFirstObjectByType<TerrainLod>();
            if (!vanilla || !vanilla.m_material) return false;
            if (!root)
            {
                world = WorldGenerator.instance;
                material = vanilla.m_material;
                layer = vanilla.transform.childCount > 0 ? vanilla.transform.GetChild(0).gameObject.layer : vanilla.gameObject.layer;
                root = new GameObject("Overhaul far view");
                UnityEngine.Object.DontDestroyOnLoad(root);
                epoch++;
                if (worker == null) { worker = new Thread(Work) { IsBackground = true, Name = "Overhaul far view" }; worker.Start(); }
            }
            if (vanilla.enabled) vanilla.enabled = false;
            return true;
        }

        static void Stop()
        {
            epoch++;
            foreach (var ring in rings) { foreach (var t in ring.Shown.Values) Destroy(t); foreach (var t in ring.Coming.Values) Destroy(t); }
            rings = new Ring[0];
            if (root) UnityEngine.Object.Destroy(root);
            root = null;
            if (vanilla && !vanilla.enabled) vanilla.enabled = true;
            while (done.TryDequeue(out _)) { }
        }

        static void Destroy(Tile tile)
        {
            if (tile.Go) UnityEngine.Object.Destroy(tile.Go);
            if (tile.Mesh) UnityEngine.Object.Destroy(tile.Mesh);
            if (tile.Mask) UnityEngine.Object.Destroy(tile.Mask);
        }

        // A ring is a 4x4 square of tiles centred on the camera (snapped to its own tile grid), without the tiles the finer
        // ring inside it covers entirely.
        static void Plan(int level, Vector3 eye)
        {
            var ring = rings[level];
            float size = TileSize(level);
            int cx = Mathf.RoundToInt(eye.x / size), cz = Mathf.RoundToInt(eye.z / size);
            // Planned again when it moved, or when the finer ring inside it moved (what it may leave out changed).
            int fx = level > 0 ? rings[level - 1].CentreX : 0, fz = level > 0 ? rings[level - 1].CentreZ : 0;
            if (cx == ring.CentreX && cz == ring.CentreZ && fx == ring.FinerX && fz == ring.FinerZ) return;
            ring.CentreX = cx; ring.CentreZ = cz; ring.FinerX = fx; ring.FinerZ = fz;
            ring.Wanted.Clear();
            for (int x = cx - 2; x < cx + 2; x++)
                for (int z = cz - 2; z < cz + 2; z++)
                {
                    if (level > 0 && CoveredByFiner(level, x, z)) continue;
                    float minX = x * size, minZ = z * size;
                    if (Mathf.Max(Mathf.Abs(minX + size / 2), Mathf.Abs(minZ + size / 2)) - size / 2 > 10500f + size) continue;
                    ring.Wanted.Add((x, z));
                }
            foreach (var key in ring.Wanted)
                if (!ring.Shown.ContainsKey(key) && !ring.Coming.ContainsKey(key))
                {
                    ring.Coming[key] = new Tile { Level = level, X = key.Item1, Z = key.Item2 };
                    jobs.Add(new Job { Level = level, X = key.Item1, Z = key.Item2, Epoch = epoch, World = world });
                }
        }

        static bool CoveredByFiner(int level, int x, int z)
        {
            var finer = rings[level - 1];
            if (finer.CentreX == int.MinValue) return false;
            float fineSize = TileSize(level - 1), size = TileSize(level);
            float fMinX = (finer.CentreX - 2) * fineSize, fMaxX = (finer.CentreX + 2) * fineSize;
            float fMinZ = (finer.CentreZ - 2) * fineSize, fMaxZ = (finer.CentreZ + 2) * fineSize;
            return x * size >= fMinX && (x + 1) * size <= fMaxX && z * size >= fMinZ && (z + 1) * size <= fMaxZ;
        }

        // ---- Background work: the tile's heights, colours and mask, as the game makes its distant land ----
        static void Work()
        {
            foreach (var job in jobs.GetConsumingEnumerable())
            {
                try { if (job.Epoch == epoch) { Build(job); done.Enqueue(job); } }
                catch (Exception e) { Log.LogWarning("[FarView] tile failed: " + e.Message); }
            }
        }

        static void Build(Job job)
        {
            float spacing = Spacing(job.Level), size = TileSize(job.Level);
            float x0 = job.X * size, z0 = job.Z * size;
            var w = job.World;
            var heights = new float[Verts * Verts];
            var colours = new Color32[Verts * Verts];
            var mask = new Color[Verts * Verts];
            for (int k = 0; k < Verts; k++)
                for (int l = 0; l < Verts; l++)
                {
                    float wx = x0 + l * spacing, wz = z0 + k * spacing;
                    var biome = w.GetBiome(wx, wz, 0.02f, false);
                    heights[k * Verts + l] = w.GetBiomeHeight(biome, wx, wz, out var m, false, true);
                    mask[k * Verts + l] = m;
                    colours[k * Verts + l] = Heightmap.GetBiomeColor(w.GetBiomeSector(wx, wz, false));
                }
            // The game's smoothing of its distant land: 4 passes softening steps over 10 m.
            var copy = new float[heights.Length];
            for (int pass = 0; pass < 4; pass++)
            {
                Array.Copy(heights, copy, heights.Length);
                for (int k = 1; k < Verts - 1; k++)
                    for (int l = 1; l < Verts - 1; l++)
                    {
                        int i = k * Verts + l; float h = copy[i];
                        if (Mathf.Abs(h - copy[i - Verts]) > 10f) h = (h + copy[i - Verts]) * 0.5f;
                        if (Mathf.Abs(h - copy[i + Verts]) > 10f) h = (h + copy[i + Verts]) * 0.5f;
                        if (Mathf.Abs(h - copy[i - 1]) > 10f) h = (h + copy[i - 1]) * 0.5f;
                        if (Mathf.Abs(h - copy[i + 1]) > 10f) h = (h + copy[i + 1]) * 0.5f;
                        heights[i] = h;
                    }
            }
            var normals = new Vector3[Verts * Verts];
            for (int k = 0; k < Verts; k++)
                for (int l = 0; l < Verts; l++)
                {
                    float left = heights[k * Verts + Mathf.Max(0, l - 1)], right = heights[k * Verts + Mathf.Min(Verts - 1, l + 1)];
                    float down = heights[Mathf.Max(0, k - 1) * Verts + l], up = heights[Mathf.Min(Verts - 1, k + 1) * Verts + l];
                    normals[k * Verts + l] = new Vector3(left - right, 2f * spacing, down - up).normalized;
                }
            job.Heights = heights; job.Colours = colours; job.Mask = mask; job.Normals = normals;
        }

        // ---- Main thread: meshes from finished jobs, within a time budget ----
        static void Upload()
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalMilliseconds < UploadBudgetMs && done.TryDequeue(out var job))
            {
                if (job.Epoch != epoch || job.Level >= rings.Length) continue;
                if (!rings[job.Level].Coming.TryGetValue((job.X, job.Z), out var tile)) continue;
                Make(tile, job);
            }
        }

        static void Make(Tile tile, Job job)
        {
            float spacing = Spacing(job.Level), size = TileSize(job.Level);
            var vertices = new Vector3[Verts * Verts + SkirtVerts];
            var normals = new Vector3[vertices.Length];
            var colours = new Color32[vertices.Length];
            var uv = new Vector2[vertices.Length];
            for (int k = 0; k < Verts; k++)
                for (int l = 0; l < Verts; l++)
                {
                    int i = k * Verts + l;
                    vertices[i] = new Vector3(l * spacing, job.Heights[i], k * spacing);
                    normals[i] = job.Normals[i]; colours[i] = job.Colours[i]; uv[i] = uvs[i];
                }
            // Skirts: each edge's vertices again, 2 points' spacing lower, hiding cracks against coarser or finer rings.
            for (int s = 0; s < SkirtVerts; s++)
            {
                int src = SkirtSource(s);
                vertices[Verts * Verts + s] = vertices[src] - new Vector3(0, 2f * spacing, 0);
                normals[Verts * Verts + s] = normals[src]; colours[Verts * Verts + s] = colours[src]; uv[Verts * Verts + s] = uv[src];
            }
            var mesh = new Mesh { name = "Overhaul far land" };
            mesh.SetVertices(vertices); mesh.SetNormals(normals); mesh.SetColors(colours); mesh.SetUVs(0, uv);
            mesh.SetIndices(Indices(), MeshTopology.Triangles, 0, true);
            mesh.RecalculateTangents();
            mesh.UploadMeshData(true);

            var maskTexture = new Texture2D(Verts, Verts, TextureFormat.RGBA32, false) { name = "Overhaul far land mask", wrapMode = TextureWrapMode.Clamp };
            maskTexture.SetPixels(job.Mask); maskTexture.Apply(false, true);

            var go = new GameObject("Far land " + job.Level + " " + job.X + "," + job.Z) { layer = layer };
            go.transform.SetParent(root.transform, false);
            // Each coarser ring a little lower than the finer one inside it.
            go.transform.position = new Vector3(job.X * size, job.Level == 0 ? 0f : -2f * job.Level, job.Z * size);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.receiveShadows = false;
            renderer.shadowCastingMode = GraphicsSettingsManager.Instance != null && GraphicsSettingsManager.Instance.ActiveSettings.m_distantShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            go.SetActive(false);

            var props = new MaterialPropertyBlock();
            props.SetTexture("_ClearedMaskTex", maskTexture);
            props.SetFloatArray("_depth", CornerDepths(job.Heights));
            tile.Go = go; tile.Mesh = mesh; tile.Mask = maskTexture; tile.Props = props;
        }

        // As the game's own tiles: the sea depth at the four corners (water level 30 m).
        static float[] CornerDepths(float[] h) => new[]
        {
            Mathf.Max(0f, 30f - h[Quads * Verts]), Mathf.Max(0f, 30f - h[Quads * Verts + Quads]),
            Mathf.Max(0f, 30f - h[Quads]), Mathf.Max(0f, 30f - h[0]),
        };

        // A ring's new tiles replace its old ones only once all of them are made (no holes while moving).
        static void Swap(int level)
        {
            var ring = rings[level];
            foreach (var key in ring.Wanted) if (ring.Coming.TryGetValue(key, out var t) && !t.Go) return;
            var gone = new List<(int, int)>();
            foreach (var pair in ring.Shown) if (!ring.Wanted.Contains(pair.Key)) gone.Add(pair.Key);
            foreach (var key in gone) { Destroy(ring.Shown[key]); ring.Shown.Remove(key); }
            foreach (var pair in ring.Coming) { if (pair.Value.Go) pair.Value.Go.SetActive(true); ring.Shown[pair.Key] = pair.Value; }
            ring.Coming.Clear();
        }

        // Where the game draws its real terrain (changes with the simulation distance), the far land hides.
        static float nextProps;
        static void UpdateProperties()
        {
            if (Time.time < nextProps) return;
            nextProps = Time.time + 1f;
            float hide = ZoneSystem.instance ? Heightmap.GetLodHideDistance() : 128f;
            foreach (var ring in rings)
                foreach (var tile in ring.Shown.Values)
                {
                    if (!tile.Go) continue;
                    tile.Props.SetFloat("_LodHideDistance", hide);
                    tile.Go.GetComponent<MeshRenderer>().SetPropertyBlock(tile.Props);
                }
        }

        static Vector2[] MakeUvs()
        {
            var uv = new Vector2[Verts * Verts];
            for (int k = 0; k < Verts; k++) for (int l = 0; l < Verts; l++) uv[k * Verts + l] = new Vector2((float)l / Quads, (float)k / Quads);
            return uv;
        }

        // Skirt vertex s: bottom edge, top edge, left edge, right edge, 65 each.
        static int SkirtSource(int s)
        {
            int edge = s / Verts, i = s % Verts;
            return edge == 0 ? i : edge == 1 ? Quads * Verts + i : edge == 2 ? i * Verts : i * Verts + Quads;
        }

        static int[] Indices()
        {
            if (indices != null) return indices;
            var list = new List<int>(Quads * Quads * 6 + 4 * Quads * 12);
            for (int k = 0; k < Quads; k++)
                for (int l = 0; l < Quads; l++)
                {
                    int a = k * Verts + l, b = a + 1, c = a + Verts, d = c + 1;
                    list.Add(a); list.Add(c); list.Add(b); list.Add(b); list.Add(c); list.Add(d);
                }
            // Skirt quads, both windings (seen from either side).
            for (int edge = 0; edge < 4; edge++)
                for (int i = 0; i < Quads; i++)
                {
                    int top0 = SkirtSource(edge * Verts + i), top1 = SkirtSource(edge * Verts + i + 1);
                    int low0 = Verts * Verts + edge * Verts + i, low1 = low0 + 1;
                    list.Add(top0); list.Add(low0); list.Add(top1); list.Add(top1); list.Add(low0); list.Add(low1);
                    list.Add(top0); list.Add(top1); list.Add(low0); list.Add(top1); list.Add(low1); list.Add(low0);
                }
            return indices = list.ToArray();
        }
    }
}
