using System.Collections.Generic;
using HarmonyLib;
using Overhaul.Utility;

namespace Overhaul.Patches
{
    [HarmonyPatch(typeof(Humanoid))]
    internal static class HumanoidPatches
    {
        // Keep native ownership, action and animation checks; only remove the weight veto for players.
        private static bool WeightPreventsBlocking(Character character) => !(character is Player) && character.IsEncumbered();

        [HarmonyPatch(nameof(Humanoid.IsBlocking))]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> IsBlocking_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var check=AccessTools.Method(typeof(Character),nameof(Character.IsEncumbered));
            var replacement=AccessTools.Method(typeof(HumanoidPatches),nameof(WeightPreventsBlocking));
            foreach(var instruction in instructions)
            {
                if(instruction.Calls(check))
                {
                    instruction.opcode=System.Reflection.Emit.OpCodes.Call;
                    instruction.operand=replacement;
                }
                yield return instruction;
            }
        }

        [HarmonyPatch("BlockAttack")]
        [HarmonyPrefix]
        private static void BlockAttack_Prefix(Humanoid __instance, out float __state)
        {
            __state = __instance.m_blockStaminaDrain;
            if (__instance is Player)
            {
                __instance.m_blockStaminaDrain = OverhaulConfig.BlockUseStamina.Value
                    ? OverhaulConfig.BlockStaminaDrain.Value
                    : 0f;
            }
        }

        [HarmonyPatch("BlockAttack")]
        [HarmonyPostfix]
        private static void BlockAttack_Postfix(Humanoid __instance, float __state)
        {
            __instance.m_blockStaminaDrain = __state;
        }

        [HarmonyPatch(nameof(Humanoid.EquipItem))]
        [HarmonyPostfix]
        private static void EquipItem_Postfix(bool __result, Humanoid __instance, ItemDrop.ItemData item)
        {
            if (!__result || !(__instance is Player) || item == null
                || item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.OneHandedWeapon
                || __instance.GetLeftItem() != null) return;

            List<ItemDrop.ItemData> items = __instance.m_inventory.GetHotbar();
            ItemDrop.ItemData shield = items.Find(candidate => candidate.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield);
            if (shield == null)
            {
                shield = __instance.m_inventory.GetAllItems().Find(candidate => candidate.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield);
            }
            if (shield != null) __instance.EquipItem(shield, false);
        }
    }
}
