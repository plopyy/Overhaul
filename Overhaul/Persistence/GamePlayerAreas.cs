using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    // The native dedicated server only keeps a scene around its reference position.
    // Server action validation needs the physical scene around every admitted player.
    internal static class GamePlayerAreas
    {
        private static readonly List<Vector3> centers=new List<Vector3>();
        private static readonly HashSet<Vector2s> centerZones=new HashSet<Vector2s>(),zones=new HashSet<Vector2s>();
        private static readonly HashSet<ZDO> nearSet=new HashSet<ZDO>(),distantSet=new HashSet<ZDO>();
        private static readonly List<ZDO> scratchNear=new List<ZDO>(),scratchDistant=new List<ZDO>(),candidates=new List<ZDO>();
        private static float refreshed=-1;
        private static int cursor;
        private static bool spawning;
        private static bool Enabled=>PlayerPersistenceConfig.Enabled?.Value==true && ZNet.instance && ZNet.instance.IsServer();
        internal static void Clear()
        {centers.Clear();centerZones.Clear();zones.Clear();nearSet.Clear();distantSet.Clear();scratchNear.Clear();scratchDistant.Clear();candidates.Clear();refreshed=-1;cursor=0;spawning=false;GameCreatureAuthority.Clear();}
        internal static void Refresh(bool force=false)
        {
            if(!force && Time.time<refreshed+.1f)return;refreshed=Time.time;
            centers.Clear();centerZones.Clear();zones.Clear();
            void Center(Vector3 position)
            {
                if(float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z) || Math.Abs(position.x)>1000000 || Math.Abs(position.y)>1000000 || Math.Abs(position.z)>1000000)return;
                var zone=ZoneSystem.GetZone(position);if(!centerZones.Add(zone))return;centers.Add(position);
                var system=ZoneSystem.instance;var distance=ZNet.instance.GetSyncedSimulationDistance();int radius=distance.NearSimulationDistance;
                for(int y=zone.y-radius;y<=zone.y+radius;y++)for(int x=zone.x-radius;x<=zone.x+radius;x++)
                {var candidate=new Vector2s(x,y);if(distance.IsClassic || system.ZonesWithinRadius(zone,candidate,radius,false))zones.Add(candidate);}
            }
            // A host also needs its normal reference area while its own character is spawning.
            if(!ZNet.instance.IsDedicated())Center(ZNet.instance.GetReferencePosition());
            foreach(var actor in PlayerSessionGame.ActiveActors())Center(actor.GetPosition());
            // Keep the supporting terrain alive until outstanding actions have published.
            // Keeping only their ZNetViews would let ZoneSystem unload the ground below them.
            if(ZNetScene.instance)foreach(var pair in ZNetScene.instance.m_instances)
                if(GamePersistence.ActionReserved(pair.Key.m_uid) || GamePersistence.InventoryReserved(pair.Key.m_uid))zones.Add(pair.Key.GetSector());
        }
        internal static bool Contains(Vector3 position)
        {Refresh();foreach(var center in centers)if(ZNetScene.InActiveArea(position,center))return true;return false;}
        internal static void Collect(ZNetScene scene)
        {
            Refresh();nearSet.Clear();distantSet.Clear();
            foreach(var center in centers)
            {
                scratchNear.Clear();scratchDistant.Clear();
                ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(center),ZNet.instance.GetSyncedSimulationDistance(),scratchNear,scratchDistant);
                foreach(var data in scratchNear)nearSet.Add(data);foreach(var data in scratchDistant)distantSet.Add(data);
            }
            // A committed action must finish publishing even if its player leaves the area.
            foreach(var pair in scene.m_instances)
                if(GamePersistence.ActionReserved(pair.Key.m_uid) || GamePersistence.InventoryReserved(pair.Key.m_uid))nearSet.Add(pair.Key);
            distantSet.ExceptWith(nearSet);
            foreach(var data in nearSet)GameCreatureAuthority.Claim(data);
            scene.m_tempCurrentObjects.Clear();scene.m_tempCurrentObjects.AddRange(nearSet);
            scene.m_tempCurrentDistantObjects.Clear();scene.m_tempCurrentDistantObjects.AddRange(distantSet);
        }
        private static void Create(ZNetScene scene)
        {
            candidates.Clear();
            foreach(var data in scene.m_tempCurrentObjects)
            {
                if(data.Created || !ZoneSystem.instance.m_zones.ContainsKey(data.GetSector()) || !ZoneSystem.instance.IsZoneReadyForType(data.GetSector(),data.Type) || !scene.GetPrefab(data.GetPrefab()))continue;
                float distance=float.MaxValue;foreach(var center in centers)distance=Mathf.Min(distance,(data.GetPosition()-center).sqrMagnitude);
                data.m_tempSortValue=distance;candidates.Add(data);
            }
            candidates.Sort((a,b)=>a.Type==b.Type?a.m_tempSortValue.CompareTo(b.m_tempSortValue):((int)b.Type).CompareTo((int)a.Type));
            long started=Stopwatch.GetTimestamp();int count=0;
            foreach(var data in candidates)
            {
                scene.CreateObject(data);count++;
                if(count>=16 || Stopwatch.GetTimestamp()-started>=Stopwatch.Frequency/500)break;
            }
            // Distant visuals are not needed by a dedicated simulation. Hosts retain them.
            if(!ZNet.instance.IsDedicated() && count<16)scene.CreateDistantObjects(scene.m_tempCurrentDistantObjects,16,ref count);
        }
        [HarmonyPatch(typeof(ZoneSystem),"CreateLocalZones")]
        private static class LocalZones
        {
            private static bool Prefix(ZoneSystem __instance,ref bool __result)
            {
                if(!Enabled || spawning)return true;Refresh();
                foreach(var zone in zones)if(__instance.m_zones.TryGetValue(zone,out var data))data.m_ttl=0;
                __result=false;if(centers.Count==0)return false;
                spawning=true;
                try
                {
                    for(int i=0;i<centers.Count;i++)
                    {int index=(cursor+i)%centers.Count;if(__instance.CreateLocalZones(centers[index])){cursor=(index+1)%centers.Count;__result=true;break;}}
                }
                finally{spawning=false;}
                return false;
            }
        }
        [HarmonyPatch(typeof(ZNetScene),"CreateDestroyObjects")]
        private static class Scene
        {
            private static bool Prefix(ZNetScene __instance)
            {
                if(!Enabled)return true;
                Collect(__instance);Create(__instance);__instance.RemoveObjects(__instance.m_tempCurrentObjects,__instance.m_tempCurrentDistantObjects);return false;
            }
        }
        [HarmonyPatch(typeof(ZNetScene),nameof(ZNetScene.OutsideActiveArea),new[]{typeof(Vector3)})]
        private static class ActiveArea
        {private static bool Prefix(Vector3 point,ref bool __result){if(!Enabled)return true;__result=!Contains(point);return false;}}
    }
}
