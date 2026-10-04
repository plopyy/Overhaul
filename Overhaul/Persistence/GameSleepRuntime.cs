using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameSleepRuntime
    {
        private static Player[] Players()=>PlayerSessionGame.ActiveActors().Select(actor=>ZNetScene.instance.FindInstance(actor.m_uid)?.GetComponent<Player>()).Where(player=>player).ToArray();
        private static PlayerActionPlan Wake(PlayerSnapshot state,Player player)
        {
            if(!player||GameDeathProgress.IsDead(state))return null;
            var rows=new List<PlayerChange>(GameStatusImpact.Prepare(state,player,SEMan.s_statusEffectRested,0,0,-1,ZDOID.None));
            rows.Add(PlayerCraftProgressGame.Increment(state,"statistics:0:values",((int)PlayerStatType.Sleep).ToString(CultureInfo.InvariantCulture),1));
            return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(Guid.NewGuid().ToString("N"),state.Revision,rows),new Dictionary<long,ObjectRecord>()),()=>
            {if(player){GameAttachmentRuntime.Sleeping(player,false);player.Message(MessageHud.MessageType.Center,"$msg_goodmorning");}});
        }
        [HarmonyPatch(typeof(Game),"UpdateSleeping")]
        private static class Update
        {
            private static bool Prefix(Game __instance)
            {
                if(!GameCreatureAuthority.Enabled)return true;
                if(!EnvMan.instance)return false;
                if(__instance.m_sleeping)
                {
                    if(EnvMan.instance.IsTimeSkipping())return false;
                    __instance.m_lastSleepTime=ZNet.instance.GetTimeSeconds();__instance.m_sleeping=false;
                    foreach(var player in Players())if(player.m_sleeping)InventoryMoveGame.TimedAction(player,state=>Wake(state,player));
                    foreach(var piece in WearNTear.GetAllInstances().ToArray())if(piece)piece.OnSleep();
                    return false;
                }
                if(EnvMan.instance.IsTimeSkipping()||!EnvMan.IsAfternoon()&&!EnvMan.IsNight()||ZNet.instance.GetTimeSeconds()-__instance.m_lastSleepTime<10)return false;
                var players=Players();
                if(players.Length==0||players.Any(player=>player.IsDead()||!GameAttachmentRuntime.Bed(player.GetZDOID())))return false;
                EnvMan.instance.SkipToMorning();__instance.m_sleeping=true;
                foreach(var player in players)GameAttachmentRuntime.Sleeping(player,true);
                return false;
            }
        }
        // Furniture confirmations carry the individual sleep state. Native global
        // callbacks would grant local stats/buffs and trigger another full save.
        [HarmonyPatch(typeof(Game),"SleepStart")]
        private static class Start
        {private static bool Prefix()=>!PlayerSessionGame.Managed&&!GameCreatureAuthority.Enabled;}
        [HarmonyPatch(typeof(Game),"SleepStop")]
        private static class Stop
        {private static bool Prefix()=>!PlayerSessionGame.Managed&&!GameCreatureAuthority.Enabled;}
    }
}
