using System;
using System.Collections.Generic;
using HarmonyLib;

namespace Overhaul.Persistence
{
    internal static class GameAdrenalineRuntime
    {
        private static bool Queue(Player player,float amount)
        {
            if(!player||!player.m_nview||!player.m_nview.IsValid()||InventoryMoveGame.State(player.GetZDOID())==null)return false;
            if(amount==0||float.IsNaN(amount)||float.IsInfinity(amount))return true;
            return InventoryMoveGame.ServerAction(player.GetZDOID(),state=>
            {
                var rows=GameAdrenaline.Change(state,player,amount);if(rows.Length==0)return null;
                return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(Guid.NewGuid().ToString("N"),state.Revision,rows),new Dictionary<long,ObjectRecord>()),()=>{});
            });
        }
        [HarmonyPatch(typeof(Player),nameof(Player.AddAdrenaline))]
        private static class Gain
        {
            private static bool Prefix(Player __instance,float v,bool __runOriginal)
            {
                if(!__runOriginal)return false;
                if(GameCreatureAuthority.Enabled){Queue(__instance,v);return false;}
                return !PlayerSessionGame.Managed;
            }
        }
        [HarmonyPatch(typeof(Character),"RPC_AddAdrenaline")]
        private static class Rpc
        {
            private static bool Prefix(Character __instance,long sender,float amount)
            {
                if(!(__instance is Player player))return true;
                if(GameCreatureAuthority.Enabled){if(sender==ZNet.GetUID())Queue(player,amount);return false;}
                return !PlayerSessionGame.Managed;
            }
        }
    }
}
