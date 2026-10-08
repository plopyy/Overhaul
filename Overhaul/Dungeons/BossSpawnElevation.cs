using HarmonyLib;
using UnityEngine;

namespace Overhaul.Dungeons
{
    [HarmonyPatch]
    internal static class BossSpawnElevation
    {
        private static System.Reflection.MethodBase[] TargetMethods()=>new System.Reflection.MethodBase[]
        {
            AccessTools.Method(typeof(RandomSpawn),nameof(RandomSpawn.Randomize)),
            AccessTools.Method(typeof(RandomObject),nameof(RandomObject.Randomize))
        };
        private static void Prefix(ref Vector3 pos,DungeonGenerator dg)
        {
            if(!dg || BossNavigation.Lane(dg.transform.position.y)==0)return;
            // Evaluate native spawn restrictions in the original crypt height band.
            // This changes only Randomize's test position, never the generated object's position.
            pos.y-=dg.transform.position.y-5000f;
        }
    }
}
