using System;
using UnityEngine;

namespace Overhaul
{
    // Samples the game's real Movement blend tree on a transform-only humanoid.
    // No meshes, colliders, gameplay components or animation events are duplicated.
    internal sealed class AttackLocomotionPose : IDisposable
    {
        private readonly Animator target;
        private readonly Animator sample;
        private readonly GameObject sampleRoot;
        private readonly Transform[] targetLegs;
        private readonly Transform[] sampleLegs;
        private readonly Transform targetHips;
        private readonly Transform sampleHips;
        private readonly Transform targetSpine;
        private Vector2 movementSpeed;
        private float gaitPhase;
        private readonly bool crouched;
        private readonly int movementState;
        private static readonly int Movement = Animator.StringToHash("Base Layer.Movement");
        private static readonly int Forward = Animator.StringToHash("forward_speed");
        private static readonly int Sideways = Animator.StringToHash("sideway_speed");
        private static readonly int Posture = Animator.StringToHash("statef");
        private static readonly int PostureIndex = Animator.StringToHash("statei");
        private static readonly HumanBodyBones[] LegBones =
        {
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg,
            HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes,
            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg,
            HumanBodyBones.RightFoot, HumanBodyBones.RightToes
        };

        internal Animator Target => target;

        internal AttackLocomotionPose(Animator animator, bool crouched = false)
        {
            this.crouched=crouched;
            movementState=crouched?Animator.StringToHash("Base Layer.Crouch"):Movement;
            if (!animator.isHuman || !animator.avatar || !animator.avatar.isValid)
                throw new InvalidOperationException("Attack locomotion requires a valid Humanoid avatar.");
            target = animator;
            sampleRoot = new GameObject("Overhaul attack locomotion sampler");
            sampleRoot.hideFlags = HideFlags.HideAndDontSave;
            sampleRoot.SetActive(false);
            CopyChildren(target.transform, sampleRoot.transform);
            sampleRoot.transform.SetPositionAndRotation(target.transform.position, target.transform.rotation);
            sampleRoot.transform.localScale = target.transform.lossyScale;
            sample = sampleRoot.AddComponent<Animator>();
            sample.avatar = target.avatar;
            sample.runtimeAnimatorController = target.runtimeAnimatorController;
            sample.fireEvents = false;
            sample.applyRootMotion = false;
            sample.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            sampleRoot.SetActive(true);
            sample.Rebind();
            sample.enabled = false; // Evaluated manually only when needed.
            if (!sample.HasState(0, movementState))
                throw new InvalidOperationException("Player controller has no Base Layer.Movement state.");
            sample.Play(movementState, 0, 0f);
            sample.Update(0f);
            for (int layer = 1; layer < sample.layerCount; ++layer)
                sample.SetLayerWeight(layer, 0f);
            targetLegs = new Transform[LegBones.Length];
            sampleLegs = new Transform[LegBones.Length];
            targetHips = target.GetBoneTransform(HumanBodyBones.Hips);
            sampleHips = sample.GetBoneTransform(HumanBodyBones.Hips);
            targetSpine = target.GetBoneTransform(HumanBodyBones.Spine);
            for (int i = 0; i < LegBones.Length; ++i)
            {
                targetLegs[i] = target.GetBoneTransform(LegBones[i]);
                sampleLegs[i] = sample.GetBoneTransform(LegBones[i]);
            }
        }

        internal void Apply(float forwardSpeed, float sidewaySpeed, float deltaTime, float weight)
        {
            sampleRoot.transform.SetPositionAndRotation(target.transform.position, target.transform.rotation);
            sampleRoot.transform.localScale = target.transform.lossyScale;
            sample.SetFloat(Posture, target.GetFloat(Posture));
            sample.SetInteger(PostureIndex, target.GetInteger(PostureIndex));
            movementSpeed = Vector2.Lerp(movementSpeed, new Vector2(forwardSpeed, sidewaySpeed),
                1f - Mathf.Exp(-Mathf.Max(0f, deltaTime) / 0.08f));
            sample.SetFloat(Forward, movementSpeed.x);
            sample.SetFloat(Sideways, movementSpeed.y);
            sample.SetBool("onGround", true);
            sample.SetBool("inWater", false);
            if(crouched){sample.SetBool("crouching",true);sample.SetBool("bow_aim",false);}
            // Explicit sampling: do not depend on time progression of a disabled Animator.
            // This also prevents transitions from resetting the gait to its first frame.
            AnimatorStateInfo state = sample.GetCurrentAnimatorStateInfo(0);
            float cycleDuration = state.fullPathHash == movementState && state.length > 0.01f
                ? state.length : 1f;
            gaitPhase = Mathf.Repeat(gaitPhase + Mathf.Max(0f, deltaTime) / cycleDuration, 1f);
            sample.Play(movementState, 0, gaitPhase);
            sample.speed = 1f; // Attack speed must not accelerate the walking cycle.
            sample.Update(0f);
            weight = Mathf.Clamp01(weight);
            // Leg rotations are relative to the pelvis: keeping the attack pelvis twists
            // the sampled gait. Transfer it too, preserving the attacking torso in world space.
            Vector3 spinePosition = targetSpine ? targetSpine.position : Vector3.zero;
            Quaternion spineRotation = targetSpine ? targetSpine.rotation : Quaternion.identity;
            if (targetHips && sampleHips)
            {
                if(crouched)
                {
                    targetHips.position=Vector3.Lerp(targetHips.position,target.transform.TransformPoint(sample.transform.InverseTransformPoint(sampleHips.position)),weight);
                    targetHips.rotation=Quaternion.Slerp(targetHips.rotation,target.transform.rotation*Quaternion.Inverse(sample.transform.rotation)*sampleHips.rotation,weight);
                }
                else
                {
                    targetHips.localPosition = Vector3.Lerp(targetHips.localPosition, sampleHips.localPosition, weight);
                    targetHips.localRotation = Quaternion.Slerp(targetHips.localRotation, sampleHips.localRotation, weight);
                }
            }
            for (int i = 0; i < targetLegs.Length; ++i)
                if (targetLegs[i] && sampleLegs[i])
                    targetLegs[i].localRotation = Quaternion.Slerp(
                        targetLegs[i].localRotation, sampleLegs[i].localRotation, weight);
            if (targetSpine)
            {
                if(crouched)targetSpine.rotation=spineRotation; // Lower the aiming torso with the pelvis, preserving its aim.
                else targetSpine.SetPositionAndRotation(spinePosition, spineRotation);
            }
        }

        private static void CopyChildren(Transform source, Transform destination)
        {
            for (int i = 0; i < source.childCount; ++i)
            {
                Transform child = source.GetChild(i);
                Transform copy = new GameObject(child.name).transform;
                copy.SetParent(destination, false);
                copy.localPosition = child.localPosition;
                copy.localRotation = child.localRotation;
                copy.localScale = child.localScale;
                CopyChildren(child, copy);
            }
        }

        public void Dispose()
        {
            if (sampleRoot)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(sampleRoot);
                else UnityEngine.Object.DestroyImmediate(sampleRoot);
            }
        }
    }
}
