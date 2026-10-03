using System;
using HarmonyLib;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace Overhaul.Storage
{
    // One persistent data object per world. No production zones are loaded.
    internal static class ProductionClock
    {
        internal const string PrefabName="Overhaul_ProductionClock",Rpc="Overhaul_ProductionClock_v1";
        private static readonly int PrefabHash=PrefabName.GetStableHashCode(),TicksKey="overhaul_production_ticks".GetStableHashCode();
        private static ZDOMan world;
        private static ZDO clock;
        private static double baseSeconds,baseRealtime,nextSend;
        internal static bool Ready {get;private set;}
        internal static double Now=>baseSeconds+Math.Max(0,Time.realtimeSinceStartupAsDouble-baseRealtime);
        internal static void Initialize()=>PrefabManager.OnVanillaPrefabsAvailable+=Register;
        internal static void Shutdown(){PrefabManager.OnVanillaPrefabsAvailable-=Register;Clear();}
        private static void Register()
        {
            if(PrefabManager.Instance.GetPrefab(PrefabName))return;
            var prefab=new GameObject(PrefabName);prefab.SetActive(false);
            var view=prefab.AddComponent<ZNetView>();view.m_persistent=true;
            PrefabManager.Instance.AddPrefab(new CustomPrefab(prefab,false));
            prefab.SetActive(true);
        }
        internal static void Clear(){world=null;clock=null;Ready=false;baseSeconds=baseRealtime=nextSend=0;}
        internal static void Tick()
        {
            if(!ZNet.instance || ZDOMan.instance==null || !ZNetScene.instance){if(world!=null)Clear();return;}
            if(world!=ZDOMan.instance){Clear();world=ZDOMan.instance;}
            if(!ZNet.instance.IsServer())return;
            if(clock==null)
            {
                foreach(var data in world.m_objectsByID.Values)if(data.GetPrefab()==PrefabHash){clock=data;break;}
                if(clock==null){clock=world.CreateNewZDO(Vector3.zero,PrefabHash);clock.SetPrefab(PrefabHash);clock.Persistent=true;}
                clock.SetOwner(ZNet.GetUID());
                baseSeconds=clock.GetLong(TicksKey,0)/(double)TimeSpan.TicksPerSecond;
                baseRealtime=Time.realtimeSinceStartupAsDouble;Ready=true;
            }
            if(Time.realtimeSinceStartupAsDouble<nextSend)return;
            nextSend=Time.realtimeSinceStartupAsDouble+5;
            Persist();
            double now=Now;
            foreach(var peer in ZNet.instance.m_peers)if(peer.IsReady())peer.m_rpc.Invoke(Rpc,now);
        }
        internal static void Persist()
        {if(Ready && clock!=null && ZNet.instance && ZNet.instance.IsServer())clock.Set(TicksKey,(long)(Now*TimeSpan.TicksPerSecond));}
        private static void Receive(ZRpc sender,double seconds)
        {
            if(!ZNet.instance || ZNet.instance.IsServer() || ZNet.instance.GetServerPeer()?.m_rpc!=sender ||
                double.IsNaN(seconds)||double.IsInfinity(seconds)||seconds<0)return;
            if(world!=ZDOMan.instance){Clear();world=ZDOMan.instance;}
            baseSeconds=seconds;baseRealtime=Time.realtimeSinceStartupAsDouble;Ready=true;
        }
        [HarmonyPatch(typeof(ZNet),"OnNewConnection")]
        private static class Connection {private static void Postfix(ZNetPeer peer)=>peer.m_rpc.Register<double>(Rpc,Receive);}
        [HarmonyPatch(typeof(ZNet),"SaveWorld",new[]{typeof(bool)})]
        private static class Save {private static void Prefix()=>Persist();}
        [HarmonyPatch(typeof(ZNetScene),"OnDestroy")]
        private static class SessionEnd {private static void Prefix(){Persist();Clear();}}
    }
}
