using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using Overhaul.Performance;
using Overhaul.Utility;

namespace Overhaul.Visuals
{
    // Far view, buildings: the game only creates the pieces near you, the rest of a base exists as network data (ZDO) the
    // game knows (your bases, others' you have been near, world ruins of places it generated). Beyond the zones the game
    // fills, out to 1.5 / 2.5 / 3.5 km (Low / Medium / High), those pieces are drawn as the game's own meshes and
    // materials (the least detailed level of each piece), merged per 256 m cell into one mesh per material. A cell is
    // merged again when its pieces change (count or data revision) or when the zones the game fills move over it; one
    // cell per frame within a time budget, the nearest first. Not drawn far off: small pieces (under 1 m), pieces with
    // physics, ships and carts, pieces not using the game's piece shader.
    internal static partial class FarView
    {
        const int BuildingCellZones = 4;
        const double BuildingBudgetMs = 3.0;
        static float BuildingReach(ClientVisuals.Level level) => level == ClientVisuals.Level.Low ? 1500f : level == ClientVisuals.Level.Medium ? 2500f : 3500f;

        // Each piece prefab: its far parts (mesh, submesh, material, local matrix), or none.
        sealed class PieceModel { internal readonly List<(Mesh Mesh, int Submesh, Material Material, Matrix4x4 Local)> Parts = new List<(Mesh, int, Material, Matrix4x4)>(); }
        static readonly Dictionary<int, PieceModel> pieceModels = new Dictionary<int, PieceModel>();

        sealed class BuildingCell { internal GameObject Go; internal long Print; internal readonly List<Mesh> Meshes = new List<Mesh>(); }
        static readonly Dictionary<(int, int), BuildingCell> buildingCells = new Dictionary<(int, int), BuildingCell>();
        static GameObject buildingRoot;
        static int logMerges = 12;
        static float nextVisibleLog;
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
                var seen = new System.Text.StringBuilder();
                foreach (var r in renderers)
                {
                    if (r) seen.Append(r.name).Append(r.enabled ? "" : "(off)").Append(r.gameObject.activeSelf ? "" : "(inactive)").Append(":").Append(string.Join("/", System.Linq.Enumerable.Select(r.sharedMaterials, m => m && m.shader ? m.shader.name : "-"))).Append(" ");
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
                else Utility.Log.LogInfo("[FarView] far buildings leave out " + prefab.name + ": " + (candidate.Parts.Count == 0 ? "no part with the piece shader" : "smaller than 1 m") + " [" + seen + "]");
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
                    if (!inReach.Contains(key)) { FreeCell(buildingCells[key]); buildingCells.Remove(key); ForgetCellGlows(key); }
            }
            if (Time.time >= nextVisibleLog && buildingRoot)
            {
                nextVisibleLog = Time.time + 10f;
                var all = buildingRoot.GetComponentsInChildren<MeshRenderer>();
                int seen = 0; foreach (var mr in all) if (mr.isVisible) seen++;
                var first = all.Length > 0 ? all[0] : null;
                Utility.Log.LogInfo("[FarView] far buildings: " + seen + " of " + all.Length + " meshes drawn" + (first ? ", first on layer " + first.gameObject.layer + " (" + LayerMask.LayerToName(first.gameObject.layer) + "), active " + first.gameObject.activeInHierarchy + ", bounds " + first.bounds : "") + ", camera mask " + (Camera.main ? Camera.main.cullingMask.ToString("X") : "-") + ", far clip " + (Camera.main ? Camera.main.farClipPlane : 0));
            }
            if (buildingOrder.Count == 0) return;
            var player = ZoneSystem.GetZone(eye);
            int near = ZNet.instance.GetSyncedSimulationDistance().NearSimulationDistance;
            var watch = Stopwatch.StartNew();
            for (int n = 0; n < buildingOrder.Count && watch.Elapsed.TotalMilliseconds < BuildingBudgetMs; n++)
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
            FreeCell(cell);
            cell.Print = print; // after FreeCell, which clears it
            GlowsOfCell(key, cellPieces);
            if (count == 0) return;
            Utility.Log.LogInfo("[FarView] far buildings: cell " + key.Item1 + "," + key.Item2 + " merged, " + count + " pieces");
            cell.Go = new GameObject("Far buildings " + key.Item1 + "," + key.Item2);
            cell.Go.transform.SetParent(buildingRoot.transform, false);
            cell.Go.transform.position = origin;
            foreach (var pair in mergeGroups)
            {
                if (pair.Value.Count == 0) continue;
                var mesh = new Mesh { name = "Overhaul far buildings", indexFormat = IndexFormat.UInt32 };
                try { mesh.CombineMeshes(pair.Value.ToArray(), true, true, false); }
                catch (Exception e) { UnityEngine.Object.Destroy(mesh); Log.LogWarning("[FarView] far buildings: " + e.Message); continue; }
                if (logMerges == 12) { var mat = pair.Key; var sb = new System.Text.StringBuilder("[FarView] piece material " + mat.name + " shader " + mat.shader.name + " keywords [" + string.Join(",", mat.shaderKeywords) + "] queue " + mat.renderQueue + ":"); for (int i = 0; i < mat.shader.GetPropertyCount(); i++) { var n = mat.shader.GetPropertyName(i); var t = mat.shader.GetPropertyType(i); sb.Append(" " + n + "=" + (t == UnityEngine.Rendering.ShaderPropertyType.Float || t == UnityEngine.Rendering.ShaderPropertyType.Range ? mat.GetFloat(n).ToString("0.###") : t == UnityEngine.Rendering.ShaderPropertyType.Color ? mat.GetColor(n).ToString() : t == UnityEngine.Rendering.ShaderPropertyType.Vector ? mat.GetVector(n).ToString() : "tex")); } Utility.Log.LogInfo(sb.ToString()); }
                if (logMerges-- > 0) Utility.Log.LogInfo("[FarView] far buildings mesh " + pair.Key.name + ": " + mesh.vertexCount + " vertices from " + pair.Value.Count + " parts, bounds " + mesh.bounds + " at " + origin);
                mesh.UploadMeshData(true);
                cell.Meshes.Add(mesh);
                var part = new GameObject(pair.Key.name) { layer = LayerMask.NameToLayer("piece") >= 0 ? LayerMask.NameToLayer("piece") : 0 };
                part.transform.SetParent(cell.Go.transform, false);
                part.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = part.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = pair.Key;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = true;
            }
        }

        static void FreeCell(BuildingCell cell)
        {
            if (cell.Go) UnityEngine.Object.Destroy(cell.Go);
            foreach (var m in cell.Meshes) if (m) UnityEngine.Object.Destroy(m);
            cell.Meshes.Clear(); cell.Go = null; cell.Print = 0;
        }

        static void StopBuildings()
        {
            foreach (var cell in buildingCells.Values) FreeCell(cell);
            buildingCells.Clear();
            cellGlows.Clear(); glowsDirty = true;
            if (buildingRoot) UnityEngine.Object.Destroy(buildingRoot);
            buildingRoot = null;
        }
    }
}
