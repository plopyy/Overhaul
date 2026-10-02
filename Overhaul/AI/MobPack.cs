using System.Linq;
using UnityEngine;

namespace Overhaul.AI
{
    // Each owner publishes readiness; only the leader owner opens the shared assault window.
    internal static class MobPack
    {
        internal const string Ready="overhaul_pack_ready", ReadyTarget="overhaul_pack_ready_target",
            WaveTarget="overhaul_pack_target", WaveStart="overhaul_pack_start", WaveEnd="overhaul_pack_end", GatherEnd="overhaul_pack_gather";
        internal static bool Eligible(MonsterAI ai)=>ai&&ai.m_character&&!ai.m_character.IsDead()&&ai.m_nview&&ai.m_nview.IsValid()&&
            !MobBehaviorConfig.Excluded(ai.m_character)&&!ai.IsSleeping()&&!ai.HaveRider()&&
            MobBehaviorConfig.Rule(ai.m_character).Intelligence!=Intelligence.Dumb;

        internal static bool Formed(MonsterAI ai,MobBehavior.State state)
        {
            if(!ai.m_targetCreature||ai.m_targetCreature.IsDead()){state.Pack.Clear();return false;}
            MobBehavior.RefreshPack(ai,state);
            state.Pack.RemoveAll(m=>!Eligible(m)||(m.transform.position-ai.m_targetCreature.transform.position).sqrMagnitude>400||
                MobBehaviorConfig.Rule(m.m_character).Group!=GroupBehavior.Pack||(m!=ai&&!MobBehavior.Allies(ai.m_character,m.m_character))||
                (m.m_nview.IsOwner()?m.m_targetCreature!=ai.m_targetCreature:m.m_nview.GetZDO().GetZDOID(MobBehavior.TargetKey)!=ai.m_targetCreature.GetZDOID()));
            return state.Pack.Count>=3&&state.Pack.Contains(ai);
        }
        internal static Vector3 Slot(MonsterAI ai,MobBehavior.State state)
        {
            var target=ai.m_targetCreature;
            float angle=2*Mathf.PI*state.Pack.IndexOf(ai)/state.Pack.Count;
            float radius=Mathf.Max(target.GetRadius()+ai.m_character.GetRadius()+.6f,ai.m_character.GetRadius()*state.Pack.Count/Mathf.PI+.6f);
            // World-space slots stay stable when the victim turns to face a different attacker.
            return target.transform.position+new Vector3(Mathf.Cos(angle),0,Mathf.Sin(angle))*radius;
        }
        internal static bool Positioned(MonsterAI ai,MobBehavior.State state)
        {
            var offset=ai.transform.position-ai.m_targetCreature.transform.position;offset.y=0;
            var slot=Slot(ai,state)-ai.m_targetCreature.transform.position;slot.y=0;
            return Mathf.Abs(offset.magnitude-slot.magnitude)<1&&Vector3.Angle(offset,slot)<25&&offset.sqrMagnitude>.1f;
        }
        internal static bool ReadyNow(MonsterAI ai,MobBehavior.State state)
        {
            var body=ai.m_character as Humanoid;var weapon=body?body.GetCurrentWeapon():null;
            return body&&weapon!=null&&weapon.m_shared.m_aiTargetType==ItemDrop.ItemData.AiTarget.Enemy&&ai.IsAlerted()&&
                !body.InAttack()&&!body.IsStaggering()&&MobBehavior.Now>=state.BlockUntil&&MobBehavior.Now>=state.EvadeUntil&&
                Time.time-weapon.m_lastAttackTime>=weapon.m_shared.m_aiAttackInterval&&body.GetTimeSinceLastAttack()>=ai.m_minAttackInterval&&
                ai.CanSeeTarget(ai.m_targetCreature)&&Positioned(ai,state);
        }
        internal static bool Allowed(MonsterAI ai,MobBehavior.State state)
        {
            if(!ai.m_targetCreature||!Formed(ai,state))return true;
            var now=MobBehavior.Now;var leader=state.Pack[0];var data=leader.m_nview.GetZDO();var target=ai.m_targetCreature.GetZDOID();
            if(leader.m_nview.IsOwner())
            {
                if(data.GetZDOID(WaveTarget)!=target)
                {data.Set(WaveTarget,target);data.Set(WaveStart,0L);data.Set(WaveEnd,0L);data.Set(GatherEnd,(long)((now+4)*1000));}
                if(data.GetLong(WaveEnd,0)/1000d<=now)
                {
                    var ready=state.Pack.Where(m=>
                    {
                        var z=m.m_nview.GetZDO();var stamp=z.GetLong(Ready,0)/1000d;
                        return z.GetZDOID(ReadyTarget)==target&&stamp>0&&now-stamp>=0&&now-stamp<.75&&
                            !m.m_character.InAttack()&&!m.m_character.IsStaggering()&&
                            (!m.m_nview.IsOwner()||ReadyNow(m,MobBehavior.States.GetOrCreateValue(m)));
                    }).ToArray();
                    bool surrounded=ready.Any(a=>ready.Any(b=>a!=b&&Vector3.Angle(
                        Vector3.ProjectOnPlane(a.transform.position-ai.m_targetCreature.transform.position,Vector3.up),
                        Vector3.ProjectOnPlane(b.transform.position-ai.m_targetCreature.transform.position,Vector3.up))>=60));
                    if(surrounded||data.GetLong(GatherEnd,0)/1000d<=now)
                    {
                        // A short lead time lets remote owners receive the same opening.
                        data.Set(WaveStart,(long)((now+.25)*1000));data.Set(WaveEnd,(long)((now+2.25)*1000));
                        data.Set(GatherEnd,(long)((now+6.25)*1000));
                    }
                }
            }
            if(data.GetZDOID(WaveTarget)==target&&data.GetLong(WaveEnd,0)/1000d>now)
            {state.PackFallback=now+4;return data.GetLong(WaveStart,0)/1000d<=now;}
            // Never wait forever for an absent/stalled remote coordinator.
            if(state.PackFallback==0)state.PackFallback=now+4;
            if(now>=state.PackFallback+2)state.PackFallback=now+4;
            return now>=state.PackFallback;
        }
        internal static bool Move(MonsterAI ai,MobBehavior.State state,float dt)
        {
            if(!Formed(ai,state))return false;
            var data=ai.m_nview.GetZDO();var now=MobBehavior.Now;
            if(now>=state.NextPackReady)
            {
                data.Set(ReadyTarget,ai.m_targetCreature.GetZDOID());data.Set(Ready,ReadyNow(ai,state)?(long)(now*1000):0L);
                state.NextPackReady=now+.2;
            }
            if(Allowed(ai,state))return false;
            state.Charging=false;
            if(Positioned(ai,state)){ai.StopMoving();ai.LookAt(ai.m_targetCreature.GetCenterPoint());return true;}
            var center=ai.m_targetCreature.transform.position;var slot=Slot(ai,state)-center;
            var offset=ai.transform.position-center;offset.y=0;
            if(offset.sqrMagnitude<.01f)offset=slot;
            // Approach the near edge, then walk along the ring instead of crossing through the prey.
            var direction=offset.magnitude>slot.magnitude+2?offset.normalized:
                Vector3.RotateTowards(offset.normalized,slot.normalized,35*Mathf.Deg2Rad,0);
            var point=center+direction*slot.magnitude;
            if(ai.HavePath(point))ai.MoveTo(dt,point,.35f,true);
            else ai.StopMoving(); // The bounded gathering period releases native pursuit on blocked terrain.
            return true;
        }
    }
}
