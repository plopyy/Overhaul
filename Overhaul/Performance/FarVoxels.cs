using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using Overhaul.Utility;
using Object = UnityEngine.Object;

namespace Overhaul.Performance
{
    // Experimental far view (prototype, off by default): trees and big rocks beyond the loaded zones, drawn as voxel models.
    //
    // Where: the world's vegetation is placed again on the client for the zones between the edge of the loaded area and
    // FarRange, the same way ZoneSystem.PlaceVegetation does it (same seeds, same random sequence, same biome / altitude /
    // vegetation mask / forest / tilt rules, heights from WorldGenerator). Checks that need the loaded world (blocked spots,
    // locations' clear areas, the terrain delta) are skipped, and trees cut or rocks mined there still show: the server
    // summary of far zones is the next step.
    // What: each tree / rock prefab is voxelised once, like Nanite foliage: only leaf texels that pass the alpha cut make
    // voxels, a voxel stays if enough of the leaf surface crossing it is opaque, keeps its averaged colour and normal, and a
    // baked occlusion (rays through the voxel grid) gives crowns their dark inside. Three voxel sizes, chosen by distance.
    // The voxel models are drawn instanced with a copy of a game rock material (game lighting and fog), colours in a palette
    // texture. Model building runs on worker threads (only mesh / texture read-backs and the final upload use the main thread).
    internal static class FarVoxels
    {
        internal const float FarRange = 1000f;
        static readonly float[] Sizes = { .4f, .8f, 1.6f };
        static readonly float[] LevelEnd = { 400f, 800f, float.MaxValue };
        const float Coverage = .45f, AoFloor = .12f, ZoneSize = 64f;
        const int Batch = 1023;

        sealed class Model
        {
            internal string Name; internal bool Ready, Failed;
            internal Mesh[] Meshes = new Mesh[3]; internal Material[] Materials = new Material[3];
            internal readonly List<Matrix4x4>[] Draw = { new List<Matrix4x4>(), new List<Matrix4x4>(), new List<Matrix4x4>() };
            internal int[] Triangles = new int[3];
        }
        sealed class Zone { internal Vector2Int Id; internal Bounds Bounds; internal readonly List<(int Model, Matrix4x4 Matrix)> Items = new List<(int, Matrix4x4)>(); }

        static readonly List<Model> models = new List<Model>();
        static readonly Dictionary<string, int> modelIndex = new Dictionary<string, int>();
        static readonly Dictionary<Vector2Int, Zone> zones = new Dictionary<Vector2Int, Zone>();
        static readonly Queue<Action> mainThread = new Queue<Action>();
        static List<(ZoneSystem.ZoneVegetation Veg, int Model)> candidates;
        static Material template;
        static bool active;
        static readonly Plane[] planes = new Plane[6];
        static readonly Matrix4x4[] batch = new Matrix4x4[Batch];
        internal static int Zones, Copies, Drawn, Calls, Triangles, ModelsReady, ModelsTotal;

        static bool On => PerfMonitor.On(OverhaulConfig.PerfFarVoxels);

        internal static void Tick()
        {
            bool want = On && ZoneSystem.instance && WorldGenerator.instance != null && ZNetScene.instance && Player.m_localPlayer && !ZNet.instance.IsDedicated();
            if (!want) { if (active) Clear(); return; }
            active = true;
            lock (mainThread) { int n = 0; while (mainThread.Count > 0 && n++ < 2) mainThread.Dequeue()(); }
            if (candidates == null) Prepare();
            if (template == null) return;
            Generate();
            Draw();
        }

        // ---- Setup: which vegetation entries are far-view worthy (trees, big rocks) and the material template ----
        static void Prepare()
        {
            candidates = new List<(ZoneSystem.ZoneVegetation, int)>();
            var rock = ZNetScene.instance.GetPrefab("Rock_3");
            var rockRenderer = rock ? rock.GetComponentInChildren<MeshRenderer>(true) : null;
            if (rockRenderer && rockRenderer.sharedMaterial)
            {
                template = new Material(rockRenderer.sharedMaterial) { name = "Overhaul far voxels", enableInstancing = true };
                foreach (var k in new[] { "_NORMALMAP", "_METALLICGLOSSMAP", "_SPECGLOSSMAP", "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A", "_EMISSION" }) template.DisableKeyword(k);
                foreach (var p in new[] { "_BumpMap", "_MetallicGlossMap", "_SpecGlossMap", "_MossTex", "_EmissionMap" }) if (template.HasProperty(p)) template.SetTexture(p, null);
                foreach (var p in new[] { "_Glossiness", "_Metallic", "_MossBlend", "_GlossMapScale" }) if (template.HasProperty(p)) template.SetFloat(p, 0f);
                if (template.HasProperty("_Color")) template.SetColor("_Color", Color.white);
            }
            else { Log.LogWarning("[Performance] Far voxels: no rock material found, far view off"); return; }
            foreach (var veg in ZoneSystem.instance.m_vegetation)
            {
                if (!veg.m_enable || !veg.m_prefab || veg.m_max <= 0f || !string.IsNullOrEmpty(veg.AltBiomeParent)) continue;
                var prefab = veg.m_prefab;
                if (prefab.GetComponent<Pickable>()) continue;
                bool tree = prefab.GetComponent<TreeBase>();
                if (!tree && !Big(prefab)) continue;
                if (!modelIndex.TryGetValue(prefab.name, out int index))
                {
                    index = models.Count; modelIndex[prefab.name] = index;
                    var model = new Model { Name = prefab.name }; models.Add(model);
                    Build(prefab, model);
                }
                candidates.Add((veg, index));
            }
            ModelsTotal = models.Count;
            Log.LogInfo("[Performance] Far voxels: " + candidates.Count + " vegetation entries, " + models.Count + " models");
        }

        static bool Big(GameObject prefab)
        {
            var b = new Bounds(); bool any = false;
            foreach (var r in prefab.GetComponentsInChildren<MeshRenderer>(true))
                if (Shown(r.transform, prefab.transform) && r.GetComponent<MeshFilter>()?.sharedMesh) { if (any) b.Encapsulate(r.bounds); else { b = r.bounds; any = true; } }
            return any && Mathf.Max(b.size.x, b.size.y, b.size.z) >= 3f;
        }

        static bool Shown(Transform t, Transform root)
        {
            for (; t; t = t.parent) { if (!t.gameObject.activeSelf) return false; if (t == root) return true; }
            return true;
        }

        // ---- Model building ----
        struct Surface
        {
            internal Vector3[] Verts, Normals; internal Vector2[] Uv; internal Color32[] VColor; internal int[] Tris;
            internal Color32[] Pixels; internal int W, H; internal Color Tint; internal float Cutoff, Dark; internal bool Foliage; internal Vector3 Center;
        }

        static void Build(GameObject prefab, Model model)
        {
            var surfaces = new List<Surface>();
            try
            {
                var root = prefab.transform; var toRoot = root.worldToLocalMatrix;
                var renderers = new List<Renderer>();
                var group = prefab.GetComponentInChildren<LODGroup>(true);
                if (group && group.GetLODs().Length > 0) renderers.AddRange(group.GetLODs()[0].renderers);
                else renderers.AddRange(prefab.GetComponentsInChildren<MeshRenderer>(true));
                foreach (var r in renderers)
                {
                    if (!(r is MeshRenderer) || !Shown(r.transform, root)) continue;
                    string n = r.name.ToLowerInvariant();
                    if (n.Contains("destruct") || n.Contains("frac") || n.Contains("snow") || n.Contains("worn") || n.Contains("broken")) continue;
                    var mf = r.GetComponent<MeshFilter>(); if (!mf || !mf.sharedMesh) continue;
                    var mesh = Readable(mf.sharedMesh); if (mesh == null || mesh.vertexCount == 0) continue;
                    var m = toRoot * r.transform.localToWorldMatrix;
                    var verts = mesh.vertices; var normals = mesh.normals; var uv = mesh.uv; var vcol = mesh.colors32;
                    for (int i = 0; i < verts.Length; i++) verts[i] = m.MultiplyPoint3x4(verts[i]);
                    if (normals.Length == verts.Length) for (int i = 0; i < normals.Length; i++) normals[i] = m.MultiplyVector(normals[i]).normalized; else normals = null;
                    Vector3 center = toRoot.MultiplyPoint3x4(r.bounds.center);
                    var mats = r.sharedMaterials;
                    for (int sub = 0; sub < mesh.subMeshCount && sub < mats.Length; sub++)
                    {
                        var mat = mats[sub]; if (!mat) continue;
                        var tex = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
                        var s = new Surface
                        {
                            Verts = verts, Normals = normals, Uv = uv.Length == verts.Length ? uv : null, VColor = vcol.Length == verts.Length ? vcol : null,
                            Tris = mesh.GetTriangles(sub), Tint = mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white,
                            Cutoff = mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff") : .5f, Center = center,
                            // The game's PBR "metal" share darkens the diffuse colour; folded into the albedo.
                            Dark = 1f - (mat.HasProperty("_MetalGloss") ? mat.GetFloat("_MetalGloss") : mat.HasProperty("_Metallic") ? mat.GetFloat("_Metallic") : 0f),
                        };
                        if (tex) { s.Pixels = Pixels(tex, out s.W, out s.H); s.Foliage = mat.HasProperty("_Cutoff") && mat.shader.name.Contains("Vegetation") && (mat.IsKeywordEnabled("_NORMALMAP") || n.Contains("leaf") || mat.name.Contains("leaf") || mat.name.Contains("needle")) || mat.IsKeywordEnabled("_ALPHATEST_ON"); }
                        surfaces.Add(s);
                    }
                }
            }
            catch (Exception e) { Log.LogWarning("[Performance] Far voxels: " + prefab.name + ": " + e.Message); }
            if (surfaces.Count == 0) { model.Failed = true; return; }
            Task.Run(() =>
            {
                try
                {
                    for (int level = 0; level < Sizes.Length; level++)
                    {
                        var built = Voxelise(surfaces, Sizes[level], model.Name.GetHashCode());
                        int l = level;
                        lock (mainThread) mainThread.Enqueue(() => Upload(model, l, built));
                    }
                }
                catch (Exception e) { Log.LogWarning("[Performance] Far voxels: " + model.Name + ": " + e); model.Failed = true; }
            });
        }

        sealed class Cell { internal Vector3 Color, Normal; internal int N, Hits, Total; internal float Ao; }
        sealed class Built { internal Vector3[] Verts, Normals; internal Vector2[] Uv; internal int[] Tris; internal Color32[] Palette; internal int PaletteW, PaletteH; }
        static readonly Vector3Int[] Faces = { Vector3Int.right, Vector3Int.left, Vector3Int.up, Vector3Int.down, new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1) };

        static Built Voxelise(List<Surface> surfaces, float size, int seed)
        {
            var cells = new Dictionary<Vector3Int, Cell>();
            var rng = new System.Random(seed);
            float step = size / 3f;
            foreach (var s in surfaces)
            {
                var v = s.Verts; var t = s.Tris;
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    int a = t[i], b = t[i + 1], c = t[i + 2];
                    float area = Vector3.Cross(v[b] - v[a], v[c] - v[a]).magnitude * .5f;
                    int samples = Mathf.Clamp(Mathf.CeilToInt(area / (step * step)), 1, 2000);
                    for (int k = 0; k < samples; k++)
                    {
                        float u = (float)rng.NextDouble(), w = (float)rng.NextDouble();
                        if (u + w > 1f) { u = 1f - u; w = 1f - w; }
                        float w0 = 1f - u - w;
                        Vector3 p = v[a] * w0 + v[b] * u + v[c] * w;
                        var key = new Vector3Int(Mathf.FloorToInt(p.x / size), Mathf.FloorToInt(p.y / size), Mathf.FloorToInt(p.z / size));
                        if (!cells.TryGetValue(key, out var cell)) cells[key] = cell = new Cell();
                        cell.Total++;
                        Color col = s.Tint;
                        if (s.Pixels != null && s.Uv != null)
                        {
                            Vector2 uv = s.Uv[a] * w0 + s.Uv[b] * u + s.Uv[c] * w;
                            int x = (int)(Mathf.Repeat(uv.x, 1f) * s.W) % s.W, y = (int)(Mathf.Repeat(uv.y, 1f) * s.H) % s.H;
                            Color tc = s.Pixels[y * s.W + x];
                            if (s.Foliage && tc.a < s.Cutoff) continue; // the holes between leaves stay empty
                            col *= tc;
                        }
                        if (s.VColor != null) col *= (Color)s.VColor[a];
                        Vector3 n = s.Normals != null ? (s.Normals[a] * w0 + s.Normals[b] * u + s.Normals[c] * w).normalized : Vector3.Cross(v[b] - v[a], v[c] - v[a]).normalized;
                        // Leaf cards are two-sided: their normals point away from the crown so they add up instead of cancelling.
                        if (s.Foliage && Vector3.Dot(n, p - s.Center) < 0f) n = -n;
                        Color lin = col.linear * s.Dark;
                        cell.Color += new Vector3(lin.r, lin.g, lin.b); cell.Normal += n; cell.N++; cell.Hits++;
                        if (!s.Foliage) cell.Total = Math.Max(cell.Total, cell.Hits);
                    }
                }
            }
            var drop = new List<Vector3Int>();
            foreach (var kv in cells) if (kv.Value.N == 0 || kv.Value.Hits < kv.Value.Total * Coverage) drop.Add(kv.Key);
            foreach (var k in drop) cells.Remove(k);

            // Occlusion: share of rays (weighted towards the sky) that leave the voxels within ~3 m.
            int reach = Mathf.Clamp(Mathf.CeilToInt(3f / size), 2, 8);
            foreach (var kv in cells)
            {
                float open = 0f, weight = 0f;
                for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
                {
                    if (x == 0 && y == 0 && z == 0) continue;
                    var r = new Vector3Int(x, y, z); float wgt = y > 0 ? 1.5f : y < 0 ? .5f : 1f; weight += wgt;
                    bool hit = false;
                    for (int i = 1; i <= reach && !hit; i++) hit = cells.ContainsKey(kv.Key + r * i);
                    if (!hit) open += wgt;
                }
                kv.Value.Ao = open / weight;
            }

            int count = cells.Count, pw = Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.CeilToInt(Mathf.Sqrt(count))), 1, 1024), ph = Mathf.Max(1, Mathf.CeilToInt(count / (float)pw));
            var built = new Built { Palette = new Color32[pw * ph], PaletteW = pw, PaletteH = ph };
            var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uvs = new List<Vector2>(); var tris = new List<int>();
            int index = 0;
            foreach (var kv in cells)
            {
                var cell = kv.Value;
                Vector3 c = cell.Color / cell.N * Mathf.Lerp(AoFloor, 1f, cell.Ao);
                built.Palette[index] = new Color(c.x, c.y, c.z).gamma;
                var uv = new Vector2((index % pw + .5f) / pw, (index / pw + .5f) / ph); index++;
                Vector3 n = cell.Normal.sqrMagnitude > 1e-6f ? cell.Normal.normalized : Vector3.up;
                Vector3 o = (Vector3)kv.Key * size + Vector3.one * (size * .5f);
                foreach (var d in Faces)
                {
                    if (cells.ContainsKey(kv.Key + d)) continue;
                    Vector3 fn = d, t1 = Mathf.Abs(fn.y) > .5f ? Vector3.right : Vector3.up, t2 = Vector3.Cross(fn, t1);
                    Vector3 fc = o + fn * (size * .5f); float h = size * .5f; int b0 = verts.Count;
                    verts.Add(fc + (-t1 - t2) * h); verts.Add(fc + (t1 - t2) * h); verts.Add(fc + (t1 + t2) * h); verts.Add(fc + (-t1 + t2) * h);
                    for (int i = 0; i < 4; i++) { norms.Add(n); uvs.Add(uv); }
                    tris.Add(b0); tris.Add(b0 + 2); tris.Add(b0 + 1); tris.Add(b0); tris.Add(b0 + 3); tris.Add(b0 + 2);
                }
            }
            built.Verts = verts.ToArray(); built.Normals = norms.ToArray(); built.Uv = uvs.ToArray(); built.Tris = tris.ToArray();
            return built;
        }

        static void Upload(Model model, int level, Built b)
        {
            if (!active || b.Verts.Length == 0) { if (b.Verts.Length == 0) model.Failed = true; return; }
            var mesh = new Mesh { name = "Far voxels " + model.Name + " " + level, indexFormat = b.Verts.Length > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.vertices = b.Verts; mesh.normals = b.Normals; mesh.uv = b.Uv; mesh.triangles = b.Tris; mesh.RecalculateBounds(); mesh.UploadMeshData(true);
            var tex = new Texture2D(b.PaletteW, b.PaletteH, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = mesh.name };
            tex.SetPixels32(b.Palette); tex.Apply(false, true);
            var mat = new Material(template) { name = mesh.name, enableInstancing = true }; mat.SetTexture("_MainTex", tex);
            model.Meshes[level] = mesh; model.Materials[level] = mat; model.Triangles[level] = b.Tris.Length / 3;
            if (model.Meshes[0] && model.Meshes[1] && model.Meshes[2]) { model.Ready = true; ModelsReady++; }
        }

        // GPU read-back of a mesh the game keeps unreadable on the CPU (same layout and sub-meshes), as a plain mesh.
        static Mesh Readable(Mesh source)
        {
            if (source.isReadable) return source;
            source.vertexBufferTarget |= GraphicsBuffer.Target.Raw; source.indexBufferTarget |= GraphicsBuffer.Target.Raw;
            var mesh = new Mesh { indexFormat = source.indexFormat };
            mesh.SetVertexBufferParams(source.vertexCount, source.GetVertexAttributes());
            for (int stream = 0; stream < source.vertexBufferCount; stream++)
                using (var vb = source.GetVertexBuffer(stream))
                {
                    var data = new byte[vb.count * vb.stride]; vb.GetData(data);
                    mesh.SetVertexBufferData(data, 0, 0, data.Length, stream, MeshUpdateFlags.DontValidateIndices);
                }
            using (var ib = source.GetIndexBuffer())
            {
                var bytes = new byte[ib.count * ib.stride]; ib.GetData(bytes);
                mesh.SetIndexBufferParams(ib.count, source.indexFormat);
                mesh.SetIndexBufferData(bytes, 0, 0, bytes.Length, MeshUpdateFlags.DontValidateIndices);
            }
            mesh.subMeshCount = source.subMeshCount;
            for (int i = 0; i < source.subMeshCount; i++) mesh.SetSubMesh(i, source.GetSubMesh(i), MeshUpdateFlags.DontRecalculateBounds);
            return mesh;
        }

        static Color32[] Pixels(Texture tex, out int w, out int h)
        {
            w = Mathf.Min(tex.width, 256); h = Mathf.Min(tex.height, 256);
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var old = RenderTexture.active;
            Graphics.Blit(tex, rt);
            var copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
            RenderTexture.active = rt; copy.ReadPixels(new Rect(0, 0, w, h), 0, 0); copy.Apply(); RenderTexture.active = old;
            RenderTexture.ReleaseTemporary(rt);
            var px = copy.GetPixels32(); Object.Destroy(copy);
            return px;
        }

        // ---- Placement: ZoneSystem.PlaceVegetation replayed for far zones ----
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
                        zones[id] = Place(id);
                        if (stop.Elapsed.TotalMilliseconds >= 2.0) break;
                    }
            // Forget zones far behind.
            if (Time.frameCount % 120 == 0)
            {
                var gone = new List<Vector2Int>();
                foreach (var id in zones.Keys) if (Math.Abs(id.x - center.x) > ring + 2 || Math.Abs(id.y - center.y) > ring + 2) gone.Add(id);
                foreach (var id in gone) zones.Remove(id);
            }
        }

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
                            if ((veg.m_minDistanceFromCenter > 0f || veg.m_maxDistanceFromCenter > 0f))
                            {
                                float d = Utils.LengthXZ(p);
                                if (veg.m_minDistanceFromCenter > 0f && d < veg.m_minDistanceFromCenter || veg.m_maxDistanceFromCenter > 0f && d > veg.m_maxDistanceFromCenter) continue;
                            }
                            if (veg.m_inForest) { float f = WorldGenerator.GetForestFactor(p); if (f < veg.m_forestTresholdMin || f > veg.m_forestTresholdMax) continue; }
                            if (veg.m_surroundCheckVegetation) continue; // needs the loaded heightmap
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
            zone.Bounds = new Bounds(new Vector3(zoneCenter.x, (minY + maxY) * .5f + 20f, zoneCenter.z), new Vector3(ZoneSize + 30f, maxY - minY + 80f, ZoneSize + 30f));
            return zone;
        }

        static bool Loaded(Vector2Int id, Vector2s center) => ZNetScene.InActiveArea(ZoneSystem.GetZonePos(new Vector2s(id.x, id.y)), center);

        static Vector3 Normal(WorldGenerator gen, Vector3 p)
        {
            float hx0 = gen.GetHeight(p.x - 1f, p.z), hx1 = gen.GetHeight(p.x + 1f, p.z), hz0 = gen.GetHeight(p.x, p.z - 1f), hz1 = gen.GetHeight(p.x, p.z + 1f);
            return new Vector3(hx0 - hx1, 2f, hz0 - hz1).normalized;
        }

        // ---- Drawing ----
        static void Draw()
        {
            var camera = Utils.GetMainCamera();
            if (!camera) return;
            GeometryUtility.CalculateFrustumPlanes(camera, planes);
            Vector3 eye = camera.transform.position;
            var center = ZoneSystem.GetZone(Player.m_localPlayer.transform.position);
            foreach (var m in models) foreach (var l in m.Draw) l.Clear();
            int copies = 0, drawn = 0, tris = 0;
            foreach (var zone in zones.Values)
            {
                copies += zone.Items.Count;
                // Loaded zones show the real objects.
                if (Loaded(zone.Id, center)) continue;
                if (zone.Items.Count == 0 || !GeometryUtility.TestPlanesAABB(planes, zone.Bounds)) continue;
                float dist = Vector3.Distance(eye, zone.Bounds.center);
                if (dist > FarRange + ZoneSize) continue;
                int level = 0; while (dist > LevelEnd[level]) level++;
                foreach (var (modelId, matrix) in zone.Items)
                {
                    var model = models[modelId]; if (!model.Ready) continue;
                    model.Draw[level].Add(matrix); drawn++; tris += model.Triangles[level];
                }
            }
            int calls = 0;
            foreach (var model in models)
            {
                if (!model.Ready) continue;
                for (int level = 0; level < 3; level++)
                {
                    var list = model.Draw[level]; if (list.Count == 0) continue;
                    var rp = new RenderParams(model.Materials[level])
                    {
                        shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false, layer = 0,
                        worldBounds = new Bounds(eye, Vector3.one * (FarRange * 2.5f)),
                        lightProbeUsage = LightProbeUsage.Off, reflectionProbeUsage = ReflectionProbeUsage.Off,
                    };
                    for (int s = 0; s < list.Count; s += Batch)
                    {
                        int take = Math.Min(Batch, list.Count - s);
                        list.CopyTo(s, batch, 0, take);
                        Graphics.RenderMeshInstanced(rp, model.Meshes[level], 0, batch, take);
                        calls++;
                    }
                }
            }
            Zones = zones.Count; Copies = copies; Drawn = drawn; Calls = calls; Triangles = tris;
        }

        static void Clear()
        {
            active = false;
            foreach (var m in models) for (int i = 0; i < 3; i++)
            {
                if (m.Meshes[i]) Object.Destroy(m.Meshes[i]);
                if (m.Materials[i]) { Object.Destroy(m.Materials[i].GetTexture("_MainTex")); Object.Destroy(m.Materials[i]); }
            }
            models.Clear(); modelIndex.Clear(); zones.Clear(); candidates = null;
            if (template) Object.Destroy(template);
            template = null;
            lock (mainThread) mainThread.Clear();
            Zones = Copies = Drawn = Calls = Triangles = ModelsReady = ModelsTotal = 0;
        }
    }
}
