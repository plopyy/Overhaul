using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameDamageOverTime
    {
        internal sealed class Result
        {
            internal PlayerChange[] Changes;
            internal HitData LastHit;
            internal bool Lethal;
            internal float Damage;
        }
        private sealed class Frame
        {
            internal Player Player;
            internal PlayerSnapshot State;
            internal readonly List<PlayerChange> Changes=new List<PlayerChange>();
            internal HitData LastHit;
            internal float Damage;
        }
        [ThreadStatic] private static Frame current;
        internal static bool Supported(StatusEffect effect)=>effect && (effect is SE_Burning || effect is SE_Poison || effect.GetType()==typeof(StatusEffect) || effect.GetType()==typeof(SE_Frost) || effect.GetType()==typeof(SE_Shield));
        private static void Change(Frame frame,IEnumerable<PlayerChange> rows)
        {
            var changes=rows.ToArray();
            foreach(var row in changes){frame.Changes.RemoveAll(r=>r.SameKey(row));frame.Changes.Add(row);}
            frame.State=PlayerProgressService.Overlay(frame.State,changes);
            if(GameCombatContext.Matches(frame.Player))GameCombatContext.Current.State=frame.State;
        }
        internal static Result Advance(PlayerSnapshot state,Player player,double seconds)
        {
            if(!player || double.IsNaN(seconds)||double.IsInfinity(seconds)||seconds<0||seconds>60)throw new ArgumentOutOfRangeException(nameof(seconds));
            if(seconds==0 || PlayerResources.Read(state,"health")<=0)return new Result{Changes=Array.Empty<PlayerChange>()};
            var frame=new Frame{Player=player,State=state};var previous=current;current=frame;
            try
            {
                GameCombatContext.Run(player,state,null,null,()=>GameStatusGame.Simulate(()=>
                {
                    foreach(var header in state.Rows.Where(r=>r.Table=="status"))
                    {
                        int id=Convert.ToInt32(header.Values[0]);var rows=state.Rows.Where(r=>(r.Table=="status"||r.Table=="status_data")&&Convert.ToInt32(r.Values[0])==id);
                        var effect=GameStatusCodec.Restore(rows,player);
                        if(!Supported(effect))continue;
                        if(!(effect is SE_Burning) && !(effect is SE_Poison))
                        {
                            // These native types only advance their age. Avoid
                            // IsDone on shields: it raises skills and emits FX.
                            effect.m_time+=(float)seconds;
                            if(effect.m_ttl>0&&effect.m_time>effect.m_ttl)
                                Change(frame,new[]{new PlayerChange("status",true,id)});
                            else Change(frame,GameStatusCodec.Delta(frame.State,effect,new ZDOID(Convert.ToInt64(header.Values[4]),checked((uint)Convert.ToInt64(header.Values[5])))));
                            continue;
                        }
                        double remaining=seconds;int iterations=0;
                        while(remaining>0 && !effect.IsDone())
                        {
                            float interval=effect is SE_Burning burning?burning.m_damageInterval:((SE_Poison)effect).m_damageInterval;
                            float timer=effect is SE_Burning fire?fire.m_timer:((SE_Poison)effect).m_timer;
                            if(interval<=0 || float.IsNaN(interval)||float.IsInfinity(interval) || ++iterations>16384)throw new InvalidOperationException("Invalid damage-over-time cadence");
                            // Jump directly to the next native damage event. No
                            // replay of every physics frame while SQL was busy.
                            double step=Math.Min(remaining,timer>0?timer:.02);
                            bool wet=effect is SE_Burning b && b.m_fireDamageLeft>0 && frame.State.Rows.Any(r=>r.Table=="status"&&Convert.ToInt32(r.Values[0])==SEMan.s_statusEffectWet);
                            // Float ages need an expiry step larger than their
                            // rounding unit; a fixed microsecond stalls at long TTLs.
                            double expiryEpsilon=Math.Max(.000001,Math.Abs((double)effect.m_ttl)*.000001);
                            if(effect.m_ttl>0)step=Math.Min(step,Math.Max(expiryEpsilon,(effect.m_ttl-effect.m_time)/(wet?6d:1d)+expiryEpsilon));
                            effect.UpdateStatusEffect((float)step);remaining=Math.Max(0,remaining-step);
                        }
                        if(effect.IsDone())Change(frame,new[]{new PlayerChange("status",true,id)});
                        else Change(frame,GameStatusCodec.Delta(frame.State,effect,new ZDOID(Convert.ToInt64(header.Values[4]),checked((uint)Convert.ToInt64(header.Values[5])))));
                    }
                }));
                return new Result{Changes=frame.Changes.ToArray(),LastHit=frame.LastHit,Damage=frame.Damage,Lethal=PlayerResources.Read(frame.State,"health")<=0};
            }
            finally{current=previous;}
        }
        [HarmonyPatch(typeof(Character),nameof(Character.ApplyDamage))]
        private static class Health
        {
            [HarmonyPriority(Priority.First+300)]
            private static bool Prefix(Character __instance,HitData hit)
            {
                if(current==null || __instance!=current.Player)return true;
                var copy=hit.Clone();copy.ApplyModifier(Game.m_localDamgeTakenRate);float damage=copy.GetTotalDamage();
                if(float.IsNaN(damage)||float.IsInfinity(damage)||damage<0)throw new InvalidOperationException("Invalid native periodic damage");
                double health=PlayerResources.Read(current.State,"health");
                if(damage>.1f && health>0)
                {current.Damage+=damage;current.LastHit=copy;Change(current,new[]{PlayerResources.Row("health",Math.Max(0,health-damage))});}
                return false;
            }
        }
        [HarmonyPatch(typeof(SEMan),nameof(SEMan.HaveStatusEffect),new[]{typeof(int)})]
        private static class Wet
        {
            private static bool Prefix(SEMan __instance,int nameHash,ref bool __result)
            {
                if(current==null || __instance.m_character!=current.Player)return true;
                __result=current.State.Rows.Any(r=>(r.Table=="status"||r.Table=="effects")&&Convert.ToInt32(r.Values[0])==nameHash);return false;
            }
        }
        [HarmonyPatch(typeof(EffectList),nameof(EffectList.Create))]
        private static class Visuals
        {private static bool Prefix(ref GameObject[] __result){if(current==null)return true;__result=Array.Empty<GameObject>();return false;}}
        [HarmonyPatch]
        private static class Messages
        {
            private static IEnumerable<MethodBase> TargetMethods()=>typeof(Character).GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly).Where(m=>m.Name==nameof(Character.Message));
            private static bool Prefix(Character __instance)=>current==null || __instance!=current.Player;
        }
    }
}
