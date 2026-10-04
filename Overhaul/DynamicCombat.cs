using System;
using System.Collections.Generic;
using HarmonyLib;
using Overhaul.Utility;
using SoftReferenceableAssets;
using TMPro;
using UnityEngine;

namespace Overhaul
{
    internal static class DynamicCombat
    {
        private const float AttackLungeDistance = 4f;
        private const float DodgeAttackWindow = 0.3f;
        private const float DodgeAttackAfterWindow = 0.7f;
        private static Player lastDodgingPlayer;
        private static float dodgeEndedAt = float.NegativeInfinity;
        private static Player dodgeClickPlayer;
        private static float dodgeClickAt = float.NegativeInfinity;
        private static bool dodgeClickSecondary;

        [HarmonyPatch(typeof(Player), nameof(Player.SetControls))]
        private static class DodgeClickCapturePatch
        {
            private static void Postfix(Player __instance, bool attack, bool secondaryAttack)
            {
                if (__instance != Player.m_localPlayer || (!attack && !secondaryAttack)) return;
                dodgeClickPlayer = __instance;
                dodgeClickAt = Time.time;
                dodgeClickSecondary = secondaryAttack;
            }
        }

        [HarmonyPatch(typeof(Player), "UpdateDodge")]
        private static class DodgeEndWindowPatch
        {
            private static void Prefix(Player __instance, out bool __state) => __state = __instance.InDodge();
            private static void Postfix(Player __instance, bool __state)
            {
                if (__instance != Player.m_localPlayer) return;
                if (__instance.InDodge()) dodgeEndedAt = float.NegativeInfinity;
                else if (__state)
                {
                    lastDodgingPlayer = __instance;
                    dodgeEndedAt = Time.time;
                }
            }
        }
        private static Attack dodgeFollowup;
        private static Attack empoweredAttack;
        private static Player pendingAuraPlayer;
        private static ItemDrop.ItemData pendingAuraWeapon;
        private static Attack pendingAuraPreviousAttack;
        private static bool pendingAuraSecondary;
        private static float pendingAuraUntil;
        private static GameObject dodgeAura;
        private static readonly List<Material> dodgeAuraMaterials = new List<Material>();
        private static Attack resolvingAttack;

        private static Color AuraRed(Color color, float opacity = 1f)
        {
            float intensity = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
            return new Color(intensity, intensity * 0.015f, intensity * 0.015f, color.a * opacity);
        }

        private static Gradient AuraRedGradient(Gradient source, float opacity = 1f)
        {
            var result = new Gradient();
            var keys = source.colorKeys;
            for (int i = 0; i < keys.Length; ++i) keys[i].color = AuraRed(keys[i].color);
            var alphaKeys = source.alphaKeys;
            for (int i = 0; i < alphaKeys.Length; ++i) alphaKeys[i].alpha *= opacity;
            result.SetKeys(keys, alphaKeys);
            result.mode = source.mode;
            return result;
        }

        private static ParticleSystem.MinMaxGradient AuraRedColors(ParticleSystem.MinMaxGradient source, float opacity = 1f)
        {
            switch (source.mode)
            {
                case ParticleSystemGradientMode.Color:
                    return new ParticleSystem.MinMaxGradient(AuraRed(source.color, opacity));
                case ParticleSystemGradientMode.TwoColors:
                    return new ParticleSystem.MinMaxGradient(AuraRed(source.colorMin, opacity), AuraRed(source.colorMax, opacity));
                case ParticleSystemGradientMode.TwoGradients:
                    return new ParticleSystem.MinMaxGradient(AuraRedGradient(source.gradientMin, opacity), AuraRedGradient(source.gradientMax, opacity));
                default:
                    var result = new ParticleSystem.MinMaxGradient(AuraRedGradient(source.gradient, opacity));
                    result.mode = source.mode;
                    return result;
            }
        }

        private const string AuraRpc = "Overhaul_DodgeAura";
        private static Player auraOwner;
        private static float auraRefreshAt;

        private static void SendAura(bool active)
        {
            if (auraOwner && auraOwner.m_nview && auraOwner.m_nview.IsValid() && auraOwner.m_nview.IsOwner())
                auraOwner.m_nview.InvokeRPC(ZNetView.Everybody, AuraRpc, active);
            auraRefreshAt = Time.time + 0.5f;
        }

        private static void StartDodgeAura(Player player)
        {
            StopDodgeAura();
            auraOwner = player;
            dodgeAura = CreateDodgeAura(player, dodgeAuraMaterials);
            SendAura(true);
        }

        private static GameObject CreateDodgeAura(Player player, List<Material> dodgeAuraMaterials)
        {
            var nest = FindLoadedPrefab("Spawner_GreydwarfNest");
            var visual = nest ? nest.transform.Find("particles") : null;
            if (!visual)
            {
                Debug.LogWarning("[Overhaul] Greydwarf nest aura prefab unavailable");
                return null;
            }
            // Clone only the visual group: no spawner, collider, network object or ambient sound.
            var dodgeAura = UnityEngine.Object.Instantiate(visual.gameObject, player.transform, false);
            dodgeAura.name = "Overhaul dodge attack red aura";
            dodgeAura.transform.localPosition = Vector3.up * 0.5f;
            dodgeAura.transform.localRotation = Quaternion.identity;
            dodgeAura.transform.localScale = Vector3.one * 0.55f;
            dodgeAura.SetActive(true);
            foreach (var renderer in dodgeAura.GetComponentsInChildren<ParticleSystemRenderer>(true))
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; ++i)
                {
                    if (!materials[i]) continue;
                    var material = new Material(materials[i]);
                    dodgeAuraMaterials.Add(material);
                    foreach (string property in new[] { "_Color", "_TintColor", "_BaseColor", "_EmissionColor" })
                        if (material.HasProperty(property)) material.SetColor(property, AuraRed(material.GetColor(property)));
                    materials[i] = material;
                }
                renderer.sharedMaterials = materials;
            }
            foreach (var light in dodgeAura.GetComponentsInChildren<Light>(true)) light.color = Color.red;
            foreach (var particles in dodgeAura.GetComponentsInChildren<ParticleSystem>(true))
            {
                particles.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = particles.main;
                main.startColor = AuraRedColors(main.startColor, 0.5f);
                main.loop = true;
                main.startDelay = 0f;
                main.simulationSpace = ParticleSystemSimulationSpace.Local;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                var colors = particles.colorOverLifetime;
                if (colors.enabled) colors.color = AuraRedColors(colors.color);
                var speedColors = particles.colorBySpeed;
                if (speedColors.enabled) speedColors.color = AuraRedColors(speedColors.color);
                particles.Simulate(1f, false, true);
                particles.Play(false);
            }
            return dodgeAura;
        }

        internal static void StopDodgeAura()
        {
            SendAura(false);
            auraOwner = null;
            if (dodgeAura) UnityEngine.Object.Destroy(dodgeAura);
            foreach (var material in dodgeAuraMaterials)
                if (material) UnityEngine.Object.Destroy(material);
            dodgeAuraMaterials.Clear();
            dodgeAura = null;
        }
        internal static void UpdateDodgeAura()
        {
            if (empoweredAttack == null) return;
            var player = Player.m_localPlayer;
            if (!player || player.m_currentAttack != empoweredAttack || empoweredAttack.m_attackDone
                || player.IsDead() || player.IsStaggering() || player.InDodge())
            { empoweredAttack = null; StopDodgeAura(); }
            else if (Time.time >= auraRefreshAt) SendAura(true);
        }

        [HarmonyPatch]
        private static class DodgeCriticalAttackScope
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(Attack), "DoMeleeAttack");
                yield return AccessTools.Method(typeof(Attack), "DoAreaAttack");
            }
            private static void Prefix(Attack __instance, out Attack __state)
            { __state = resolvingAttack; resolvingAttack = __instance; }
            private static void Finalizer(Attack __state) { resolvingAttack = __state; }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class DodgeCriticalDamagePatch
        {
            private static void Prefix(Character __instance, HitData hit)
            {
                if (empoweredAttack == null || resolvingAttack != empoweredAttack
                    || hit.GetAttacker() != Player.m_localPlayer) return;
                if (!__instance.IsPlayer() && __instance.IsStaggering()) return;
                hit.ApplyModifier(2f);
                __instance.m_critHitEffects.Create(hit.m_point, Quaternion.identity, __instance.transform,
                    1f, -1, Player.m_localPlayer.GetZDOID());
            }
        }
        private static Animator dodgeFollowupAnimator;
        private static float dodgeBaseSpeed;
        private static float dodgeBoostedSpeed;
        private static float dodgeFollowupStarted;
        private const float DodgeAttackBlendDuration = 0.1f;
        private static Transform[] dodgeBlendBones;
        private static Quaternion[] dodgeBlendRotations;
        private static Vector3 dodgeBlendHipPosition;
        private static Attack dodgeBlendAttack;
        private static float dodgeBlendStarted;

        private static void CaptureDodgePose(Animator animator)
        {
            dodgeBlendAttack = null;
            if (!animator.isHuman) { dodgeBlendBones = null; return; }
            int count = (int)HumanBodyBones.LastBone;
            dodgeBlendBones = new Transform[count];
            dodgeBlendRotations = new Quaternion[count];
            for (int i = 0; i < count; i++)
            {
                var bone = animator.GetBoneTransform((HumanBodyBones)i);
                dodgeBlendBones[i] = bone;
                if (bone) dodgeBlendRotations[i] = bone.localRotation;
            }
            var hips = dodgeBlendBones[(int)HumanBodyBones.Hips];
            if (hips) dodgeBlendHipPosition = hips.localPosition;
        }

        internal static void UpdateDodgeAttackBlend()
        {
            if (dodgeBlendAttack == null || dodgeBlendBones == null) return;
            var player = Player.m_localPlayer;
            float elapsed = Time.time - dodgeBlendStarted;
            if (!player || player.m_currentAttack != dodgeBlendAttack || dodgeBlendAttack.m_attackDone
                || player.IsDead() || player.IsStaggering() || player.IsKnockedBack()
                || player.InDodge() || IsDashing(player)
                || elapsed >= DodgeAttackBlendDuration)
            {
                dodgeBlendAttack = null; dodgeBlendBones = null; return;
            }
            float weight = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / DodgeAttackBlendDuration));
            // Blend the captured roll pose into the freshly evaluated attack/locomotion pose.
            // Never modify the player root, scale, hitboxes or animation event timeline.
            for (int i = 0; i < dodgeBlendBones.Length; i++)
            {
                var bone = dodgeBlendBones[i];
                if (bone) bone.localRotation = Quaternion.Slerp(dodgeBlendRotations[i], bone.localRotation, weight);
            }
            var hips = dodgeBlendBones[(int)HumanBodyBones.Hips];
            if (hips) hips.localPosition = Vector3.Lerp(dodgeBlendHipPosition, hips.localPosition, weight);
        }

        internal static void AdjustDodgeAttackSpeed(Player player, ref float speed)
        {
            if (dodgeFollowup == null || player.m_currentAttack != dodgeFollowup) return;
            dodgeBaseSpeed = speed;
            speed *= 1.5f;
            dodgeBoostedSpeed = speed;
        }

        internal static void UpdateDodgeAttackSpeed()
        {
            if (dodgeFollowup == null) return;
            var player = Player.m_localPlayer;
            bool stop = !player || player.m_currentAttack != dodgeFollowup || dodgeFollowup.m_attackDone
                || player.IsDead() || player.IsStaggering() || player.InDodge();
            if (dodgeFollowupAnimator)
            {
                var state = dodgeFollowupAnimator.GetCurrentAnimatorStateInfo(0);
                if (dodgeFollowupAnimator.IsInTransition(0)) state = dodgeFollowupAnimator.GetNextAnimatorStateInfo(0);
                stop |= state.tagHash == Humanoid.s_animatorTagAttack
                    ? state.normalizedTime >= 0.25f : Time.time - dodgeFollowupStarted > 0.3f;
                if (stop && Mathf.Approximately(dodgeFollowupAnimator.speed, dodgeBoostedSpeed))
                    dodgeFollowupAnimator.speed = dodgeBaseSpeed;
            }
            if (stop) { dodgeFollowup = null; dodgeFollowupAnimator = null; }
        }

        [HarmonyPatch(typeof(Player), "PlayerAttackInput")]
        private static class DodgeAttackCancelPatch
        {
            private static bool Prefix(Player __instance, out bool __state)
            {
                TryDodgeAttack(__instance, out __state);
                if (__instance != Player.m_localPlayer || __state) return true;
                var animator = __instance.m_animator;
                int dodgeTag = Animator.StringToHash("dodge");
                bool rolling = __instance.InDodge() || animator.GetCurrentAnimatorStateInfo(0).tagHash == dodgeTag
                    || (animator.IsInTransition(0) && animator.GetNextAnimatorStateInfo(0).tagHash == dodgeTag);
                if (!rolling) return true;
                // A rejected roll click must not enter the native half-second attack queue.
                __instance.m_queuedAttackTimer = 0f;
                __instance.m_queuedSecondAttackTimer = 0f;
                __instance.m_attack = false;
                __instance.m_secondaryAttack = false;
                if (dodgeClickPlayer == __instance) dodgeClickAt = float.NegativeInfinity;
                return false;
            }

            private static void TryDodgeAttack(Player __instance, out bool __state)
            {
                __state = false;
                var player = __instance;
                bool captured = dodgeClickPlayer == player && Time.time - dodgeClickAt <= 0.05f;
                if (player != Player.m_localPlayer || (!captured && !player.m_attack && !player.m_secondaryAttack)
                    || !player.TakeInput() || player.InPlaceMode() || !player.CanMove()
                    || player.IsStaggering() || player.IsKnockedBack() || player.InMinorAction()) return;
                var weapon = player.GetCurrentWeapon();
                if (weapon == null || !weapon.IsWeapon()
                    || weapon.m_shared.m_skillType == Skills.SkillType.Pickaxes) return;
                bool secondary = captured ? dodgeClickSecondary : player.m_secondaryAttack;
                if (secondary ? !weapon.HaveSecondaryAttack() : !weapon.HavePrimaryAttack()) return;
                var type = (secondary ? weapon.m_shared.m_secondaryAttack : weapon.m_shared.m_attack).m_attackType;
                if (type != Attack.AttackType.Horizontal && type != Attack.AttackType.Vertical
                    && type != Attack.AttackType.Area) return;
                var animator = player.m_animator;
                var state = animator.GetCurrentAnimatorStateInfo(0);
                int dodgeTag = Animator.StringToHash("dodge");
                if (animator.IsInTransition(0))
                {
                    var next = animator.GetNextAnimatorStateInfo(0);
                    if (next.tagHash == dodgeTag) state = next;
                }
                bool rolling = player.InDodge() || state.tagHash == dodgeTag;
                if (!rolling && (lastDodgingPlayer != player || Time.time - dodgeEndedAt > DodgeAttackAfterWindow
                    || player.InAttack())) return;
                if (rolling)
                {
                if (state.tagHash != dodgeTag) return;
                float speed = Mathf.Abs(animator.speed * state.speed * state.speedMultiplier);
                if (speed <= 0f || state.length <= 0f) return;
                float remaining = Mathf.Max(0f, 1f - state.normalizedTime) * state.length / speed;
                if (remaining > DodgeAttackWindow) return;
                }

                CaptureDodgePose(animator);
                pendingAuraPlayer = player;
                pendingAuraWeapon = weapon;
                pendingAuraPreviousAttack = player.m_currentAttack;
                pendingAuraSecondary = secondary;
                pendingAuraUntil = Time.time + 0.5f;
                dodgeClickAt = float.NegativeInfinity;
                player.m_attack = !secondary;
                player.m_secondaryAttack = secondary;
                dodgeEndedAt = float.NegativeInfinity;
                if (rolling)
                {
                player.m_queuedDodgeTimer = 0f;
                player.m_inDodge = false;
                player.m_dodgeInvincible = false;
                player.m_dodgeInvincibleCached = false;
                player.m_nview.GetZDO().Set(ZDOVars.s_dodgeinv, false);
                animator.ResetTrigger("dodge");
                player.m_zanim.SetBool("dodge", false);
                PlayMovementState(animator);
                animator.Update(0f);
                }
                __state = true;
                // Native PlayerAttackInput now handles the click, costs and attack normally.
            }
            private static void Postfix(Player __instance, bool __state)
            {
                if (__instance != pendingAuraPlayer) return;
                if (Time.time > pendingAuraUntil || __instance.IsDead() || __instance.IsStaggering()
                    || __instance.GetCurrentWeapon() != pendingAuraWeapon)
                {
                    pendingAuraPlayer = null;
                    dodgeBlendBones = null;
                    return;
                }
                var startedAttack = __instance.m_currentAttack;
                if (startedAttack == null || startedAttack == pendingAuraPreviousAttack || startedAttack.m_attackDone) return;
                pendingAuraPlayer = null;
                if (__instance.m_currentAttackIsSecondary != pendingAuraSecondary) { dodgeBlendBones = null; return; }
                dodgeFollowup = __instance.m_currentAttack;
                dodgeBlendAttack = dodgeFollowup;
                dodgeBlendStarted = Time.time;
                dodgeFollowupAnimator = __instance.m_animator;
                dodgeFollowupStarted = Time.time;
                dodgeBaseSpeed = dodgeFollowupAnimator.speed;
                dodgeBoostedSpeed = dodgeBaseSpeed * 1.5f;
                dodgeFollowupAnimator.speed = dodgeBoostedSpeed;
                empoweredAttack = dodgeFollowup;
                StartDodgeAura(__instance);
            }
        }
        private static Player lungingPlayer;
        private static Attack lungingAttack;
        private static Vector3 lungeDirection;
        private static float lungeDistance;
        private static float lungeProgress;
        private static int lungeStateHash;
        private static bool lungeAnimationStarted;
        private static int lungePreviousStateHash;
        private static float lungePreviousStateTime;
        private static AttackLocomotionPose attackLocomotion;
        private static bool attackLocomotionFailed;
        private static Vector3 lastLungeVisualPosition;
        private static float lungePoseWeight;

        internal static void ReleaseAttackLocomotion()
        {
            attackLocomotion?.Dispose();
            attackLocomotion = null;
            lungePoseWeight = 0f;
        }

        internal static void UpdateAttackLocomotionPose()
        {
            Player player = Player.m_localPlayer;
            if (attackLocomotion != null && (player == null || attackLocomotion.Target != player.m_animator))
            {
                ReleaseAttackLocomotion();
                attackLocomotionFailed = false;
            }
            if (player == null || player != lungingPlayer || !lungeAnimationStarted
                || player.m_currentAttack != lungingAttack || lungingAttack.m_attackDone
                || PreservesFullBodyAttackPose(lungingAttack)
                || player.IsDead() || player.IsStaggering() || player.IsKnockedBack()
                || player.InDodge() || player.m_blocking || player.IsAttached() || !player.IsOnGround())
            {
                lungePoseWeight = 0f;
                if (player != null) lastLungeVisualPosition = player.transform.position;
                return;
            }
            if (attackLocomotionFailed) return;
            try
            {
                if (attackLocomotion == null)
                    attackLocomotion = new AttackLocomotionPose(player.m_animator);
                float dt = Time.deltaTime;
                Vector3 velocity = dt > 0f ? (player.transform.position - lastLungeVisualPosition) / dt : Vector3.zero;
                lastLungeVisualPosition = player.transform.position;
                velocity.y = 0f;
                Vector3 local = player.transform.InverseTransformDirection(velocity);
                lungePoseWeight = Mathf.MoveTowards(lungePoseWeight, 1f, dt / 0.08f);
                attackLocomotion.Apply(local.z, local.x, dt, lungePoseWeight);
            }
            catch (Exception error)
            {
                ReleaseAttackLocomotion();
                attackLocomotionFailed = true;
                UnityEngine.Debug.LogWarning("[Overhaul] Attack locomotion disabled: " + error);
            }
        }

        internal static bool PreservesFullBodyAttackPose(Attack attack)
        {
            // These native attacks animate the jump through hips and legs even while
            // the gameplay capsule is grounded. A walking overlay cancels that leap.
            return attack!=null&&(attack.m_attackAnimation=="knife_secondary"||
                attack.m_attackAnimation=="dual_knives_secondary");
        }

        [HarmonyPatch(typeof(Attack), "Start")]
        private static class AttackLungeStartPatch
        {
            private static void Postfix(Attack __instance, bool __result)
            {
                if (!__result || __instance.m_currentAttackCainLevel != 0)
                    return;
                Player player = __instance.m_character as Player;
                ItemDrop.ItemData weapon = __instance.m_weapon;
                if (player == null || player != Player.m_localPlayer || weapon == null
                    || !weapon.IsWeapon() || IsDashing(player) || player.IsAttached()
                    || !IsLungeAttack(__instance.m_attackType, weapon.m_shared.m_skillType,
                        weapon.m_shared.m_itemType))
                    return;
                // Attack.Start runs before Humanoid updates m_currentAttackIsSecondary.
                // Use the primary/secondary context captured around Humanoid.StartAttack.
                if (!Utility.WeaponAttackSpeeds.Movement(weapon,Utility.WeaponReworkEffects.IsSecondary(__instance,weapon)))
                    return;

                lungingPlayer = player;
                lungingAttack = __instance;
                lastLungeVisualPosition = player.transform.position;
                lungePoseWeight = 0f;
                lungeDirection = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up).normalized;
                lungeDistance = AttackLungeDistance;
                lungeProgress = 0f;
                lungeStateHash = 0;
                lungeAnimationStarted = false;
                AnimatorStateInfo previous = player.m_animator.GetCurrentAnimatorStateInfo(0);
                lungePreviousStateHash = previous.fullPathHash;
                lungePreviousStateTime = previous.normalizedTime;
            }
        }

        internal static bool IsLungeAttack(Attack.AttackType attackType, Skills.SkillType skill,
            ItemDrop.ItemData.ItemType itemType)
        {
            return (attackType == Attack.AttackType.Horizontal
                    || attackType == Attack.AttackType.Vertical || attackType == Attack.AttackType.Area);
        }

        // Called before native walking, just like the dash: physics still resolves the movement.
        internal static bool UpdateAttackLunge(Player player, float dt)
        {
            if (player == null || player != lungingPlayer)
                return false;
            bool interrupted = player != Player.m_localPlayer || player.IsDead() || player.IsStaggering()
                || player.InDodge() || player.IsKnockedBack() || player.IsAttached() || player.m_blocking
                || player.m_currentAttack != lungingAttack || lungingAttack.m_attackDone || IsDashing(player)
                || IsStaffShieldCasting(player) || IsStaffShieldStunned(player);
            if (interrupted)
            {
                lungingPlayer = null;
                lungingAttack = null;
                lungeAnimationStarted = false;
                // Do not overwrite a dodge, dash or hit reaction on interruption.
                if (!player.IsStaggering() && !player.IsKnockedBack() && !player.InDodge() && !IsDashing(player))
                {
                    Vector3 velocity = player.m_body.linearVelocity;
                    player.m_body.linearVelocity = new Vector3(0f, velocity.y, 0f);
                    player.m_currentVel = Vector3.zero;
                }
                return false;
            }
            if (dt <= 0f)
                return true;

            Animator animator = player.m_animator;
            AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
            bool enteringAttack = false;
            // During entry blending the new attack is the next state, not the current state.
            if (animator.IsInTransition(0))
            {
                AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(0);
                if (next.tagHash == Humanoid.s_animatorTagAttack
                    && (!lungeAnimationStarted || next.fullPathHash == lungeStateHash))
                {
                    state = next;
                    enteringAttack = true;
                }
            }
            float progress = lungeProgress;
            if (state.tagHash == Humanoid.s_animatorTagAttack
                && (lungeAnimationStarted || enteringAttack || state.fullPathHash != lungePreviousStateHash
                    || state.normalizedTime < lungePreviousStateTime)
                && (!lungeAnimationStarted || state.fullPathHash == lungeStateHash))
            {
                lungeAnimationStarted = true;
                lungeStateHash = state.fullPathHash;
                progress = Mathf.Max(lungeProgress, Mathf.Clamp01(state.normalizedTime));
            }

            Vector3 direction = lungeDirection;
            if (player.IsOnGround())
                direction = Vector3.ProjectOnPlane(direction, player.m_lastGroundNormal).normalized;
            // Distance follows animation time, including attack speed changes and pauses.
            // Consume blocked progress too: never accumulate a burst against an obstacle.
            float step = lungeDistance * (progress - lungeProgress);
            lungeProgress = progress;
            foreach (RaycastHit hit in player.m_body.SweepTestAll(direction, step + 0.03f, QueryTriggerInteraction.Ignore))
            {
                Collider collider = hit.collider;
                if (collider == null || collider.attachedRigidbody == player.m_body
                    || Physics.GetIgnoreLayerCollision(player.gameObject.layer, collider.gameObject.layer)
                    || Physics.GetIgnoreCollision(player.m_collider, collider)
                    || Vector3.Dot(hit.normal, direction) >= -0.1f)
                    continue;
                step = Mathf.Min(step, Mathf.Max(0f, hit.distance - 0.03f));
            }
            Vector3 movement = direction * (step / dt);
            if (!player.IsOnGround())
                movement.y = player.m_body.linearVelocity.y;
            player.m_body.linearVelocity = movement;
            player.m_currentVel = movement;
            player.m_rootMotion = Vector3.zero;
            player.m_lastPos = player.transform.position;
            return true;
        }

        internal const string AttackCancelTrigger = "overhaul_attack_cancel";
        internal static readonly int MovementState = Animator.StringToHash("Base Layer.Movement");
		private static bool sprintWasHeld;
        private sealed class DashState
        {
            internal float Remaining,LastJump=float.NegativeInfinity;
            internal Vector3 Direction;
            internal bool Upward,GroundPropelled,Jumped;
            internal GameObject Visual;
            internal readonly List<KeyValuePair<Collider,Collider>> BushPairs=new List<KeyValuePair<Collider,Collider>>();
        }
        private static readonly Dictionary<Player,DashState> dashes=new Dictionary<Player,DashState>();
        private static DashState Dash(Player player)
        {if(!dashes.TryGetValue(player,out var state))dashes.Add(player,state=new DashState());return state;}
		private const float JumpDashWindow = 0.35f;
		private const float JumpDashAngle = 40f;
        private const float BaseDashAngle = 2f;
		private static bool missingDashEffectWasLogged;
		private static bool missingDashSoundWasLogged;
		private static GameObject cachedDashEffectPrefab;
		private static GameObject cachedDashSoundPrefab;
		private static SE_Shield activeStaffShield;
		private static bool staffShieldIsActive;
		private static bool staffShieldBrokenWhileHeld;
		private static bool staffShieldStunActive;
		private static float staffShieldStunUntil;
		private static bool staffShieldCasting;
		private static float staffShieldCastUntil;
		private const float StaffShieldBreakStunDuration = 1.5f;
		private const float StaffShieldCastDuration = 0.5f;
		private const float StaffShieldAbsorbPerUpgradeLevel = 50f;
		private const float StaffShieldBaseAbsorb = 200f;
		private const string StaffShieldStatusEffectName = "Staff_shield";
		private const string StaffShieldAnimationTrigger = "staff_shield";
		private const string FireStaffPrefab = "StaffFireball";
		private const float StraightLaunchAngle = 0f;
		private const float StraightProjectileGravity = 0f;
		private static readonly AssetID JotunDashEffectAssetId = new AssetID(
			0x79016731u, 0x2a396484u, 0x9b3d6d38u, 0x874e9373u);
		private static readonly string[] DashEffectPrefabNames =
		{
			"fx_JotunWitch_Dodge",
			"fx_perfectdodge"
		};
		private static readonly string[] DashSoundPrefabNames =
		{
			"sfx_jotunwitch_dodge",
			"sfx_perfect_dodge"
		};

		public static void Update(Player player)
        {
            if(!player)return; var state=Dash(player);
			if (player == null || player != Player.m_localPlayer)
            {
                return;
            }

			if (state.Jumped && player.IsOnGround() && Time.time - state.LastJump > JumpDashWindow)
				state.Jumped = false;

			if (player.m_blocking && player.m_currentAttack != null && player.InAttack()
				&& player.m_animator != null && player.m_animator.HasState(0, MovementState))
			{
				StopAttack(player);
			}

			bool held = ZInput.GetButton("Run") || ZInput.GetButton("JoyRun");
			if (ConsumeDashPress(held, player.TakeInput())) StartDash(player);
        }

        internal static bool ConsumeDashPress(bool held, bool takeInput)
        {
            // Input availability is not a key release. Never defer a held press until
            // controls return, or turn that transition into an unsolicited second dash.
            bool pressed = held && !sprintWasHeld;
            sprintWasHeld = held;
            return pressed && takeInput;
        }

		public static void ProcessControls(Player player, ref Vector3 moveDir, ref bool attack,
			ref bool attackHold, ref bool secondaryAttack, ref bool secondaryAttackHold,
			ref bool block, ref bool blockHold, ref bool jump, ref bool crouch,
			ref bool run, ref bool autoRun, ref bool dodge)
		{
			if (player == null || player != Player.m_localPlayer)
			{
				return;
			}

            bool wantsToBlock = block || blockHold;
            if(Persistence.PlayerSessionGame.Managed)
            {
                bool staff=IsStaff(player.GetCurrentWeapon());
                Persistence.GameStaffGuardRuntime.Input(staff&&wantsToBlock&&player.TakeInput());
                bool frozen=Persistence.GameStaffGuardRuntime.Casting(player)||Persistence.GameStaffGuardRuntime.Stunned(player);
                if(staff&&wantsToBlock||frozen){block=blockHold=attack=attackHold=secondaryAttack=secondaryAttackHold=false;player.m_blocking=false;player.m_zanim.SetBool(Humanoid.s_blocking,false);}
                if(frozen){moveDir=Vector3.zero;jump=crouch=run=autoRun=dodge=false;}
                return;
            }
			UpdateInactiveStaffShield(player, Time.deltaTime);
			UpdateStaffShieldStun(player);
			if (staffShieldStunActive)
			{
				if (!wantsToBlock)
				{
					staffShieldBrokenWhileHeld = false;
				}
				CancelStaffShieldCast(player);
				RemoveStaffShield(player);
				moveDir = Vector3.zero;
				attack = false;
				attackHold = false;
				secondaryAttack = false;
				secondaryAttackHold = false;
				block = false;
				blockHold = false;
				jump = false;
				crouch = false;
				run = false;
				autoRun = false;
				dodge = false;
				return;
			}

			ItemDrop.ItemData weapon = player.GetCurrentWeapon();
			if (!IsStaff(weapon))
			{
				CancelStaffShieldCast(player);
				RemoveStaffShield(player);
				if (!wantsToBlock)
				{
					staffShieldBrokenWhileHeld = false;
				}
				return;
			}

			if (!wantsToBlock)
			{
				staffShieldBrokenWhileHeld = false;
				CancelStaffShieldCast(player);
				RemoveStaffShield(player);
				return;
			}

			block = false;
			blockHold = false;
			player.m_blocking = false;
			player.m_zanim.SetBool(Humanoid.s_blocking, false);
			attack = false;
			attackHold = false;
			secondaryAttack = false;
			secondaryAttackHold = false;
			if (!staffShieldIsActive && !staffShieldBrokenWhileHeld)
			{
				if (!staffShieldCasting)
				{
					StartStaffShieldCast(player);
				}

				if (Time.time >= staffShieldCastUntil)
				{
					staffShieldCasting = false;
					ApplyStaffShield(player, weapon);
				}
			}

			if (staffShieldCasting)
			{
				moveDir = Vector3.zero;
				jump = false;
				crouch = false;
				run = false;
				autoRun = false;
				dodge = false;
			}
		}

		private static void StartStaffShieldCast(Player player)
		{
			if (player.m_currentAttack != null)
			{
				StopAttack(player, false);
			}
			staffShieldCasting = true;
			staffShieldCastUntil = Time.time + StaffShieldCastDuration;
			player.m_zanim.SetTrigger(StaffShieldAnimationTrigger);
		}

		private static void CancelStaffShieldCast(Player player)
		{
			if (!staffShieldCasting)
			{
				return;
			}

			staffShieldCasting = false;
			PlayMovementState(player.m_animator);
		}

		private static bool IsStaff(ItemDrop.ItemData item)
		{
			return item != null && item.IsWeapon() && item.m_dropPrefab != null
				&& item.m_dropPrefab.name.StartsWith("Staff", StringComparison.OrdinalIgnoreCase);
		}

		private static void ApplyStaffShield(Player player, ItemDrop.ItemData staff)
		{
			if (activeStaffShield != null && player.GetSEMan().HaveStatusEffect(activeStaffShield))
			{
				activeStaffShield.m_ttl = 0f;
				activeStaffShield.Setup(player);
				staffShieldIsActive = true;
				HideVanillaStaffShieldVisuals(activeStaffShield);
				StaffShieldVfx.Show(player, staff);
				return;
			}

			StatusEffect template = ObjectDB.instance != null
				? ObjectDB.instance.GetStatusEffect(StaffShieldStatusEffectName.GetStableHashCode())
				: null;
			if (!(template is SE_Shield))
			{
				Log.LogError($"Status effect '{StaffShieldStatusEffectName}' was not found");
				return;
			}

			float bloodMagicLevel = player.GetSkillLevel(Skills.SkillType.BloodMagic);
			activeStaffShield = player.GetSEMan().AddStatusEffect(template, false,
				staff.m_quality, bloodMagicLevel, -1) as SE_Shield;
			if (activeStaffShield != null)
			{
				activeStaffShield.m_ttl = 0f;
				activeStaffShield.m_totalAbsorbDamage = StaffShieldBaseAbsorb
					+ Mathf.Max(0, staff.m_quality - 1) * StaffShieldAbsorbPerUpgradeLevel;
				staffShieldIsActive = true;
				HideVanillaStaffShieldVisuals(activeStaffShield);
				StaffShieldVfx.Show(player, staff);
			}
		}

		private static void HideVanillaStaffShieldVisuals(SE_Shield shield)
		{
			if (shield == null || shield.m_startEffectInstances == null)
			{
				return;
			}

			foreach (GameObject effect in shield.m_startEffectInstances)
			{
				if (effect == null)
				{
					continue;
				}

				foreach (Renderer renderer in effect.GetComponentsInChildren<Renderer>(true))
				{
					renderer.enabled = false;
				}
				foreach (ParticleSystem particles in effect.GetComponentsInChildren<ParticleSystem>(true))
				{
					particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
				}
			}
		}

		private static void RemoveStaffShield(Player player)
		{
			if (activeStaffShield == null)
			{
				return;
			}

			if (activeStaffShield.m_damage <= 0f)
			{
				ClearStaffShield(player);
				return;
			}

			if (staffShieldIsActive)
			{
				StaffShieldVfx.Hide();
				activeStaffShield.Stop();
				staffShieldIsActive = false;
			}
		}

		private static void UpdateInactiveStaffShield(Player player, float dt)
		{
			if (activeStaffShield == null || staffShieldIsActive || dt <= 0f)
			{
				return;
			}

			activeStaffShield.m_damage = Mathf.Max(0f,
				activeStaffShield.m_damage - OverhaulConfig.StaffShieldRegenerationPerSecond.Value * dt);
			if (activeStaffShield.m_damage <= 0f)
			{
				ClearStaffShield(player);
			}
		}

		private static void ClearStaffShield(Player player)
		{
			if (activeStaffShield != null && player.GetSEMan().HaveStatusEffect(activeStaffShield))
			{
				player.GetSEMan().RemoveStatusEffect(activeStaffShield, true);
			}
			activeStaffShield = null;
			staffShieldIsActive = false;
			StaffShieldVfx.Hide();
		}

		private static void UpdateStaffShieldStun(Player player)
		{
			if (!staffShieldStunActive || Time.time < staffShieldStunUntil)
			{
				return;
			}

			staffShieldStunActive = false;
			PlayMovementState(player.m_animator);
		}

		public static bool IsStaffShieldStunned(Player player)
		{
			if(Persistence.PlayerSessionGame.Managed||Persistence.GameMovementRuntime.Managed(player))return Persistence.GameStaffGuardRuntime.Stunned(player);
            return player != null && player == Player.m_localPlayer && staffShieldStunActive
				&& Time.time < staffShieldStunUntil;
		}

		public static bool IsStaffShieldCasting(Player player)
		{
			if(Persistence.PlayerSessionGame.Managed||Persistence.GameMovementRuntime.Managed(player))return Persistence.GameStaffGuardRuntime.Casting(player);
            return player != null && player == Player.m_localPlayer && staffShieldCasting
				&& Time.time < staffShieldCastUntil;
		}

		private static void OnStaffShieldBroken(SE_Shield shield)
		{
			if (shield == null || shield != activeStaffShield)
			{
				return;
			}

			Player player = Player.m_localPlayer;
			activeStaffShield = null;
			staffShieldIsActive = false;
			StaffShieldVfx.Hide();
			staffShieldBrokenWhileHeld = true;
			staffShieldStunActive = true;
			staffShieldStunUntil = Time.time + StaffShieldBreakStunDuration;
			if (player != null)
			{
				player.Stagger(Vector3.zero);
			}
		}

		private static bool IsTrackedStaffShield(SE_Shield shield)
		{
			return shield != null && shield == activeStaffShield;
		}

		private static void StartDash(Player player)
		{
            if(Persistence.PlayerSessionGame.Managed&&player==Player.m_localPlayer)
            {if(!IsDashBlockedByInteraction(player))Persistence.GameDashAction.Send(player);return;}
            var state=Dash(player);
			if (IsDashing(player) || !player.CanMove() || player.InAttack() || player.InDodge()
				|| player.InMinorAction() || player.IsStaggering() || IsDashBlockedByInteraction(player))
			{
				return;
			}

			float staminaCost = OverhaulConfig.DashStaminaCost.Value;
			if (staminaCost > 0f && !player.HaveStamina(staminaCost))
			{
				if (Hud.instance != null) Hud.instance.StaminaBarEmptyFlash();
				return;
			}

            player.UseStamina(staminaCost);
            BeginDash(player,player.m_moveDir);
        }

        internal static void BeginDash(Player player,Vector3 direction)
        {
            var state=Dash(player);
            if(player.IsOnGround()&&Time.time-state.LastJump>JumpDashWindow)state.Jumped=false;
			state.Direction = direction;
			state.Direction.y = 0f;
			if (state.Direction.sqrMagnitude < 0.01f)
			{
				state.Direction = player.transform.forward;
				state.Direction.y = 0f;
			}
			state.Direction.Normalize();
            state.GroundPropelled = false;
			
			state.Upward = state.Jumped &&
				(Time.time - state.LastJump <= JumpDashWindow || (!player.IsOnGround() && player.m_body.linearVelocity.y > 0.1f));
            state.Direction = InclineDash(state.Direction, state.Upward ? JumpDashAngle : BaseDashAngle);
			state.Remaining = (OverhaulConfig.DashDuration?.Value??.2f);
			// Discard the height accumulated before this dash, not subsequent climbing.
			player.m_maxAirAltitude = player.transform.position.y;
			player.m_run = false;
			state.Visual = PlayDashEffects(player, state.Direction);
            SendDashVisual(player, state.Remaining);
			ApplyDashVelocity(player);
			Log.LogDebug("Dash started");
		}

        private static bool IsDashBlockedByInteraction(Player player)
        {
            var held = player.GetRightItem();
            if (held != null && (held.m_shared.m_name == "$item_hammer"
                || held.m_shared.m_name == "$item_cultivator")) return true;

            // Use the same target as the HUD, without depending on HUD update order or Auga.
            var target = player.GetHoverObject();
            if (!target || (TextViewer.instance && TextViewer.instance.IsVisible())) return false;
            var hover = target.GetComponentInParent<Hoverable>();
            return hover != null && target.GetComponentInParent<Interactable>() != null
                && !string.IsNullOrWhiteSpace(hover.GetHoverText());
        }

		public static void OnJump(Player player)
		{
            if(!player)return; var state=Dash(player);
			if (player != Player.m_localPlayer && !Persistence.GameMovementRuntime.Managed(player)) return;
			state.Jumped = true;
			state.LastJump = Time.time;
			// Handle jump and dash pressed together, regardless of their update order.
			if (IsDashing(player) && !state.Upward)
			{
				state.Upward = true;
				TiltDashUpward(player);
				ApplyDashVelocity(player);
			}
		}

		private static void TiltDashUpward(Player player)
		{
            var state=Dash(player);
            state.Direction = InclineDash(state.Direction, JumpDashAngle);
		}
        internal static Vector3 InclineDash(Vector3 direction, float degrees)
        {
            float angle=degrees*Mathf.Deg2Rad;
            return Vector3.ProjectOnPlane(direction,Vector3.up).normalized*Mathf.Cos(angle)+Vector3.up*Mathf.Sin(angle);
        }

        private const string DashVisualRpc = "Overhaul_DashVisual";

        private static void SendDashVisual(Player player, float duration)
        {
            var state=Dash(player);
            if (duration > 0f) DashAnimationPlayback.Start(player, state.Direction, duration);
            if (player.m_nview != null && player.m_nview.IsValid() && (player.m_nview.IsOwner()||Persistence.GameMovementRuntime.Managed(player)))
                player.m_nview.InvokeRPC(ZNetView.Everybody, DashVisualRpc, state.Direction, duration);
        }

        [HarmonyPatch(typeof(Player), nameof(Player.Awake))]
        private static class RegisterDashVisualPatch
        {
            private static void Postfix(Player __instance)
            {
                if (__instance.m_nview == null || !__instance.m_nview.IsValid()) return;
                RemoteDashVisual visual = __instance.gameObject.AddComponent<RemoteDashVisual>();
                visual.player = __instance;
                __instance.m_nview.Register<Vector3, float>(DashVisualRpc, visual.Receive);
                var aura = __instance.gameObject.AddComponent<RemoteDodgeAura>();
                aura.player = __instance;
                __instance.m_nview.Register<bool>(AuraRpc, aura.Receive);
            }
        }

        public sealed class RemoteDodgeAura : MonoBehaviour
        {
            internal Player player;
            private GameObject visual;
            private readonly List<Material> materials = new List<Material>();
            private float expires;
            internal void Receive(long sender, bool active)
            {
                if (!player || !player.m_nview || !player.m_nview.IsValid()
                    || player.m_nview.IsOwner() || sender != player.m_nview.GetZDO().GetOwner()) return;
                if (!active) { Clear(); return; }
                expires = Time.time + 2f;
                if (!visual) visual = CreateDodgeAura(player, materials);
            }
            private void LateUpdate()
            {
                if (visual && (Time.time >= expires || !player || player.IsDead())) Clear();
            }
            private void Clear()
            {
                if (visual) UnityEngine.Object.Destroy(visual);
                visual = null;
                foreach (var material in materials) if (material) UnityEngine.Object.Destroy(material);
                materials.Clear();
            }
            private void OnDestroy() => Clear();
        }
        // Remote effects are per player and never change the local dash physics.
        public sealed class RemoteDashVisual : MonoBehaviour
        {
            internal Player player;
            private GameObject visual;
            private float remaining;

            internal void Receive(long sender, Vector3 direction, float duration)
            {
                if (player == null || player.m_nview == null || !player.m_nview.IsValid()) return;
                bool managed=Persistence.PlayerSessionGame.Managed||Persistence.GameMovementRuntime.Managed(player);
                if(managed)
                {
                    long server=ZNet.instance.IsServer()?ZNet.GetUID():ZNet.instance.GetServerPeer()?.m_uid??0;
                    if(sender!=server||Persistence.GameCreatureAuthority.Enabled)return;
                }
                else if(player.m_nview.IsOwner()||sender!=player.m_nview.GetZDO().GetOwner())return;
                if (float.IsNaN(duration) || float.IsInfinity(duration)) return;
                if (duration <= 0f) { if(managed&&player==Player.m_localPlayer){if(IsDashing(player))StopDashMovement(player);ForgetDash(player);}StopVisual(); return; }
                float magnitude = direction.sqrMagnitude;
                if (float.IsNaN(magnitude) || float.IsInfinity(magnitude) || magnitude < 0.001f) return;
                StopVisual();
                visual = PlayDashEffects(player, direction.normalized);
                // Expire even if the owner disconnects before sending the stop message.
                remaining = Mathf.Clamp(duration, 0.01f, 10f);
                if(managed&&player==Player.m_localPlayer){var state=Dash(player);state.Direction=direction.normalized;state.Remaining=remaining;state.Upward=state.Direction.y>.3f;state.GroundPropelled=false;player.m_maxAirAltitude=player.transform.position.y;}
                DashAnimationPlayback.Start(player, direction.normalized, remaining);
            }

            private void LateUpdate()
            {
                if (visual == null) return;
                remaining -= Time.deltaTime;
                if (remaining <= 0f || player == null || player.IsDead()) StopVisual();
            }

            private void StopVisual()
            {
                if (visual == null) return;
                FreezeJotunDashVisual(visual);
                visual.transform.SetParent(null, true);
                UnityEngine.Object.Destroy(visual, 5f);
                visual = null;
            }

            private void OnDestroy() { StopVisual(); }
        }

		private static GameObject PlayDashEffects(Player player, Vector3 direction)
		{
            GameObject jotunVisual;
			bool dashVisualWasCreated = PlayJotunDashVisual(player, direction, out jotunVisual);
			if (!dashVisualWasCreated && cachedDashEffectPrefab == null)
			{
				for (int i = 0; i < DashEffectPrefabNames.Length && cachedDashEffectPrefab == null; i++)
				{
					cachedDashEffectPrefab = FindLoadedPrefab(DashEffectPrefabNames[i]);
				}
			}

			if (!dashVisualWasCreated && cachedDashEffectPrefab != null)
			{
				Quaternion effectRotation = Quaternion.LookRotation(direction, Vector3.up);
				GameObject effect = UnityEngine.Object.Instantiate(cachedDashEffectPrefab, player.transform.position, effectRotation);
				effect.SetActive(true);
				dashVisualWasCreated = true;
			}
			if (!dashVisualWasCreated)
			{
				dashVisualWasCreated = PlayPlayerPerfectDodgeVisual(player, direction);
			}
			if (dashVisualWasCreated)
			{
				missingDashEffectWasLogged = false;
			}
			else if (!missingDashEffectWasLogged)
			{
				Log.LogWarning("No compatible loaded dash VFX prefab was found");
				missingDashEffectWasLogged = true;
			}

			if (cachedDashSoundPrefab == null)
			{
				for (int i = 0; i < DashSoundPrefabNames.Length && cachedDashSoundPrefab == null; i++)
				{
					cachedDashSoundPrefab = FindLoadedPrefab(DashSoundPrefabNames[i]);
				}
			}

			if (cachedDashSoundPrefab != null)
			{
				GameObject sound = UnityEngine.Object.Instantiate(cachedDashSoundPrefab, player.transform.position,
					player.transform.rotation);
				AudioSource[] sources = sound.GetComponentsInChildren<AudioSource>(true);
				for (int i = 0; i < sources.Length; i++)
				{
					sources[i].volume = 1f;
				}
				sound.SetActive(true);
			}
			else if (!missingDashSoundWasLogged)
			{
				Log.LogWarning("No compatible loaded dash sound prefab was found");
				missingDashSoundWasLogged = true;
			}
            return jotunVisual;
		}

		private static bool PlayJotunDashVisual(Player player, Vector3 direction, out GameObject visual)
		{
            visual = null;
			SoftReference<GameObject> effectReference = new SoftReference<GameObject>(JotunDashEffectAssetId);
			LoadResult loadResult = effectReference.Load();
			if (loadResult != LoadResult.Succeeded || effectReference.Asset == null)
			{
				effectReference.Release();
				return false;
			}

			Quaternion effectRotation = Quaternion.LookRotation(direction, Vector3.up);
			GameObject effect = SoftReferenceableAssets.Utils.Instantiate(effectReference,
				player.transform.position, effectRotation);
			effectReference.Release();
			if (effect == null)
			{
				return false;
			}

			effect.SetActive(true);
			ParticleSystem[] particleSystems = effect.GetComponentsInChildren<ParticleSystem>(true);
			for (int i = 0; i < particleSystems.Length; i++)
			{
				ParticleSystem.MainModule main = particleSystems[i].main;
				main.simulationSpace = ParticleSystemSimulationSpace.World;
				ParticleSystem.EmissionModule emission = particleSystems[i].emission;
				emission.rateOverDistanceMultiplier *= 0.3f;
			}
			effect.transform.SetParent(player.transform, true);
			visual = effect;
			return true;
		}

		private static bool PlayPlayerPerfectDodgeVisual(Player player, Vector3 direction)
		{
			if (player.m_perfectDodgeEffects == null || player.m_perfectDodgeEffects.m_effectPrefabs == null)
			{
				return false;
			}

			Quaternion effectRotation = Quaternion.LookRotation(direction, Vector3.up);
			bool visualWasCreated = false;
			EffectList.EffectData[] effects = player.m_perfectDodgeEffects.m_effectPrefabs;
			for (int i = 0; i < effects.Length; i++)
			{
				EffectList.EffectData effectData = effects[i];
				if (effectData == null || !effectData.m_enabled || effectData.m_prefab == null)
				{
					continue;
				}

				string prefabName = effectData.m_prefab.name.ToLowerInvariant();
				if (!prefabName.StartsWith("fx_") && !prefabName.StartsWith("vfx_"))
				{
					continue;
				}

				GameObject visual = UnityEngine.Object.Instantiate(effectData.m_prefab, player.transform.position, effectRotation);
				if (effectData.m_scale)
				{
					visual.transform.localScale = Vector3.one;
				}
				if (effectData.m_attach)
				{
					visual.transform.SetParent(player.transform, true);
				}
				visual.SetActive(true);
				visualWasCreated = true;
			}

			return visualWasCreated;
		}

		private static GameObject FindLoadedPrefab(string prefabName) =>
            Jotunn.Managers.PrefabManager.Instance.GetPrefab(prefabName);

		public static bool IsDashing(Player player)
		{
			return player && dashes.TryGetValue(player,out var state) && state.Remaining > 0f;
		}

		public static void UpdateDash(Player player, float dt)
		{
            if(!player)return; var state=Dash(player);
			if (!IsDashing(player))
			{
				return;
			}

			state.Remaining = Mathf.Max(0f, state.Remaining - dt);
            ApplyDashVelocity(player);

			if (state.Remaining <= 0f)
			{
				StopDashMovement(player);
                SendDashVisual(player, 0f);
				if (state.Visual != null)
				{
					FreezeJotunDashVisual(state.Visual);
					state.Visual.transform.SetParent(null, true);
					state.Visual = null;
				}
			}
		}

        internal static void CancelDash(Player player)
        {if(!IsDashing(player))return;StopDashMovement(player);Dash(player).Remaining=0;SendDashVisual(player,0);ForgetDash(player);}
        internal static void ForgetDash(Player player)
        {
            if(ReferenceEquals(player,null)||!dashes.TryGetValue(player,out var state))return;
            foreach(var pair in state.BushPairs)if(pair.Key&&pair.Value)Physics.IgnoreCollision(pair.Key,pair.Value,false);
            if(state.Visual){FreezeJotunDashVisual(state.Visual);state.Visual.transform.SetParent(null,true);UnityEngine.Object.Destroy(state.Visual,5f);}
            dashes.Remove(player);
        }
        [HarmonyPatch(typeof(Player),"OnDestroy")]
        private static class DashCleanup
        {private static void Prefix(Player __instance)=>ForgetDash(__instance);}
		private static void FreezeJotunDashVisual(GameObject visual)
		{
			ParticleSystem[] particleSystems = visual.GetComponentsInChildren<ParticleSystem>(true);
			for (int i = 0; i < particleSystems.Length; i++)
			{
				ParticleSystem particleSystem = particleSystems[i];
				ParticleSystem.EmissionModule emission = particleSystem.emission;
				emission.enabled = false;

				ParticleSystem.VelocityOverLifetimeModule velocity = particleSystem.velocityOverLifetime;
				velocity.enabled = false;
				ParticleSystem.LimitVelocityOverLifetimeModule limitVelocity = particleSystem.limitVelocityOverLifetime;
				limitVelocity.enabled = false;
				ParticleSystem.InheritVelocityModule inheritVelocity = particleSystem.inheritVelocity;
				inheritVelocity.enabled = false;
				ParticleSystem.ForceOverLifetimeModule force = particleSystem.forceOverLifetime;
				force.enabled = false;
				ParticleSystem.NoiseModule noise = particleSystem.noise;
				noise.enabled = false;
				ParticleSystem.ExternalForcesModule externalForces = particleSystem.externalForces;
				externalForces.enabled = false;

				int particleCount = particleSystem.particleCount;
				if (particleCount <= 0)
				{
					continue;
				}

				ParticleSystem.Particle[] particles = new ParticleSystem.Particle[particleCount];
				int aliveParticles = particleSystem.GetParticles(particles);
				for (int particleIndex = 0; particleIndex < aliveParticles; particleIndex++)
				{
					particles[particleIndex].velocity = Vector3.zero;
				}
				particleSystem.SetParticles(particles, aliveParticles);
			}
		}

		private static void StopDashMovement(Player player)
		{
            var state=Dash(player);
            RestoreDashBushes(player);
			Vector3 velocity = player.m_body.linearVelocity;
			player.m_body.linearVelocity = new Vector3(0f, state.Upward ? 0f : RemoveGroundDashLift(velocity.y, state.GroundPropelled), 0f);
            state.GroundPropelled = false;
			player.m_currentVel = player.m_body.linearVelocity;
			player.m_lastPos = player.transform.position;
		}

		private static void ApplyDashVelocity(Player player)
		{
            var state=Dash(player);
			if (state.Upward && player.IsOnGround())
			{
				player.ResetGroundContact();
				player.m_lastGroundTouch = 1f;
			}
			Vector3 velocity = player.m_body.linearVelocity;
			Vector3 dashVelocity = state.Direction * (OverhaulConfig.DashSpeed?.Value??20f);
			Vector3 desired = new Vector3(dashVelocity.x, state.Upward ? dashVelocity.y : velocity.y, dashVelocity.z);
            bool vegetation = IgnoreDashBushes(player);
            if (!state.Upward && !player.IsSwimming())
            {
                desired.y = RemoveGroundDashLift(desired.y, state.GroundPropelled);
                if (player.IsOnGround()) TryDashRelief(player.m_body,desired,Time.fixedDeltaTime,player.m_lastGroundNormal);
                desired = FollowDashGround(player.m_body, desired, Time.fixedDeltaTime,
                    player.IsOnGround() ? player.m_lastGroundNormal : Vector3.zero);
                if (player.IsOnGround() || desired.y > velocity.y + .001f) state.GroundPropelled = true;
            }
            if (!state.Upward && !player.IsSwimming())
            {
                // A fixed small take-off angle, never added to the previous frame's lift.
                float speed = (OverhaulConfig.DashSpeed?.Value??20f);
                float lift = speed * Mathf.Sin(BaseDashAngle * Mathf.Deg2Rad);
                if (desired.y >= 0f && desired.y < lift)
                { desired = InclineDash(desired, BaseDashAngle) * speed; state.GroundPropelled = true; }
            }
            if (vegetation) desired *= .8f;
            player.m_body.linearVelocity = LimitDashMotion(player.m_body, desired, Time.fixedDeltaTime, out _);
			player.m_currentVel = player.m_body.linearVelocity;
			player.m_lastPos = player.transform.position;
		}

        private static void RestoreDashBushes(Player player)
        {
            var state=Dash(player);
            foreach(var pair in state.BushPairs)if(pair.Key&&pair.Value)Physics.IgnoreCollision(pair.Key,pair.Value,false);
            state.BushPairs.Clear();
        }
        internal static bool IsDashVegetation(Collider obstacle)
        {
            var plant=obstacle.GetComponentInParent<Plant>(true);
            if(plant)
                foreach(var grown in plant.m_grownPrefabs)
                    if(grown && grown.GetComponentInChildren<TreeBase>(true))return true;
            var view=obstacle.GetComponentInParent<ZNetView>();if(!view)return false;
            string name=Utils.GetPrefabName(view.gameObject);
            return (name=="Bush01" || name=="Bush02" || name=="Bush01_heath") && obstacle.bounds.size.y<=2f;
        }
        private static bool IgnoreDashBushes(Player player)
        {
            var state=Dash(player);
            bool touching=false;
            foreach(var obstacle in Physics.OverlapSphere(player.transform.position,3f,~0,QueryTriggerInteraction.Ignore))
            {
                if(!IsDashVegetation(obstacle))continue;
                foreach(var own in player.m_body.GetComponentsInChildren<Collider>())
                {
                    if(own.attachedRigidbody!=player.m_body || own.isTrigger || !own.enabled)continue;
                    if(own.bounds.Intersects(obstacle.bounds))touching=true;
                    if(Physics.GetIgnoreCollision(own,obstacle))continue;
                    Physics.IgnoreCollision(own,obstacle,true);state.BushPairs.Add(new KeyValuePair<Collider,Collider>(own,obstacle));
                }
            }
            return touching;
        }
        internal static bool TryDashStep(Rigidbody body,Vector3 velocity,float dt)
        { return TryDashRelief(body,velocity,dt,Vector3.up); }

        internal static bool TryDashRelief(Rigidbody body,Vector3 velocity,float dt,Vector3 supportNormal)
        {
            const float height=.45f;
            var horizontal=Vector3.ProjectOnPlane(velocity,Vector3.up);
            Vector3 normal=supportNormal.normalized;
            if(normal.y<.17365f)normal=Vector3.up;
            horizontal=Vector3.ProjectOnPlane(horizontal,normal).normalized*horizontal.magnitude;
            if(horizontal.sqrMagnitude<.01f || LimitDashVelocity(body,horizontal,dt).sqrMagnitude>=horizontal.sqrMagnitude*.99f)return false;
            if(Vector3.Dot(LimitDashVelocity(body,normal*height,1f),normal)<height-.005f)return false;
            var capsule=body.GetComponent<CapsuleCollider>();if(!capsule)return false;
            var start=body.position;
            float radius=capsule.radius*Mathf.Max(Mathf.Abs(capsule.transform.lossyScale.x),Mathf.Abs(capsule.transform.lossyScale.z));
            float half=Mathf.Max(0,capsule.height*Mathf.Abs(capsule.transform.lossyScale.y)*.5f-radius);
            Vector3 center=body.position+body.rotation*Vector3.Scale(capsule.center,capsule.transform.lossyScale);
            Vector3 foot=center-normal*(radius+half*Mathf.Abs(Vector3.Dot(body.rotation*Vector3.up,normal)));
            var ahead=foot+horizontal*dt+horizontal.normalized*(radius+.03f);
            int mask=ZoneSystem.instance ? ZoneSystem.instance.m_solidRayMask : Physics.DefaultRaycastLayers;
            if(!Physics.Raycast(ahead+normal*(height+.05f),-normal,out var ground,height+.1f,mask,QueryTriggerInteraction.Ignore)
                ||Vector3.Dot(ground.normal,normal)<.7f)return false;
            float relief=Vector3.Dot(ground.point-ahead,normal);
            if(relief<=.001f || relief>height)return false;
            float lift=relief+.02f;
            body.position=start+normal*lift;Physics.SyncTransforms();
            if(LimitDashVelocity(body,horizontal,dt).sqrMagnitude<horizontal.sqrMagnitude*.99f)
            {body.position=start;Physics.SyncTransforms();return false;}
            return true;
        }
        internal static float RemoveGroundDashLift(float vertical, bool groundPropelled)
        { return groundPropelled ? Mathf.Min(0f, vertical) : vertical; }
        internal static bool IsDashTerrain(Collider collider)
        { return collider is TerrainCollider || collider.GetComponentInParent<Heightmap>(true); }

        internal static Vector3 FollowDashGround(Rigidbody body, Vector3 velocity, float dt, Vector3 groundNormal)
        {
            Vector3 horizontal = Vector3.ProjectOnPlane(velocity, Vector3.up);
            if (horizontal.sqrMagnitude < .001f || dt <= 0f) return velocity;
            const float minimumUp = .17365f; // Slopes up to 80 degrees; vertical walls remain obstacles.
            Vector3 normal = groundNormal.normalized;
            if (normal.y <= 0f) normal = Vector3.up;
            var capsule = body.GetComponent<CapsuleCollider>();
            float nearest = float.PositiveInfinity;
            bool terrainContact = false;
            // Only low contacts can redirect propulsion. An overhead rock or wall cannot.
            foreach (var hit in body.SweepTestAll(horizontal.normalized,
                horizontal.magnitude * dt + .02f, QueryTriggerInteraction.Ignore))
            {
                bool terrain = hit.collider && IsDashTerrain(hit.collider);
                if (!capsule || !hit.collider || hit.collider.attachedRigidbody == body
                    || hit.collider.GetComponentInParent<TreeBase>() || hit.collider.GetComponentInParent<TreeLog>()
                    || (terrain ? hit.normal.y < 0f : hit.normal.y < minimumUp || hit.point.y > capsule.bounds.min.y + .45f)
                    || Vector3.Dot(hit.normal, horizontal) >= -.001f
                    || Physics.GetIgnoreLayerCollision(capsule.gameObject.layer, hit.collider.gameObject.layer)
                    || Physics.GetIgnoreCollision(capsule, hit.collider) || hit.distance >= nearest) continue;
                normal = hit.normal;
                terrainContact = terrain;
                nearest = hit.distance;
            }
            if (normal.y > .9999f) return groundNormal.sqrMagnitude > .1f || nearest < float.PositiveInfinity ? horizontal : velocity;
            // Preserve configured speed along the surface, not its horizontal component.
            // The caller still sweeps this new trajectory against every solid obstacle.
            Vector3 tangent = Vector3.ProjectOnPlane(horizontal, normal);
            if (terrainContact && tangent.sqrMagnitude < .0001f) tangent = Vector3.up;
            return tangent.normalized * horizontal.magnitude;
        }

        internal static Vector3 LimitDashVelocity(Rigidbody body, Vector3 velocity, float dt)
        { return LimitDashMotion(body, velocity, dt, out _); }

        internal static Vector3 LimitDashMotion(Rigidbody body, Vector3 velocity, float dt, out bool hardObstacle)
        { return LimitDashMotionCore(body, velocity, dt, true, out hardObstacle); }

        private static Vector3 LimitDashMotionCore(Rigidbody body, Vector3 velocity, float dt, bool slideDown, out bool hardObstacle)
        {
            hardObstacle = false;
            float speed = velocity.magnitude;
            if (speed < 0.001f || dt <= 0f) return velocity;
            const float skin = 0.02f;
            Vector3 direction = velocity / speed;
            float travel = speed * dt;
            float allowed = travel;
            Vector3 supportNormal = Vector3.zero;
            float supportDistance = float.PositiveInfinity;
            var capsule = body.GetComponent<CapsuleCollider>();
            // Sweep the body's actual collider volume across the entire next physics step.
            // Discrete contacts alone can miss thin walls at dash speeds.
            Collider[] own = body.GetComponentsInChildren<Collider>();
            foreach (RaycastHit hit in body.SweepTestAll(direction, travel + skin, QueryTriggerInteraction.Ignore))
            {
                Collider obstacle = hit.collider;
                if (!obstacle || obstacle.attachedRigidbody == body || Vector3.Dot(hit.normal, direction) >= -0.001f) continue;
                bool collides = false;
                foreach (Collider collider in own)
                {
                    if (!collider.enabled || collider.isTrigger || collider.attachedRigidbody != body) continue;
                    if (!Physics.GetIgnoreLayerCollision(collider.gameObject.layer, obstacle.gameObject.layer)
                        && !Physics.GetIgnoreCollision(collider, obstacle)) { collides = true; break; }
                }
                if (collides)
                {
                    allowed = Mathf.Min(allowed, Mathf.Max(0f, hit.distance - skin));
                    bool descendingSupport = slideDown && velocity.y < -.001f && capsule && hit.normal.y >= .7f
                        && hit.point.y <= capsule.bounds.min.y + .45f
                        && !obstacle.GetComponentInParent<TreeBase>() && !obstacle.GetComponentInParent<TreeLog>();
                    if (descendingSupport)
                    {
                        if (hit.distance < supportDistance) { supportDistance = hit.distance; supportNormal = hit.normal; }
                    }
                    else if (!IsDashTerrain(obstacle)) hardObstacle = true;
                }
            }
            if (supportNormal.sqrMagnitude > .1f && !hardObstacle)
            {
                // Landing on the next tread is a ground contact, not the end of a dash.
                // Sweep the tangent as well, so a wall after the tread still stops movement.
                Vector3 tangent = Vector3.ProjectOnPlane(velocity, supportNormal);
                return LimitDashMotionCore(body, tangent, dt, false, out hardObstacle);
            }
            return velocity * (allowed / travel);
        }

        public static void StopAttack(Player player, bool startBlocking = true)
        {
            Attack attack = player.m_currentAttack;
            if (attack == null)
            {
                return;
            }

            attack.Stop();
            player.m_currentAttack = null;
            player.m_previousAttack = null;
            player.m_queuedAttackTimer = 0f;
            player.m_attack = false;
            player.m_attackHold = false;
            player.m_secondaryAttack = false;
            player.m_secondaryAttackHold = false;
            player.m_animEvent.ResetChain();
            player.m_animator.speed = 1f;

            PlayMovementState(player.m_animator);
			if (startBlocking)
			{
				// UpdateBlock owns both the animator and the replicated blocking state.
				player.m_blocking = true;
			}
            player.m_zanim.SetTrigger(AttackCancelTrigger);

            Log.LogDebug("Attack cancelled to start blocking");
        }

        public static void PlayMovementState(Animator animator)
        {
            if (animator != null && animator.HasState(0, MovementState))
            {
                animator.Play(MovementState, 0, 0f);
            }
        }

		[HarmonyPatch(typeof(Attack), nameof(Attack.Start))]
		private static class StaffAttackPatch
		{
			[HarmonyPrefix]
			private static void Prefix(Attack __instance, ItemDrop.ItemData weapon)
			{
				if (OverhaulConfig.FireStaffStraightProjectiles.Value && IsFireStaff(weapon) && !AlterItemStat.Has(weapon, "m_launchAngle"))
				{
					__instance.m_launchAngle = StraightLaunchAngle;
				}
			}
		}

		[HarmonyPatch(typeof(Projectile), nameof(Projectile.Setup))]
		private static class FireStaffProjectilePatch
		{
			[HarmonyPrefix]
			private static void Prefix(Projectile __instance, ref Vector3 velocity, ItemDrop.ItemData item)
			{
				if (!IsFireStaff(item))
				{
					return;
				}

				if (!AlterItemStat.Has(item, "m_projectileVel")) velocity *= OverhaulConfig.FireStaffProjectileSpeedMultiplier.Value;
				if (OverhaulConfig.FireStaffStraightProjectiles.Value)
				{
					__instance.m_gravity = StraightProjectileGravity;
				}
			}
		}

		private static bool IsFireStaff(ItemDrop.ItemData item)
		{
			return item != null && item.m_dropPrefab != null && item.m_dropPrefab.name == FireStaffPrefab;
		}

		[HarmonyPatch(typeof(SE_Shield), nameof(SE_Shield.IsDone))]
		private static class StaffShieldBreakPatch
		{
			[HarmonyPostfix]
			private static void Postfix(SE_Shield __instance, bool __result)
			{
				if (__result)
				{
					OnStaffShieldBroken(__instance);
				}
			}
		}

		[HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.GetIconText))]
		private static class StaffShieldIconTextPatch
		{
			[HarmonyPostfix]
			private static void Postfix(StatusEffect __instance, ref string __result)
			{
				SE_Shield shield = __instance as SE_Shield;
				if (!(shield is Persistence.SE_StaffGuard) && !IsTrackedStaffShield(shield))
				{
					return;
				}

				float remainingDamage = Mathf.Max(0f,
					shield.m_totalAbsorbDamage - shield.m_damage);
				__result = $"{Mathf.CeilToInt(remainingDamage)}/{Mathf.CeilToInt(shield.m_totalAbsorbDamage)}";
			}
		}

		[HarmonyPatch(typeof(SE_Shield), nameof(SE_Shield.OnDamaged))]
		private static class InactiveStaffShieldDamagePatch
		{
			[HarmonyPrefix]
			private static bool Prefix(SE_Shield __instance)
			{
				return !IsTrackedStaffShield(__instance) || staffShieldIsActive;
			}
		}

		[HarmonyPatch(typeof(Hud), "UpdateStatusEffects")]
		private static class StaffShieldHudPatch
		{
			[HarmonyPostfix]
			private static void Postfix(Hud __instance, List<StatusEffect> statusEffects)
			{
				for (int i = 0; i < statusEffects.Count && i < __instance.m_statusEffects.Count; i++)
				{
					RectTransform slot = __instance.m_statusEffects[i];
					Transform markerTransform = slot.Find("OverhaulInactiveShield");
					bool showMarker = statusEffects[i] is Persistence.SE_StaffGuard guard ? !guard.m_guardActive : IsTrackedStaffShield(statusEffects[i] as SE_Shield)
						&& !staffShieldIsActive;

					if (markerTransform == null && showMarker)
					{
						GameObject markerObject = new GameObject("OverhaulInactiveShield",
							typeof(RectTransform), typeof(TextMeshProUGUI));
						RectTransform markerRect = markerObject.GetComponent<RectTransform>();
						markerRect.SetParent(slot, false);
						markerRect.anchorMin = Vector2.zero;
						markerRect.anchorMax = Vector2.one;
						markerRect.offsetMin = Vector2.zero;
						markerRect.offsetMax = Vector2.zero;

						TextMeshProUGUI marker = markerObject.GetComponent<TextMeshProUGUI>();
						TMP_Text referenceText = slot.Find("TimeText").GetComponent<TMP_Text>();
						marker.font = referenceText.font;
						marker.fontSize = 46f;
						marker.fontStyle = FontStyles.Bold;
						marker.alignment = TextAlignmentOptions.Center;
						marker.color = new Color(0.9f, 0.05f, 0.05f, 0.9f);
						marker.raycastTarget = false;
						marker.text = "X";
						markerTransform = markerRect;
					}

					if (markerTransform != null)
					{
						markerTransform.gameObject.SetActive(showMarker);
						markerTransform.SetAsLastSibling();
					}
				}
			}
		}
    }
}
