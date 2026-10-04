using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Overhaul.Persistence
{
    // Classify from the server prefab registry, never from client-supplied ZDO flags.
    internal static class GameCreatureAuthority
    {
        private static readonly Dictionary<int,bool> prefabs=new Dictionary<int,bool>();
        internal static bool Enabled=>PlayerPersistenceConfig.Enabled?.Value==true && ZNet.instance && ZNet.instance.IsServer();
        internal static void Clear()=>prefabs.Clear();
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
            private static bool Prefix(Character __instance,long sender)
            {return !__instance.m_nview || !__instance.m_nview.IsValid() || !Owns(__instance.m_nview.GetZDO()) || sender==ZNet.GetUID();}
        }
        [HarmonyPatch(typeof(SEMan),"RPC_AddStatusEffect")]
        private static class EffectOrigin
        {
            private static bool Prefix(SEMan __instance,long sender)
            {return !__instance.m_nview || !__instance.m_nview.IsValid() || !Owns(__instance.m_nview.GetZDO()) || sender==ZNet.GetUID();}
        }
    }
}
