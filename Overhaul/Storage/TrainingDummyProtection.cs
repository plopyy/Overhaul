using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace Overhaul.Storage
{
    internal static class TrainingDummyProtection
    {
        private static readonly int Prefab = "piece_TrainingDummy".GetStableHashCode();
        internal static readonly int HammerRemoval = "overhaul_training_dummy_hammer_remove".GetStableHashCode();
        internal static Character ReceivingAttack;
        internal static bool IsDummy(Character character) => character && character.GetComponent<Piece>() &&
            character.m_nview && character.m_nview.IsValid() && character.m_nview.GetZDO().GetPrefab() == Prefab;

        // Called only from the native hammer removal branch, after its normal
        // permissions, range and build-station checks. The marker survives the RPC.
        internal static void RemoveWithHammer(Character character, HitData hit)
        {
            if (IsDummy(character)) hit.m_statusEffectHash = HammerRemoval;
            character.Damage(hit);
        }
    }

    [HarmonyPatch(typeof(Player), "RemovePiece")]
    internal static class TrainingDummyHammerPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var damage = AccessTools.Method(typeof(Character), nameof(Character.Damage));
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(damage))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(TrainingDummyProtection), nameof(TrainingDummyProtection.RemoveWithHammer));
                }
                yield return instruction;
            }
        }
    }

    [HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
    internal static class TrainingDummyDamagePatch
    {
        private static void Prefix(Character __instance, HitData hit, out Character __state)
        {
            __state = TrainingDummyProtection.ReceivingAttack;
            TrainingDummyProtection.ReceivingAttack = TrainingDummyProtection.IsDummy(__instance) &&
                __instance.m_nview.IsOwner() && hit.m_statusEffectHash != TrainingDummyProtection.HammerRemoval ? __instance : null;
        }
        private static void Finalizer(Character __state) => TrainingDummyProtection.ReceivingAttack = __state;
    }

    [HarmonyPatch(typeof(Character), nameof(Character.SetHealth))]
    internal static class TrainingDummyHealPatch
    {
        private static void Prefix(Character __instance, ref float health)
        {
            if (__instance == TrainingDummyProtection.ReceivingAttack)
                health = __instance.GetMaxHealth();
        }
    }

    [HarmonyPatch(typeof(Piece), nameof(Piece.DropResources))]
    internal static class TrainingDummyNoAttackRefundPatch
    {
        private static bool Prefix(Piece __instance) => !TrainingDummyProtection.ReceivingAttack ||
            __instance.GetComponent<Character>() != TrainingDummyProtection.ReceivingAttack;
    }
}
