using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace OverhaulAnimationPreview
{
    // Applies an FbxClip to a character's bones, matched by name. Each bone gets its own rest
    // pose plus the clip's offset from the file's rest pose. The model's top node is not a bone:
    // its travel is returned as root motion.
    public sealed class PoseMapper
    {
        private sealed class Target
        {
            public FbxClip.Bone Source;
            public Transform Bone;
            public Quaternion RestRotation;
            public Vector3 RestPosition;
        }

        private readonly FbxClip clip;
        private readonly List<Target> targets = new List<Target>();
        private readonly float scale;
        public float Scale => scale; // game units (m) per file unit
        public int Matched => targets.Count;
        public IEnumerable<string> Missing { get; }

        // rest: the character's rest pose per bone name (local rotation, local position).
        public PoseMapper(FbxClip clip, Transform skeletonRoot, IDictionary<string, (Quaternion rotation, Vector3 position)> rest)
        {
            this.clip = clip;
            var bones = skeletonRoot.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
            foreach (var source in clip.Bones)
                if (source != clip.MotionRoot && bones.TryGetValue(source.Name, out var bone) && rest.TryGetValue(source.Name, out var pose))
                    targets.Add(new Target { Source = source, Bone = bone, RestRotation = pose.rotation, RestPosition = pose.position });
            Missing = clip.Bones.Where(b => b != clip.MotionRoot).Select(b => b.Name).Where(n => !bones.ContainsKey(n)).ToList();
            // File units vs game units, from the rest offsets of the matched bones.
            var ratios = targets.Where(t => t.Source.RestPosition.sqrMagnitude > 1e-6f && t.RestPosition.sqrMagnitude > 1e-6f)
                .Select(t => t.RestPosition.magnitude / t.Source.RestPosition.magnitude).OrderBy(r => r).ToList();
            scale = ratios.Count > 0 ? ratios[ratios.Count / 2] : 1f;
        }

        // Poses the bones at this time and returns the root motion offset from the clip's start,
        // in the character's local space (game units).
        public Vector3 Apply(float seconds)
        {
            clip.Sample(seconds);
            foreach (var t in targets)
            {
                t.Bone.localRotation = t.RestRotation * (Quaternion.Inverse(t.Source.RestRotation) * t.Source.Rotation);
                t.Bone.localPosition = t.RestPosition + (t.Source.Position - t.Source.RestPosition) * scale;
            }
            var root = clip.MotionRoot;
            return root == null ? Vector3.zero : (root.Position - root.RestPosition) * scale;
        }
    }
}
