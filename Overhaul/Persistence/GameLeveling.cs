using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using Overhaul.Leveling;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameLeveling
    {
        private sealed class Intent{public string Op,Passive;public Dictionary<string,int> Stats;}
        private static bool rulesReady;
        internal static void Initial(){rulesReady=ZNet.instance&&ZNet.instance.IsServer();}
        internal static void Rules(){rulesReady=true;Refresh(Player.m_localPlayer,GameCharacterView.State);}
        internal static OverhaulCharacterData Read(PlayerSnapshot state)
        {
            var row=state.Rows.FirstOrDefault(r=>r.Table=="custom_data"&&(string)r.Values[0]==OverhaulCharacter.SaveKey);
            var data=row==null?new OverhaulCharacterData():JsonConvert.DeserializeObject<OverhaulCharacterData>((string)row.Values[1]);LevelingSystem.Reconcile(data);return data;
        }
        private static PlayerChange Row(OverhaulCharacterData data)=>new PlayerChange("custom_data",false,OverhaulCharacter.SaveKey,JsonConvert.SerializeObject(data));
        internal static void Refresh(Player player,PlayerSnapshot state)
        {
            if(!player||state==null)return;var component=OverhaulCharacter.Get(player);
            component.Data=Read(state);component.InvalidSave=false;component.Ready=GameCreatureAuthority.Enabled||rulesReady;
            player.m_customData[OverhaulCharacter.SaveKey]=JsonConvert.SerializeObject(component.Data);
            if(player==Player.m_localPlayer&&component.Ready){NutritionDuration.Sync(player);NutritionDuration.RefreshTooltip(player);LevelingWindow.RefreshCurrent();}
        }
        internal static void Action(string op,Dictionary<string,int> stats,string passive)
        {InventoryMoveGame.Client?.Leveling(JsonConvert.SerializeObject(new Intent{Op=op,Stats=stats,Passive=passive}));}
        internal static void Receive(ZDO actor,string json)
        {
            if(actor==null||json==null||json.Length>8192)return;
            var request=JsonConvert.DeserializeObject<Intent>(json,new JsonSerializerSettings{MaxDepth=8});
            if(request==null||request.Stats?.Count>128||request.Passive?.Length>128||request.Op!="allocate"&&request.Op!="reset"&&request.Op!="passive")return;
            var instance=ZNetScene.instance.FindInstance(actor.m_uid);var player=instance?instance.GetComponent<Player>():null;
            if(!player)return;
            InventoryMoveGame.TimedAction(player,state=>
            {
                var data=Read(state);bool combat=OverhaulCharacter.Get(player).InCombat;
                bool accepted=request.Op=="allocate"?LevelingSystem.Allocate(data,request.Stats,combat):request.Op=="reset"?LevelingSystem.Reset(data,combat):LevelingSystem.ChoosePassive(data,request.Passive,combat,DateTime.UtcNow.Ticks);
                if(!accepted){player.Message(MessageHud.MessageType.Center,LevelingText.Get("refused"));return null;}
                return Plan(state,data);
            });
        }
        private static PlayerActionPlan Plan(PlayerSnapshot state,OverhaulCharacterData data,Action publish=null)
            =>new PlayerActionPlan(new PlayerWorldAction(new PlayerBatch(Guid.NewGuid().ToString("N"),state.Revision,new[]{Row(data)}),new Dictionary<long,ObjectRecord>()),publish??(()=>{}));
        internal static void Reward(ZDO actor,long amount,string name,int stars)
        {
            if(amount<=0)return;var instance=ZNetScene.instance.FindInstance(actor.m_uid);var player=instance?instance.GetComponent<Player>():null;if(!player)return;
            InventoryMoveGame.TimedAction(player,state=>
            {
                var data=Read(state);int before=data.Level;LevelingSystem.AddExperience(data,amount);int after=data.Level;
                return Plan(state,data,()=>
                {
                    if(!player||!player.m_nview||!player.m_nview.IsValid())return;
                    var packet=new ZPackage();packet.Write(name);packet.Write(stars);packet.Write(amount);packet.Write(before);packet.Write(after);
                    player.m_nview.InvokeRPC("Overhaul_Experience",packet);
                });
            });
        }
        internal static string Admin(ZNetPeer peer,long amount,bool reset)
        {
            var actor=peer==null?Player.m_localPlayer?.m_nview?.GetZDO():PlayerSessionGame.Actor(peer.m_rpc);
            var instance=actor==null?null:ZNetScene.instance.FindInstance(actor.m_uid);var player=instance?instance.GetComponent<Player>():null;
            if(!player)return "Overhaul : personnage serveur indisponible.";
            bool queued=InventoryMoveGame.TimedAction(player,state=>
            {var data=reset?new OverhaulCharacterData():Read(state);if(!reset)LevelingSystem.AdjustExperience(data,amount);return Plan(state,data);});
            return queued?"Overhaul : modification de progression envoyée à la sauvegarde serveur.":"Overhaul : modification refusée.";
        }
        internal static Action Presentation(IEnumerable<PlayerChange> source,Player player)
        {
            var row=source.FirstOrDefault(r=>r.Table=="custom_data"&&(string)r.Values[0]==OverhaulCharacter.SaveKey);
            if(row==null)return ()=>{};
            // Resolve/validate before the inventory presentation mutates anything.
            var state=new PlayerSnapshot(0,new[]{row});Read(state);
            return ()=>Refresh(player,state);
        }
        [HarmonyPatch(typeof(Player),nameof(Player.Awake))]
        private static class RewardView
        {
            private static void Postfix(Player __instance)
            {
                var player=__instance;if(!player.m_nview||!player.m_nview.IsValid())return;
                player.m_nview.Register<ZPackage>("Overhaul_Experience",(sender,packet)=>
                {
                    if(!PlayerSessionGame.Managed||player!=Player.m_localPlayer||sender!=(ZNet.instance.IsServer()?ZNet.GetUID():ZNet.instance.GetServerPeer()?.m_uid))return;
                    ExperienceFeed.Reward(packet.ReadString(),packet.ReadInt(),packet.ReadLong(),packet.ReadInt(),packet.ReadInt());
                });
            }
        }
    }
}
