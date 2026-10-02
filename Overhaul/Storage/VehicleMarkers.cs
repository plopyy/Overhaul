using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Storage
{
    internal static class VehicleMarkers
    {
        internal const string Rpc="Overhaul_VehicleMarkers_v1";
        internal enum Kind { Ship, Cart, Ram }
        internal struct Entry {internal ZDOID Id;internal Kind Type;internal Vector3 Position;}
        static ZDOMan session;
        static ZNetScene scene;
        static Minimap map;
        static float nextUpdate;
        static bool indexed, removing;
        static readonly ConcurrentQueue<ZDOID> changed=new ConcurrentQueue<ZDOID>();
        static readonly ConcurrentDictionary<int,Kind> kinds=new ConcurrentDictionary<int,Kind>();
        static readonly HashSet<ZDOID> vehicles=new HashSet<ZDOID>();
        internal static readonly Dictionary<ZDOID,Entry> positions=new Dictionary<ZDOID,Entry>();
        internal static readonly Dictionary<ZDOID,Minimap.PinData> pins=new Dictionary<ZDOID,Minimap.PinData>();
        static readonly HashSet<Minimap.PinData> protectedPins=new HashSet<Minimap.PinData>();
        internal static Kind? Classify(GameObject prefab)
        {
            if(!prefab)return null;
            if(Utils.GetPrefabName(prefab).Equals("BatteringRam",StringComparison.OrdinalIgnoreCase))return Kind.Ram;
            if(Utils.GetPrefabName(prefab).Equals("Catapult",StringComparison.OrdinalIgnoreCase))return null;
            if(prefab.GetComponent<Ship>())return Kind.Ship;
            if(prefab.GetComponent<Vagon>())return Kind.Cart;
            return null;
        }
        internal static void Clear()
        {
            RemovePins();positions.Clear();vehicles.Clear();kinds.Clear();while(changed.TryDequeue(out _)){}
            session=null;scene=null;map=null;indexed=false;nextUpdate=0;
        }
        static void RemovePins()
        {
            removing=true;
            try{if(map)foreach(var pin in pins.Values)map.RemovePin(pin);}
            finally{removing=false;pins.Clear();protectedPins.Clear();}
        }
        static void Track(ZDO data)
        {
            if(data==null||!data.IsValid()||!kinds.ContainsKey(data.GetPrefab()))return;
            vehicles.Add(data.m_uid);
        }
        internal static List<Entry> Snapshot()
        {
            if(!indexed)
            {
                foreach(var pair in ZNetScene.instance.m_namedPrefabs){var kind=Classify(pair.Value);if(kind.HasValue)kinds[pair.Key]=kind.Value;}
                foreach(var data in ZDOMan.instance.m_objectsByID.Values)Track(data);
                indexed=true;
            }
            while(changed.TryDequeue(out var id))Track(ZDOMan.instance.GetZDO(id));
            var result=new List<Entry>();
            foreach(var id in vehicles.ToArray())
            {
                var data=ZDOMan.instance.GetZDO(id);
                if(data==null||!data.IsValid()||!kinds.TryGetValue(data.GetPrefab(),out var kind)||data.GetFloat(ZDOVars.s_health,1)<=0)
                {vehicles.Remove(id);continue;}
                result.Add(new Entry{Id=id,Type=kind,Position=data.GetPosition()});
            }
            return result;
        }
        internal static ZPackage Encode(List<Entry> entries)
        {
            var packet=new ZPackage();packet.Write(entries.Count);
            foreach(var entry in entries){packet.Write(entry.Id);packet.Write((int)entry.Type);packet.Write(entry.Position);}
            return packet;
        }
        internal static bool Decode(ZPackage packet)
        {
            try
            {
                int count=packet.ReadInt();if(count<0||count>100000)return false;
                var snapshot=new Dictionary<ZDOID,Entry>();
                for(int i=0;i<count;i++)
                {
                    var entry=new Entry{Id=packet.ReadZDOID(),Type=(Kind)packet.ReadInt(),Position=packet.ReadVector3()};
                    if(entry.Id==ZDOID.None||entry.Type<Kind.Ship||entry.Type>Kind.Ram||!PieceRelocation.Finite(entry.Position)||snapshot.ContainsKey(entry.Id))return false;
                    snapshot.Add(entry.Id,entry);
                }
                positions.Clear();foreach(var entry in snapshot)positions.Add(entry.Key,entry.Value);return true;
            }
            catch(Exception){return false;}
        }
        internal static void Receive(ZRpc sender,ZPackage packet)
        {
            if(!ZNet.instance||ZNet.instance.IsServer()||sender==null||ZNet.instance.GetServerPeer()?.m_rpc!=sender)return;
            if(Decode(packet))SyncPins(Minimap.instance);
        }
        internal static void Tick()
        {
            if(!ZNet.instance||ZDOMan.instance==null||!ZNetScene.instance){if(session!=null)Clear();return;}
            if(session!=ZDOMan.instance||scene!=ZNetScene.instance){Clear();session=ZDOMan.instance;scene=ZNetScene.instance;}
            if(Time.realtimeSinceStartup<nextUpdate)return;nextUpdate=Time.realtimeSinceStartup+1f;
            if(ZNet.instance.IsServer())
            {
                var snapshot=Snapshot();positions.Clear();foreach(var entry in snapshot)positions[entry.Id]=entry;
                var packet=Encode(snapshot);foreach(var peer in ZNet.instance.m_peers)if(peer.IsReady())peer.m_rpc.Invoke(Rpc,packet);
            }
            SyncPins(Minimap.instance);
        }
        internal static void SyncPins(Minimap current)
        {
            if(map!=current){RemovePins();map=current;}
            if(!map)return;
            foreach(var id in pins.Keys.Where(id=>!positions.ContainsKey(id)).ToArray())
            {
                removing=true;try{map.RemovePin(pins[id]);}finally{removing=false;}
                protectedPins.Remove(pins[id]);pins.Remove(id);
            }
            foreach(var entry in positions.Values)
            {
                if(!pins.TryGetValue(entry.Id,out var pin)||!map.m_pins.Contains(pin))
                {
                    if(pin!=null)protectedPins.Remove(pin);
                    pin=map.AddPin(entry.Position,Minimap.PinType.None,"",false,false);pins[entry.Id]=pin;protectedPins.Add(pin);
                }
                pin.m_pos=entry.Position;pin.m_icon=VehicleMarkerIcons.Get(entry.Type);pin.m_save=false;pin.m_checked=false;
                string key=entry.Type==Kind.Ship?"$overhaul_map_ship":entry.Type==Kind.Cart?"$overhaul_map_cart":"$overhaul_map_ram";
                pin.m_name=Localization.instance.Localize(key);
                if(pin.m_NamePinData==null)pin.m_NamePinData=new Minimap.PinNameData(pin);
            }
            map.m_pinUpdateRequired=true;
        }
        internal static void Tint()
        {
            foreach(var pair in pins)
            {
                if(!positions.TryGetValue(pair.Key,out var entry))continue;
                var pin=pair.Value;var color=VehicleMarkerIcons.ColorFor(entry.Type);
                if(pin.m_iconElement){pin.m_iconElement.color=color;pin.m_iconElement.sprite=pin.m_icon;}
                if(pin.m_NamePinData!=null&&pin.m_NamePinData.PinNameText)pin.m_NamePinData.PinNameText.color=color;
            }
        }
        [HarmonyPatch(typeof(ZNet),"OnNewConnection")]
        private static class Connection {private static void Postfix(ZNetPeer peer)=>peer.m_rpc.Register<ZPackage>(Rpc,Receive);}
        [HarmonyPatch]
        private static class Changed
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()=>AccessTools.GetDeclaredMethods(typeof(ZDO)).Where(m=>m.Name=="SetPrefab"||m.Name=="Deserialize"||m.Name=="Load");
            private static void Postfix(ZDO __instance){if(session!=null&&kinds.ContainsKey(__instance.GetPrefab()))changed.Enqueue(__instance.m_uid);}
        }
        [HarmonyPatch(typeof(Minimap),"UpdatePins")]
        private static class PinColors {[HarmonyPriority(Priority.Last)]private static void Postfix()=>Tint();}
        [HarmonyPatch(typeof(Minimap),nameof(Minimap.RemovePin),new[]{typeof(Minimap.PinData)})]
        private static class Protect {private static bool Prefix(Minimap.PinData pin)=>removing||!protectedPins.Contains(pin);}
        [HarmonyPatch(typeof(Minimap),"UpdatePlayerMarker")]
        private static class ControlledShip
        {
            private static void Postfix(Minimap __instance,Player player)
            {
                var ship=player?player.GetControlledShip():null;
                var view=ship?ship.GetComponent<ZNetView>():null;
                if(view&&view.IsValid()&&pins.ContainsKey(view.GetZDO().m_uid))
                {__instance.m_smallShipMarker.gameObject.SetActive(false);__instance.m_largeShipMarker.gameObject.SetActive(false);}
            }
        }
    }
}
