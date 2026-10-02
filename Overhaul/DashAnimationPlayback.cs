using System;
using System.Collections.Generic;
using UnityEngine;

namespace Overhaul
{
    internal static class DashAnimationPlayback
    {
        private sealed class Playback
        {
            internal Player Player;
            internal DashAnimationPose Pose;
            internal float Started, Duration;
            internal float MovementDuration, Interrupted = -1f;
            internal bool WasGrounded;
            internal string Direction;
        }
        private static readonly Dictionary<Player, Playback> Active = new Dictionary<Player, Playback>();
        private static readonly List<Player> Finished = new List<Player>();
        private static bool failed;

        internal static void Start(Player player, Vector3 direction, float duration)
        {
            if (failed || !player || duration <= 0f) return;
            try
            {
                Playback playback;
                if (!Active.TryGetValue(player, out playback))
                {
                    playback = new Playback { Player = player, Pose = new DashAnimationPose(player.m_animator) };
                    Active.Add(player, playback);
                }
                Vector3 local = player.transform.InverseTransformDirection(direction);
                playback.Direction = Mathf.Abs(local.x) > Mathf.Abs(local.z)
                    ? (local.x < 0f ? "Left" : "Right") : (local.z < 0f ? "Back" : "Forward");
                playback.Started = Time.time;
                // Preserve the animation speed from the slow-motion test while
                // physics returns to its original duration (0.2s -> 1s visually).
                playback.Duration = duration * 5f;
                playback.MovementDuration = duration;
                playback.Interrupted = -1f;
                playback.WasGrounded = IsGrounded(player);
            }
            catch (Exception error)
            {
                failed = true;
                Release();
                Debug.LogWarning("[Overhaul] Dash animation unavailable: " + error);
            }
        }

        internal static void Update()
        {
            Finished.Clear();
            foreach (var pair in Active)
            {
                Playback p = pair.Value;
                float elapsed = Time.time - p.Started;
                if (!p.Player || p.Player.IsDead() || p.Player.m_animator != p.Pose.Target || elapsed >= p.Duration + 0.08f)
                {
                    Finished.Add(pair.Key);
                    continue;
                }
                bool grounded = IsGrounded(p.Player);
                bool landed = !p.WasGrounded && grounded;
                bool newJump = p.WasGrounded && !grounded && elapsed > p.MovementDuration;
                p.WasGrounded = grounded;
                if (p.Interrupted < 0f && (landed || newJump || HasPriorityAction(p.Player)
                    || (elapsed >= p.MovementDuration && grounded && p.Player.m_moveDir.sqrMagnitude > 0.01f)))
                    p.Interrupted = Time.time;
                float fadeStart = p.Interrupted >= 0f ? p.Interrupted : p.Started + p.Duration;
                float fadeDuration = p.Interrupted >= 0f ? 0.05f : 0.08f;
                float weight = Mathf.Clamp01(elapsed / 0.035f)
                    * Mathf.Clamp01(1f - (Time.time - fadeStart) / fadeDuration);
                if (p.Interrupted >= 0f && Time.time >= fadeStart + fadeDuration)
                {
                    Finished.Add(pair.Key);
                    continue;
                }
                try { p.Pose.Apply("HugeStep", p.Direction, elapsed / p.Duration, weight); }
                catch (Exception error)
                {
                    Debug.LogWarning("[Overhaul] Dash pose stopped: " + error);
                    Finished.Add(pair.Key);
                }
            }
            foreach (Player player in Finished)
            {
                Active[player].Pose.Dispose();
                Active.Remove(player);
            }
        }

        internal static void Release()
        {
            foreach (Playback playback in Active.Values) playback.Pose.Dispose();
            Active.Clear();
        }

        private static bool IsGrounded(Player player)
        {
            // Remote characters receive the grounded animation parameter over the network.
            return player.m_nview != null && player.m_nview.IsValid() && !player.m_nview.IsOwner()
                ? player.m_animator.GetBool("onGround") : player.IsOnGround();
        }

        private static bool HasPriorityAction(Player player)
        {
            return player.InAttack() || player.IsDrawingBow() || player.IsBlocking() || player.m_blocking
                || player.InDodge() || player.IsStaggering() || player.IsKnockedBack()
                || player.InMinorAction() || player.InEmote() || player.IsSitting()
                || player.IsAttached() || player.IsSwimming() || player.IsTeleporting();
        }
    }
}
