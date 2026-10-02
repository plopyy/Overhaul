using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Dungeons
{
    // Separate native navigation tiles per vertical dungeon lane, keeping surface tiles intact.
    internal static class BossNavigation
    {
        internal const int Stride=1024;
        internal static int Lane(float y)=>y>=11936&&y<11936+BossInteriorReservation.Lanes*128?Mathf.RoundToInt((y-12000)/128)+1:0;
        internal static float TileHeight(float original,Pathfinding.NavMeshTile tile)=>tile.m_tile.z>=Stride?96:original;
        internal static float GroundTop(float original,Vector3 point)=>Lane(point.y)>0?12000+(Lane(point.y)-1)*128+48:original;
        internal static float GroundRange(float original,Vector3 point)=>Lane(point.y)>0?96:original;
    }
    [HarmonyPatch(typeof(Pathfinding),"GetTile")]
    internal static class BossNavigationTile
    {
        private static void Postfix(Vector3 point,ref Vector3Int __result)
        {__result.z+=BossNavigation.Lane(point.y)*BossNavigation.Stride;}
    }
    [HarmonyPatch(typeof(Pathfinding),"GetTilePos")]
    internal static class BossNavigationPosition
    {
        private static void Postfix(Vector3Int id,ref Vector3 __result)
        {if(id.z>=BossNavigation.Stride)__result.y=12000+(id.z/BossNavigation.Stride-1)*128;}
    }
    [HarmonyPatch(typeof(Pathfinding),"GetSettings")]
    internal static class BossNavigationSettings
    {
        private static void Prefix(ref Pathfinding.AgentType agentType)
        {agentType=(Pathfinding.AgentType)((int)agentType%BossNavigation.Stride);}
    }
    [HarmonyPatch(typeof(Pathfinding),"BuildTile")]
    internal static class BossNavigationBounds
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int count=0;
            foreach(var instruction in instructions)
            {
                yield return instruction;
                if(instruction.opcode==OpCodes.Ldc_R4&&(float)instruction.operand==6000f)
                {count++;yield return new CodeInstruction(OpCodes.Ldarg_1);yield return CodeInstruction.Call(typeof(BossNavigation),nameof(BossNavigation.TileHeight));}
            }
            if(count!=2)throw new InvalidOperationException("Native navigation build bounds changed");
        }
    }
    [HarmonyPatch(typeof(Pathfinding),"FindGround")]
    internal static class BossNavigationGround
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int count=0;
            foreach(var instruction in instructions)
            {
                yield return instruction;
                if(instruction.opcode!=OpCodes.Ldc_R4)continue;
                float value=(float)instruction.operand;if(value!=6000f&&value!=10000f)continue;
                count++;yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return CodeInstruction.Call(typeof(BossNavigation),value==6000f?nameof(BossNavigation.GroundTop):nameof(BossNavigation.GroundRange));
            }
            if(count!=2)throw new InvalidOperationException("Native navigation ground scan changed");
        }
    }
}
