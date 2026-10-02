using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.AI
{
    // Keep native perception and danger timers; replace only the random predator flee destinations.
    internal static class AnimalEscape
    {
        internal sealed class State
        {
            internal Character Predator;
            internal Vector3 Point;
            internal double CheckAt;
            internal bool HasPoint;
            internal float Side;
        }
        internal static readonly ConditionalWeakTable<AnimalAI,State> States=new ConditionalWeakTable<AnimalAI,State>();
        internal static bool Applies(BaseAI ai,Vector3 from)=>ai is AnimalAI animal&&MobBehavior.Owner(ai)&&
            !MobBehaviorConfig.Excluded(ai.m_character)&&animal.m_target&&!animal.m_target.IsDead()&&
            Utils.GetPrefabName(ai.gameObject)!="Goblin_Gem"&&(animal.m_target.transform.position-from).sqrMagnitude<.01f;

        internal static bool Safe(AnimalAI ai,Vector3 point)
        {
            if(!ai.HavePath(point))return false;
            if(ai.m_avoidWater&&!ai.m_character.IsSwimming()&&(!ZoneSystem.instance||ZoneSystem.instance.GetSolidHeight(point)<30))return false;
            return !ai.m_avoidLavaFlee||ZoneSystem.instance&&!ZoneSystem.instance.IsLava(point,false);
        }
        internal static bool Run(AnimalAI ai,float dt,Vector3 from)
        {
            var state=States.GetOrCreateValue(ai);var position=ai.transform.position;
            var away=position-from;away.y=0;
            if(away.sqrMagnitude<.01f){away=ai.transform.forward;away.y=0;}
            if(away.sqrMagnitude<.01f)away=Vector3.forward;away.Normalize();
            if(state.Predator!=ai.m_target)
            {state.Predator=ai.m_target;state.HasPoint=false;state.CheckAt=0;state.Side=(ai.m_character.GetZDOID().ID%2==0)?1:-1;}
            var remaining=state.Point-position;remaining.y=0;
            // A moving predator can invalidate the old escape route immediately.
            bool unsafeDirection=state.HasPoint&&Vector3.Dot(remaining.normalized,away)<.35f;
            bool reached=state.HasPoint&&remaining.sqrMagnitude<4;
            if(unsafeDirection||reached)state.HasPoint=false;
            if(unsafeDirection||reached||MobBehavior.Now>=state.CheckAt)
            {
                state.CheckAt=MobBehavior.Now+.75;
                if(state.HasPoint&&!Safe(ai,state.Point))state.HasPoint=false;
                if(!state.HasPoint)
                {
                    float range=Mathf.Max(4,ai.m_fleeRange);
                    foreach(float length in new[]{range,range*.5f,Mathf.Min(3,range*.25f)})
                    {
                        foreach(float angle in new[]{0,30*state.Side,-30*state.Side,60*state.Side,-60*state.Side})
                        {
                            var point=position+(Quaternion.Euler(0,angle,0)*away)*length;
                            if(!Safe(ai,point))continue;
                            state.Point=point;state.HasPoint=true;
                            if(angle!=0)state.Side=Mathf.Sign(angle);
                            break;
                        }
                        if(state.HasPoint)break;
                    }
                }
            }
            ai.m_lastFlee=Time.time;ai.m_fleeTargetUpdateTime=Time.time;
            if(!state.HasPoint){ai.StopMoving();return false;}
            ai.m_fleeTarget=state.Point;
            return ai.MoveTo(dt,state.Point,1,true);
        }
        [HarmonyPatch(typeof(BaseAI),"Flee")]
        private static class Flee
        {
            private static bool Prefix(BaseAI __instance,float dt,Vector3 from,ref bool __result)
            {
                if(!Applies(__instance,from))return true;
                __result=Run((AnimalAI)__instance,dt,from);return false;
            }
        }
    }
}
