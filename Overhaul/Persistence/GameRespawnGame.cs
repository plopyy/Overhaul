using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace Overhaul.Persistence
{
    internal static class GameRespawnGame
    {
        internal const string Command="player.respawn",After="server_respawn_after";
        private static bool requested,confirmed,restarting;
        internal static void Clear(){requested=false;confirmed=false;restarting=false;}
        internal static bool Intent(InventoryMoveRequest request)=>request?.Gameplay?.Kind==PlayerActionKind.UseOn&&request.Gameplay.Definition==Command;
        internal static PlayerActionPlan Prepare(InventoryMoveRequest request,PlayerSnapshot state)
        {
            if(!Intent(request)||request.Action.Amount!=1||request.Gameplay.TargetId!=0||!GameDeathProgress.IsDead(state))throw new InvalidOperationException("Character is not awaiting respawn");
            var deadline=state.Rows.SingleOrDefault(r=>r.Table=="state"&&(string)r.Values[0]==After);
            if(deadline==null||Convert.ToInt64(deadline.Values[1])>DateTime.UtcNow.Ticks)throw new InvalidOperationException("Respawn delay has not elapsed");
            var definition=Game.instance?Game.instance.m_playerPrefab?.GetComponent<Player>():null;
            if(!definition)throw new InvalidOperationException("Respawn definition is unavailable");
            double health=definition.m_baseHP,stamina=definition.m_baseStamina;
            if(PlayerCraftProgressGame.Passive(state,"vitality"))health+=global::Overhaul.Leveling.LevelingConfig.Current.VitalityHealth;
            var changes=new List<PlayerChange>{new PlayerChange("state",false,GameDeathProgress.Dead,0,null,null,null),new PlayerChange("state",false,After,0L,null,null,null),
                PlayerResources.Row("health",health),PlayerResources.Row("max_health",health),PlayerResources.Row("stamina",stamina),PlayerResources.Row("max_stamina",stamina),
                PlayerResources.Row("eitr",0),PlayerResources.Row("max_eitr",0),PlayerResources.Row(PlayerResources.StaminaDelay,0),PlayerResources.Row(PlayerResources.EitrDelay,0),PlayerResources.Row(PlayerResources.FoodRegen,0)};
            return new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(request.Action.Operation,state.Revision,changes),new Dictionary<long,ObjectRecord>()),()=>{});
        }
        internal static void Reply(InventoryMoveReply reply,InventoryMoveRequest request)
        {
            if(!Intent(request)||reply.Notification)return;
            if(reply.Accepted){confirmed=true;requested=false;}
            else if(!reply.Snapshot||reply.Player.ExpectedRevision<=request.Action.PlayerRevision){requested=false;}
        }
        internal static void DeathConfirmed(Player player)
        {if(PlayerSessionGame.Managed&&player==Player.m_localPlayer){requested=false;confirmed=false;Game.instance.RequestRespawn(10,true);}}
        internal static void Tick()
        {
            var controller=InventoryMoveGame.Client?.Controller;
            if(controller==null||controller.Closed||controller.Busy)return;
            if(confirmed)
            {
                confirmed=false;GameCharacterView.PrepareSpawn();restarting=true;
                try{Game.instance._RequestRespawn();}finally{restarting=false;}
                return;
            }
            if(requested)controller.Act(new PlayerActionCommand{Kind=PlayerActionKind.UseOn,Definition=Command});
        }
        [HarmonyPatch(typeof(Game),"_RequestRespawn")]
        private static class Request
        {
            private static bool Prefix()
            {
                if(!PlayerSessionGame.Managed||restarting||!Game.instance.m_respawnAfterDeath)return true;
                requested=true;return false;
            }
        }
        [HarmonyPatch(typeof(PlayerProfile),nameof(PlayerProfile.SavePlayerData))]
        private static class KeepServerProfile
        {private static bool Prefix()=>!restarting;}
    }
}
