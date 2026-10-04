using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameSnapshot
    {
        internal static float[] Components(Vector3 v)=>new[]{v.x,v.y,v.z};
        internal static Vector3 Vector(float[] v)=>new Vector3(v[0],v[1],v[2]);
        internal static ObjectRecord Capture(ZDO z,long id)
        {
            var chunk=ZoneSystem.GetZonesChunk(z.GetSectorIndex());
            var o=new ObjectRecord{Id=id,User=z.m_uid.UserID,NetworkId=z.m_uid.ID,Chunk=1+chunk.Chunk,Prefab=z.m_prefab,Name=NameCatalog.Prefab(z.m_prefab),
                Position=Components(z.m_position),Rotation=Components(z.m_rotation),Flags=(z.Persistent?256:0)|(z.Distant?512:0)|((int)z.Type<<10)};
            ZDOExtraData.GetData(z.m_uid,out var floats,out var vectors,out var quats,out var ints,out var longs,out var strings,out var bytes,out var connection);
            void Add(int key,string type,object value){if(!ZDOExtraData.s_sessionOnly.Contains(key))o.Properties.Add(new PropertyRecord{Key=key,Name=NameCatalog.Key(key),Type=type,Value=value});}
            if(floats!=null)foreach(var p in floats)Add(p.Key,"float",p.Value);
            if(vectors!=null)foreach(var p in vectors)Add(p.Key,"vector3",Components(p.Value));
            if(quats!=null)foreach(var p in quats)Add(p.Key,"quaternion",new[]{p.Value.x,p.Value.y,p.Value.z,p.Value.w});
            if(ints!=null)foreach(var p in ints)Add(p.Key,"int",p.Value);
            if(longs!=null)foreach(var p in longs)Add(p.Key,"long",p.Value);
            if(strings!=null)foreach(var p in strings)Add(p.Key,"string",p.Value);
            if(bytes!=null)foreach(var p in bytes)Add(p.Key,"bytes",(byte[])p.Value.Clone());
            if(connection!=null){o.ConnectionType=(int)connection.m_type;o.TargetUser=connection.m_target.UserID;o.TargetId=connection.m_target.ID;}
            PlayerFishingCastGame.FilterCapture(z,o);return o;
        }
        internal static ZDO Restore(ZDOMan manager,ObjectRecord o)
        {
            var uid=new ZDOID(o.User,o.NetworkId);var z=manager.CreateNewZDO(uid,Vector(o.Position),o.Prefab);
            z.m_prefab=o.Prefab;z.m_rotation=Vector(o.Rotation);z.Persistent=(o.Flags&256)!=0;z.Distant=(o.Flags&512)!=0;z.Type=(ZDO.ObjectType)((o.Flags>>10)&3);z.SetOwnerInternal(0);
            foreach(var p in o.Properties)switch(p.Type)
            {
                case "float":ZDOExtraData.Set(uid,p.Key,(float)p.Value);break;
                case "int":ZDOExtraData.Set(uid,p.Key,(int)p.Value);break;
                case "long":ZDOExtraData.Set(uid,p.Key,(long)p.Value);break;
                case "string":ZDOExtraData.Set(uid,p.Key,(string)p.Value);break;
                case "bytes":ZDOExtraData.Set(uid,p.Key,(byte[])p.Value);break;
                case "vector3":ZDOExtraData.Set(uid,p.Key,Vector((float[])p.Value));break;
                case "quaternion":var q=(float[])p.Value;ZDOExtraData.Set(uid,p.Key,new Quaternion(q[0],q[1],q[2],q[3]));break;
                default:throw new InvalidDataException("Unknown world property type");
            }
            if(o.ConnectionType!=0)
            {
                var type=(ZDOExtraData.ConnectionType)o.ConnectionType;
                if(o.TargetUser.HasValue)ZDOExtraData.SetConnection(uid,type,new ZDOID(o.TargetUser.Value,o.TargetId));
                else ZDOExtraData.SetConnectionData(uid,type,o.ConnectionHash);
            }
            PlayerFishingCastGame.TrackRestored(z);return z;
        }
        internal static void ApplyHeader(World target,WorldRecord source)
        {
            target.m_name=source.Name;target.m_seedName=source.SeedName;target.m_seed=source.Seed;target.m_uid=source.Uid;target.m_worldGenVersion=source.Generation;
            target.m_needsDB=source.NeedsData;target.m_chunkedSave=true;target.m_worldVersion=(global::Version.World)NativeFormat.WorldVersion;
            target.m_startingGlobalKeys.Clear();target.m_startingGlobalKeys.AddRange(source.InitialKeys);target.m_playerHistory.Clear();
            foreach(var p in source.Players){var package=new ZPackage();foreach(string text in p)package.Write(text);package.SetPos(0);target.m_playerHistory.Add(ZNet.CrossNetworkUserInfo.Read(package));}
        }
        internal static WorldRecord CaptureWorld(ZNet net)
        {
            var w=ZNet.m_world;var zones=ZoneSystem.instance;var events=RandEventSystem.instance;
            var r=new WorldRecord{Name=w.m_name,SeedName=w.m_seedName,Seed=w.m_seed,Uid=w.m_uid,Generation=w.m_worldGenVersion,NeedsData=true,Time=net.m_netTime,
                LocationVersion=zones.m_locationVersion,LocationsGenerated=zones.m_locationsGenerated,EventTimer=events.m_eventTimer,
                PersistentEvents=JsonUtility.ToJson(PersistentEventSystem.instance.m_activePersistentEvents)};
            r.ProtectedKeys = PlayerWorldKeyGame.Protected;
            r.InitialKeys.AddRange(w.m_startingGlobalKeys);
            foreach(var key in zones.GetGlobalKeys()){ZoneSystem.GetKeyValue(key,out _,out var type);if(type>=GlobalKeys.NonServerOption)r.Keys.Add(key);}
            foreach(var p in w.m_playerHistory)r.Players.Add(new[]{p.m_id.ToString(),p.m_displayName??"",p.m_serverAssignedDisplayName??"",p.m_playfabId??""});
            foreach(var zone in zones.m_generatedZones)r.Zones.Add(new[]{(int)zone.x,(int)zone.y});
            foreach(var l in zones.m_locationInstances.Values)r.Locations.Add(new LocationRecord{Prefab=l.m_location.m_prefabName.GetStableHashCode(),X=l.m_position.x,Y=l.m_position.y,Z=l.m_position.z,Placed=l.m_placed});
            if(events.m_randomEvent!=null){var ev=events.m_randomEvent;r.EventName=ev.m_name;r.EventTime=ev.m_time;r.EventPosition=Components(ev.m_pos);}
            return r;
        }
        internal static void RestoreWorld(ZNet net,WorldRecord state)
        {
            ApplyHeader(ZNet.m_world,state);net.m_netTime=state.Time;var zones=ZoneSystem.instance;
            zones.m_generatedZones.Clear();zones.m_locationInstances.Clear();zones.m_locationIDCache.Clear();zones.m_locationGroupCache.Clear();zones.m_locationMaxGroupCache.Clear();zones.ClearGlobalKeys();
            foreach(var zone in state.Zones)zones.m_generatedZones.Add(new Vector2s((short)zone[0],(short)zone[1]));
            foreach(string key in state.Keys)zones.GlobalKeyAdd(key,true);
            foreach(var l in state.Locations)
            {
                var location=zones.GetLocation(l.Prefab);
                if(location==null)throw new InvalidDataException("Missing location prefab "+l.Prefab+"; refusing to discard saved locations.");
                zones.RegisterLocation(location,new Vector3(l.X,l.Y,l.Z),l.Placed);
            }
            zones.m_locationsGenerated=state.LocationsGenerated&&state.LocationVersion==zones.m_locationVersion;
            var events=RandEventSystem.instance;events.m_eventTimer=state.EventTimer;
            if(!string.IsNullOrEmpty(state.EventName))
            {
                events.SetRandomEventByName(state.EventName,Vector(state.EventPosition));
                if(events.m_randomEvent==null)throw new InvalidDataException("Missing saved raid "+state.EventName);
                events.m_randomEvent.m_time=state.EventTime;events.m_randomEvent.m_pos=Vector(state.EventPosition);
            }
            var persistent=PersistentEventSystem.instance;
            persistent.m_activePersistentEvents=JsonUtility.FromJson<PersistentEventSystem.ActiveEventsList>(state.PersistentEvents);
            if(persistent.m_activePersistentEvents?.list==null)throw new InvalidDataException("Invalid persistent events");
            persistent.m_eventIdCounter=persistent.m_activePersistentEvents.list.Count==0?0:persistent.m_activePersistentEvents.list.Max(e=>e.eventId)+1;
        }
    }
}

