using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.AI
{
    internal static class MobDefense
    {
        internal static bool CanBlock(Humanoid body)
        {
            var item=body.GetCurrentBlocker();
            return item!=null&&item.m_shared.m_blockPower>0&&body.m_animator&&body.m_animator.runtimeAnimatorController&&
                body.m_animator.parameters.Any(p=>p.type==AnimatorControllerParameterType.Bool&&p.name=="blocking");
        }
        internal static string EvadeTrigger(Animator animator)
        {
            if(!animator||!animator.runtimeAnimatorController)return null;
            // Only invoke existing triggers; never play a guessed state or an attack animation.
            return animator.parameters.Where(p=>p.type==AnimatorControllerParameterType.Trigger)
                .Select(p=>p.name).FirstOrDefault(n=>new[]{"dodge_back","dodgeback","dodge_backward","dodge","evade_back","evade"}.Contains(n.ToLowerInvariant()));
        }
        internal static void Start(MonsterAI ai,Humanoid body,Character target,MobBehavior.State state)
        {
            state.Charging=false;
            if(CanBlock(body)){state.BlockUntil=MobBehavior.Now+1.2;return;}
            if(!body.CanMove()||body.IsKnockedBack())return;
            var direction=body.transform.position-target.transform.position;direction.y=0;
            if(direction.sqrMagnitude<.01f)direction=-body.transform.forward;
            state.EvadeDirection=direction.normalized;
            state.EvadeLateral=false;
            if(!ChooseDirection(ai,body,state))return;
            state.EvadeUntil=MobBehavior.Now+.45;
            var trigger=EvadeTrigger(body.m_animator);
            if(state.EvadeLateral&&trigger!=null&&trigger.IndexOf("back",StringComparison.OrdinalIgnoreCase)>=0)trigger=null;
            if(trigger!=null&&ai.m_animator)ai.m_animator.SetTrigger(trigger);
        }
        internal static bool ChooseDirection(MonsterAI ai,Humanoid body,MobBehavior.State state)
        {
            if(Clear(ai,body,state.EvadeDirection))return true;
            if(state.EvadeLateral)return false;
            var side=Vector3.Cross(Vector3.up,state.EvadeDirection)*(UnityEngine.Random.value<.5f?1:-1);
            foreach(var candidate in new[]{side,-side})
                if(Clear(ai,body,candidate)){state.EvadeDirection=candidate;state.EvadeLateral=true;return true;}
            return false;
        }
        internal static bool Clear(MonsterAI ai,Humanoid body,Vector3 direction)
        {
            // Native navigation rejects inaccessible ground. The sweep also rejects walls
            // even when a longer path around them exists. Movement itself stays physical.
            if(!ai.HavePath(body.transform.position+direction*2))return false;
            float radius=Mathf.Max(.15f,body.GetRadius()*.8f);
            return !Physics.SphereCastAll(body.GetCenterPoint(),radius,direction,2,Physics.DefaultRaycastLayers,QueryTriggerInteraction.Ignore)
                .Any(h=>h.collider&&!h.collider.transform.IsChildOf(body.transform));
        }
        internal static bool Retreat(MonsterAI ai,Humanoid body,Character target,MobBehavior.State state)
        {
            if(MobBehavior.Now>=state.EvadeUntil||!body.CanMove()||body.IsKnockedBack()||!ChooseDirection(ai,body,state))return false;
            ai.LookAt(target.GetCenterPoint());body.SetWalk(false);body.SetRun(true);body.SetMoveDir(state.EvadeDirection);return true;
        }
        internal static bool Retreating(Character body)
        {
            var ai=body.m_baseAI as MonsterAI;
            return ai&&MobBehavior.Active(ai)&&MobBehavior.States.TryGetValue(ai,out var state)&&MobBehavior.Now<state.EvadeUntil&&
                state.Target&&!state.Target.IsDead()&&!body.InAttack()&&!body.IsStaggering()&&
                (MobBehaviorConfig.Rule(body).Intelligence==Intelligence.Normal||MobBehaviorConfig.Rule(body).Intelligence==Intelligence.Smart);
        }
        [HarmonyPatch(typeof(Character),"AlwaysRotateCamera")]
        private static class FaceEnemy
        {
            private static void Postfix(Character __instance,ref bool __result){if(Retreating(__instance))__result=true;}
        }
        [HarmonyPatch(typeof(Character),nameof(Character.GetRunSpeedFactor))]
        private static class RetreatSpeed
        {
            private static void Postfix(Character __instance,ref float __result){if(Retreating(__instance))__result*=1.5f;}
        }
    }
}
