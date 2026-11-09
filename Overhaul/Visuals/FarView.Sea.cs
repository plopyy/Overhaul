using UnityEngine;
using UnityEngine.Rendering;

namespace Overhaul.Visuals
{
    // Far view, sea: the game's ocean is one plane (EnvMan/WaterPlane/watersurface) that ends short of the far land. Around
    // it, a ring of sea out to the far view's reach, child of that plane (so it follows it the same way) and drawn with the
    // plane's own material (same waves, colours, world edge). Its inner edge is the plane's own edge, measured on its mesh.
    internal static partial class FarView
    {
        static GameObject sea;
        static int seaRings;

        static void TickSea()
        {
            if (seaRings == rings.Length && sea) return;
            if (sea) Object.Destroy(sea);
            sea = null; seaRings = rings.Length;
            if (rings.Length == 0 || !EnvMan.instance) return;
            var surface = EnvMan.instance.transform.Find("WaterPlane/watersurface");
            var renderer = surface ? surface.GetComponent<MeshRenderer>() : null;
            var filter = surface ? surface.GetComponent<MeshFilter>() : null;
            if (!renderer || !filter || !filter.sharedMesh || !renderer.sharedMaterial) return;
            var bounds = filter.sharedMesh.bounds;
            var scale = surface.lossyScale;
            if (Mathf.Abs(scale.x) < 1e-4f || Mathf.Abs(scale.z) < 1e-4f) return;
            // Plane-local sizes: the plane's own half extent inside, the far view's reach (+ a margin) outside.
            float reach = TileSize(rings.Length - 1) * 2f + 512f;
            Vector2 inner = new Vector2(bounds.extents.x, bounds.extents.z) * 0.995f, centre = new Vector2(bounds.center.x, bounds.center.z);
            Vector2 outer = new Vector2(reach / Mathf.Abs(scale.x), reach / Mathf.Abs(scale.z));
            if (outer.x <= inner.x || outer.y <= inner.y) return;
            Utility.Log.LogInfo("[FarView] game sea plane: " + filter.sharedMesh.vertexCount + " vertices, local extents " + bounds.extents + ", scale " + scale + "; far sea from there to " + reach.ToString("0") + " m");
            sea = new GameObject("Overhaul far sea") { layer = surface.gameObject.layer };
            sea.transform.SetParent(surface, false);
            sea.AddComponent<MeshFilter>().sharedMesh = SquareRing(centre, inner, outer, bounds.center.y, 32);
            var ring = sea.AddComponent<MeshRenderer>();
            ring.sharedMaterial = renderer.sharedMaterial;
            ring.shadowCastingMode = ShadowCastingMode.Off;
            ring.receiveShadows = renderer.receiveShadows;
        }

        static void StopSea() { if (sea) Object.Destroy(sea); sea = null; seaRings = 0; }

        // The band between two centred rectangles, each side split in steps (outward rows of quads) so the water's
        // per-vertex effects keep some resolution far off.
        static Mesh SquareRing(Vector2 c, Vector2 inner, Vector2 outer, float y, int steps)
        {
            var vertices = new System.Collections.Generic.List<Vector3>();
            var tris = new System.Collections.Generic.List<int>();
            var corners = new[] { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) };
            const int Rows = 8;
            for (int side = 0; side < 4; side++)
            {
                Vector2 a = corners[side], b = corners[(side + 1) % 4];
                int start = vertices.Count;
                for (int r = 0; r <= Rows; r++)
                {
                    float t = (float)r / Rows; t *= t;
                    var size = Vector2.Lerp(inner, outer, t);
                    for (int s = 0; s <= steps; s++)
                    {
                        var p = Vector2.Lerp(a, b, (float)s / steps);
                        vertices.Add(new Vector3(c.x + p.x * size.x, y, c.y + p.y * size.y));
                    }
                }
                for (int r = 0; r < Rows; r++)
                    for (int s = 0; s < steps; s++)
                    {
                        int i0 = start + r * (steps + 1) + s, i1 = i0 + 1, i2 = i0 + steps + 1, i3 = i2 + 1;
                        tris.Add(i0); tris.Add(i2); tris.Add(i1); tris.Add(i1); tris.Add(i2); tris.Add(i3);
                    }
            }
            var mesh = new Mesh { name = "Overhaul far sea", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(tris, 0);
            var normals = new Vector3[vertices.Count];
            for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
            mesh.normals = normals;
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            return mesh;
        }
    }
}
