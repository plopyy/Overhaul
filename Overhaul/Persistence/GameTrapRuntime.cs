using System.Collections.Generic;
using System.Globalization;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameTrapRuntime
    {
        private static Player TriggeringPlayer(Collider collider)=>GameCreatureAuthority.Enabled?collider.GetComponentInParent<Player>():Player.m_localPlayer;
        private static void Count(Player player,PlayerStatType statistic)
        {if(GameMovementRuntime.Managed(player))InventoryMoveGame.Progress(player.GetZDOID(),state=>new[]{PlayerCraftProgressGame.Increment(state,"statistics:0:values",((int)statistic).ToString(CultureInfo.InvariantCulture),1)});}
        [HarmonyPatch(typeof(Trap),"OnTriggerEnter")]
        private static class Contact
        {
            private static bool Prefix(Trap __instance)=>GameCreatureAuthority.Enabled?__instance.m_nview&&__instance.m_nview.IsValid()&&!GamePersistence.ActionReserved(__instance.m_nview.GetZDO().m_uid):!PlayerSessionGame.Managed;
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                foreach(var code in instructions)
                {
                    if(code.opcode==OpCodes.Ldsfld&&Equals(code.operand,AccessTools.Field(typeof(Player),nameof(Player.m_localPlayer))))
                    {yield return new CodeInstruction(OpCodes.Ldarg_1){labels=code.labels,blocks=code.blocks};yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(GameTrapRuntime),nameof(TriggeringPlayer)));}
                    else yield return code;
                }
            }
        }
        [HarmonyPatch(typeof(Trap),"RPC_RequestStateChange")]
        private static class Request
        {
            private static bool Prefix(Trap __instance,long senderID,int value,out Player __state)
            {
                __state=null;if(!GameCreatureAuthority.Enabled)return true;
                if(!__instance.m_nview||!__instance.m_nview.IsValid()||GamePersistence.ActionReserved(__instance.m_nview.GetZDO().m_uid))return false;
                if(senderID==ZNet.GetUID())return true;
                if(value!=(int)Trap.TrapState.Armed||__instance.IsArmed()||__instance.IsCoolingDown())return false;
                var actor=GameWorldInteraction.Nearby(__instance,senderID);if(actor==null)return false;
                __state=ZNetScene.instance.FindInstance(actor.m_uid)?.GetComponent<Player>();return true;
            }
            private static void Postfix(Trap __instance,Player __state){if(__state&&__instance.IsArmed())Count(__state,PlayerStatType.TrapArmed);}
        }
        [HarmonyPatch(typeof(Trap),"RPC_OnStateChanged")]
        private static class State
        {
            private static bool Prefix(Trap __instance,long uid,int value,long idOfClientModifyingState)
            {
                if(!GameCreatureAuthority.Enabled)return true;
                if(uid!=ZNet.GetUID()||!__instance.m_nview||!__instance.m_nview.IsValid())return false;
                __instance.m_onReceiveOwnershipActions.Clear();
                if(value==(int)Trap.TrapState.Active)
                {
                    var player=__instance.m_tempTriggeringHumanoid as Player;
                    __instance.TriggerTrap();Count(player,PlayerStatType.TrapTriggered);
                    if(GameMovementRuntime.Managed(player))GameMovementRuntime.Record(player);
                }
                else
                {
                    if(value==(int)Trap.TrapState.Armed&&idOfClientModifyingState==ZNet.GetUID())
                    {__instance.m_armEffects.Create(__instance.transform.position,__instance.transform.rotation);Count(Player.m_localPlayer,PlayerStatType.TrapArmed);}
                    __instance.m_tempTriggeringHumanoid=null;
                }
                __instance.UpdateState((Trap.TrapState)value);return false;
            }
        }
    }
}
