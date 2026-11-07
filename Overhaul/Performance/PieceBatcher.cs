using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Overhaul.Utility;
using Object = UnityEngine.Object;

namespace Overhaul.Performance
{
    // Building pieces are merged, per 16 m cell of the world, into a few big meshes (one per material and shadow setting), so a
    // base is drawn with tens of draw calls instead of thousands. The pieces themselves stay as they are (colliders, network,
    // wear, support, interaction): only their renderers are switched off while their look is part of the merged meshes.
    // The merged meshes keep the pieces' level of detail: a detailed set and a low one (each piece's first and last LOD level),
    // switched by a LODGroup on the cell at the distances the pieces themselves would use.
    //
    // A piece takes part only while nothing makes it look different from its prefab: the game gives a piece its own shader values
    // (MaterialMan: the hammer's highlight, Ashlands heat, Deep North snow) and such a piece is left out and drawn alone, as soon
    // as the value is set. Left out too: moving pieces (on ships, carts), pieces with animations, doors, stands, and any renderer
    // not using the game's piece shader or whose mesh cannot be read.
    // Any change in a cell (a piece built, destroyed, worn, broken, highlighted, moved) gives the cell back its own pieces at
    // once; the cell is merged again when it has been quiet for a moment, one cell per frame.
    internal static class PieceBatcher
    {
        const float CellSize = 16f, QuietTime = .75f;
        const int MinPieces = 4;
        const string PieceShader = "Custom/Piece";

        sealed class Cell
        {
            internal Vector2Int Key;
            internal readonly HashSet<WearNTear> Pieces = new HashSet<WearNTear>();
            internal readonly List<Renderer> Hidden = new List<Renderer>();
            internal readonly List<Mesh> Meshes = new List<Mesh>();
            internal GameObject Root;
            internal bool Dirty = true;
            internal float ChangedAt;
        }

        sealed class PieceState { internal Cell Cell; internal Vector3 Position; internal Quaternion Rotation; internal int Visual; internal float ExemptUntil; internal bool Merged; internal MaterialMan.PropertyContainer Container; }

        static readonly Dictionary<Vector2Int, Cell> cells = new Dictionary<Vector2Int, Cell>();
        static readonly Dictionary<WearNTear, PieceState> states = new Dictionary<WearNTear, PieceState>();
        static readonly Dictionary<MaterialMan.PropertyContainer, WearNTear> byContainer = new Dictionary<MaterialMan.PropertyContainer, WearNTear>();
        static readonly List<Cell> dirtyScan = new List<Cell>();
        static readonly Dictionary<Shader, bool> shaderOk = new Dictionary<Shader, bool>();
        static bool active;
        static int checkIndex;
        internal static int MergedPieces, MergedCells, MergedRenderers, MergedDrawn;

        static bool On => PerfMonitor.On(OverhaulConfig.PerfMergePieces);

        static Vector2Int KeyOf(Vector3 p) => new Vector2Int(Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.z / CellSize));

        // ---- Registration ----
        static void Add(WearNTear wear)
        {
            if (!wear || states.ContainsKey(wear)) return;
            var t = wear.transform;
            var key = KeyOf(t.position);
            if (!cells.TryGetValue(key, out var cell)) cells[key] = cell = new Cell { Key = key };
            cell.Pieces.Add(wear);
            states[wear] = new PieceState { Cell = cell, Position = t.position, Rotation = t.rotation, Visual = Visual(wear) };
            Touch(cell);
        }

        static void Remove(WearNTear wear)
        {
            if (!states.TryGetValue(wear, out var state)) return;
            states.Remove(wear);
            if (state.Container != null) byContainer.Remove(state.Container);
            state.Cell.Pieces.Remove(wear);
            Touch(state.Cell);
        }

        // Something changed in the cell: its pieces are shown as themselves at once, merged again later.
        static void Touch(Cell cell)
        {
            if (cell.Root) Unmerge(cell);
            cell.Dirty = true;
            cell.ChangedAt = Time.time;
        }

        static void Touch(WearNTear wear, float exempt = 0f)
        {
            if (!wear || !states.TryGetValue(wear, out var state)) return;
            if (exempt > 0f) state.ExemptUntil = Mathf.Max(state.ExemptUntil, Time.time + exempt);
            // Already drawn alone: nothing to rebuild.
            if (exempt > 0f && !state.Merged) return;
            Touch(state.Cell);
        }

        // Which of the new / worn / broken looks is shown.
        static int Visual(WearNTear wear) =>
            (wear.m_new && wear.m_new.activeSelf ? 1 : 0) | (wear.m_worn && wear.m_worn.activeSelf ? 2 : 0) | (wear.m_broken && wear.m_broken.activeSelf ? 4 : 0);

        [HarmonyPatch(typeof(WearNTear), "Awake")]
        private static class OnAwake
        {
            private static void Postfix(WearNTear __instance)
            {
                if (__instance.m_nview && __instance.m_nview.GetZDO() != null) Add(__instance);
            }
        }

        [HarmonyPatch(typeof(WearNTear), "OnDestroy")]
        private static class OnDestroyed { private static void Prefix(WearNTear __instance) => Remove(__instance); }

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.SetHealthVisual))]
        private static class OnVisual
        {
            private static void Postfix(WearNTear __instance)
            {
                if (!states.TryGetValue(__instance, out var state)) return;
                int visual = Visual(__instance);
                if (visual == state.Visual) return;
                state.Visual = visual;
                Touch(state.Cell);
            }
        }

        // The game gives a piece shader values of its own (highlight, heat, snow): drawn alone while it has any.
        [HarmonyPatch(typeof(MaterialMan), nameof(MaterialMan.QueuePropertyUpdate))]
        private static class OnShaderValues
        {
            private static void Prefix(MaterialMan.PropertyContainer p)
            {
                if (p != null && byContainer.TryGetValue(p, out var wear) && p.m_shaderProperties.Count > 0) Touch(wear, .5f);
            }
        }

        // The hammer's highlight lasts 0.2 s after the last call: the piece stays out a little longer.
        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Highlight))]
        private static class OnHighlight { private static void Prefix(WearNTear __instance) => Touch(__instance, 1f); }

        // ---- Per frame ----
        internal static void Tick()
        {
            bool on = On;
            if (on != active)
            {
                active = on;
                if (!on) foreach (var cell in cells.Values) { if (cell.Root) Unmerge(cell); cell.Dirty = true; }
                else foreach (var cell in cells.Values) { cell.Dirty = true; cell.ChangedAt = Time.time - QuietTime; }
            }
            if (!on) return;
            CheckMoves();
            // One quiet dirty cell per frame, the closest to the camera first.
            Cell best = null; float bestDistance = float.MaxValue;
            var camera = Utils.GetMainCamera(); Vector3 eye = camera ? camera.transform.position : Vector3.zero;
            float now = Time.time;
            foreach (var cell in cells.Values)
            {
                if (!cell.Dirty || now - cell.ChangedAt < QuietTime) continue;
                if (cell.Pieces.Count < MinPieces) { cell.Dirty = false; continue; }
                float d = (new Vector2((cell.Key.x + .5f) * CellSize, (cell.Key.y + .5f) * CellSize) - new Vector2(eye.x, eye.z)).sqrMagnitude;
                if (d < bestDistance) { bestDistance = d; best = cell; }
            }
            if (best != null) Merge(best);
            if (cells.Count > 0 && Time.frameCount % 600 == 0) Prune();
        }

        // A few pieces checked per frame: moved (relocation, physics) or worn/broken without SetHealthVisual.
        static void CheckMoves()
        {
            if (states.Count == 0) return;
            int n = Math.Min(64, states.Count);
            var list = PerfLists.Pieces(states.Keys);
            for (int i = 0; i < n; i++)
            {
                if (checkIndex >= list.Count) checkIndex = 0;
                var wear = list[checkIndex++];
                if (!wear || !states.TryGetValue(wear, out var state)) continue;
                var t = wear.transform;
                if (t.position != state.Position || t.rotation != state.Rotation)
                {
                    Remove(wear); Add(wear);
                }
                else if (Visual(wear) != state.Visual) { state.Visual = Visual(wear); Touch(state.Cell); }
            }
        }

        static void Prune()
        {
            var empty = new List<Vector2Int>();
            foreach (var pair in cells) if (pair.Value.Pieces.Count == 0 && !pair.Value.Root) empty.Add(pair.Key);
            foreach (var key in empty) cells.Remove(key);
        }

        // ---- Merge ----
        static void Unmerge(Cell cell)
        {
            foreach (var wear in cell.Pieces) if (wear && states.TryGetValue(wear, out var state)) state.Merged = false;
            foreach (var r in cell.Hidden) if (r) r.enabled = true;
            cell.Hidden.Clear();
            foreach (var m in cell.Meshes) if (m) Object.Destroy(m);
            cell.Meshes.Clear();
            if (cell.Root) Object.Destroy(cell.Root);
            cell.Root = null;
            MergedCells = Math.Max(0, MergedCells - 1);
        }

        sealed class Part { internal Renderer Renderer; internal Mesh Mesh; internal bool High, Low; }
        static readonly List<Part> parts = new List<Part>();
        static readonly List<Renderer> pieceRenderers = new List<Renderer>();
        static readonly Dictionary<(Material, ShadowCastingMode, bool, bool), List<CombineInstance>> groups = new Dictionary<(Material, ShadowCastingMode, bool, bool), List<CombineInstance>>();

        static bool UsableShader(Material m)
        {
            if (!m || !m.shader) return false;
            if (!shaderOk.TryGetValue(m.shader, out bool ok)) shaderOk[m.shader] = ok = m.shader.name == PieceShader;
            return ok;
        }

        // The renderers of a piece that can be merged, with the LOD set they belong to; false leaves the whole piece alone.
        static bool Collect(WearNTear wear, PieceState state, List<Part> into)
        {
            if (state.ExemptUntil > Time.time || !wear.enabled || !wear.gameObject.activeInHierarchy || wear.transform.parent) return false;
            if (wear.GetComponentInChildren<Animator>() || wear.GetComponent<Door>() || wear.GetComponent<ItemStand>() || wear.GetComponent<ArmorStand>()
                || wear.GetComponent<Ship>() || wear.GetComponent<Vagon>() || wear.GetComponentInParent<Rigidbody>() is Rigidbody body && !body.isKinematic) return false;
            // Shader values of its own (highlight, heat, snow): drawn alone.
            var man = MaterialMan.instance;
            if (man && man.m_blocks.TryGetValue(wear.gameObject.GetInstanceID(), out var container))
            {
                if (state.Container != container) { if (state.Container != null) byContainer.Remove(state.Container); state.Container = container; byContainer[container] = wear; }
                if (container.m_shaderProperties.Count > 0) return false;
            }
            var lod = wear.GetComponentInChildren<LODGroup>();
            LOD[] lods = lod ? lod.GetLODs() : null;
            int start = into.Count;
            pieceRenderers.Clear();
            wear.GetComponentsInChildren(false, pieceRenderers);
            foreach (var r in pieceRenderers)
            {
                if (!r.enabled || r is ParticleSystemRenderer) continue;
                if (!(r is MeshRenderer)) { if (InLod(lods, r) >= 0) goto refuse; continue; }
                var filter = r.GetComponent<MeshFilter>(); var mesh = filter ? filter.sharedMesh : null;
                var materials = r.sharedMaterials;
                bool usable = mesh && mesh.isReadable && materials.Length > 0 && materials.Length <= mesh.subMeshCount;
                if (usable) foreach (var m in materials) if (!UsableShader(m)) { usable = false; break; }
                int level = InLod(lods, r);
                if (!usable)
                {
                    // A LOD level we cannot merge would be left switching on its own: keep the whole piece as it is.
                    if (level >= 0) goto refuse;
                    continue;
                }
                bool high = lods == null || level < 0 || level == 0, low = lods == null || level < 0 || level == lods.Length - 1;
                // Middle LOD levels are not drawn while merged (their piece is shown by the first and last levels).
                into.Add(new Part { Renderer = r, Mesh = mesh, High = high, Low = low });
            }
            return true;
        refuse:
            into.RemoveRange(start, into.Count - start);
            return false;
        }

        static int InLod(LOD[] lods, Renderer r)
        {
            if (lods == null) return -1;
            for (int i = 0; i < lods.Length; i++) foreach (var x in lods[i].renderers) if (x == r) return i;
            return -1;
        }

        static void Merge(Cell cell)
        {
            cell.Dirty = false;
            if (cell.Root) Unmerge(cell);
            parts.Clear();
            int pieces = 0;
            float highDistance = 0, cullDistance = 0; int lodPieces = 0;
            foreach (var wear in cell.Pieces)
            {
                if (!wear || !states.TryGetValue(wear, out var state)) continue;
                int before = parts.Count;
                if (!Collect(wear, state, parts) || parts.Count == before) continue;
                state.Merged = true;
                pieces++;
                var lod = wear.GetComponentInChildren<LODGroup>();
                if (lod)
                {
                    // Distance at which the piece leaves its first LOD level and disappears, in "size / height" units
                    // (the cell's LODGroup turns them back into screen heights for its own size).
                    var lods = lod.GetLODs(); float size = lod.size * MaxScale(lod.transform);
                    if (lods.Length > 0 && size > 0)
                    {
                        highDistance += size / Mathf.Max(.0001f, lods[0].screenRelativeTransitionHeight);
                        cullDistance += size / Mathf.Max(.0001f, lods[lods.Length - 1].screenRelativeTransitionHeight);
                        lodPieces++;
                    }
                }
            }
            if (pieces < MinPieces)
            {
                foreach (var wear in cell.Pieces) if (wear && states.TryGetValue(wear, out var state)) state.Merged = false;
                parts.Clear(); return;
            }

            var root = new GameObject("Overhaul merged pieces " + cell.Key);
            root.transform.position = new Vector3((cell.Key.x + .5f) * CellSize, 0f, (cell.Key.y + .5f) * CellSize);
            int layer = LayerMask.NameToLayer("piece");
            root.layer = layer;
            var toLocal = root.transform.worldToLocalMatrix;
            var high = Build(root, "high", true, toLocal, layer, cell);
            var low = Build(root, "low", false, toLocal, layer, cell);
            foreach (var p in parts) { p.Renderer.enabled = false; cell.Hidden.Add(p.Renderer); }
            // Middle LOD levels of merged pieces: hidden too.
            foreach (var wear in cell.Pieces)
            {
                if (!wear) continue;
                var lod = wear.GetComponentInChildren<LODGroup>(); if (!lod) continue;
                bool merged = false;
                foreach (var p in parts) if (p.Renderer.transform.IsChildOf(wear.transform)) { merged = true; break; }
                if (!merged) continue;
                var lods = lod.GetLODs();
                for (int i = 1; i < lods.Length - 1; i++) foreach (var r in lods[i].renderers) if (r && r.enabled) { r.enabled = false; cell.Hidden.Add(r); }
            }
            if (lodPieces > 0 && low.Count > 0)
            {
                var group = root.AddComponent<LODGroup>();
                var bounds = new Bounds(); bool first = true;
                foreach (var r in high) { if (first) { bounds = r.bounds; first = false; } else bounds.Encapsulate(r.bounds); }
                foreach (var r in low) { if (first) { bounds = r.bounds; first = false; } else bounds.Encapsulate(r.bounds); }
                float size = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
                group.localReferencePoint = root.transform.InverseTransformPoint(bounds.center);
                group.size = size;
                float h0 = Mathf.Clamp(size / (highDistance / lodPieces), .002f, .9f), h1 = Mathf.Clamp(size / (cullDistance / lodPieces), .0005f, h0 * .9f);
                group.SetLODs(new[] { new LOD(h0, high.ToArray()), new LOD(h1, low.ToArray()) });
            }
            else foreach (var r in low) r.enabled = false;
            cell.Root = root;
            MergedCells++;
            parts.Clear();
            Recount();
        }

        static float MaxScale(Transform t) { var s = t.lossyScale; return Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)); }

        static List<Renderer> Build(GameObject root, string name, bool high, Matrix4x4 toLocal, int layer, Cell cell)
        {
            groups.Clear();
            foreach (var p in parts)
            {
                if (high ? !p.High : !p.Low) continue;
                var r = p.Renderer; var materials = r.sharedMaterials;
                var matrix = toLocal * r.localToWorldMatrix;
                for (int i = 0; i < materials.Length; i++)
                {
                    var key = (materials[i], r.shadowCastingMode, r.receiveShadows, false);
                    if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<CombineInstance>();
                    list.Add(new CombineInstance { mesh = p.Mesh, subMeshIndex = i, transform = matrix });
                }
            }
            var made = new List<Renderer>();
            foreach (var pair in groups)
            {
                var mesh = new Mesh { name = "merged " + pair.Key.Item1.name, indexFormat = IndexFormat.UInt32 };
                mesh.CombineMeshes(pair.Value.ToArray(), true, true, false);
                mesh.UploadMeshData(true);
                cell.Meshes.Add(mesh);
                var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer)) { layer = layer };
                go.transform.SetParent(root.transform, false);
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.GetComponent<MeshRenderer>();
                mr.sharedMaterial = pair.Key.Item1;
                mr.shadowCastingMode = pair.Key.Item2;
                mr.receiveShadows = pair.Key.Item3;
                made.Add(mr);
            }
            return made;
        }

        static void Recount()
        {
            int pieces = 0, renderers = 0, drawn = 0;
            foreach (var cell in cells.Values)
            {
                if (!cell.Root) continue;
                renderers += cell.Hidden.Count;
                drawn += cell.Root.GetComponentsInChildren<MeshRenderer>().Length;
                pieces += cell.Pieces.Count;
            }
            MergedPieces = pieces; MergedRenderers = renderers; MergedDrawn = drawn;
        }
    }

    // A reusable list view of a key collection, rebuilt only when its count changes (the move check walks it a slice at a time).
    internal static class PerfLists
    {
        static readonly List<WearNTear> list = new List<WearNTear>();
        static int version = -1;
        internal static List<WearNTear> Pieces(ICollection<WearNTear> keys)
        {
            if (keys.Count != version || Time.frameCount % 300 == 0) { list.Clear(); list.AddRange(keys); version = keys.Count; }
            return list;
        }
    }
}
