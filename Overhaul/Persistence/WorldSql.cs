using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Overhaul.Persistence
{
    internal sealed class WorldRecord
    {
        internal string Name,SeedName,PersistentEvents="{\"list\":[]}",EventName="";
        internal int Seed,Generation,LocationVersion;
        internal long Uid;
        internal bool NeedsData,LocationsGenerated;
        internal double Time;
        internal float EventTimer,EventTime;
        internal float[] EventPosition=new float[3];
        internal HashSet<string> ProtectedKeys;
        internal readonly List<string> InitialKeys=new List<string>(),Keys=new List<string>();
        internal readonly List<string[]> Players=new List<string[]>();
        internal readonly List<int[]> Zones=new List<int[]>();
        internal readonly List<LocationRecord> Locations=new List<LocationRecord>();
    }
    internal struct LocationRecord
    {
        internal int Prefab;
        internal float X,Y,Z;
        internal bool Placed;
        // Zone coordinates are stable when native location list enumeration changes.
        internal long Id => (long)(int)Math.Floor((X+32f)/64f)*65536+(int)Math.Floor((Z+32f)/64f)+32768;
    }
    internal static class WorldSql
    {
        internal static WorldRecord Parse(byte[] fwl,byte[] db2,Func<byte[],byte[]> brotli)
        {
            var result=new WorldRecord();
            using(var outer=NativeFormat.Reader(fwl))
            using(var r=NativeFormat.Reader(NativeFormat.Bytes(outer)))
            {
                NativeFormat.End(outer);if(r.ReadInt32()!=NativeFormat.WorldVersion)throw new InvalidDataException("Unsupported world metadata version");
                result.Name=r.ReadString();result.SeedName=r.ReadString();result.Seed=r.ReadInt32();result.Uid=r.ReadInt64();result.Generation=r.ReadInt32();result.NeedsData=r.ReadBoolean();
                int count=r.ReadInt32();for(int i=0;i<count;i++)result.InitialKeys.Add(r.ReadString());
                count=r.ReadInt32();for(int i=0;i<count;i++)result.Players.Add(new[]{r.ReadString(),r.ReadString(),r.ReadString(),r.ReadString()});NativeFormat.End(r);
            }
            using(var r=NativeFormat.Reader(db2))
            {
                if(r.ReadInt32()!=NativeFormat.WorldVersion)throw new InvalidDataException("Unsupported world data version");result.Time=r.ReadDouble();
                using(var zones=NativeFormat.Reader(NativeFormat.Inflate(NativeFormat.Bytes(r))))
                {
                    int count=zones.ReadInt32();for(int i=0;i<count;i++)result.Zones.Add(new int[]{zones.ReadInt16(),zones.ReadInt16()});
                    result.LocationVersion=zones.ReadInt32();count=zones.ReadInt32();for(int i=0;i<count;i++)result.Keys.Add(zones.ReadString());
                    result.LocationsGenerated=zones.ReadBoolean();count=zones.ReadInt32();
                    for(int i=0;i<count;i++)result.Locations.Add(new LocationRecord{Prefab=zones.ReadInt32(),X=zones.ReadSingle(),Y=zones.ReadSingle(),Z=zones.ReadSingle(),Placed=zones.ReadBoolean()});NativeFormat.End(zones);
                }
                result.EventTimer=r.ReadSingle();result.EventName=r.ReadString();result.EventTime=r.ReadSingle();result.EventPosition=NativeFormat.Vector(r);
                if(r.BaseStream.Position<r.BaseStream.Length)result.PersistentEvents=Encoding.UTF8.GetString(brotli(NativeFormat.Bytes(r)));NativeFormat.End(r);
            }
            return result;
        }
        internal static void Write(SqliteDatabase db,WorldRecord state)
        {
            new DatabaseRow("world","id,name,seed_name,seed,uid,generation_version,needs_data,time,location_version,locations_generated",1,
                1,state.Name,state.SeedName,state.Seed,state.Uid,state.Generation,state.NeedsData,state.Time,state.LocationVersion,state.LocationsGenerated).Write(db);
            Replace(db,"zones","x,z",2,state.Zones.Select(z=>new DatabaseRow("zones","x,z",2,z[0],z[1])));
            Replace(db,"locations","id",1,state.Locations.Select(l=>new DatabaseRow("locations","id,prefab_hash,prefab,x,y,z,placed",1,l.Id,l.Prefab,NameCatalog.Prefab(l.Prefab),l.X,l.Y,l.Z,l.Placed)));
            Replace(db,"world_keys","source,key",2,state.InitialKeys.Select(k=>new DatabaseRow("world_keys","source,key",2,"initial",k)).Concat(state.Keys.Select(k=>new DatabaseRow("world_keys","source,key",2,"progress",k))),
                v => state.ProtectedKeys != null && (string)v[0] == "progress" && state.ProtectedKeys.Contains((string)v[1]));
            Replace(db,"player_history","id",1,state.Players.Select((p,i)=>new DatabaseRow("player_history","id,platform_id,name,server_name,playfab_id",1,i+1,p[0],p[1],p[2],p[3])));
            new DatabaseRow("events","id,type,name,time,x,y,z,json",1,1,"raid_timer","",state.EventTimer,null,null,null,null).Write(db);
            new DatabaseRow("events","id,type,name,time,x,y,z,json",1,2,"raid",state.EventName,state.EventTime,state.EventPosition[0],state.EventPosition[1],state.EventPosition[2],null).Write(db);
            new DatabaseRow("events","id,type,name,time,x,y,z,json",1,3,"persistent",null,null,null,null,null,state.PersistentEvents).Write(db);
        }
        internal static void WriteHeader(SqliteDatabase db,WorldRecord state)
        {
            db.Write("UPDATE world SET name=?,seed_name=?,seed=?,generation_version=? WHERE id=1 AND uid=?",state.Name,state.SeedName,state.Seed,state.Generation,state.Uid);
            db.Write("DELETE FROM world_keys WHERE source='initial'");
            foreach(string key in state.InitialKeys)db.Write("INSERT INTO world_keys(source,key) VALUES ('initial',?)",key);
        }
        private static void Replace(SqliteDatabase db,string table,string keys,int keyCount,IEnumerable<DatabaseRow> data,Func<object[],bool> protect=null)
        {
            var previous=new Dictionary<string,object[]>();string[] columns=keys.Split(',').Select(k=>"\""+k+"\"").ToArray();
            using(var rows=db.Query("SELECT "+string.Join(",",columns)+" FROM \""+table+"\""))
                while(rows.Read()){var values=new object[keyCount];for(int i=0;i<keyCount;i++)values[i]=rows.Value(i);previous.Add(string.Join(":",values),values);}
            foreach(var row in data){if(protect?.Invoke(row.Values)==true)continue;row.Write(db);previous.Remove(string.Join(":",row.Values.Take(keyCount)));}
            foreach(var values in previous.Values.Where(v=>protect?.Invoke(v)!=true))db.Write("DELETE FROM \""+table+"\" WHERE "+string.Join(" AND ",columns.Select(k=>k+"=?")),values);
        }
        internal static WorldRecord Read(SqliteDatabase db)
        {
            var result=new WorldRecord();
            using(var r=db.Query("SELECT name,seed_name,seed,uid,generation_version,needs_data,time,location_version,locations_generated FROM world WHERE id=1"))
            {
                if(!r.Read())throw new InvalidDataException("Missing world metadata");
                result.Name=r.Text(0);result.SeedName=r.Text(1);result.Seed=(int)r.Long(2);result.Uid=r.Long(3);result.Generation=(int)r.Long(4);result.NeedsData=r.Long(5)!=0;result.Time=r.Double(6);result.LocationVersion=(int)r.Long(7);result.LocationsGenerated=r.Long(8)!=0;
            }
            using(var r=db.Query("SELECT x,z FROM zones"))while(r.Read())result.Zones.Add(new[]{(int)r.Long(0),(int)r.Long(1)});
            using(var r=db.Query("SELECT prefab_hash,x,y,z,placed FROM locations"))while(r.Read())result.Locations.Add(new LocationRecord{Prefab=(int)r.Long(0),X=(float)r.Double(1),Y=(float)r.Double(2),Z=(float)r.Double(3),Placed=r.Long(4)!=0});
            using(var r=db.Query("SELECT source,key FROM world_keys"))while(r.Read())(r.Text(0)=="initial"?result.InitialKeys:result.Keys).Add(r.Text(1));
            using(var r=db.Query("SELECT platform_id,name,server_name,playfab_id FROM player_history ORDER BY id"))while(r.Read())result.Players.Add(new[]{r.Text(0),r.Text(1),r.Text(2),r.Text(3)});
            using(var r=db.Query("SELECT id,name,time,x,y,z,json FROM events"))while(r.Read())
            {
                if(r.Long(0)==1)result.EventTimer=(float)r.Double(2);
                if(r.Long(0)==2){result.EventName=r.Text(1);result.EventTime=(float)r.Double(2);result.EventPosition=new[]{(float)r.Double(3),(float)r.Double(4),(float)r.Double(5)};}
                if(r.Long(0)==3)result.PersistentEvents=r.Text(6);
            }
            return result;
        }
    }
}

