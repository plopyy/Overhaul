using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Overhaul.Performance;
using Overhaul.Utility;

namespace Overhaul.Visuals
{
    // Far view, buildings: the game only creates the pieces near you, the rest of a base exists as network data (ZDO) the
    // game knows (your bases, others' you have been near, world ruins of places it generated). Beyond the zones the game
    // fills, out to 1.5 / 2.5 / 3.5 km (Low / Medium / High), those pieces are drawn as the game's own meshes and
    // materials (the least detailed level of each piece), merged per 64 m cell (one zone) into one mesh per material. A cell is
    // merged again when its pieces change (count or data revision) or when the zones the game fills move over it; one
    // cell per frame within a time budget, the nearest first. Not drawn far off: small pieces (under 1 m), pieces with
    // physics, ships and carts, pieces not using the game's piece shader.
    internal static partial class FarView
    {
        // One zone per cell: a 256 m cell holding a big base (5000+ pieces) was merged in one go (300-400 ms frames), again
        // each time the zones the game fills moved over it while walking near the base.
        const int BuildingCellZones = 1;
        const double BuildingBudgetMs = 3.0;
        static float BuildingReach(ClientVisuals.Level level) => level == ClientVisuals.Level.Low ? 1500f : level == ClientVisuals.Level.Medium ? 2500f : 3500f;

        // Each piece prefab: its far parts (mesh, submesh, material, local matrix), or none.
        sealed class PieceModel { internal readonly List<(Mesh Mesh, int Submesh, Material Material, Matrix4x4 Local)> Parts = new List<(Mesh, int, Material, Matrix4x4)>(); }
        static readonly Dictionary<int, PieceModel> pieceModels = new Dictionary<int, PieceModel>();

        sealed class BuildingCell { internal GameObject Go; internal long Print; internal readonly List<Mesh> Meshes = new List<Mesh>(); }
        static readonly Dictionary<(int, int), BuildingCell> buildingCells = new Dictionary<(int, int), BuildingCell>();
        static GameObject buildingRoot;
        static float nextBuildingScan;
        static readonly List<ZDO> zonePieces = new List<ZDO>();

        // The game's piece and static rock shaders draw the same far off (opaque, lit by the world).
        static bool FarShader(Material m) => m && m.shader && (m.shader.name == "Custom/Piece" || m.shader.name == "Custom/StaticRock");
        static bool Drawable(Renderer r) => r is MeshRenderer && r.enabled && r.gameObject.activeSelf && r.GetComponent<MeshFilter>() && System.Linq.Enumerable.Any(r.sharedMaterials, FarShader);

        static PieceModel ModelOf(int hash)
        {
            if (pieceModels.TryGetValue(hash, out var model)) return model;
            model = null;
            var prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab(hash) : null;
            if (prefab && (prefab.GetComponent<Piece>() || prefab.GetComponent<WearNTear>()) && !prefab.GetComponent<Rigidbody>() && !prefab.GetComponent<Ship>() && !prefab.GetComponent<Vagon>())
            {
                // The least detailed level of detail that has drawable parts (the last one is often the snow variant,
                // switched off outside the mountains); all mesh renderers when the piece has no LOD group.
                var group = prefab.GetComponentInChildren<LODGroup>(true);
                var lods = group ? group.GetLODs() : new LOD[0];
                IEnumerable<Renderer> renderers = prefab.GetComponentsInChildren<MeshRenderer>(true);
                for (int l = lods.Length - 1; l >= 0; l--)
                    if (System.Linq.Enumerable.Any(lods[l].renderers, x => Drawable(x))) { renderers = lods[l].renderers; break; }
                var toRoot = prefab.transform.worldToLocalMatrix;
                var candidate = new PieceModel();
                var bounds = new Bounds(); bool any = false;
                foreach (var r in renderers)
                {
                    if (!(r is MeshRenderer) || !r.enabled || !r.gameObject.activeSelf) continue;
                    var filter = r.GetComponent<MeshFilter>();
                    if (!filter || !filter.sharedMesh) continue;
                    var local = toRoot * r.transform.localToWorldMatrix;
                    var materials = r.sharedMaterials;
                    for (int s = 0; s < filter.sharedMesh.subMeshCount && s < materials.Length; s++)
                    {
                        var m = materials[s];
                        if (!FarShader(m)) continue;
                        candidate.Parts.Add((filter.sharedMesh, s, m, local));
                    }
                    var b = filter.sharedMesh.bounds;
                    var worldish = new Bounds(local.MultiplyPoint3x4(b.center), Vector3.Scale(b.size, local.lossyScale));
                    if (!any) { bounds = worldish; any = true; } else bounds.Encapsulate(worldish);
                }
                if (candidate.Parts.Count > 0 && Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z) >= 1f) model = candidate;
            }
            pieceModels[hash] = model;
            return model;
        }

        static readonly List<(int, int)> buildingOrder = new List<(int, int)>();
        static int buildingCursor, orderX = int.MinValue, orderZ, orderR;

        // A few cells per frame within a time budget, round-robin over the cells in reach (nearest first in each round).
        static void TickBuildings(Vector3 eye)
        {
            if (!ClientVisuals.FarBuildings || ZDOMan.instance == null || !ZNetScene.instance) { StopBuildings(); return; }
            if (!buildingRoot) { buildingRoot = new GameObject("Overhaul far buildings"); UnityEngine.Object.DontDestroyOnLoad(buildingRoot); }
            float reach = BuildingReach(ClientVisuals.FarViewInEffect), cellSize = BuildingCellZones * 64f;
            int r = Mathf.CeilToInt(reach / cellSize);
            int cx = Mathf.FloorToInt(eye.x / cellSize), cz = Mathf.FloorToInt(eye.z / cellSize);
            if (cx != orderX || cz != orderZ || r != orderR)
            {
                orderX = cx; orderZ = cz; orderR = r; buildingCursor = 0;
                buildingOrder.Clear();
                for (int x = cx - r; x <= cx + r; x++) for (int z = cz - r; z <= cz + r; z++) buildingOrder.Add((x, z));
                buildingOrder.Sort((a, b) => ((a.Item1 - cx) * (a.Item1 - cx) + (a.Item2 - cz) * (a.Item2 - cz)).CompareTo((b.Item1 - cx) * (b.Item1 - cx) + (b.Item2 - cz) * (b.Item2 - cz)));
                var inReach = new HashSet<(int, int)>(buildingOrder);
                foreach (var key in new List<(int, int)>(buildingCells.Keys))
                    if (!inReach.Contains(key)) { if (pending != null && pending.Key.Equals(key)) DropPending(); FreeCell(buildingCells[key]); buildingCells.Remove(key); ForgetCellGlows(key); }
            }
            if (buildingOrder.Count == 0) return;
            var player = ZoneSystem.GetZone(eye);
            int near = ZNet.instance.GetSyncedSimulationDistance().NearSimulationDistance;
            var watch = Stopwatch.StartNew();
            // The cell being merged goes on first, a chunk at a time within the budget.
            while (pending != null && watch.Elapsed.TotalMilliseconds < BuildingBudgetMs) StepMerge();
            for (int n = 0; n < buildingOrder.Count && pending == null && watch.Elapsed.TotalMilliseconds < BuildingBudgetMs; n++)
            {
                var key = buildingOrder[buildingCursor];
                buildingCursor = (buildingCursor + 1) % buildingOrder.Count;
                long print = CellPrint(key, player, near);
                buildingCells.TryGetValue(key, out var cell);
                if (cell != null && cell.Print == print) continue;
                if (cell == null) buildingCells[key] = cell = new BuildingCell();
                MergeCell(key, cell, player, near, print);
            }
        }

        static bool LoadedByGame(Vector2s zone, Vector2s player, int near) => Mathf.Abs(zone.x - player.x) <= near && Mathf.Abs(zone.y - player.y) <= near;

        // What a cell's merge depends on: its far pieces (count, data revisions, ids) and which of its zones the game fills.
        static long CellPrint((int, int) key, Vector2s player, int near)
        {
            long print = 17;
            for (int x = 0; x < BuildingCellZones; x++)
                for (int z = 0; z < BuildingCellZones; z++)
                {
                    var zone = new Vector2s(key.Item1 * BuildingCellZones + x, key.Item2 * BuildingCellZones + z);
                    if (LoadedByGame(zone, player, near)) { print = print * 31 + 1; continue; }
                    var list = ZoneObjects(zone);
                    if (list == null) continue;
                    foreach (var zdo in list)
                    {
                        // Which pieces and where (not their data revision, which changes all the time with wear or fuel);
                        // for a light, whether it burns.
                        int prefab = zdo.GetPrefab();
                        bool shown = ModelOf(prefab) != null, glows = GlowOf(prefab) != null;
                        if (!shown && !glows) continue;
                        var p = zdo.GetPosition();
                        print = print * 31 + zdo.m_uid.ID * 7 + Mathf.RoundToInt(p.x * 4f) * 13 + Mathf.RoundToInt(p.z * 4f) * 17 + Mathf.RoundToInt(p.y * 4f);
                        if (glows) print = print * 3 + (zdo.GetFloat(ZDOVars.s_fuel, 0f) > 0f ? 1 : 0);
                    }
                }
            return print;
        }

        static List<ZDO> ZoneObjects(Vector2s zone)
        {
            var index = ZoneSystem.SectorToIndex(zone).Sector;
            var sectors = ZDOMan.instance.m_objectsBySector;
            return index >= 0 && index < sectors.Length ? sectors[index] : null;
        }

        // A cell being merged a little each frame: its pieces gathered at once (quick), then merged in chunks of at most
        // ChunkPieces parts per mesh, within the frame budget, the cell's old meshes kept on screen until all are done.
        // (A whole cell merged in one go took 300-900 ms near a big base.)
        const int ChunkPieces = 200;
        sealed class PendingMerge
        {
            internal (int, int) Key; internal BuildingCell Cell; internal long Print; internal Vector3 Origin;
            internal List<(Material Material, CombineInstance[] Parts)> Groups; internal int Group, Offset;
            internal List<ZDO> Glows; internal GameObject Go; internal readonly List<Mesh> Meshes = new List<Mesh>();
        }
        static PendingMerge pending;

        // One chunk merged; true once the whole cell is done (then shown in place of its old meshes).
        static bool StepMerge()
        {
            var p = pending;
            if (p.Group >= p.Groups.Count) { FinishMerge(); return true; }
            var (material, parts) = p.Groups[p.Group];
            int take = Math.Min(ChunkPieces, parts.Length - p.Offset);
            var chunk = new CombineInstance[take]; Array.Copy(parts, p.Offset, chunk, 0, take);
            p.Offset += take;
            if (p.Offset >= parts.Length) { p.Group++; p.Offset = 0; }
            var mesh = new Mesh { name = "Overhaul far buildings", indexFormat = IndexFormat.UInt32 };
            try { mesh.CombineMeshes(chunk, true, true, false); }
            catch (Exception e) { UnityEngine.Object.Destroy(mesh); Log.LogWarning("[FarView] far buildings: " + e.Message); return false; }
            mesh.UploadMeshData(true);
            p.Meshes.Add(mesh);
            if (!p.Go) { p.Go = new GameObject("Far buildings " + p.Key.Item1 + "," + p.Key.Item2); p.Go.SetActive(false); p.Go.transform.SetParent(buildingRoot.transform, false); p.Go.transform.position = p.Origin; }
            var part = new GameObject(material.name) { layer = LayerMask.NameToLayer("piece") >= 0 ? LayerMask.NameToLayer("piece") : 0 };
            part.transform.SetParent(p.Go.transform, false);
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = part.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            return false;
        }

        static void FinishMerge()
        {
            var p = pending; pending = null;
            FreeCell(p.Cell);
            p.Cell.Print = p.Print; // after FreeCell, which clears it
            GlowsOfCell(p.Key, p.Glows);
            p.Cell.Go = p.Go; p.Cell.Meshes.AddRange(p.Meshes);
            if (p.Go) p.Go.SetActive(true);
        }

        // A merge left unfinished (the cell went out of reach, the far view stopped): what it built so far is dropped.
        static void DropPending()
        {
            if (pending == null) return;
            if (pending.Go) UnityEngine.Object.Destroy(pending.Go);
            foreach (var m in pending.Meshes) if (m) UnityEngine.Object.Destroy(m);
            pending = null;
        }

        static readonly Dictionary<Material, List<CombineInstance>> mergeGroups = new Dictionary<Material, List<CombineInstance>>();

        static void MergeCell((int, int) key, BuildingCell cell, Vector2s player, int near, long print)
        {
            foreach (var g in mergeGroups.Values) g.Clear();
            var origin = new Vector3(key.Item1 * BuildingCellZones * 64f, 0, key.Item2 * BuildingCellZones * 64f);
            int count = 0;
            var cellPieces = new List<ZDO>();
            for (int x = 0; x < BuildingCellZones; x++)
                for (int z = 0; z < BuildingCellZones; z++)
                {
                    var zone = new Vector2s(key.Item1 * BuildingCellZones + x, key.Item2 * BuildingCellZones + z);
                    if (LoadedByGame(zone, player, near)) continue;
                    var list = ZoneObjects(zone);
                    if (list == null) continue;
                    foreach (var zdo in list)
                    {
                        if (GlowOf(zdo.GetPrefab()) != null) cellPieces.Add(zdo);
                        var model = ModelOf(zdo.GetPrefab());
                        if (model == null) continue;
                        var world = Matrix4x4.TRS(zdo.GetPosition() - origin, zdo.GetRotation(), Vector3.one);
                        foreach (var part in model.Parts)
                        {
                            if (!mergeGroups.TryGetValue(part.Material, out var group)) mergeGroups[part.Material] = group = new List<CombineInstance>();
                            group.Add(new CombineInstance { mesh = part.Mesh, subMeshIndex = part.Submesh, transform = world * part.Local });
                        }
                        count++;
                    }
                }
            // Merged a chunk at a time from the next frames on (StepMerge); an empty cell is cleared at once.
            pending = new PendingMerge
            {
                Key = key, Cell = cell, Print = print, Origin = origin, Glows = cellPieces,
                Groups = count == 0 ? new List<(Material, CombineInstance[])>() : mergeGroups.Where(g => g.Value.Count > 0).Select(g => (g.Key, g.Value.ToArray())).ToList(),
            };
            if (count == 0) FinishMerge();
        }

        static void FreeCell(BuildingCell cell)
        {
            if (cell.Go) UnityEngine.Object.Destroy(cell.Go);
            foreach (var m in cell.Meshes) if (m) UnityEngine.Object.Destroy(m);
            cell.Meshes.Clear(); cell.Go = null; cell.Print = 0;
        }

        static void StopBuildings()
        {
            DropPending();
            foreach (var cell in buildingCells.Values) FreeCell(cell);
            buildingCells.Clear();
            cellGlows.Clear(); glowsDirty = true;
            if (buildingRoot) UnityEngine.Object.Destroy(buildingRoot);
            buildingRoot = null;
        }
    }
}
