using System;
using System.IO;
using UnityEngine;

namespace Overhaul
{
    // Retarget on the player's own avatar. Never transfer root/bone translations.
    internal sealed class DashAnimationPose : IDisposable
    {
        private static AssetBundle bundle;
        private static RuntimeAnimatorController controller;
        private static bool loadAttempted;
        private readonly Animator target;
        private readonly Animator sample;
        private readonly GameObject root;
        private readonly Transform[] destination = new Transform[(int)HumanBodyBones.LastBone];
        private readonly Transform[] source = new Transform[(int)HumanBodyBones.LastBone];
        internal Animator Target => target;

        internal static bool Load()
        {
            if (loadAttempted) return controller != null;
            loadAttempted = true;
            bundle = Utility.EmbeddedAssets.LoadBundle("Overhaul.Assets.overhaul_dash");
            controller = bundle.LoadAsset<RuntimeAnimatorController>("Assets/OverhaulDash/Dash.controller");
            if (!controller) throw new InvalidOperationException("Dash controller missing");
            return true;
        }

        internal DashAnimationPose(Animator animator)
        {
            if (!animator || !animator.isHuman || !animator.avatar || !animator.avatar.isValid)
                throw new InvalidOperationException("Dash requires a valid Humanoid avatar");
            if (!Load()) throw new InvalidOperationException("Dash animations unavailable");
            target = animator;
            root = new GameObject("Overhaul dash pose sampler");
            root.hideFlags = HideFlags.HideAndDontSave;
            root.SetActive(false);
            CopyChildren(target.transform, root.transform);
            root.transform.SetPositionAndRotation(target.transform.position, target.transform.rotation);
            root.transform.localScale = target.transform.lossyScale;
            sample = root.AddComponent<Animator>();
            sample.avatar = target.avatar;
            sample.runtimeAnimatorController = controller;
            sample.fireEvents = false;
            sample.applyRootMotion = false;
            sample.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            root.SetActive(true);
            sample.Rebind();
            sample.enabled = false;
            for (int i = 0; i < destination.Length; i++)
            {
                destination[i] = target.GetBoneTransform((HumanBodyBones)i);
                source[i] = sample.GetBoneTransform((HumanBodyBones)i);
            }
        }

        internal void Apply(string step, string direction, float progress, float weight)
        {
            root.transform.SetPositionAndRotation(target.transform.position, target.transform.rotation);
            root.transform.localScale = target.transform.lossyScale;
            sample.Play("Base Layer.Dodge_" + step + "_" + direction, 0, Mathf.Clamp01(progress));
            sample.Update(0f);
            for (int i = 0; i < destination.Length; i++)
                if (destination[i] && source[i])
                    destination[i].localRotation = Quaternion.Slerp(destination[i].localRotation,
                        source[i].localRotation, Mathf.Clamp01(weight));
        }

        private static void CopyChildren(Transform from, Transform to)
        {
            for (int i = 0; i < from.childCount; i++)
            {
                Transform child = from.GetChild(i);
                var copy = new GameObject(child.name).transform;
                copy.SetParent(to, false);
                copy.localPosition = child.localPosition;
                copy.localRotation = child.localRotation;
                copy.localScale = child.localScale;
                CopyChildren(child, copy);
            }
        }

        public void Dispose()
        {
            if (!root) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(root);
            else UnityEngine.Object.DestroyImmediate(root);
        }

        internal static void Unload()
        {
            if (bundle) bundle.Unload(true);
            controller = null;
            bundle = null;
            loadAttempted = false;
        }
    }
}
