using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    // Weather is selected at each actor's position. EnvMan's current environment
    // belongs to the local camera and cannot describe every server player.
    internal static class GameEnvironmentWeather
    {
        private sealed class Zone{internal EnvZone Value;internal double Seen;}
        private static readonly Dictionary<ZDOID,Zone> zones=new Dictionary<ZDOID,Zone>();
        internal static void Forget(ZDOID actor)=>zones.Remove(actor);
        internal static void Clear()=>zones.Clear();
        internal static EnvSetup Select(EnvMan manager,BiomeSector biome,Vector3 point,long seconds)
        {
            var random=UnityEngine.Random.state;
            try
            {
                UnityEngine.Random.InitState(unchecked((int)(seconds/Math.Max(1,manager.m_environmentDuration))));
                var available=manager.GetAvailableEnvironments(biome);if(available==null||available.Count==0)return null;
                var result=manager.SelectWeightedEnvironment(available);
                bool ashlands=WorldGenerator.IsAshlands(point.x,point.z),north=WorldGenerator.IsDeepnorth(point.x,point.z);
                foreach(var entry in available)if(entry.m_ashlandsOverride&&ashlands||entry.m_deepnorthOverride&&north)result=entry.m_env;
                return result;
            }
            finally{UnityEngine.Random.state=random;}
        }
        internal static EnvSetup Read(Player player)
        {
            var manager=EnvMan.instance;if(!manager||WorldGenerator.instance==null||!ZNet.instance)return null;
            var point=player.transform.position;var biome=WorldGenerator.instance.GetBiomeSector(point,false);
            bool ashlands=WorldGenerator.IsAshlands(point.x,point.z),north=WorldGenerator.IsDeepnorth(point.x,point.z);
            if(ashlands||north)
            {
                var heightmap=Heightmap.FindHeightmap(point);
                if(heightmap&&heightmap.GetWorldHeight(point,out var ground)&&ground<=manager.m_oceanLevelEnvCheckAshlandsDeepnorth)
                    biome=ZNet.World.m_biomeData.Biomes[ashlands?Heightmap.Biome.AshLands:Heightmap.Biome.DeepNorth].Sectors[0];
            }
            EnvSetup Named(string name)=>string.IsNullOrEmpty(name)?null:manager.GetEnv(name);
            var debug=Named(manager.m_debugEnv);if(debug!=null)return debug;
            bool inZone=zones.TryGetValue(player.GetZDOID(),out var zone)&&zone.Value&&Time.timeAsDouble-zone.Seen<.5;
            if(inZone&&zone.Value.m_force){var forced=Named(zone.Value.m_environment);if(forced!=null)return forced;}
            if(player.InIntro()){var intro=Named(manager.m_introEnvironment);if(intro!=null)return intro;}
            var events=RandEventSystem.instance;
            if(events)
            {
                foreach(var candidate in new[]{events.m_forcedEvent,events.m_randomEvent})
                    if(candidate!=null&&(candidate.m_biome&biome.Biome)!=Heightmap.Biome.None&&events.IsInsideRandomEventArea(candidate,point))
                    {var forced=Named(candidate.m_forceEnvironment);if(forced!=null)return forced;}
            }
            foreach(var alternate in biome.AltBiomes){var forced=Named(alternate.m_forceEnvironment);if(forced!=null)return forced;}
            var persistent=PersistentEventSystem.instance;
            if(persistent)foreach(var active in persistent.m_activePersistentEvents.list)
                if((active.position-point).sqrMagnitude<active.radius*active.radius)
                {var forced=Named(active.Source.GetEnvironmentOverride(point));if(forced!=null)return forced;}
            if(inZone)
            {var forced=Named(zone.Value.m_environment);if(forced!=null)return forced;}
            return Select(manager,biome,point,(long)ZNet.instance.GetTimeSeconds());
        }
        [HarmonyPatch(typeof(EnvZone),"OnTriggerStay")]
        private static class Enter
        {
            private static void Prefix(EnvZone __instance,Collider collider)
            {
                var player=collider?collider.GetComponent<Player>():null;if(!GameMovementRuntime.Managed(player))return;
                var id=player.GetZDOID();if(!zones.TryGetValue(id,out var zone))zones.Add(id,zone=new Zone());zone.Value=__instance;zone.Seen=Time.timeAsDouble;
            }
        }
        [HarmonyPatch(typeof(EnvZone),"OnTriggerExit")]
        private static class Exit
        {
            private static void Prefix(EnvZone __instance,Collider collider)
            {var player=collider?collider.GetComponent<Player>():null;if(player&&zones.TryGetValue(player.GetZDOID(),out var zone)&&zone.Value==__instance)zones.Remove(player.GetZDOID());}
        }
    }
}
