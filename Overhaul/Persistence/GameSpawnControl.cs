using System;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    // The native client still loads its scene and plays the arrival presentation,
    // but receives the spawn position from its admitted server session.
    internal static class GameSpawnControl
    {
        private static int generation;
        private static bool ready, consumed;
        private static Vector3 position;
        private static int kind;
        private static double nextRequest;
        internal static void Clear() { generation=0;ready=false;consumed=false;nextRequest=0; }
        internal static void Respawn() { generation=checked(generation+1);ready=false;consumed=false;nextRequest=0; }
        internal static ZPackage Encode(int epoch,Vector3 point,int source)
        {var packet=new ZPackage();packet.Write(epoch);packet.Write(point);packet.Write((byte)source);return packet;}
        internal static bool Receive(ZPackage packet)
        {
            if(packet.Size()!=17)return false;
            int epoch=packet.ReadInt();var point=packet.ReadVector3();int source=packet.ReadByte();
            if(consumed||epoch!=generation||source>2||!Finite(point.x)||!Finite(point.y)||!Finite(point.z))return false;
            position=point;kind=source;ready=true;return true;
        }
        private static bool Finite(float value)=>!float.IsNaN(value)&&!float.IsInfinity(value)&&Math.Abs(value)<=1000000;

        [HarmonyPatch(typeof(Game),"FindSpawnPoint")]
        private static class Find
        {
            private static bool Prefix(Game __instance,ref Vector3 point,ref bool usedLogoutPoint,float dt,ref bool __result)
            {
                if(!PlayerSessionGame.Managed)return true;
                point=Vector3.zero;usedLogoutPoint=false;__result=false;__instance.m_respawnWait+=dt;
                if(!ready)
                {
                    if(!consumed&&Time.timeAsDouble>=nextRequest)
                    {nextRequest=Time.timeAsDouble+.25;InventoryMoveGame.Client?.SpawnRequest(generation);}
                    return false;
                }
                ZNet.instance.SetReferencePosition(position);
                if(kind<2&&__instance.m_respawnWait<=__instance.m_respawnLoadDuration||!ZNetScene.instance.IsAreaReady(position))return false;
                point=position;usedLogoutPoint=kind==0;__result=true;return false;
            }
        }
        [HarmonyPatch(typeof(Game),"SpawnPlayer")]
        private static class Spawned
        {
            private static void Prefix(){if(PlayerSessionGame.Managed)GameCharacterView.PrepareSpawn();}
            private static void Postfix(){if(PlayerSessionGame.Managed){consumed=true;ready=false;}}
        }
    }
}
