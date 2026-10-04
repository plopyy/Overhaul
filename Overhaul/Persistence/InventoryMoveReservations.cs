using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class InventoryMoveReservations
    {
        private sealed class Deferred
        {
            internal Component Target;
            internal MethodBase Method;
            internal object[] Arguments;
            internal ZDOID Id;
        }
        private static readonly List<Deferred> deferred = new List<Deferred>();
        private static readonly HashSet<ZDOID> destroyed = new HashSet<ZDOID>();
        private static bool Held(ZDOID id) => GamePersistence.ActionReserved(id) || GamePersistence.InventoryReserved(id);
        internal static bool Defer(Component target, MethodBase method, object[] arguments, bool once = false)
        {
            var view = target ? target.GetComponent<ZNetView>() : null;
            if (!view || !view.IsValid() || !Held(view.GetZDO().m_uid)) return false;
            if (!once || !deferred.Any(d => d.Target == target && d.Method == method))
                deferred.Add(new Deferred { Target = target, Method = method, Arguments = (object[])arguments.Clone(), Id = view.GetZDO().m_uid });
            return true;
        }
        internal static void Release(ZDOID id)
        {
            var ready = deferred.Where(d => d.Id == id).ToArray(); deferred.RemoveAll(d => d.Id == id);
            foreach (var action in ready) if (action.Target) action.Method.Invoke(action.Target, action.Arguments);
            if (destroyed.Remove(id)) ZDOMan.instance.HandleDestroyedZDO(id);
        }
        // A committed deletion supersedes delayed component callbacks (including resource drops).
        internal static void DiscardMutations(ZDOID id) => deferred.RemoveAll(d => d.Id == id);
        internal static void Clear() { deferred.Clear(); destroyed.Clear(); }
        [HarmonyPatch(typeof(ZDOMan),"HandleDestroyedZDO")]
        private static class NetworkDestruction
        {
            [HarmonyPriority(Priority.First + 200)]
            private static bool Prefix(ZDOID uid)
            { if (!Held(uid)) return true; destroyed.Add(uid); return false; }
        }
        [HarmonyPatch]
        private static class Ownership
        {
            private static IEnumerable<MethodBase> TargetMethods()
            { yield return AccessTools.Method(typeof(ZDO),nameof(ZDO.SetOwner)); yield return AccessTools.Method(typeof(ZDO),nameof(ZDO.SetOwnerInternal)); }
            private static void Prefix(ZDO __instance,ref long uid)
            { if (ZNet.instance && ZNet.instance.IsServer() && (Held(__instance.m_uid) || PlayerFishingCastGame.ServerOwned(__instance.m_uid) || GameCreatureAuthority.Owns(__instance) || GameWorldAuthority.Owns(__instance))) uid = ZNet.GetUID(); }
        }
        [HarmonyPatch(typeof(ZDOMan),"RPC_ZDOData")]
        private static class IncomingWorldData
        {
            [HarmonyPriority(Priority.First + 200)]
            private static bool Prefix(ZDOMan __instance,ZRpc rpc,ref ZPackage pkg)
            {
                if (!ZNet.instance || !ZNet.instance.IsServer() || !GamePersistence.HasReservations && !PlayerFishingCastGame.HasServerLines && !GameCreatureAuthority.Enabled) return true;
                var peer = __instance.FindPeer(rpc); if (peer == null) return false;
                try
                {
                    var input = new ZPackage(pkg.GetArray()); input.SetPos(pkg.GetPos()); var output = new ZPackage();
                    int count = input.ReadInt();
                    if (count < 0 || count > input.Size()/12) throw new System.IO.InvalidDataException("Invalid sector invalidation count");
                    var sectors = new List<ZDOID>();
                    for (int i = 0; i < count; i++) { var id = input.ReadZDOID(); if (!Held(id) && !PlayerFishingCastGame.ServerOwned(id) && !GameCreatureAuthority.Owns(id) && !GameWorldAuthority.Owns(id)) sectors.Add(id); }
                    output.Write(sectors.Count); foreach (var id in sectors) output.Write(id);
                    while (true)
                    {
                        var id = input.ReadZDOID(); if (id.IsNone()) { output.Write(id); break; }
                        ushort owner = input.ReadUShort(); uint revision = input.ReadUInt(); long ownerId = input.ReadLong();
                        var position = input.ReadVector3(); var body = input.ReadPackage();
                        if (Held(id) || PlayerFishingCastGame.ServerOwned(id) || GameCreatureAuthority.Owns(id) || GameWorldAuthority.Owns(id) || GameCreatureAuthority.IncomingCreature(body) || GameWorldAuthority.Incoming(body)) { peer.m_zdos.Remove(id); continue; }
                        output.Write(id); output.Write(owner); output.Write(revision); output.Write(ownerId); output.Write(position); output.Write(body);
                    }
                    if (input.GetPos() != input.Size()) throw new System.IO.InvalidDataException("Unexpected world data suffix");
                    pkg = new ZPackage(output.GetArray()); return true;
                }
                catch (Exception error)
                { ZLog.LogError("[Overhaul reserved world data] " + error); rpc.GetSocket().Close(); return false; }
            }
        }
        [HarmonyPatch(typeof(ZDOMan),"RPC_DestroyZDO")]
        private static class ProtectedDestruction
        {
            private static bool Prefix(long sender,ref ZPackage pkg)
            {
                if(!PlayerFishingCastGame.Enabled || !ZNet.instance || sender==(ZNet.instance.IsServer()?ZNet.GetUID():ZNet.instance.GetServerPeer()?.m_uid))return true;
                try
                {
                    var input=new ZPackage(pkg.GetArray());input.SetPos(pkg.GetPos());int count=input.ReadInt();
                    if(count<0 || count>(input.Size()-input.GetPos())/12)throw new System.IO.InvalidDataException("Invalid destroyed object count");
                    var keep=new List<ZDOID>();for(int i=0;i<count;i++){var id=input.ReadZDOID();if(!PlayerFishingCastGame.ServerOwned(id) && !GameCreatureAuthority.Owns(id) && !GameWorldAuthority.Owns(id))keep.Add(id);}
                    if(input.GetPos()!=input.Size())throw new System.IO.InvalidDataException("Invalid destroyed object suffix");
                    var output=new ZPackage();output.Write(keep.Count);foreach(var id in keep)output.Write(id);pkg=output;return true;
                }
                catch(Exception error){ZLog.LogWarning("[Overhaul protected object deletion] "+error.Message);return false;}
            }
        }
        [HarmonyPatch(typeof(ZDOMan),"HandleDestroyedZDO")]
        private static class RemoveProtectedLine
        {private static void Postfix(ZDOMan __instance,ZDOID uid){if(__instance.GetZDO(uid)==null)PlayerFishingCastGame.RemoveLine(uid);}}
        [HarmonyPatch]
        private static class Destruction
        {
            private static IEnumerable<MethodBase> TargetMethods()
            { yield return AccessTools.Method(typeof(WearNTear), "Destroy"); yield return AccessTools.Method(typeof(Destructible), "Destroy"); }
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(Component __instance, MethodBase __originalMethod, object[] __args)
            {
                return !Defer(__instance,__originalMethod,__args,true);
            }
        }
    }
}

