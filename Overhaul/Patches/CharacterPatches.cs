using HarmonyLib;
using System.Reflection;
using System.Collections.Generic;
using System.Reflection.Emit;
using UnityEngine;

namespace Overhaul.Patches
{
    [HarmonyPatch(typeof(Character), nameof(Character.Jump))]
    internal static class PlayerSlopeJumpPatch
    {
        private static Vector3 JumpNormal(Character character)
        {
            return character.IsPlayer() ? Vector3.up : character.m_lastGroundNormal;
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var normal = AccessTools.Field(typeof(Character), "m_lastGroundNormal");
            var replacement = AccessTools.Method(typeof(PlayerSlopeJumpPatch), nameof(JumpNormal));
            foreach (var instruction in instructions)
            {
                // Change only the normal used by Jump, preserving real ground contacts.
                if (instruction.LoadsField(normal))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                }
                yield return instruction;
            }
        }
    }

    [HarmonyPatch(typeof(Character), nameof(Character.GetSlideAngle))]
    internal static class PlayerSlopeSlidePatch
    {
        [HarmonyPostfix]
        private static void Postfix(Character __instance, ref float __result)
        {
            if (__instance.IsPlayer()) __result = 50f;
            else if (Utils.GetPrefabName(__instance.gameObject) == "Hen") __result = 35f;
        }
    }

    [HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyFallDamage))]
    internal static class PlayerFallDamagePatch
    {
        internal static float CalculateDamage(float height)
        {
            if (height <= 4f) return 0f;
            return UnityEngine.Mathf.Min(100f, height <= 10f
                ? (height - 4f) * 2f
                : 12f + (height - 10f) * 6.25f);
        }

        [HarmonyPrefix]
        private static void Prefix(Character ___m_character, ref float baseDamage, ref float damage)
        {
            if (___m_character == null || !___m_character.IsPlayer()) return;
            // Replace the base curve before native status effects modify the damage.
            float height = ___m_character.m_maxAirAltitude - ___m_character.transform.position.y;
            baseDamage = damage = CalculateDamage(height);
        }
    }

    [HarmonyPatch]
    internal class CharacterPatches
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Humanoid), nameof(Humanoid.UpdateEquipment));
            yield return AccessTools.Method(typeof(Humanoid), nameof(Humanoid.EquipItem));
        }

        private static bool EquipmentSwimming(Character character) =>
            !(character.IsPlayer() && character.m_swimTimer < .5f) && character.IsSwimming();

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var original = AccessTools.Method(typeof(Character), nameof(Character.IsSwimming));
            var replacement = AccessTools.Method(typeof(CharacterPatches), nameof(EquipmentSwimming));
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
