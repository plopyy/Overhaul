using System;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Storage
{
    internal static class UnloadedProduction
    {
        private static readonly int TimeKey="overhaul_production_time_v1".GetStableHashCode();
        private static readonly int PowerKey="overhaul_production_power_v1".GetStableHashCode();
        private static readonly int WorkingKey="overhaul_production_working_v1".GetStableHashCode();

        // Keep native float rounding and one-second production semantics, but
        // work on local numbers, stop when empty, and bound the catch-up budget.
        internal static int Advance(ref float fuel,ref float bake,ref double seconds,float duration,float power,int fuelPerProduct,bool usesFuel,int queued)
        {
            if(seconds<1)return 0;
            if(duration<=0 || power<=0 || queued<=0 || usesFuel && fuel<=0)
            {seconds-=Math.Floor(seconds);return 0;}
            int budget=10000,produced=0;
            float burn=usesFuel ? power/(duration/(float)fuelPerProduct) : 0;
            while(seconds>=1 && produced<queued && (!usesFuel || fuel>0) && budget-->0)
            {
                seconds-=1;
                if(usesFuel){fuel-=burn;if(fuel<.0001f)fuel=0;}
                bake+=power;
                if(bake>=duration){bake=0;produced++;}
            }
            if(produced>=queued || usesFuel && fuel<=0)seconds-=Math.Floor(seconds);
            return produced;
        }

        [HarmonyPatch(typeof(Smelter),"UpdateSmelter")]
        private static class Update
        {
            private static bool Prefix(Smelter __instance)
            {
                var smelter=__instance;
                if(!smelter.m_nview || !smelter.m_nview.IsValid() || !smelter.m_nview.IsOwner())return true;
                // Leave exotic source-free modded producers on their native path.
                if(smelter.m_maxOre<=0 || smelter.m_secPerProduct<=0)return true;
                if(!ProductionClock.Ready)return false;
                var data=smelter.m_nview.GetZDO();double now=ProductionClock.Now;
                long previous=data.GetLong(TimeKey,-1);
                double elapsed=previous<0 ? Math.Max(0,(ZNet.instance.GetTime().Ticks-data.GetLong(ZDOVars.s_startTime,ZNet.instance.GetTime().Ticks))/(double)TimeSpan.TicksPerSecond) : Math.Max(0,now-previous/(double)TimeSpan.TicksPerSecond);
                smelter.UpdateRoof();smelter.UpdateSmoke();smelter.UpdateState();
                float currentPower=smelter.m_windmill?smelter.m_windmill.GetPowerOutput():1f;
                bool currentWorking=(!smelter.m_requiresRoof || smelter.m_haveRoof) && !smelter.m_blockedSmoke;
                float accumulator=smelter.GetAccumulator();
                bool catchup=elapsed>2.5 || accumulator>=1;
                float power=catchup?data.GetFloat(PowerKey,currentPower):currentPower;
                bool working=catchup?data.GetBool(WorkingKey,currentWorking):currentWorking;
                double total=Math.Max(0,accumulator+elapsed);
                float fuel=smelter.GetFuel(),bake=smelter.GetBakeTimer();
                int produced=working?Advance(ref fuel,ref bake,ref total,smelter.m_secPerProduct,power,smelter.m_fuelPerProduct,smelter.m_maxFuel>0,smelter.GetQueueSize()):0;
                if(!working)total-=Math.Floor(total);
                // Commit native state before publishing outputs. Repeated updates
                // use the new clock value and cannot replay the same elapsed time.
                data.Set(TimeKey,(long)(now*TimeSpan.TicksPerSecond));
                data.Set(PowerKey,total>=1?power:currentPower);data.Set(WorkingKey,total>=1?working:currentWorking);
                data.Set(ZDOVars.s_startTime,ZNet.instance.GetTime().Ticks);
                smelter.SetAccumulator((float)total);
                smelter.SetFuel(fuel);smelter.SetBakeTimer(bake);
                bool spawnStack=smelter.m_spawnStack;
                try
                {
                    if(catchup)smelter.m_spawnStack=true;
                    for(int i=0;i<produced;i++)
                    {
                        string ore=smelter.GetQueuedOre();smelter.RemoveOneOre();smelter.QueueProcessed(ore);
                    }
                    if(smelter.GetQueuedOre()=="" || smelter.m_maxFuel>0 && fuel==0 || catchup)smelter.SpawnProcessed();
                }
                finally {smelter.m_spawnStack=spawnStack;}
                return false;
            }
        }
    }
}
