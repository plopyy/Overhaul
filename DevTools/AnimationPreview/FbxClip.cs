using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Assimp;
using UnityEngine;
using AQuaternion = Assimp.Quaternion;
using Matrix4x4 = Assimp.Matrix4x4;
using Animation = Assimp.Animation;
using UQuaternion = UnityEngine.Quaternion;

namespace OverhaulAnimationPreview
{
    // An FBX animation made on the Valheim player skeleton, read at runtime with Assimp.
    // Each bone is sampled as an offset from its rest pose in the file, then applied to the same
    // bone's rest pose in game: equal skeletons give the same motion whatever the file's axes.
    public sealed class FbxClip
    {
        public sealed class Bone
        {
            public string Name;
            public UQuaternion RestRotation, Rotation; // file space, converted to Unity handedness
            public Vector3 RestPosition, Position;
        }

        public string Name { get; private set; }
        public float Duration { get; private set; }  // seconds
        public int Frames { get; private set; }
        public IReadOnlyList<Bone> Bones => bones;
        // The model's top node: its travel is the clip's root motion, not part of the skeleton pose.
        public Bone MotionRoot { get; private set; }

        private const string Helper = "_$AssimpFbx$_";
        private readonly List<Bone> bones = new List<Bone>();
        private readonly Dictionary<Node, NodeAnimationChannel> channels = new Dictionary<Node, NodeAnimationChannel>();
        private readonly Dictionary<Bone, List<Node>> chains = new Dictionary<Bone, List<Node>>(); // helper nodes then the bone node
        private double ticksPerSecond;

        public static FbxClip Load(string path)
        {
            using (var context = new AssimpContext())
            {
                // Keep FBX pivots as separate helper nodes: their animated channels target them.
                context.SetConfig(new Assimp.Configs.FBXPreservePivotsConfig(true));
                var scene = context.ImportFile(path, PostProcessSteps.None);
                if (scene == null || !scene.HasAnimations) throw new InvalidDataException("No animation in " + Path.GetFileName(path));
                return new FbxClip(scene, scene.Animations[0], Path.GetFileNameWithoutExtension(path));
            }
        }

        private FbxClip(Scene scene, Animation animation, string fileName)
        {
            Name = string.IsNullOrEmpty(animation.Name) ? fileName : animation.Name;
            ticksPerSecond = animation.TicksPerSecond > 0 ? animation.TicksPerSecond : 25;
            Duration = (float)(animation.DurationInTicks / ticksPerSecond);
            Frames = (int)Math.Round(animation.DurationInTicks) + 1;
            var byName = new Dictionary<string, Node>();
            Index(scene.RootNode, byName);
            foreach (var channel in animation.NodeAnimationChannels)
                if (byName.TryGetValue(channel.NodeName, out var node)) channels[node] = channel;
            Collect(scene.RootNode, new List<Node>());
            foreach (var bone in bones) { Sample(bone, -1); bone.RestRotation = bone.Rotation; bone.RestPosition = bone.Position; }
            if (byName.TryGetValue("Hips", out var hips) && MotionRoot != null && byName.TryGetValue(MotionRoot.Name, out var model))
                HipsHeight = Mathf.Max(1e-4f, ModelHeight(hips, model));
            Travel = MeasureTravel();
        }

        // Largest horizontal distance the model's top node covers from the first frame (file units).
        public float Travel { get; private set; }
        // Rest height of the hips in the model's space (file units, through every parent's scale):
        // makes the root motion test independent of the file's unit (m, cm...).
        public float HipsHeight { get; private set; } = 1f;
        public bool HasRootMotion => Travel > 0.05f * HipsHeight;

        private static float ModelHeight(Node node, Node modelRoot)
        {
            var matrix = Matrix4x4.Identity;
            for (var n = node; n != null && n != modelRoot; n = n.Parent) matrix = Multiply(n.Transform, matrix);
            var position = new Vector3(matrix.A4, matrix.B4, matrix.C4);
            return position.magnitude;
        }

        private float MeasureTravel()
        {
            if (MotionRoot == null) return 0;
            Sample(MotionRoot, 0);
            var start = MotionRoot.Position;
            float travel = 0;
            for (int frame = 1; frame < Frames; frame++)
            {
                Sample(MotionRoot, (float)(frame / ticksPerSecond));
                var step = MotionRoot.Position - start; step.y = 0;
                travel = Mathf.Max(travel, step.magnitude);
            }
            return travel;
        }

        private static void Index(Node node, Dictionary<string, Node> byName)
        {
            byName[node.Name] = node;
            foreach (var child in node.Children) Index(child, byName);
        }

        // Real bones are the nodes without the Assimp pivot suffix; the helpers above a bone
        // and the bone node itself make up its local transform relative to its real parent.
        private void Collect(Node node, List<Node> pending)
        {
            var chain = new List<Node>(pending) { node };
            if (node.Name.Contains(Helper))
            {
                foreach (var child in node.Children) Collect(child, chain);
                return;
            }
            if (node.Parent != null && chain.Any(channels.ContainsKey))
            {
                var bone = new Bone { Name = node.Name };
                bones.Add(bone); chains[bone] = chain;
                if (chain[0].Parent != null && chain[0].Parent.Parent == null) MotionRoot = bone;
            }
            foreach (var child in node.Children) Collect(child, new List<Node>());
        }

        // Sets every bone's Rotation/Position for this time (seconds); a negative time gives the rest pose.
        public void Sample(float seconds)
        {
            foreach (var bone in bones) Sample(bone, seconds);
        }

        private void Sample(Bone bone, float seconds)
        {
            var matrix = Matrix4x4.Identity;
            foreach (var node in chains[bone])
                matrix = Multiply(matrix, seconds >= 0 && channels.TryGetValue(node, out var channel) ? Evaluate(channel, node, seconds * ticksPerSecond) : node.Transform);
            matrix.Decompose(out _, out AQuaternion rotation, out Vector3D position);
            // FBX is right-handed, Unity left-handed: Unity's importer mirrors the X axis.
            bone.Rotation = new UQuaternion(rotation.X, -rotation.Y, -rotation.Z, rotation.W);
            bone.Position = new Vector3(-position.X, position.Y, position.Z);
        }

        private static Matrix4x4 Evaluate(NodeAnimationChannel channel, Node node, double ticks)
        {
            node.Transform.Decompose(out Vector3D restScale, out AQuaternion restRotation, out Vector3D restPosition);
            var position = channel.HasPositionKeys ? Interpolate(channel.PositionKeys, ticks) : restPosition;
            var rotation = channel.HasRotationKeys ? Interpolate(channel.RotationKeys, ticks) : restRotation;
            var scale = channel.HasScalingKeys ? Interpolate(channel.ScalingKeys, ticks) : restScale;
            return Compose(position, rotation, scale);
        }

        private static Vector3D Interpolate(List<VectorKey> keys, double ticks)
        {
            if (keys.Count == 1 || ticks <= keys[0].Time) return keys[0].Value;
            for (int i = 1; i < keys.Count; i++)
            {
                if (ticks > keys[i].Time) continue;
                var a = keys[i - 1]; var b = keys[i];
                float t = (float)((ticks - a.Time) / Math.Max(1e-9, b.Time - a.Time));
                return a.Value + (b.Value - a.Value) * t;
            }
            return keys[keys.Count - 1].Value;
        }

        private static AQuaternion Interpolate(List<QuaternionKey> keys, double ticks)
        {
            if (keys.Count == 1 || ticks <= keys[0].Time) return keys[0].Value;
            for (int i = 1; i < keys.Count; i++)
            {
                if (ticks > keys[i].Time) continue;
                var a = keys[i - 1]; var b = keys[i];
                float t = (float)((ticks - a.Time) / Math.Max(1e-9, b.Time - a.Time));
                return AQuaternion.Slerp(a.Value, b.Value, t);
            }
            return keys[keys.Count - 1].Value;
        }

        private static Matrix4x4 Compose(Vector3D position, AQuaternion rotation, Vector3D scale)
        {
            var r = rotation.GetMatrix();
            return new Matrix4x4(
                r.A1 * scale.X, r.A2 * scale.Y, r.A3 * scale.Z, position.X,
                r.B1 * scale.X, r.B2 * scale.Y, r.B3 * scale.Z, position.Y,
                r.C1 * scale.X, r.C2 * scale.Y, r.C3 * scale.Z, position.Z,
                0, 0, 0, 1);
        }

        // Plain row-by-column product (column vectors): result = a · b, i.e. b applied first.
        private static Matrix4x4 Multiply(Matrix4x4 a, Matrix4x4 b)
        {
            float[] x = { a.A1, a.A2, a.A3, a.A4, a.B1, a.B2, a.B3, a.B4, a.C1, a.C2, a.C3, a.C4, a.D1, a.D2, a.D3, a.D4 };
            float[] y = { b.A1, b.A2, b.A3, b.A4, b.B1, b.B2, b.B3, b.B4, b.C1, b.C2, b.C3, b.C4, b.D1, b.D2, b.D3, b.D4 };
            var r = new float[16];
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    for (int k = 0; k < 4; k++) r[i * 4 + j] += x[i * 4 + k] * y[k * 4 + j];
            return new Matrix4x4(r[0], r[1], r[2], r[3], r[4], r[5], r[6], r[7], r[8], r[9], r[10], r[11], r[12], r[13], r[14], r[15]);
        }
    }
}
