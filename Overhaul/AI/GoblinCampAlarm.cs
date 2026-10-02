using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.AI
{
    internal static class GoblinCampAlarm
    {
        internal const string Request="Overhaul_CampAlarmRequest", Broadcast="Overhaul_CampAlarm";
        internal const string JoinedKey="overhaul_camp_joined";
        internal sealed class Alert
        {
            internal Vector3 Center, SoundPoint;
            internal float Radius;
            internal ZDOID Target;
            internal double Contact, Sent, Epoch, Heard;
            internal bool Fighting;
        }
        internal sealed class Poll { internal double Next, Joined; internal bool HasCamp; }
        internal sealed class Participant { internal Vector3 Center; internal bool Active; internal double Updated; }
        internal static readonly Dictionary<Vector3,Alert> Server=new Dictionary<Vector3,Alert>(),Received=new Dictionary<Vector3,Alert>();
        internal static readonly Dictionary<ZDOID,Participant> Participants=new Dictionary<ZDOID,Participant>();
        private static ConditionalWeakTable<MonsterAI,Poll> polls=new ConditionalWeakTable<MonsterAI,Poll>();
        private static List<ZoneSystem.LocationInstance> camps=new List<ZoneSystem.LocationInstance>();
        private static double nextCampScan;
        private static readonly HashSet<int> Goblins=new HashSet<int>(new[]{"Goblin","GoblinArcher","Goblin_Gem","GoblinBrute","GoblinShaman","GoblinDeepNorth",
            "GoblinBrute_Hildir","GoblinBruteBros","GoblinBruteBros_nochest","GoblinShaman_Hildir","GoblinShaman_Hildir_nochest"}.Select(n=>n.GetStableHashCode()));
        internal static bool Eligible(MonsterAI ai)=>MobBehavior.Active(ai)&&Goblins.Contains(ai.m_nview.GetZDO().GetPrefab())&&
            !ai.m_character.IsTamed()&&MobBehaviorConfig.Rule(ai.m_character).Intelligence!=Intelligence.Dumb;
        internal static void Raise(MonsterAI ai,Character enemy)
        {
            if(!Eligible(ai)||!enemy||enemy.IsDead()||!ai.IsEnemy(enemy)||ZRoutedRpc.instance==null)return;
            ZRoutedRpc.instance.InvokeRoutedRPC(Request,ai.m_character.GetZDOID(),enemy.GetZDOID());
        }
        internal static void Tick(MonsterAI ai)
        {
            if(!Eligible(ai))return;
            var poll=polls.GetOrCreateValue(ai);if(MobBehavior.Now<poll.Next)return;poll.Next=MobBehavior.Now+1;
            if(poll.Joined==0){poll.Joined=ai.m_nview.GetZDO().GetLong(JoinedKey,0)/1000d;poll.HasCamp=poll.Joined>0;}
            foreach(var alert in Received.Values)
            {
                if(MobBehavior.Now-alert.Contact>10||(ai.transform.position-alert.Center).sqrMagnitude>alert.Radius*alert.Radius)continue;
                poll.HasCamp=true;if(poll.Joined+.001>=alert.Epoch)continue;
                var obj=ZNetScene.instance?ZNetScene.instance.FindInstance(alert.Target):null;var target=obj?obj.GetComponent<Character>():null;
                if(!target||target.IsDead()||!ai.IsEnemy(target))continue;
                poll.Joined=alert.Epoch;ai.m_nview.GetZDO().Set(JoinedKey,(long)(alert.Epoch*1000));
                if(!ai.m_targetCreature||ai.m_targetCreature.IsDead())
                {ai.m_targetCreature=null;ai.SetTarget(target);ai.m_lastKnownTargetPos=target.transform.position;ai.SetAlerted(true);}
            }
            if(ai.IsAlerted()&&ai.m_targetCreature&&!ai.m_targetCreature.IsDead()&&
                (poll.HasCamp||ai.m_targetCreature.IsPlayer()&&ai.CanSeeTarget(ai.m_targetCreature)))Raise(ai,ai.m_targetCreature);
            else if(poll.HasCamp&&ZRoutedRpc.instance!=null)ZRoutedRpc.instance.InvokeRoutedRPC(Request,ai.m_character.GetZDOID(),ZDOID.None);
        }
        internal static bool FindCamp(Vector3 position,out Vector3 center,out float radius)
        {
            center=default;radius=0;if(!ZoneSystem.instance)return false;
            if(MobBehavior.Now>=nextCampScan)
            {
                nextCampScan=MobBehavior.Now+5;
                camps=ZoneSystem.instance.GetLocationList().Where(e=>e.m_placed&&e.m_location!=null&&
                    (e.m_location.m_prefabName=="GoblinCamp2"||e.m_location.m_prefabName=="GoblinCamp2_1")).ToList();
            }
            float nearest=float.MaxValue;
            foreach(var entry in camps)
            {
                if(!entry.m_placed||entry.m_location==null||!(entry.m_location.m_prefabName=="GoblinCamp2"||entry.m_location.m_prefabName=="GoblinCamp2_1"))continue;
                float range=Mathf.Max(50,entry.m_location.m_exteriorRadius+10);float distance=(position-entry.m_position).sqrMagnitude;
                if(distance<=range*range&&distance<nearest){nearest=distance;center=entry.m_position;radius=range;}
            }
            return nearest<float.MaxValue;
        }
        internal static bool Accept(long sender,ZDO source,ZDO target)
        {
            return source!=null&&target!=null&&source.GetOwner()==sender&&Goblins.Contains(source.GetPrefab())&&
                source.GetInt(MobBehaviorConfig.IntelligenceKey,-1)!=(int)Intelligence.Dumb&&!source.GetBool(ZDOVars.s_tamed)&&
                source.GetFloat(ZDOVars.s_health,1)>0&&target.GetFloat(ZDOVars.s_health,1)>0&&
                (source.GetPosition()-target.GetPosition()).sqrMagnitude<22500;
        }
        internal static void OnRequest(long sender,ZDOID sourceId,ZDOID targetId)
        {
            if(!ZNet.instance||!ZNet.instance.IsServer()||ZDOMan.instance==null)return;
            var source=ZDOMan.instance.GetZDO(sourceId);var target=ZDOMan.instance.GetZDO(targetId);
            if(source==null||source.GetOwner()!=sender||!Goblins.Contains(source.GetPrefab()))return;
            Participants.TryGetValue(sourceId,out var participant);
            if(targetId==ZDOID.None)
            {
                if(participant!=null){if(participant.Active){participant.Active=false;participant.Updated=MobBehavior.Now;}TryCalm(participant.Center);}
                return;
            }
            if(!Accept(sender,source,target))return;
            Vector3 center;float radius;
            if(participant!=null&&Server.TryGetValue(participant.Center,out var previous)&&previous.Fighting)
            {center=participant.Center;radius=previous.Radius;}
            else if(!FindCamp(source.GetPosition(),out center,out radius))return;
            TryCalm(center);
            if(participant==null)Participants[sourceId]=participant=new Participant();
            participant.Center=center;participant.Active=true;participant.Updated=MobBehavior.Now;
            var alert=Record(center,radius,source.GetPosition(),targetId);
            if(MobBehavior.Now-alert.Sent<1)return;alert.Sent=MobBehavior.Now;
            var packet=new ZPackage();packet.Write(center);packet.Write(radius);packet.Write(alert.SoundPoint);packet.Write(targetId);packet.Write(alert.Epoch);
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody,Broadcast,packet);
        }
        internal static Alert Record(Vector3 center,float radius,Vector3 source,ZDOID target)
        {
            var now=MobBehavior.Now;
            if(!Server.TryGetValue(center,out var alert))Server[center]=alert=new Alert{Contact=double.NegativeInfinity,Sent=double.NegativeInfinity};
            // Only a new encounter can sound the horn. Reinforcements refresh the same alert.
            if(!alert.Fighting){alert.Fighting=true;alert.Epoch=now;alert.SoundPoint=source;alert.Sent=double.NegativeInfinity;}
            alert.Center=center;alert.Radius=radius;alert.Target=target;alert.Contact=now;return alert;
        }
        internal static void TryCalm(Vector3 center)
        {
            if(!Server.TryGetValue(center,out var alert)||!alert.Fighting)return;
            double quietSince=alert.Contact;
            foreach(var member in Participants.Values.Where(p=>p.Center==center))
            {
                if(member.Active&&MobBehavior.Now-member.Updated<5)return;
                quietSince=System.Math.Max(quietSince,member.Updated+(member.Active?5:0));
            }
            // Require the whole camp to leave combat, with a short grace for network latency.
            if(MobBehavior.Now-quietSince>=5)alert.Fighting=false;
        }
        internal static void OnBroadcast(long sender,ZPackage packet)
        {
            if(ZRoutedRpc.instance==null||sender!=ZRoutedRpc.instance.GetServerPeerID())return;
            var center=packet.ReadVector3();float radius=packet.ReadSingle();var sound=packet.ReadVector3();var target=packet.ReadZDOID();double epoch=packet.ReadDouble();
            if(!Received.TryGetValue(center,out var alert))Received[center]=alert=new Alert();
            if(epoch<alert.Epoch)return;
            alert.Center=center;alert.Radius=radius;alert.Target=target;alert.Contact=MobBehavior.Now;alert.Epoch=epoch;
            if(epoch>alert.Heard){alert.Heard=epoch;if(MobBehavior.Now-epoch<=2)CampHorn.Play(sound);}
        }
        [HarmonyPatch(typeof(ZRoutedRpc),MethodType.Constructor,typeof(bool))]
        private static class Register
        {
            private static void Postfix(ZRoutedRpc __instance)
            {
                Server.Clear();Received.Clear();Participants.Clear();polls=new ConditionalWeakTable<MonsterAI,Poll>();
                camps.Clear();nextCampScan=0;
                __instance.Register<ZDOID,ZDOID>(Request,OnRequest);__instance.Register<ZPackage>(Broadcast,OnBroadcast);
            }
        }
        [HarmonyPatch(typeof(MonsterAI),nameof(MonsterAI.UpdateAI))]
        private static class Update { private static void Prefix(MonsterAI __instance)=>Tick(__instance); }
        [HarmonyPatch(typeof(BaseAI),nameof(BaseAI.OnDamaged))]
        private static class Damage
        { private static void Postfix(BaseAI __instance,float damage,Character attacker){if(damage>0&&__instance is MonsterAI ai)Raise(ai,attacker);} }
    }
}
