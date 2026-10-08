using HarmonyLib;
using UnityEngine;

namespace Overhaul.Dungeons
{
    [HarmonyPatch(typeof(TombStone), nameof(TombStone.Setup))]
    internal static class DungeonDeathGrave
    {
        private static void Postfix(TombStone __instance)
        {
            var view=__instance.GetComponent<ZNetView>();
            if(!view || !view.IsValid() || !view.IsOwner() || !Character.InInterior(__instance.transform.position))return;
            if(!TryExterior(__instance.transform.position,out var destination))
            { Utility.Log.LogWarning("Dungeon : sortie introuvable, tombe conservee a sa position pour ne pas perdre son inventaire.");return; }
            var zdo=view.GetZDO();
            zdo.Set(ZDOVars.s_spawnPoint,destination);zdo.SetPosition(destination);
            zdo.Set(ZDOVars.s_velHash,Vector3.zero);zdo.Set(ZDOVars.s_bodyVelHash,Vector3.zero);zdo.Set(ZDOVars.s_bodyAVelHash,Vector3.zero);
            __instance.transform.position=destination;
            var body=__instance.GetComponent<Rigidbody>();
            if(body){body.position=destination;body.linearVelocity=Vector3.zero;body.angularVelocity=Vector3.zero;}
            ZDOMan.instance.ForceSendZDO(zdo.m_uid);
            Utility.Log.LogInfo("Dungeon : tombe placee a l'entree exterieure "+destination);
        }

        internal static bool TryExterior(Vector3 death,out Vector3 destination)
        {
            destination=Vector3.zero;
            if(!ZNetScene.instance || !ZoneSystem.instance)return false;
            Teleport exit=null;
            foreach(var view in ZNetScene.instance.m_instances.Values)
            {
                if(!view)continue;
                var proxy=view.GetComponent<LocationProxy>();
                if(!proxy || !proxy.m_instance)continue;
                var data=view.GetZDO();
                if(death.y>=11000)
                {if(!BossInteriorReservation.Lane(data) || !BossDungeonLayout.InLane(data,death))continue;}
                else if(ZoneSystem.GetZone(view.transform.position)!=ZoneSystem.GetZone(death))continue;
                foreach(var teleport in proxy.m_instance.GetComponentsInChildren<Teleport>())
                {
                    if(!Character.InInterior(teleport.transform.position) || !teleport.m_targetPoint || Character.InInterior(teleport.m_targetPoint.transform.position))continue;
                    if(exit && exit!=teleport.m_targetPoint)return false;
                    exit=teleport.m_targetPoint;
                }
            }
            if(!exit)return false;
            var entrance=exit.GetTeleportPoint();
            int mask=ZoneSystem.instance.m_solidRayMask|ZoneSystem.instance.m_terrainRayMask;
            Physics.SyncTransforms();
            for(int i=0;i<24;i++)
            {
                var candidate=entrance+Quaternion.Euler(0,i*137.5f,0)*Vector3.forward*(3f+i/8);
                if(!Physics.Raycast(candidate+Vector3.up*6,Vector3.down,out var hit,12,mask,QueryTriggerInteraction.Ignore)||hit.normal.y<.7f)continue;
                candidate=hit.point+Vector3.up*.5f;
                if(Physics.CheckCapsule(candidate+Vector3.up*.2f,candidate+Vector3.up, .6f,mask,QueryTriggerInteraction.Ignore))continue;
                destination=candidate;return true;
            }
            // The native arrival point is the fallback, never an arbitrary map coordinate.
            destination=entrance+Vector3.up*.5f;return true;
        }
    }
}
