using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameCartographyRuntime
    {
        private static readonly HashSet<ZDOID> busy=new HashSet<ZDOID>();
        internal static bool Reserve(ZDOID id)=>busy.Add(id);
        internal static void Release(ZDOID id)=>busy.Remove(id);
        internal static MapTable Resolve(ZDO actor,ZDOID id)
        {
            if(actor==null)return null;var instance=ZNetScene.instance.FindInstance(id);var table=instance?instance.GetComponent<MapTable>():null;
            if(!table||!table.m_nview||!table.m_nview.IsValid()||Vector3.Distance(actor.GetPosition(),table.transform.position)>5||
                !Storage.ChestAccess.WardAccessAt(table.transform.position,actor.GetLong(ZDOVars.s_playerID,0)))return null;
            return table;
        }
        [HarmonyPatch(typeof(MapTable),"OnRead",new[]{typeof(Switch),typeof(Humanoid),typeof(ItemDrop.ItemData),typeof(bool)})]
        private static class Read
        {private static bool Prefix(MapTable __instance,Humanoid user,ItemDrop.ItemData item,ref bool __result){if(!PlayerSessionGame.Managed||user!=Player.m_localPlayer)return true;__result=false;if(item==null)Send(__instance,false);return false;}}
        [HarmonyPatch(typeof(MapTable),"OnWrite")]
        private static class Write
        {private static bool Prefix(MapTable __instance,Humanoid user,ItemDrop.ItemData item,ref bool __result){if(!PlayerSessionGame.Managed||user!=Player.m_localPlayer)return true;__result=false;if(item==null){Send(__instance,true);__result=true;}return false;}}
        private static void Send(MapTable table,bool write)
        {if(table.m_nview&&table.m_nview.IsValid()){GameMapPins.Tick();InventoryMoveGame.Client?.ShareMap(table.m_nview.GetZDO().m_uid,write);}}
        [HarmonyPatch(typeof(MapTable),"RPC_MapData")]
        private static class Legacy{private static bool Prefix()=>!GameCreatureAuthority.Enabled;}
    }
}
