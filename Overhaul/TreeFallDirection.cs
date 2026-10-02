using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Overhaul
{
    internal static class TreeFallDirection
    {
        [ThreadStatic] internal static Vector3? ChopDirection;

        internal static Vector3 Away(Vector3 tree, Vector3 player, Vector3 fallback)
        {
            var direction = Vector3.ProjectOnPlane(tree - player, Vector3.up);
            if (direction.sqrMagnitude < .0001f) direction = Vector3.ProjectOnPlane(fallback, Vector3.up);
            return direction.sqrMagnitude > .0001f ? direction.normalized : Vector3.zero;
        }

        [HarmonyPatch(typeof(TreeBase), "RPC_Damage")]
        internal static class ChopScope
        {
            private static void Prefix(TreeBase __instance, HitData hit, out Vector3? __state)
            {
                __state = ChopDirection;
                ChopDirection = null;
                var attacker = hit.GetAttacker();
                if (attacker is Player && hit.m_damage.m_chop > 0f)
                {
                    var direction = Away(__instance.transform.position, attacker.transform.position, hit.m_dir);
                    if (direction != Vector3.zero) ChopDirection = direction;
                }
            }
            private static Exception Finalizer(Vector3? __state, Exception __exception)
            {
                ChopDirection = __state;
                return __exception;
            }
        }

        internal static void PushLog(Rigidbody body, Vector3 force, Vector3 point, ForceMode mode)
        {
            if (ChopDirection.HasValue)
            {
                // Redirect the native impulse without adding force or angular speed.
                force = ChopDirection.Value * force.magnitude;
            }
            body.AddForceAtPosition(force, point, mode);
        }

        [HarmonyPatch(typeof(TreeBase), "SpawnLog")]
        internal static class FallingLog
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var original = AccessTools.Method(typeof(Rigidbody), nameof(Rigidbody.AddForceAtPosition),
                    new[] { typeof(Vector3), typeof(Vector3), typeof(ForceMode) });
                var replacement = AccessTools.Method(typeof(TreeFallDirection), nameof(PushLog));
                foreach (var instruction in instructions)
                {
                    if (instruction.Calls(original))
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = replacement;
                    }
                    yield return instruction;
                }
            }
        }
    }
}
