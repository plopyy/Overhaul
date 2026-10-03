using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace Overhaul.Leveling
{
    internal static class LevelingNetwork
    {
        private const string Rpc="Overhaul_Leveling";
        private sealed class Packet
        {
            public string Op, Json, Passive, AdminMessage, WeaponSpeeds, MobBehaviors;
            public OverhaulCharacterData Data;
            public Dictionary<string,int> Stats;
            public string Victim;
            public string RewardName;
            public int RewardStars;
            public long RewardAmount;
            public List<string> Participants;
        }
        private sealed class Session { internal OverhaulCharacterData Data; internal float CombatUntil; }
        private static readonly Dictionary<long,Session> Sessions=new Dictionary<long,Session>();
        private static readonly HashSet<ZDOID> Rewarded=new HashSet<ZDOID>();
        private static ZDOMan world;
        private static float nextRewardCleanup;
        private static void EnsureWorld()
        { if(world==ZDOMan.instance)return;world=ZDOMan.instance;Sessions.Clear();Rewarded.Clear();nextRewardCleanup=0; }
        private static void PruneRewards()
        {
            if(world==null || Time.realtimeSinceStartup<nextRewardCleanup)return;
            nextRewardCleanup=Time.realtimeSinceStartup+60f;
            // Missing ZDOs cannot pass Reward's validation again. Keep the guard
            // for every surviving ZDO, including unloaded but persistent objects.
            Rewarded.RemoveWhere(id=>world.GetZDO(id)==null);
        }
        internal static void Register(ZNetPeer peer)=>peer.m_rpc.Register<string>(Rpc,Receive);
        // Entry point for authenticated admin commands only; never exposed as a player action.
        internal static string AdminReset(ZNetPeer peer)
        {
            if(!ZNet.instance || !ZNet.instance.IsServer())return "Overhaul : serveur requis.";
            EnsureWorld();string name;
            if(peer==null)
            {
                var state=OverhaulCharacter.Get(Player.m_localPlayer);
                if(state==null || !state.Ready || state.InvalidSave)return "Overhaul : progression du joueur pas encore chargee.";
                state.Data=new OverhaulCharacterData();state.CombatUntil=0;state.Store();
                if(LevelingWindow.Current)LevelingWindow.Current.Close();
                name=Player.m_localPlayer.GetPlayerName();
            }
            else
            {
                if(!peer.IsReady() || peer.m_characterID.IsNone() || !Sessions.TryGetValue(peer.m_uid,out Session session))return "Overhaul : progression du joueur pas encore chargee.";
                session.Data=new OverhaulCharacterData();session.CombatUntil=0;
                Send(peer.m_rpc,new Packet{Op="admin_reset",Data=session.Data});name=peer.m_playerName;
            }
            string message="Overhaul : progression remise a zero pour "+name+" (niveau 1, EXP 0, points 0, statistiques et passive effacees).";
            Utility.Log.LogInfo(message);return message;
        }
        internal static string AdminExperience(ZNetPeer peer,long amount)
        {
            if(!ZNet.instance || !ZNet.instance.IsServer())return "Overhaul : serveur requis.";
            if(amount==0)return "Overhaul : montant d'EXP non nul requis.";
            EnsureWorld();OverhaulCharacter state=null;OverhaulCharacterData data;string name;
            if(peer==null)
            {
                state=OverhaulCharacter.Get(Player.m_localPlayer);
                if(state==null || !state.Ready || state.InvalidSave)return "Overhaul : progression du joueur pas encore chargee.";
                data=state.Data;name=Player.m_localPlayer.GetPlayerName();
            }
            else
            {
                if(!peer.IsReady() || peer.m_characterID.IsNone() || !Sessions.TryGetValue(peer.m_uid,out Session session))return "Overhaul : progression du joueur pas encore chargee.";
                data=session.Data;name=peer.m_playerName;
            }
            int previousLevel=data.Level;int allocated=data.AllocatedStats?.Values.Sum()??0;string passive=data.Passive;
            long applied=LevelingSystem.AdjustExperience(data,amount);
            string message="Overhaul : "+name+" : EXP "+(applied>=0?"+":"")+applied+", total "+data.TotalExperience+", niveau "+data.Level+".";
            if(data.Level<previousLevel || (allocated>0 && data.AllocatedStats.Count==0))message+=" Statistiques remises a zero ; "+data.AvailableStatPoints+" points disponibles.";
            if(!string.IsNullOrEmpty(passive) && data.Passive=="")message+=" Passive retiree : niveau requis non atteint.";
            if(state!=null){state.Store();LevelingWindow.RefreshCurrent();}
            else Send(peer.m_rpc,new Packet{Op="admin_exp",Data=data,AdminMessage=message});
            Utility.Log.LogInfo(message);return message;
        }
        private static void Send(ZRpc rpc, Packet packet)=>rpc.Invoke(Rpc,JsonConvert.SerializeObject(packet));
        internal static void Hello()
        { var peer=ZNet.instance.GetServerPeer();if(peer!=null && peer.IsReady())Send(peer.m_rpc,new Packet{Op="hello"}); }
        internal static void Action(string op, Dictionary<string,int> values=null,string passive=null)
        {
            var state=OverhaulCharacter.Get(Player.m_localPlayer);if(state==null || !state.Ready)return;
            var packet=new Packet{Op=op,Stats=values,Passive=passive};
            if(ZNet.instance.IsServer())
            {
                if(Apply(state.Data,packet,state.InCombat)){state.Store();LevelingWindow.RefreshCurrent();}
                else Player.m_localPlayer.Message(MessageHud.MessageType.Center,LevelingText.Get("refused"));
            }
            else Send(ZNet.instance.GetServerPeer().m_rpc,packet);
        }
        private static bool Apply(OverhaulCharacterData data,Packet packet,bool combat)
        {
            if(packet.Op=="allocate")return LevelingSystem.Allocate(data,packet.Stats,combat);
            if(packet.Op=="reset")return LevelingSystem.Reset(data,combat);
            if(packet.Op=="passive")return LevelingSystem.ChoosePassive(data,packet.Passive,combat,DateTime.UtcNow.Ticks);
            return false;
        }
        internal static void MarkCombat(Player player)
        {
            if(!player)return;EnsureWorld();
            var state=OverhaulCharacter.Get(player);state.CombatUntil=Time.time+(float)LevelingConfig.Current.CombatSeconds;
            if(ZNet.instance && ZNet.instance.IsServer())
            {
                ZNetPeer peer=ZNet.instance.GetPeers().FirstOrDefault(p=>p.m_characterID==player.GetZDOID());
                if(peer!=null && Sessions.TryGetValue(peer.m_uid,out Session session))session.CombatUntil=state.CombatUntil;
            }
            else if(player==Player.m_localPlayer && ZNet.instance?.GetServerPeer()!=null)
                Send(ZNet.instance.GetServerPeer().m_rpc,new Packet{Op="combat"});
        }
        internal static void Death(Character victim, List<ZDOID> participants)
        {
            if(!victim.m_nview || !victim.m_nview.IsOwner() || victim.IsPlayer() || victim.IsTamed())return;
            var packet=new Packet{Op="death",Victim=victim.GetZDOID().ToString(),Participants=participants.Select(id=>id.ToString()).ToList()};
            if(ZNet.instance.IsServer())Reward(packet,ZNet.GetUID());
            else Send(ZNet.instance.GetServerPeer().m_rpc,packet);
        }
        internal static void BirdDeath(Destructible victim, HitData hit)
        {
            var view=victim.m_nview;
            if(hit==null || victim.m_destroyed || !view || !view.IsValid() || !view.IsOwner() ||
                !LevelingConfig.IsBasicBird(Utils.GetPrefabName(victim.gameObject)) || !(hit.GetAttacker() is Player attacker))return;
            // Called before native destruction removes the victim's authoritative ZDO.
            var packet=new Packet{Op="death",Victim=view.GetZDO().m_uid.ToString(),Participants=new List<string>{attacker.GetZDOID().ToString()}};
            if(ZNet.instance.IsServer())Reward(packet,ZNet.GetUID());
            else if(ZNet.instance.GetServerPeer()!=null)Send(ZNet.instance.GetServerPeer().m_rpc,packet);
        }
        private static void Reward(Packet packet,long owner)
        {
            EnsureWorld();PruneRewards();ZDOID victimId=ParseId(packet.Victim);ZDO victim=world.GetZDO(victimId);
            // Native monster simulation belongs to its owning peer. Verify that ownership,
            // rather than requiring its final health replication to beat this reliable RPC.
            if(victim==null || victim.GetOwner()!=owner || victim.GetBool(ZDOVars.s_tamed,false) || Rewarded.Contains(victimId))return;
            GameObject prefab=ZNetScene.instance.GetPrefab(victim.GetPrefab());
            if(!prefab || prefab.GetComponent<Player>())return;
            long reward=LevelingConfig.Reward(prefab.name,victim.GetInt(ZDOVars.s_level,1));
            if(reward<=0)return;
            var ids=new HashSet<ZDOID>((packet.Participants??new List<string>()).Take(100).Select(ParseId));
            var targets=ZNet.instance.GetPeers().Where(p=>p.IsReady() && ids.Contains(p.m_characterID) && Sessions.ContainsKey(p.m_uid)
                && world.GetZDO(p.m_characterID)!=null && Vector3.Distance(world.GetZDO(p.m_characterID).GetPosition(),victim.GetPosition())<=LevelingConfig.Current.RewardRange).ToList();
            Player local=Player.m_localPlayer;
            bool includeLocal=local && ids.Contains(local.GetZDOID()) && Vector3.Distance(local.transform.position,victim.GetPosition())<=LevelingConfig.Current.RewardRange;
            int count=targets.Count+(includeLocal?1:0);if(count==0)return;
            Rewarded.Add(victimId);
            long each=reward/count, remainder=reward%count;
            foreach(var peer in targets.OrderBy(p=>p.m_uid))
            {
                var data=Sessions[peer.m_uid].Data;long amount=each+(remainder-->0?1:0);
                LevelingSystem.AddExperience(data,amount);
                Send(peer.m_rpc,new Packet{Op="state",Data=data,RewardName=prefab.GetComponent<Character>()?.m_name??prefab.name,
                    RewardStars=Math.Max(0,victim.GetInt(ZDOVars.s_level,1)-1),RewardAmount=amount});
            }
            if(includeLocal)
            {
                var state=OverhaulCharacter.Get(local);int before=state.Level;long amount=each+(remainder>0?1:0);
                LevelingSystem.AddExperience(state.Data,amount);state.Store();LevelingWindow.RefreshCurrent();
                ExperienceFeed.Reward(prefab.GetComponent<Character>()?.m_name??prefab.name,Math.Max(0,victim.GetInt(ZDOVars.s_level,1)-1),amount,before,state.Level);
            }
        }
        private static void Receive(ZRpc rpc,string json)
        {
            if(json==null || json.Length>262144 || !ZNet.instance)return;
            try
            {
                EnsureWorld();var packet=JsonConvert.DeserializeObject<Packet>(json);if(packet==null)return;
                if(!ZNet.instance.IsServer())
                {
                    if(ZNet.instance.GetServerPeer()?.m_rpc!=rpc)return;
                    var state=OverhaulCharacter.Get(Player.m_localPlayer);if(state==null || state.InvalidSave)return;
                    if(packet.Op=="rules")
                    {
                        if(packet.WeaponSpeeds!=null)Utility.WeaponAttackSpeeds.Current=Utility.WeaponAttackSpeeds.Parse(packet.WeaponSpeeds);
                        if(packet.MobBehaviors!=null)AI.MobBehaviorConfig.Current=AI.MobBehaviorConfig.Parse(packet.MobBehaviors);
                        LevelingConfig.Current=JsonConvert.DeserializeObject<LevelingRules>(packet.Json);
                        state.Reconcile();Send(rpc,new Packet{Op="profile",Data=state.Data});
                    }
                    if((packet.Op=="state" || packet.Op=="admin_reset" || packet.Op=="admin_exp") && packet.Data!=null)
                    {
                        int before=state.Level;
                        state.Data=packet.Data;state.Ready=true;
                        if(packet.RewardAmount>0)ExperienceFeed.Reward(packet.RewardName,packet.RewardStars,packet.RewardAmount,before,state.Level);
                        if(packet.Op=="admin_reset")
                        {
                            state.CombatUntil=0;if(LevelingWindow.Current)LevelingWindow.Current.Close();
                            Player.m_localPlayer.Message(MessageHud.MessageType.Center,LevelingText.Get("admin_reset"));
                        }
                        state.Store();LevelingWindow.RefreshCurrent();
                        if(packet.Op=="admin_exp" && Console.instance)Console.instance.AddString(packet.AdminMessage);
                    }
                    if(packet.Op=="refused")Player.m_localPlayer.Message(MessageHud.MessageType.Center,LevelingText.Get("refused"));
                    return;
                }
                ZNetPeer peer=ZNet.instance.GetPeer(rpc);if(peer==null || !peer.IsReady())return;
                if(packet.Op=="hello"){Send(rpc,new Packet{Op="rules",Json=JsonConvert.SerializeObject(LevelingConfig.Current),WeaponSpeeds=Utility.WeaponAttackSpeeds.Format(Utility.WeaponAttackSpeeds.Current),MobBehaviors=AI.MobBehaviorConfig.Format(AI.MobBehaviorConfig.Current)});return;}
                if(packet.Op=="death"){Reward(packet,peer.m_uid);return;}
                if(packet.Op=="profile" && !Sessions.ContainsKey(peer.m_uid) && packet.Data!=null)
                {LevelingSystem.Reconcile(packet.Data);Sessions[peer.m_uid]=new Session{Data=packet.Data};Send(rpc,new Packet{Op="state",Data=packet.Data});return;}
                if(!Sessions.TryGetValue(peer.m_uid,out Session session))return;
                if(packet.Op=="profile"){Send(rpc,new Packet{Op="state",Data=session.Data});return;}
                if(packet.Op=="combat"){session.CombatUntil=Time.time+(float)LevelingConfig.Current.CombatSeconds;return;}
                if(peer.m_characterID.IsNone() || world.GetZDO(peer.m_characterID)==null)return;
                bool accepted=Apply(session.Data,packet,Time.time<session.CombatUntil);
                Send(rpc,new Packet{Op=accepted?"state":"refused",Data=accepted?session.Data:null});
            }
            catch(Exception e){Utility.Log.LogWarning("Rejected leveling message: "+e.Message);}
        }
        internal static void Disconnect(ZNetPeer peer){Sessions.Remove(peer.m_uid);}
        private static ZDOID ParseId(string value)
        {
            string[] parts=(value??"").Split(':');
            if(parts.Length!=2 || !long.TryParse(parts[0],out long user) || !uint.TryParse(parts[1],out uint id))throw new FormatException("Invalid network object id");
            return new ZDOID(user,id);
        }
    }
    [HarmonyPatch(typeof(ZNet),"OnNewConnection")]
    internal static class LevelingConnectionPatch { private static void Postfix(ZNetPeer peer)=>LevelingNetwork.Register(peer); }
    [HarmonyPatch(typeof(ZNet),nameof(ZNet.Disconnect))]
    internal static class LevelingDisconnectPatch { private static void Prefix(ZNetPeer peer)=>LevelingNetwork.Disconnect(peer); }
    [HarmonyPatch(typeof(ZNet),nameof(ZNet.Awake))]
    internal static class LevelingLocalRulesPatch { private static void Prefix(){if(LevelingConfig.Local!=null)LevelingConfig.Current=LevelingConfig.Local;if(Utility.WeaponAttackSpeeds.Local!=null)Utility.WeaponAttackSpeeds.Current=Utility.WeaponAttackSpeeds.Local;if(AI.MobBehaviorConfig.Local!=null)AI.MobBehaviorConfig.Current=AI.MobBehaviorConfig.Local;} }
}
