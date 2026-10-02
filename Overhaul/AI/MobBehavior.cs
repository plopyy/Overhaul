using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.AI
{
    internal static class MobBehavior
    {
        internal const string RetaliationKey="overhaul_ai_retaliation";
        internal static bool CanRetaliate(BaseAI ai,Character target)=>target&&ai.m_nview&&ai.m_nview.IsValid()&&ai.m_nview.GetZDO().GetZDOID(RetaliationKey)==target.GetZDOID();
        internal const string TargetKey="overhaul_ai_target", AlarmKey="overhaul_ai_alarm", AlarmTime="overhaul_ai_alarm_time", MissKey="overhaul_ai_miss";
        internal sealed class State
        {
            internal Character Target;
            internal double ReactUntil, NextHelp, NextPack, FlankUntil, RushUntil, BlockUntil, LastMiss;
            internal bool TargetAttacking, Blocking;
            internal bool Charging, ChargeReady=true;
            internal double ChargeUntil, PackFallback, NextPackReady;
            internal double EvadeUntil;
            internal Vector3 EvadeDirection;
            internal bool EvadeLateral;
            internal List<MonsterAI> Pack=new List<MonsterAI>();
        }
        internal static readonly ConditionalWeakTable<BaseAI,State> States=new ConditionalWeakTable<BaseAI,State>();
        internal static double Now=>ZNet.instance?ZNet.instance.GetTimeSeconds():Time.time;
        internal static bool Owner(BaseAI ai)=>ai&&ai.m_character&&ai.m_nview&&ai.m_nview.IsValid()&&ai.m_nview.IsOwner()&&!ai.m_character.IsDead();
        internal static bool Allies(Character a,Character b)=>a&&b&&a!=b&&!BaseAI.IsEnemy(a,b)&&!BaseAI.IsEnemy(b,a)&&
            (a.GetFaction()==b.GetFaction()||a.IsTamed()&&b.IsTamed()||a.GetGroup().Length>0&&a.GetGroup()==b.GetGroup());
        internal static bool Active(MonsterAI ai)=>Owner(ai)&&!MobBehaviorConfig.Excluded(ai.m_character)&&!ai.IsSleeping()&&!ai.HaveRider()&&!Storage.TrainingDummyProtection.IsDummy(ai.m_character);
        internal static void ObserveTarget(MonsterAI ai,State state,MobRule rule)
        {
            var target=ai.m_targetCreature;
            if(state.Target!=target)
            {
                ReleaseBlock(ai.m_character as Humanoid,state);
                state.Target=target;state.ReactUntil=Now;state.TargetAttacking=false;state.FlankUntil=Now+2.5;state.RushUntil=0;state.NextPack=0;state.BlockUntil=0;state.Charging=false;state.ChargeReady=true;state.PackFallback=0;state.NextPackReady=0;ai.m_nview.GetZDO().Set(MobPack.Ready,0L);
            }
            if(rule.Group==GroupBehavior.Pack)ai.m_nview.GetZDO().Set(TargetKey,target?target.GetZDOID():ZDOID.None);
        }
        internal static void Help(MonsterAI ai,State state)
        {
            if(Now<state.NextHelp||ai.m_targetCreature||!Active(ai))return;state.NextHelp=Now+.5;
            foreach(var other in BaseAI.GetAllInstances())
            {
                if(!other||MobBehaviorConfig.Excluded(other.m_character)||!other.m_nview||!other.m_nview.IsValid()||!Allies(ai.m_character,other.m_character)||(other.transform.position-ai.transform.position).sqrMagnitude>900)continue;
                var data=other.m_nview.GetZDO();if(data.GetLong(AlarmTime,0)/1000d<Now)continue;
                var obj=ZNetScene.instance?ZNetScene.instance.FindInstance(data.GetZDOID(AlarmKey)):null;var attacker=obj?obj.GetComponent<Character>():null;
                if(!attacker||attacker.IsDead()||!ai.IsEnemy(attacker)||!ai.HavePath(other.transform.position))continue;
                ai.SetTarget(attacker);ai.m_lastKnownTargetPos=attacker.transform.position;ai.SetAlerted(true);return;
            }
        }
        internal static List<MonsterAI> Members(MonsterAI ai,Character target)
        {
            if(!target)return new List<MonsterAI>();
            return BaseAI.GetAllInstances().OfType<MonsterAI>().Where(other=>other&&other.m_character&&!other.m_character.IsDead()&&other.m_nview&&other.m_nview.IsValid()&&
                MobPack.Eligible(other)&&MobBehaviorConfig.Rule(other.m_character).Group==GroupBehavior.Pack&&(other==ai||Allies(ai.m_character,other.m_character))&&
                (other.transform.position-target.transform.position).sqrMagnitude<=400&&
                (other.m_nview.IsOwner()?other.m_targetCreature==target:other.m_nview.GetZDO().GetZDOID(TargetKey)==target.GetZDOID()))
                .OrderBy(other=>other.m_character.GetZDOID().ToString(),StringComparer.Ordinal).ToList();
        }
        internal static void RefreshPack(MonsterAI ai,State state)
        {if(Now>=state.NextPack){state.Pack=Members(ai,ai.m_targetCreature);state.NextPack=Now+.3;}}
        internal static Vector3 FlankPoint(MonsterAI ai,Character target,bool pack)
        {
            Vector3 forward=target.transform.forward;forward.y=0;if(forward.sqrMagnitude<.01f)forward=Vector3.forward;forward.Normalize();
            float side=(ai.m_character.GetZDOID().ID%2)==0?1:-1;
            float radius=Mathf.Max(2.5f,target.GetRadius()+ai.m_character.GetRadius()+1);
            return target.transform.position-forward*radius+Vector3.Cross(Vector3.up,forward)*side*(pack?radius:radius*.6f);
        }
        internal static bool Behind(Character mob,Character target)=>Vector3.Dot(target.transform.forward,(mob.transform.position-target.transform.position).normalized)<-.35f;
        internal static bool Combat(MonsterAI ai,float dt)
        {
            if(!Active(ai))return false;var rule=MobBehaviorConfig.Rule(ai.m_character);if(rule.Vanilla)return false;
            if(DumbFollowup.Tick(ai,rule))return true;
            var state=States.GetOrCreateValue(ai);ObserveTarget(ai,state,rule);
            if(rule.Group==GroupBehavior.Helper&&rule.Intelligence!=Intelligence.Dumb)Help(ai,state);
            var target=ai.m_targetCreature;var body=ai.m_character as Humanoid;
            if(!target||target.IsDead()||!body||!ai.IsAlerted()||!ai.IsEnemy(target)){ReleaseBlock(body,state);return false;}
            if(rule.Intelligence==Intelligence.Dumb&&!CanRetaliate(ai,target)){state.Charging=false;ReleaseBlock(body,state);ai.StopMoving();return true;}
            if(body.InAttack()||body.IsStaggering()){state.Charging=false;ReleaseBlock(body,state);return false;}
            if(Now<state.ReactUntil){ai.StopMoving();return true;}
            if(!ai.CanSeeTarget(target)){state.Charging=false;ReleaseBlock(body,state);return false;}
            if(rule.Intelligence==Intelligence.Smart||rule.Intelligence==Intelligence.Normal)
            {
                bool attacking=target.InAttack();
                if(attacking&&!state.TargetAttacking&&Now>=state.BlockUntil&&Now>=state.EvadeUntil&&Vector3.Distance(body.transform.position,target.transform.position)<6&&UnityEngine.Random.value<(rule.Intelligence==Intelligence.Smart?.8f:.35f))MobDefense.Start(ai,body,target,state);
                state.TargetAttacking=attacking;
                if(Now<state.BlockUntil&&MobDefense.CanBlock(body)){body.m_blocking=true;state.Blocking=true;ai.LookAt(target.GetCenterPoint());ai.StopMoving();return true;}
                if(MobDefense.Retreat(ai,body,target,state))return true;
            }
            ReleaseBlock(body,state);
            bool pack=rule.Group==GroupBehavior.Pack&&rule.Intelligence!=Intelligence.Dumb&&MobPack.Formed(ai,state);
            if(pack&&MobPack.Move(ai,state,dt))return true;
            if(rule.Charge&&Charge(ai,body,target,state,dt))return true;
            if(!pack&&rule.Intelligence!=Intelligence.Dumb&&rule.Aggression==Aggression.Cunning&&!Behind(body,target)&&Now<state.FlankUntil)
            {
                var point=FlankPoint(ai,target,false);
                if(ai.HavePath(point)){ai.MoveTo(dt,point,.6f,true);return true;}
                state.FlankUntil=0;
            }
            if(rule.Aggression==Aggression.Cunning&&Now>=state.FlankUntil)
            {
                if(state.RushUntil==0)state.RushUntil=Now+3;
                else if(Now>=state.RushUntil){state.FlankUntil=Now+2.5;state.RushUntil=0;}
            }
            return false;
        }
        internal static bool Charge(MonsterAI ai,Humanoid body,Character target,State state,float dt)
        {
            float distance=Vector3.Distance(body.transform.position,target.transform.position);
            if(!state.Charging&&distance<10)state.ChargeReady=true;
            if(!state.Charging&&(!state.ChargeReady||distance<10))return false;
            var weapon=MobSpecies.ChargeWeapon(ai,body,dt);
            if(weapon==null||weapon.m_shared.m_aiTargetType!=ItemDrop.ItemData.AiTarget.Enemy){state.Charging=false;return false;}
            if(!state.Charging){state.Charging=true;state.ChargeReady=false;state.ChargeUntil=Now+8;}
            if(Now>=state.ChargeUntil){state.Charging=false;return false;}
            if(distance-target.GetRadius()>weapon.m_shared.m_aiAttackRange)
            {
                // Direct running, without a circling/path detour. Native physics still blocks obstacles.
                ai.MoveTowards(target.transform.position-body.transform.position,true);return true;
            }
            ai.StopMoving();ai.LookAt(target.GetCenterPoint());
            if(!ai.IsLookingAt(target.transform.position,weapon.m_shared.m_aiAttackMaxAngle,weapon.m_shared.m_aiInvertAngleCheck))return true;
            // DoAttack retains the native attack requirements, but does not insert the ordinary AI cooldown.
            if(ai.DoAttack(target,false))state.Charging=false;
            return true;
        }
        internal static void ReleaseBlock(Humanoid body,State state)
        {
            if(state.Blocking&&body)body.m_blocking=false;state.Blocking=false;state.BlockUntil=0;
            if(state.EvadeUntil!=0&&body){body.SetMoveDir(Vector3.zero);body.SetRun(false);}state.EvadeUntil=0;
        }
        [HarmonyPatch(typeof(BaseAI),nameof(BaseAI.OnDamaged))]
        private static class Alarm
        {
            private static void Postfix(BaseAI __instance,float damage,Character attacker)
            {
                if(!Owner(__instance)||MobBehaviorConfig.Excluded(__instance.m_character)||damage<=0||!attacker||!__instance.IsEnemy(attacker))return;
                var data=__instance.m_nview.GetZDO();
                if(MobBehaviorConfig.Rule(__instance.m_character).Intelligence==Intelligence.Dumb){data.Set(RetaliationKey,attacker.GetZDOID());if(__instance is MonsterAI monster)monster.m_targetCreature=attacker;else if(__instance is AnimalAI animal)animal.m_target=attacker;}
                data.Set(AlarmKey,attacker.GetZDOID());data.Set(AlarmTime,(long)((Now+4)*1000));
            }
        }
    }
}
