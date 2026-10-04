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
            internal PlayerActionPlan Death;
            internal Action Publish;
        }
        private sealed class Frame
        {
            internal Player Player;
            internal PlayerSnapshot State;
            internal readonly List<PlayerChange> Changes=new List<PlayerChange>();
            internal HitData LastHit;
            internal float Damage;
            internal PlayerActionPlan Death;
            internal readonly List<Action> Publish=new List<Action>();
        }
        [ThreadStatic] private static Frame current;
        internal static bool Supported(StatusEffect effect)=>effect && (effect is SE_StaffGuard || effect is SE_Burning || effect is SE_Poison || effect is SE_Harpooned || effect is SE_Finder || effect is SE_Demister || effect is SE_Crowned || effect is SE_Spawn || effect is SE_HealthUpgrade || effect is SE_Puke || effect.GetType()==typeof(StatusEffect) || effect.GetType()==typeof(SE_Frost) || effect.GetType()==typeof(SE_Shield) || effect.GetType()==typeof(SE_Stats) || effect.GetType()==typeof(SE_Wet) || effect.GetType()==typeof(SE_Smoke) || effect.GetType()==typeof(SE_React) || effect.GetType()==typeof(SE_Cozy) || effect.GetType()==typeof(SE_Rested));
        private static void Change(Frame frame,IEnumerable<PlayerChange> rows)
        {
            var changes=rows.ToArray();
            GamePlayerHit.Merge(frame.Changes,changes);
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
                Change(frame,GameBlockCharges.Advance(state,player,seconds));
                GameCombatContext.Run(player,state,null,null,()=>GameStatusGame.Simulate(()=>
                {
                    // Resting refreshes Rested after its own age has advanced.
                    // Process it last so a stale pre-refresh copy cannot win.
                    foreach(var header in state.Rows.Where(r=>r.Table=="status").OrderBy(r=>ObjectDB.instance.GetStatusEffect(Convert.ToInt32(r.Values[0])) is SE_Cozy?1:0))
                    {
                        int id=Convert.ToInt32(header.Values[0]);var rows=frame.State.Rows.Where(r=>(r.Table=="status"||r.Table=="status_data")&&Convert.ToInt32(r.Values[0])==id);
                        if(!rows.Any(r=>r.Table=="status"))continue;
                        var effect=GameStatusCodec.Restore(rows,player);
                        if(!Supported(effect))continue;
                        if(effect is SE_Harpooned harpoon)
                        {
                            harpoon.m_time+=(float)seconds;
                            bool ended=GameHarpoonRuntime.Broken(harpoon)||harpoon.m_ttl>0&&harpoon.m_time>harpoon.m_ttl;
                            Change(frame,ended?new[]{new PlayerChange("status",true,id)}:GameStatusCodec.Delta(frame.State,harpoon,harpoon.m_attacker.GetZDOID()));continue;
                        }
                        if(effect is SE_Spawn spawn&&!spawn.m_spawned&&spawn.m_time+seconds>spawn.m_delay)
                        {
                            spawn.m_spawned=true;
                            frame.Publish.Add(()=>{if(!player||!spawn.m_prefab)return;var instance=UnityEngine.Object.Instantiate(spawn.m_prefab,player.transform.TransformVector(spawn.m_spawnOffset),Quaternion.identity);var projectile=instance.GetComponent<Projectile>();if(projectile)projectile.Setup(player,Vector3.zero,-1,null,null,null);spawn.m_spawnEffect.Create(instance.transform.position,instance.transform.rotation);});
                        }
                        if(effect is SE_StaffGuard guard)
                        {
                            bool held=GameStaffGuardRuntime.CanHold(frame.State,player);
                            GameStaffGuardRules.Advance(guard,held,seconds,global::Overhaul.Utility.OverhaulConfig.StaffShieldRegenerationPerSecond?.Value??5f);
                            Change(frame,guard.IsDone()?new[]{new PlayerChange("status",true,id)}:GameStatusCodec.Delta(frame.State,guard,ZDOID.None));continue;
                        }
                        if(!(effect is SE_Burning) && !(effect is SE_Poison) && !(effect is SE_Stats) && !(effect is SE_Smoke))
                        {
                            // These native types only advance their age. Avoid
                            // IsDone on shields: it raises skills and emits FX.
                            effect.m_time+=(float)seconds;
                            if(effect.m_ttl>0&&effect.m_time>effect.m_ttl)
                            {
                                if(effect is SE_HealthUpgrade upgrade)
                                {
                                    if(upgrade.m_moreHealth>0){double maximum=PlayerResources.Read(frame.State,"max_health")+upgrade.m_moreHealth;Change(frame,new[]{PlayerResources.Row("max_health",maximum),PlayerResources.Row("health",maximum)});}
                                    if(upgrade.m_moreStamina>0)Change(frame,new[]{PlayerResources.Row("max_stamina",PlayerResources.Read(frame.State,"max_stamina")+upgrade.m_moreStamina)});
                                }
                                Change(frame,new[]{new PlayerChange("status",true,id)});
                            }
                            else Change(frame,GameStatusCodec.Delta(frame.State,effect,new ZDOID(Convert.ToInt64(header.Values[4]),checked((uint)Convert.ToInt64(header.Values[5])))));
                            continue;
                        }
                        double remaining=seconds;int iterations=0;
                        while(remaining>0 && !effect.IsDone() && PlayerResources.Read(frame.State,"health")>0)
                        {
                            float interval=effect is SE_Burning burning?burning.m_damageInterval:effect is SE_Poison poisoned?poisoned.m_damageInterval:1;
                            float timer=effect is SE_Burning fire?fire.m_timer:effect is SE_Poison venom?venom.m_timer:1;
                            if(interval<=0 || float.IsNaN(interval)||float.IsInfinity(interval) || ++iterations>16384)throw new InvalidOperationException("Invalid damage-over-time cadence");
                            // Jump directly to the next native damage event. No
                            // replay of every physics frame while SQL was busy.
                            double step=Math.Min(remaining,timer>0?timer:.02);
                            bool wet=effect is SE_Burning b && b.m_fireDamageLeft>0 && frame.State.Rows.Any(r=>r.Table=="status"&&Convert.ToInt32(r.Values[0])==SEMan.s_statusEffectWet);
                            // Float ages need an expiry step larger than their
                            // rounding unit; a fixed microsecond stalls at long TTLs.
                            double expiryEpsilon=Math.Max(.000001,Math.Abs((double)effect.m_ttl)*.000001);
                            bool Has(int hash)=>frame.State.Rows.Any(r=>(r.Table=="status"||r.Table=="effects")&&Convert.ToInt32(r.Values[0])==hash);
                            double ageRate=wet?6:effect is SE_Wet?1+(Has(SEMan.s_statusEffectCampFire)?10:0)+(Has(SEMan.s_statusEffectBurning)?50:0):1;
                            if(effect.m_ttl>0)step=Math.Min(step,Math.Max(expiryEpsilon,(effect.m_ttl-effect.m_time)/ageRate+expiryEpsilon));
                            void Event(double delay)=>step=Math.Min(step,Math.Max(expiryEpsilon,delay+expiryEpsilon));
                            if(effect is SE_Stats stats)
                            {
                                if(stats.m_tickInterval>0)Event(stats.m_tickInterval-stats.m_tickTimer);
                                if(stats.m_healthOverTimeTicks>0&&stats.m_healthOverTimeInterval>0)Event(stats.m_healthOverTimeInterval-stats.m_healthOverTimeTimer);
                                if(stats.m_staminaOverTime!=0&&stats.m_time<stats.m_staminaOverTimeDuration)step=Math.Min(step,stats.m_staminaOverTimeDuration-stats.m_time);
                                if(stats.m_eitrOverTime!=0&&stats.m_time<stats.m_eitrOverTimeDuration)step=Math.Min(step,stats.m_eitrOverTimeDuration-stats.m_time);
                            }
                            if(effect is SE_Puke puke)
                            {if(puke.m_removeInterval<=0||float.IsNaN(puke.m_removeInterval)||float.IsInfinity(puke.m_removeInterval))throw new InvalidOperationException("Invalid vomit interval");Event(puke.m_removeInterval-puke.m_removeTimer);}
                            if(effect is SE_Wet water&&!player.m_tolerateWater&&water.m_damageInterval>0)Event(water.m_damageInterval-water.m_timer);
                            if(effect is SE_Smoke smoke&&smoke.m_damageInterval>0)Event(smoke.m_damageInterval-smoke.m_timer);
                            effect.UpdateStatusEffect((float)step);remaining=Math.Max(0,remaining-step);
                        }
                        if(frame.Death!=null)break;
                        if(effect.IsDone())Change(frame,new[]{new PlayerChange("status",true,id)});
                        else Change(frame,GameStatusCodec.Delta(frame.State,effect,new ZDOID(Convert.ToInt64(header.Values[4]),checked((uint)Convert.ToInt64(header.Values[5])))));
                    }
                }));
                return new Result{Changes=frame.Changes.ToArray(),LastHit=frame.LastHit,Damage=frame.Damage,Lethal=PlayerResources.Read(frame.State,"health")<=0,Death=frame.Death,Publish=()=>{foreach(var action in frame.Publish)action();}};
            }
            finally{current=previous;}
        }
        [HarmonyPatch(typeof(Player),nameof(Player.RemoveOneFood))]
        private static class Vomit
        {
            [HarmonyPriority(Priority.First+400)]
            private static bool Prefix(Player __instance,ref bool __result)
            {
                if(current==null||current.Player!=__instance)return true;
                var foods=current.State.Rows.Where(r=>r.Table=="food").ToArray();
                if(foods.Length!=0)Change(current,new[]{new PlayerChange("food",true,foods[UnityEngine.Random.Range(0,foods.Length)].Values[0])});
                // Native SE_Puke only uses this return value to flash a local HUD.
                // Dedicated servers have none; the food rows supply the client view.
                __result=false;return false;
            }
        }
        [HarmonyPatch(typeof(SEMan),nameof(SEMan.AddStatusEffect),new[]{typeof(int),typeof(bool),typeof(int),typeof(float),typeof(short)})]
        private static class NestedStatus
        {
            [HarmonyPriority(Priority.First+400)]
            private static bool Prefix(SEMan __instance,int nameHash,bool resetTime,int itemLevel,float skillLevel,short variant,ref StatusEffect __result)
            {
                if(current==null||__instance.m_character!=current.Player)return true;
                bool existing=current.State.Rows.Any(row=>row.Table=="status"&&Convert.ToInt32(row.Values[0])==nameHash);
                if(!existing||resetTime)Change(current,GameStatusImpact.Prepare(current.State,current.Player,nameHash,itemLevel,skillLevel,variant,ZDOID.None));
                var rows=current.State.Rows.Where(row=>(row.Table=="status"||row.Table=="status_data")&&Convert.ToInt32(row.Values[0])==nameHash);
                __result=rows.Any(row=>row.Table=="status")?GameStatusCodec.Restore(rows,current.Player):null;return false;
            }
        }
        [HarmonyPatch(typeof(Character),nameof(Character.Damage))]
        private static class Impact
        {
            [HarmonyPriority(Priority.First+300)]
            private static bool Prefix(Character __instance,HitData hit)
            {
                if(current==null||__instance!=current.Player)return true;
                var plan=GamePlayerHit.Prepare(current.State,current.Player,hit);
                if(plan!=null)
                {
                    double before=PlayerResources.Read(current.State,"health");Change(current,plan.Change.Player.Changes);
                    current.Damage+=(float)Math.Max(0,before-PlayerResources.Read(current.State,"health"));current.LastHit=hit.Clone();current.Publish.Add(plan.Publish);
                    if(GameDeathProgress.IsDead(current.State))current.Death=plan;
                }
                return false;
            }
        }
        [HarmonyPatch]
        private static class Resources
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(Character),nameof(Character.Heal));
                foreach(string name in new[]{"AddStamina","AddEitr","UseStamina"})yield return AccessTools.Method(typeof(Player),name);
            }
            [HarmonyPriority(Priority.First+300)]
            private static bool Prefix(Character __instance,MethodBase __originalMethod,float __0)
            {
                if(current==null||__instance!=current.Player)return true;
                if(float.IsNaN(__0)||float.IsInfinity(__0))throw new InvalidOperationException("Invalid periodic resource amount");
                string method=__originalMethod.Name,key=method=="Heal"?"health":method=="AddEitr"?"eitr":"stamina";
                bool spend=method=="UseStamina";double value=PlayerResources.Read(current.State,key);
                if(key=="health"&&value<=0)return false;
                value=Math.Max(0,Math.Min(PlayerResources.Read(current.State,"max_"+key),value+(spend?-Math.Max(0,__0)*Game.m_staminaRate:__0)));
                Change(current,new[]{PlayerResources.Row(key,value)});
                if(spend&&__0>0)Change(current,new[]{PlayerResources.Row(PlayerResources.StaminaDelay,current.Player.m_staminaRegenDelay)});
                return false;
            }
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

