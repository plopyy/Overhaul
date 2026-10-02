using HarmonyLib;
using UnityEngine;

namespace Overhaul.Patches
{
    [HarmonyPatch(typeof(ZSyncAnimation), "RPC_SetTrigger")]
    internal static class ZSyncAnimationPatches
    {
        [HarmonyPrefix]
        private static bool RPC_SetTrigger_Prefix(string name, Animator ___m_animator)
        {
            if (name != DynamicCombat.AttackCancelTrigger)
            {
                return true;
            }

            DynamicCombat.PlayMovementState(___m_animator);
            return false;
        }
    }
}
