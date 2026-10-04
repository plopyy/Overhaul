using System;
using System.Collections.Generic;
using HarmonyLib;

namespace Overhaul.Persistence
{
    internal static class GamePlayerDamageRuntime
    {
        private static void Heal(Player player,float amount)
        {
            if(amount<=0||float.IsNaN(amount)||float.IsInfinity(amount))return;
            InventoryMoveGame.TimedAction(player,state=>
            {
                if(PlayerResources.Read(state,"health")<=0||GameDeathProgress.IsDead(state))return null;
                var rows=PlayerResources.Restore(state,amount,0,0);
                return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(Guid.NewGuid().ToString("N"),state.Revision,rows),new Dictionary<long,ObjectRecord>()),()=>{});
            });
        }
        private static bool Route(Character character,HitData hit,bool direct)
        {
            if(!(character is Player player))return true;
            if(GameCreatureAuthority.Enabled)
            {
                if(player.m_nview&&player.m_nview.IsValid()&&InventoryMoveGame.State(player.GetZDOID())!=null)
                {
                    var copy=hit.Clone();if(!direct)copy.m_weakSpot=player.FindWeakSpotIndex(hit.m_hitCollider);
                    InventoryMoveGame.Damage(player,copy,direct);
                }
                return false;
            }
            return !PlayerSessionGame.Managed;
        }
        [HarmonyPatch(typeof(Character),nameof(Character.Damage))]
        private static class Impact
        {private static bool Prefix(Character __instance,HitData hit,bool __runOriginal)=>!__runOriginal||Route(__instance,hit,false);}
        [HarmonyPatch(typeof(Character),nameof(Character.ApplyDamage))]
        private static class Direct
        {private static bool Prefix(Character __instance,HitData hit,bool __runOriginal)=>!__runOriginal||Route(__instance,hit,true);}
        [HarmonyPatch(typeof(Character),"RPC_Damage")]
        private static class NativeRpc
        {
            private static bool Prefix(Character __instance,long sender,HitData hit)
            {
                if(!(__instance is Player))return true;
                if(GameCreatureAuthority.Enabled)
                {if(sender==ZNet.GetUID())Route(__instance,hit,false);return false;}
                return !PlayerSessionGame.Managed;
            }
        }
        [HarmonyPatch(typeof(Character),nameof(Character.Heal))]
        private static class Healing
        {
            private static bool Prefix(Character __instance,float hp,bool __runOriginal)
            {
                if(!__runOriginal)return false;if(!(__instance is Player player))return true;
                if(GameCreatureAuthority.Enabled){Heal(player,hp);return false;}
                return !PlayerSessionGame.Managed;
            }
        }
        [HarmonyPatch(typeof(Character),"RPC_Heal")]
        private static class HealingRpc
        {
            private static bool Prefix(Character __instance,long sender,float hp)
            {
                if(!(__instance is Player player))return true;
                if(GameCreatureAuthority.Enabled){if(sender==ZNet.GetUID())Heal(player,hp);return false;}
                return !PlayerSessionGame.Managed;
            }
        }
    }
}
