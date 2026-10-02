using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.AI
{
    // Follow-ups are real native attacks; only their destination is remembered.
    internal static class DumbFollowup
    {
        internal sealed class State
        {
            internal Character Victim;
            internal Vector3 Point;
            internal int Remaining;
            internal bool Corpse, Locked;
            internal double Until, Started;
        }
        internal static readonly ConditionalWeakTable<MonsterAI,State> States=new ConditionalWeakTable<MonsterAI,State>();
        internal static MonsterAI Starting;

        internal static void Record(MonsterAI ai,Character target)
        {
            if(Starting==ai||!target||!MobBehavior.Active(ai)||MobBehaviorConfig.Rule(ai.m_character).Intelligence!=Intelligence.Dumb)return;
            var state=States.GetOrCreateValue(ai);
            state.Victim=target;state.Point=target.GetCenterPoint();state.Corpse=false;state.Locked=false;
            state.Remaining=Random.value<.2f?1:0;state.Until=MobBehavior.Now+30;
        }

        internal static void Died(Character victim)
        {
            foreach(var entry in BaseAI.GetAllInstances())
                if(entry is MonsterAI ai&&MobBehavior.Active(ai)&&MobBehaviorConfig.Rule(ai.m_character).Intelligence==Intelligence.Dumb&&
                   States.TryGetValue(ai,out var state)&&state.Victim==victim&&!state.Corpse)
                {
                    state.Point=victim.GetCenterPoint();state.Corpse=true;
                    state.Remaining=Random.Range(1,3);state.Until=MobBehavior.Now+30;
                }
        }

        internal static bool Tick(MonsterAI ai,MobRule rule)
        {
            if(!States.TryGetValue(ai,out var state))return false;
            if(rule.Intelligence!=Intelligence.Dumb||!MobBehavior.Active(ai)||MobBehavior.Now>state.Until||
               ai.m_targetCreature&&ai.m_targetCreature!=state.Victim)
            {States.Remove(ai);return false;}
            if(state.Victim&&state.Victim.IsDead()&&!state.Corpse)Died(state.Victim);
            var body=ai.m_character as Humanoid;if(!body)return false;
            if(state.Locked)
            {
                if(body.InAttack()||MobBehavior.Now-state.Started<.2)
                {ai.StopMoving();ai.LookAt(state.Point);return true;}
                state.Locked=false;
            }
            if(state.Remaining==0)return false;
            if(body.IsStaggering()){States.Remove(ai);return false;}
            ai.StopMoving();
            // Let the first swing finish before imposing the remembered aim.
            if(body.InAttack())return true;
            ai.LookAt(state.Point);
            var weapon=body.GetCurrentWeapon();
            if(weapon==null||weapon.m_shared.m_aiTargetType!=ItemDrop.ItemData.AiTarget.Enemy){States.Remove(ai);return false;}
            if(Time.time-weapon.m_lastAttackTime<weapon.m_shared.m_aiAttackInterval||body.GetTimeSinceLastAttack()<ai.m_minAttackInterval)return true;
            if(!ai.IsLookingAt(state.Point,weapon.m_shared.m_aiAttackMaxAngle,weapon.m_shared.m_aiInvertAngleCheck))return true;
            var previous=Starting;Starting=ai;state.Locked=true;
            try
            {
                if(ai.DoAttack(null,false)){state.Remaining--;state.Started=MobBehavior.Now;}
                else state.Locked=false;
            }
            finally{Starting=previous;}
            return true;
        }

        internal static bool Aim(Humanoid body,Vector3 origin,out Vector3 direction)
        {
            direction=Vector3.zero;
            if(!(body.m_baseAI is MonsterAI ai)||!MobBehavior.Active(ai)||MobBehaviorConfig.Rule(body).Intelligence!=Intelligence.Dumb||
               !States.TryGetValue(ai,out var state)||!state.Locked)return false;
            direction=(state.Point-origin).normalized;return direction.sqrMagnitude>.001f;
        }

        [HarmonyPatch(typeof(Character),"OnDeath")]
        private static class Death
        {private static void Prefix(Character __instance)=>Died(__instance);}

        [HarmonyPatch(typeof(Humanoid),nameof(Humanoid.GetAimDir))]
        private static class MeleeAim
        {private static void Postfix(Humanoid __instance,Vector3 fromPoint,ref Vector3 __result){if(Aim(__instance,fromPoint,out var aim))__result=aim;}}

        [HarmonyPatch(typeof(Attack),"GetProjectileSpawnPoint")]
        private static class ProjectileAim
        {private static void Postfix(Attack __instance,Vector3 spawnPoint,ref Vector3 aimDir){if(Aim(__instance.m_character,spawnPoint,out var aim))aimDir=aim;}}
    }
}
