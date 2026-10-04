using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Overhaul.Persistence
{
    internal static partial class ObjectSql
    {
        private static readonly int Items = NativeFormat.Hash("items"), Rooms = NativeFormat.Hash("roomData"), Terrain = NativeFormat.Hash("TCData");
        internal static Func<int, string> PrefabName = NameCatalog.Prefab;
        internal static void Write(SqliteDatabase db, ObjectRecord o, bool importing = false)
        {
            long? oldChunk=null;
            if(!importing)using(var previous=db.Query("SELECT chunk FROM objects WHERE id=?",o.Id))if(previous.Read())oldChunk=previous.Long(0);
            db.Write("INSERT OR IGNORE INTO chunks(id,native_index,size_level,native_version,object_count) VALUES (?,?,?,0,0)", o.Chunk, (o.Chunk - 1) & 65535, (o.Chunk - 1) >> 16);
            new DatabaseRow("objects", "id,chunk,chunk_order,prefab_hash,prefab,x,y,z,rotation_x,rotation_y,rotation_z,persistent,distant,network_type,flags,raw_data,zdo_user,zdo_id", 1,
                o.Id,o.Chunk,o.Order,o.Prefab,o.Name,o.Position[0],o.Position[1],o.Position[2],o.Rotation[0],o.Rotation[1],o.Rotation[2],(o.Flags&256)!=0,(o.Flags&512)!=0,(o.Flags>>10)&3,o.Flags,FloatBytes(o.Position.Concat(o.Rotation).ToArray()),o.User,o.NetworkId).Write(db);
            if(!importing&&oldChunk!=o.Chunk)
            {
                db.Write("UPDATE chunks SET object_count=object_count+1 WHERE id=?",o.Chunk);
                if(oldChunk.HasValue)db.Write("UPDATE chunks SET object_count=object_count-1 WHERE id=?",oldChunk.Value);
            }
            var old = new HashSet<string>();
            if (!importing) using (var rows = db.Query("SELECT type,key_hash FROM properties WHERE object_id=?", o.Id))
                while (rows.Read()) old.Add(rows.Text(0) + ":" + rows.Long(1));
            foreach (var p in o.Properties)
            {
                old.Remove(p.Type + ":" + p.Key);
                // Derived readable tables only need work when their source data changed.
                bool derive = p.Type == "bytes" && (p.Key == Items || p.Key == Rooms || p.Key == Terrain);
                if (derive && !importing) using (var previous = db.Query("SELECT blob FROM properties WHERE object_id=? AND type='bytes' AND key_hash=?", o.Id, p.Key))
                    if (previous.Read() && previous.Blob(0).SequenceEqual((byte[])p.Value)) derive = false;
                object integer=null, real=null, text=null, x=null, y=null, z=null, w=null, blob=null;
                switch (p.Type)
                {
                    case "int": case "long": integer=p.Value; break;
                    case "float": real=p.Value; break;
                    case "string": text=p.Value; break;
                    case "bytes": blob=p.Value; break;
                    case "vector3": case "quaternion":
                        var vector=(float[])p.Value; x=vector[0];y=vector[1];z=vector[2];if(vector.Length==4)w=vector[3];break;
                    default: throw new InvalidDataException("Unknown property type " + p.Type);
                }
                byte[] raw=p.Type=="float"?BitConverter.GetBytes((float)p.Value):p.Value is float[] components?FloatBytes(components):null;
                new DatabaseRow("properties","object_id,type,key_hash,key,integer,real,text,x,y,z,w,blob,raw_value",3,o.Id,p.Type,p.Key,p.Name,integer,real,text,x,y,z,w,blob,raw).Write(db);
                if (derive)
                {
                    if (p.Key == Items)
                    {
                        var before = db.InventoryRevisions ? ContainerVersions.Fingerprints(db, o.Id) : null;
                        Inventory(db,o.Id,(byte[])p.Value,o.ProtectedInventorySlots);
                        if (before != null) ContainerVersions.RecordChanges(db, o.Id, before);
                    }
                    else if (p.Key == Rooms) Dungeon(db,o.Id,(byte[])p.Value);
                    else TerrainData(db,o.Id,(byte[])p.Value);
                }
            }
            foreach (string obsolete in old)
            {
                int separator=obsolete.IndexOf(':');string type=obsolete.Substring(0,separator);int key=int.Parse(obsolete.Substring(separator+1));
                db.Write("DELETE FROM properties WHERE object_id=? AND type=? AND key_hash=?",o.Id,type,key);
                if(type=="bytes")DeleteDerived(db,o.Id,key);
            }
            if(o.ConnectionType!=0)
                new DatabaseRow("connections","object_id,type,type_name,connection_hash,target_user,target_id",1,o.Id,o.ConnectionType,null,o.ConnectionHash,o.TargetUser,(object)(o.TargetUser.HasValue?(long?)o.TargetId:null)).Write(db);
            else db.Write("DELETE FROM connections WHERE object_id=?",o.Id);
        }
        private static void DeleteDerived(SqliteDatabase db,long id,int key)
        {
            if(key==Items)db.Write("DELETE FROM containers WHERE object_id=?",id);
            else if(key==Rooms)db.Write("DELETE FROM dungeons WHERE object_id=?",id);
            else if(key==Terrain)db.Write("DELETE FROM terrain WHERE object_id=?",id);
        }
        internal static void Delete(SqliteDatabase db,long id)
        {
            db.Write("UPDATE chunks SET object_count=object_count-1 WHERE id=(SELECT chunk FROM objects WHERE id=?)",id);
            db.Write("DELETE FROM objects WHERE id=?",id);
        }
        private static byte[] FloatBytes(float[] values){var bytes=new byte[values.Length*4];Buffer.BlockCopy(values,0,bytes,0,bytes.Length);return bytes;}

        private static void Inventory(SqliteDatabase db,long id,byte[] data,HashSet<int> protectedSlots=null)
        {
            using(var r=NativeFormat.Reader(data))
            {
                int version=r.ReadInt32();
                if(version!=109)
                {
                    if (protectedSlots?.Count > 0) throw new InvalidDataException("Reserved inventory changed native format");
                    db.Write("DELETE FROM containers WHERE object_id=?",id);
                    new DatabaseRow("containers","object_id,version,item_count,decode_status",1,id,version,null,"native").Write(db);
                    return; // Unknown versions remain losslessly available in the native property.
                }
                int count=r.ReadUInt16();var present=new HashSet<long>();
                new DatabaseRow("containers","object_id,version,item_count,decode_status",1,id,version,count,"complete").Write(db);
                for(int i=0;i<count;i++)
                {
                    double durability=r.ReadInt32()/100.0;int x=r.ReadByte(),y=r.ReadByte(),level=r.ReadByte(),flags=r.ReadByte();
                    int quality=(flags&4)!=0?r.ReadUInt16():1,stack=(flags&8)!=0?r.ReadUInt16():1,variant=(flags&16)!=0?r.ReadInt32():0;
                    long crafter=(flags&32)!=0?r.ReadInt64():0;string crafterName=(flags&32)!=0?r.ReadString():"";
                    int prefab=(flags&64)!=0?r.ReadInt32():0,custom=(flags&128)!=0?NativeFormat.Count(r):0;
                    long item=checked(id*65536+x*256+y);if(!present.Add(item))throw new InvalidDataException("Duplicate inventory slot for object " + id);
                    var values=new Dictionary<string,string>();for(int j=0;j<custom;j++)values.Add(r.ReadString(),r.ReadString());int flags2=r.ReadByte();
                    if (protectedSlots?.Contains(y * 256 + x) == true) continue;
                    new DatabaseRow("inventory","id,object_id,item_order,prefab_hash,prefab,stack,quality,durability,x,y,equipped,picked_up,variant,crafter_id,crafter_name,world_level,cheated,flags,extra_flags",1,
                        item,id,i,prefab,PrefabName(prefab),stack,quality,durability,x,y,(flags&2)!=0,(flags&1)!=0,variant,crafter,crafterName,level,(flags2&1)!=0,flags,flags2).Write(db);
                    foreach(var pair in values)new DatabaseRow("item_data","item_id,key,value",2,item,pair.Key,pair.Value).Write(db);
                    var removed=new List<string>();using(var old=db.Query("SELECT key FROM item_data WHERE item_id=?",item))while(old.Read())if(!values.ContainsKey(old.Text(0)))removed.Add(old.Text(0));
                    foreach(string key in removed)db.Write("DELETE FROM item_data WHERE item_id=? AND key=?",item,key);
                }
                NativeFormat.End(r);
                var obsolete=new List<long>();using(var old=db.Query("SELECT id,x,y FROM inventory WHERE object_id=?",id))while(old.Read())if(!present.Contains(old.Long(0)) && protectedSlots?.Contains((int)old.Long(2)*256+(int)old.Long(1)) != true)obsolete.Add(old.Long(0));
                foreach(long item in obsolete)db.Write("DELETE FROM inventory WHERE id=?",item);
                if (protectedSlots?.Count > 0) db.Write("UPDATE containers SET item_count=(SELECT count(*) FROM inventory WHERE object_id=?) WHERE object_id=?", id, id);
            }
        }
        private static void Dungeon(SqliteDatabase db,long id,byte[] data)
        {
            using(var r=NativeFormat.Reader(data))
            {
                int count=r.ReadInt32();if(count<0||count>100000)throw new InvalidDataException("Invalid room count");
                new DatabaseRow("dungeons","object_id,room_count",1,id,count).Write(db);
                for(int i=0;i<count;i++)
                {
                    int prefab=r.ReadInt32();var pos=NativeFormat.Vector(r);var rot=NativeFormat.Vector(r);
                    new DatabaseRow("rooms","object_id,room_order,prefab_hash,prefab,x,y,z,rotation_x,rotation_y,rotation_z",2,id,i,prefab,PrefabName(prefab),pos[0],pos[1],pos[2],rot[0],rot[1],rot[2]).Write(db);
                }
                NativeFormat.End(r);db.Write("DELETE FROM rooms WHERE object_id=? AND room_order>=?",id,count);
            }
        }
        private static void TerrainData(SqliteDatabase db,long id,byte[] data)
        {
            using(var r=NativeFormat.Reader(NativeFormat.Inflate(data)))
            {
                int version=r.ReadInt32();if(version!=1)throw new InvalidDataException("Unknown terrain format");
                int operations=r.ReadInt32();var pos=NativeFormat.Vector(r);float radius=r.ReadSingle();int count=r.ReadInt32();
                if(count<0||count>1000000)throw new InvalidDataException("Invalid terrain size");
                var heights=new List<DatabaseRow>();var indices=new HashSet<long>();
                for(int i=0;i<count;i++)if(r.ReadBoolean()){indices.Add(i);heights.Add(new DatabaseRow("terrain_heights","object_id,index,level,smooth",2,id,i,r.ReadSingle(),r.ReadSingle()));}
                int pixels=r.ReadInt32();if(pixels<0||pixels>1000000)throw new InvalidDataException("Invalid terrain paint size");
                var paint=new List<DatabaseRow>();var paintIndices=new HashSet<long>();
                for(int i=0;i<pixels;i++)if(r.ReadBoolean()){paintIndices.Add(i);paint.Add(new DatabaseRow("terrain_paint","object_id,index,r,g,b,a",2,id,i,r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle()));}
                NativeFormat.End(r);
                new DatabaseRow("terrain","object_id,version,operations,last_x,last_y,last_z,last_radius,vertex_count,pixel_count",1,id,version,operations,pos[0],pos[1],pos[2],radius,count,pixels).Write(db);
                foreach(var row in heights)row.Write(db);foreach(var row in paint)row.Write(db);
                DeleteIndices(db,"terrain_heights",id,indices);DeleteIndices(db,"terrain_paint",id,paintIndices);
            }
        }
        private static void DeleteIndices(SqliteDatabase db,string table,long id,HashSet<long> present)
        {
            var removed=new List<long>();using(var old=db.Query("SELECT \"index\" FROM " + table + " WHERE object_id=?",id))while(old.Read())if(!present.Contains(old.Long(0)))removed.Add(old.Long(0));
            foreach(long index in removed)db.Write("DELETE FROM " + table + " WHERE object_id=? AND \"index\"=?",id,index);
        }
    }
}
