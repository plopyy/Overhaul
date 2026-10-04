using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    // World resources, storage and production must use the same authority as
    // the player action that consumes, damages or creates them.
    internal static class GameWorldAuthority
    {
        private static readonly Dictionary<int,bool> prefabs=new Dictionary<int,bool>();
        internal static void Clear()=>prefabs.Clear();
        internal static bool Owns(ZDOID id)=>Owns(ZDOMan.instance?.GetZDO(id));
        internal static bool Owns(ZDO data)=>data!=null && Definition(data.GetPrefab());
        internal static bool Definition(int hash)
        {
            if(!GameCreatureAuthority.Enabled || !ZNetScene.instance)return false;
            if(!prefabs.TryGetValue(hash,out bool owned))
            {
                var prefab=ZNetScene.instance.GetPrefab(hash);if(!prefab)return false;
                owned=!prefab.GetComponent<Character>() && (prefab.GetComponent<IDestructible>()!=null || prefab.GetComponent<ItemDrop>() ||
                    prefab.GetComponent<Container>() || prefab.GetComponent<TerrainComp>() || prefab.GetComponent<Smelter>() ||
                    prefab.GetComponent<CookingStation>() || prefab.GetComponent<Fermenter>() || prefab.GetComponent<Fireplace>() ||
                    prefab.GetComponent<Beehive>() || prefab.GetComponent<SapCollector>());
                prefabs.Add(hash,owned);
            }
            return owned;
        }
        internal static bool Incoming(ZPackage body)
        {
            if(!GameCreatureAuthority.Enabled)return false;
            int position=body.GetPos();
            try{body.ReadUShort();return Definition(body.ReadInt());}
            finally{body.SetPos(position);}
        }
        internal static void Claim(ZDO data)
        {if(Owns(data) && data.GetOwner()!=ZNet.GetUID())data.SetOwner(ZNet.GetUID());}
        internal static bool Allowed(Component target,long sender)
        {var view=target.GetComponent<ZNetView>();return !view || !view.IsValid() || !Owns(view.GetZDO()) || sender==ZNet.GetUID();}
        [HarmonyPatch(typeof(ItemDrop),"RPC_RequestOwn")]
        private static class GroundOwnership
        {private static bool Prefix(ItemDrop __instance,long uid)=>Allowed(__instance,uid);}
        [HarmonyPatch]
        private static class Mutation
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach(var type in new[]{typeof(TreeBase),typeof(TreeLog),typeof(Destructible),typeof(MineRock5),typeof(WearNTear)})
                    yield return AccessTools.Method(type,"RPC_Damage");
                yield return AccessTools.Method(typeof(MineRock),"RPC_Hit");
                yield return AccessTools.Method(typeof(WearNTear),"RPC_Remove");
                yield return AccessTools.Method(typeof(ItemDrop),"RPC_MakePiece");
            }
            private static bool Prefix(Component __instance,long sender,MethodBase __originalMethod,object[] __args)
            {
                if(!Allowed(__instance,sender))return false;
                return !InventoryMoveReservations.Defer(__instance,__originalMethod,__args);
            }
        }
    }
}
