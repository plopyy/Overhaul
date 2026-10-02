using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Dungeons
{
    internal static class BossMistlandsNavigation
    {
        // Measured three-star walking/turning mesh at Scale=2 needs substantially
        // more room than its 2.4 m collision radius. Reserve room for legs and turns.
        internal const float RadiusPerScale=3.6f, HeightPerScale=3f;
        private static readonly ConditionalWeakTable<Pathfinding,Dictionary<Vector2,Pathfinding.AgentType>> agents=new ConditionalWeakTable<Pathfinding,Dictionary<Vector2,Pathfinding.AgentType>>();
        internal static void Ensure(BaseAI ai)
        {
            var character=ai.m_character;var paths=Pathfinding.instance;
            if(!paths||!character||Utils.GetPrefabName(character.gameObject)!="SeekerBrute"||BossModifiers.Data(character)==null)return;
            var scale=character.transform.lossyScale;
            float horizontal=Mathf.Max(Mathf.Abs(scale.x),Mathf.Abs(scale.z));
            var size=new Vector2(Mathf.Ceil(Mathf.Max(character.m_collider.radius*horizontal,RadiusPerScale*horizontal)*10)/10,
                Mathf.Ceil(Mathf.Max(character.m_collider.height,HeightPerScale)*Mathf.Abs(scale.y)*10)/10);
            var cache=agents.GetOrCreateValue(paths);
            if(!cache.TryGetValue(size,out var agent))
            {
                agent=(Pathfinding.AgentType)paths.m_agentSettings.Count;
                var settings=paths.AddAgent(agent,paths.m_agentSettings[(int)Pathfinding.AgentType.HumanoidBig]);
                settings.m_build.agentRadius=size.x;settings.m_build.agentHeight=size.y;
                settings.m_build.overrideVoxelSize=true;settings.m_build.voxelSize=.25f;
                cache.Add(size,agent);
            }
            if(ai.m_pathAgentType!=agent){ai.m_pathAgentType=agent;ai.m_lastFindPathTime=-1000;ai.m_path.Clear();}
        }
        [HarmonyPatch(typeof(BaseAI),"FindPath")]
        private static class FindPath
        {
            private static void Prefix(BaseAI __instance)=>Ensure(__instance);
        }
        [HarmonyPatch(typeof(BaseAI),nameof(BaseAI.HavePath))]
        private static class HavePath
        {
            private static void Prefix(BaseAI __instance)=>Ensure(__instance);
        }
    }
}
