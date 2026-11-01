using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Overhaul.Rarity
{
    // The glow of a levelled weapon: soft clouds and sparks born on the model's own surface, so it fits any item.
    internal sealed class WeaponGlow : MonoBehaviour
    {
        const float Cell = .06f;
        static readonly Dictionary<Mesh, Vector3[]> Shapes = new Dictionary<Mesh, Vector3[]>();
        Transform held;
        Vector3[] points;
        ParticleSystem clouds, sparks;
        float strength, cloudsDue, sparksDue, nextBake, logAt;
        SkinnedMeshRenderer[] skinned;
        Vector3[] fixedPoints;
        Mesh baked;

        internal void Setup(Transform weapon, float level, Material silhouette)
        {
            held = weapon; strength = level;
            clouds = GetComponent<ParticleSystem>();
            sparks = transform.Find("Sparks").GetComponent<ParticleSystem>();
            var found = weapon.GetComponentsInChildren<MeshFilter>().Where(f => f.sharedMesh)
                .SelectMany(f => Shape(f.sharedMesh, silhouette).Select(p => weapon.InverseTransformPoint(f.transform.TransformPoint(p))));
            skinned = weapon.GetComponentsInChildren<SkinnedMeshRenderer>().Where(s => s.sharedMesh).ToArray();
            fixedPoints = Grid(found);
            Bake();
            foreach (var part in GetComponentsInChildren<Transform>(true)) part.gameObject.layer = weapon.gameObject.layer;
            // Apart from the weapon: skinned items hang under the skeleton, whose odd scale would shrink the particles to nothing.
            foreach (var system in GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = system.main;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.scalingMode = ParticleSystemScalingMode.Local;
            }
            clouds.Play(true);
            logAt = Time.time + 2;
            Utility.Log.LogInfo("Weapon glow on " + weapon.name + ": " + points.Length + " points, strength " + strength + ", silhouette " + (silhouette != null) + ", weapon scale " + weapon.lossyScale);
        }
        void Update()
        {
            if (!held) { Destroy(gameObject); return; }
            if (logAt > 0 && Time.time >= logAt) { logAt = 0; Utility.Log.LogInfo("Weapon glow live: " + clouds.particleCount + " clouds, " + sparks.particleCount + " sparks at " + transform.position + ", weapon at " + held.position + ", material " + GetComponent<ParticleSystemRenderer>().sharedMaterial); }
            if (skinned.Length > 0 && Time.time >= nextBake) Bake();
            if (points == null || points.Length == 0) return;
            cloudsDue += Time.deltaTime * Mathf.Min(points.Length * 2, 300);
            sparksDue += Time.deltaTime * 14 * strength;
            var color = new Color(1f, .6f, .18f, .03f + .1f * strength);
            for (; cloudsDue >= 1; cloudsDue--)
                clouds.Emit(new ParticleSystem.EmitParams { position = held.TransformPoint(points[Random.Range(0, points.Length)]), startSize = .12f + .1f * strength, startColor = color, applyShapeToPosition = false }, 1);
            for (; sparksDue >= 1; sparksDue--)
                sparks.Emit(new ParticleSystem.EmitParams { position = held.TransformPoint(points[Random.Range(0, points.Length)]), startSize = .05f, startColor = new Color(1f, .7f, .25f, .9f), applyShapeToPosition = false }, 1);
        }
        // A skinned model (most dual weapons) follows the hands: its pose is copied ten times a second. Works on protected meshes too.
        void Bake()
        {
            nextBake = Time.time + .1f;
            if (skinned.Length == 0) { points = fixedPoints; return; }
            if (!baked) baked = new Mesh();
            var all = new List<Vector3>(fixedPoints);
            foreach (var part in skinned)
            {
                if (!part) continue;
                part.BakeMesh(baked);
                var matrix = held.worldToLocalMatrix * part.transform.localToWorldMatrix;
                foreach (var point in baked.vertices) all.Add(matrix.MultiplyPoint3x4(point));
            }
            points = Grid(all);
        }
        static Vector3[] Grid(IEnumerable<Vector3> all) => all.GroupBy(p => new Vector3Int(Mathf.RoundToInt(p.x / Cell), Mathf.RoundToInt(p.y / Cell), Mathf.RoundToInt(p.z / Cell))).Select(g => g.First()).ToArray();
        void OnDestroy() { if (baked) Destroy(baked); }
        static Vector3[] Shape(Mesh mesh, Material silhouette)
        {
            if (Shapes.TryGetValue(mesh, out var shape)) return shape;
            return Shapes[mesh] = mesh.isReadable ? mesh.vertices : silhouette ? Silhouette(mesh, silhouette) : new Vector3[0];
        }
        // A protected mesh cannot be read: it is drawn once in white, flat side to an orthographic camera, and each lit pixel gives a point.
        static Vector3[] Silhouette(Mesh mesh, Material material)
        {
            const int Size = 64, Layer = 31;
            var bounds = mesh.bounds;
            int thin = bounds.size.x <= bounds.size.y && bounds.size.x <= bounds.size.z ? 0 : bounds.size.y <= bounds.size.z ? 1 : 2;
            var axis = thin == 0 ? Vector3.right : thin == 1 ? Vector3.up : Vector3.forward;
            var model = new GameObject("Glow silhouette", typeof(MeshFilter), typeof(MeshRenderer)) { layer = Layer };
            model.transform.position = new Vector3(0, -2000, 0);
            model.GetComponent<MeshFilter>().sharedMesh = mesh;
            model.GetComponent<MeshRenderer>().sharedMaterial = material;
            var camera = new GameObject("Glow silhouette camera").AddComponent<Camera>();
            camera.enabled = false; camera.orthographic = true; camera.cullingMask = 1 << Layer;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black; camera.nearClipPlane = .01f; camera.farClipPlane = 100;
            camera.orthographicSize = Mathf.Max(bounds.extents.x, bounds.extents.y, bounds.extents.z) * 1.05f;
            camera.transform.position = model.transform.TransformPoint(bounds.center) - axis * 10;
            camera.transform.rotation = Quaternion.LookRotation(axis, thin == 1 ? Vector3.forward : Vector3.up);
            var target = RenderTexture.GetTemporary(Size, Size, 16);
            var image = new Texture2D(Size, Size, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            camera.targetTexture = target; camera.Render();
            RenderTexture.active = target; image.ReadPixels(new Rect(0, 0, Size, Size), 0, 0); RenderTexture.active = previous;
            var found = new List<Vector3>();
            for (int x = 0; x < Size; x++)
                for (int y = 0; y < Size; y++)
                    if (image.GetPixel(x, y).r > .5f)
                        found.Add(model.transform.InverseTransformPoint(camera.ViewportToWorldPoint(new Vector3((x + .5f) / Size, (y + .5f) / Size, 10))));
            camera.targetTexture = null;
            RenderTexture.ReleaseTemporary(target);
            Object.Destroy(image); Object.Destroy(camera.gameObject); Object.Destroy(model);
            return found.ToArray();
        }
    }
}
