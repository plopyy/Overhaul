using HarmonyLib;

namespace Overhaul.Persistence
{
    internal static class GameDeathRuntime
    {
        internal static void Publish(Player player,HitData hit)
        {
            if(!player||!player.m_nview||!player.m_nview.IsValid())return;
            GameAttackRuntime.Forget(player.GetZDOID());GameAttackRuntime.ForgetControls(player.GetZDOID());
            player.m_lastHit=hit.Clone();player.m_isDead=true;
            player.m_nview.GetZDO().Set(ZDOVars.s_dead,true);player.SetHealth(0);
            player.CreateDeathEffects();
            player.m_nview.InvokeRPC(ZNetView.Everybody,"OnDeath");
        }
        private static bool Managed(Player player)=>player&&(PlayerSessionGame.Managed&&player==Player.m_localPlayer||
            player.m_nview&&player.m_nview.IsValid()&&InventoryMoveGame.State(player.GetZDOID())!=null);
        [HarmonyPatch(typeof(Character),"CheckDeath")]
        private static class NativeDeathCheck
        {private static bool Prefix(Character __instance)=>!(__instance is Player player)||!Managed(player);}
        [HarmonyPatch(typeof(Player),nameof(Player.OnDeath))]
        private static class NativeDeath
        {private static bool Prefix(Player __instance)=>!Managed(__instance);}
        [HarmonyPatch(typeof(Player),"RPC_OnDeath")]
        private static class DeathOrigin
        {
            private static bool Prefix(long sender)
            {
                if(!PlayerSessionGame.Managed&&!GameCreatureAuthority.Enabled)return true;
                return ZNet.instance&&(ZNet.instance.IsServer()?sender==ZNet.GetUID():sender==ZNet.instance.GetServerPeer()?.m_uid);
            }
        }
    }
}
