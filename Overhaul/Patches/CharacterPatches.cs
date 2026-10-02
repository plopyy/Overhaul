using HarmonyLib;
using System.Diagnostics;
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

    [HarmonyPatch(typeof(Character))]
    internal class CharacterPatches
    {
        [HarmonyPatch("IsSwimming")]
        [HarmonyPrefix]
        private static bool IsSwimming_Prefix(ref bool __result, Character __instance, float ___m_swimTimer)
        {
            if (___m_swimTimer < 0.5f)
            {
                StackTrace stackTrace = new StackTrace();
                string text = "";
                int num = 2;
                while (num < stackTrace.FrameCount && num < 10)
                {
                    text = text + GeneralExtensions.FullDescription(stackTrace.GetFrame(num).GetMethod()) + "-";
                    num++;
                }
                if (__instance.IsPlayer() && (text.Contains("UpdateEquipment") || text.Contains("EquipItem")))
                {
                    __result = false;
                    return false;
                }
            }
            return true;
        }
    }
}
