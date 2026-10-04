using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Overhaul.Persistence
{
    // Classify from the server prefab registry, never from client-supplied ZDO flags.
    internal static class GameCreatureAuthority
    {
        private static readonly Dictionary<int,bool> prefabs=new Dictionary<int,bool>();
        [ThreadStatic] private static Character localMutation;
        internal static bool Enabled=>PlayerPersistenceConfig.Enabled?.Value==true && ZNet.instance && ZNet.instance.IsServer();
        internal static void Clear(){prefabs.Clear();localMutation=null;}
        internal static bool Owns(ZDOID id)=>Owns(ZDOMan.instance?.GetZDO(id));
        internal static bool Owns(ZDO data)
        {
            return data!=null && CreaturePrefab(data.GetPrefab());
        }
        internal static bool CreaturePrefab(int hash)
        {
            if(!Enabled || !ZNetScene.instance)return false;
            if(!prefabs.TryGetValue(hash,out bool creature))
            {
                var prefab=ZNetScene.instance.GetPrefab(hash);
                if(!prefab)return false; // Mod registration may still be in progress.
                creature=!prefab.GetComponent<Player>() && (prefab.GetComponent<Character>() || prefab.GetComponent<Fish>() || prefab.GetComponent<RandomFlyingBird>());
                prefabs.Add(hash,creature);
            }
            return creature;
        }
        internal static bool IncomingCreature(ZPackage body)
        {
            if(!Enabled)return false;
            int position=body.GetPos();
            try{body.ReadUShort();return CreaturePrefab(body.ReadInt());}
            finally{body.SetPos(position);}
        }
        internal static void Claim(ZDO data)
        {if(Owns(data) && data.GetOwner()!=ZNet.GetUID())data.SetOwner(ZNet.GetUID());}

        // Native damage messages contain damage amounts; only the server may originate
        // them for server-simulated creatures. Player attack intents use a separate path.
        [HarmonyPatch]
        private static class DamageOrigin
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach(string name in new[]{"RPC_Damage","RPC_Heal","RPC_AddAdrenaline","RPC_SetTamed","RPC_Stagger","RPC_TeleportTo","RPC_FreezeFrame"})
                    yield return AccessTools.Method(typeof(Character),name);
            }
            private static bool Prefix(Character __instance,long sender,MethodBase __originalMethod,object[] __args)
            {
                if(!Allowed(__instance,sender))return false;
                if(!__instance.m_nview || !__instance.m_nview.IsValid() || !GamePersistence.ActionReserved(__instance.m_nview.GetZDO().m_uid))return true;
                // A native owner-local call uses zero; its deferred replay no longer
                // has the local call stack, so retain the verified server origin.
                if(sender==0 && localMutation==__instance)__args[0]=ZNet.GetUID();
                return !InventoryMoveReservations.Defer(__instance,__originalMethod,__args);
            }
        }
        internal static bool Allowed(Character character,long sender)=>!character.m_nview || !character.m_nview.IsValid() || !Owns(character.m_nview.GetZDO()) || sender==ZNet.GetUID() || sender==0 && localMutation==character;
        // Native owner-side Heal/Stagger call their RPC handlers directly with sender 0.
        // Permit that call stack without treating arbitrary zero-sender RPCs as trusted.
        [HarmonyPatch]
        private static class LocalMutation
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {yield return AccessTools.Method(typeof(Character),nameof(Character.Heal));yield return AccessTools.Method(typeof(Character),nameof(Character.Stagger));}
            private static void Prefix(Character __instance,out Character __state)
            {__state=localMutation;localMutation=__instance.m_nview && __instance.m_nview.IsValid() && Owns(__instance.m_nview.GetZDO())?__instance:null;}
            private static void Finalizer(Character __state)=>localMutation=__state;
        }
        [HarmonyPatch(typeof(SEMan),"RPC_AddStatusEffect")]
        private static class EffectOrigin
        {
            private static bool Prefix(SEMan __instance,long sender)
            {return !__instance.m_nview || !__instance.m_nview.IsValid() || !Owns(__instance.m_nview.GetZDO()) || sender==ZNet.GetUID();}
        }
    }
}
