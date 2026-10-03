using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Overhaul.Persistence
{
    internal static partial class ObjectSql
    {
        internal static IEnumerable<ObjectRecord> Read(SqliteDatabase db)
        {
            using(var objects=db.Query("SELECT o.id,o.chunk,o.chunk_order,o.prefab_hash,o.prefab,o.x,o.y,o.z,o.rotation_x,o.rotation_y,o.rotation_z,o.flags,o.zdo_user,o.zdo_id,c.type,c.connection_hash,c.target_user,c.target_id,o.raw_data,o.persistent,o.distant,o.network_type FROM objects o LEFT JOIN connections c ON c.object_id=o.id ORDER BY o.id"))
            using(var properties=db.Query("SELECT object_id,key_hash,key,type,integer,real,text,x,y,z,w,blob,raw_value FROM properties ORDER BY object_id"))
            {
                bool hasProperty=properties.Read();
                while(objects.Read())
                {
                    var o=new ObjectRecord { Id=objects.Long(0),Chunk=(int)objects.Long(1),Order=(int)objects.Long(2),Prefab=(int)objects.Long(3),Name=objects.Text(4),
                        Position=new[]{(float)objects.Double(5),(float)objects.Double(6),(float)objects.Double(7)},Rotation=new[]{(float)objects.Double(8),(float)objects.Double(9),(float)objects.Double(10)},Flags=(int)objects.Long(11),User=objects.Long(12),NetworkId=checked((uint)objects.Long(13)),
                        ConnectionType=(int)objects.Long(14),ConnectionHash=(int)objects.Long(15),TargetUser=objects.IsNull(16)?(long?)null:objects.Long(16),TargetId=checked((uint)objects.Long(17)) };
                    o.Flags=(o.Flags&~3840)|(objects.Long(19)!=0?256:0)|(objects.Long(20)!=0?512:0)|((int)objects.Long(21)<<10);
                    var rawObject=objects.Blob(18);for(int i=0;i<3;i++){o.Position[i]=Float(objects,5+i,rawObject,i);o.Rotation[i]=Float(objects,8+i,rawObject,3+i);}
                    while(hasProperty&&properties.Long(0)==o.Id)
                    {
                        var p=new PropertyRecord{Key=(int)properties.Long(1),Name=properties.Text(2),Type=properties.Text(3)};
                        switch(p.Type)
                        {
                            case "int":p.Value=checked((int)properties.Long(4));break;
                            case "long":p.Value=properties.Long(4);break;
                            case "float":p.Value=Float(properties,5,properties.Blob(12),0);break;
                            case "string":p.Value=properties.Text(6);break;
                            case "vector3":case "quaternion":
                                var raw=properties.Blob(12);var vector=new float[p.Type=="vector3"?3:4];for(int i=0;i<vector.Length;i++)vector[i]=Float(properties,7+i,raw,i);p.Value=vector;break;
                            case "bytes":
                                p.Value=p.Key==Items?ReadInventory(db,o.Id,properties.Blob(11)):p.Key==Rooms?ReadDungeon(db,o.Id,properties.Blob(11)):p.Key==Terrain?ReadTerrain(db,o.Id):properties.Blob(11);break;
                            default:throw new InvalidDataException("Unknown property type: "+p.Type);
                        }
                        o.Properties.Add(p);hasProperty=properties.Read();
                    }
                    if(hasProperty&&properties.Long(0)<o.Id)throw new InvalidDataException("Orphan world property");
                    yield return o;
                }
                if(hasProperty)throw new InvalidDataException("Orphan world property");
            }
        }
        private static float Float(SqliteDatabase.Statement row,int column,byte[] raw,int offset)
        {
            float value=(float)row.Double(column);
            if(raw.Length>=offset*4+4)
            {
                float original=BitConverter.ToSingle(raw,offset*4);
                if(row.IsNull(column)&&float.IsNaN(original))return original;
                if(value==0&&original==0)return original;
            }
            if(row.IsNull(column))throw new InvalidDataException("Missing numeric world value");
            return value;
        }
        private static void WriteCount(BinaryWriter w,int count)
        {
            if(count<0||count>32767)throw new InvalidDataException("Native item count overflow");
            if(count<128)w.Write((byte)count);else{w.Write((byte)(128|(count>>8)));w.Write((byte)count);}
        }
        private static byte[] ReadInventory(SqliteDatabase db,long id,byte[] fallback)
        {
            using(var status=db.Query("SELECT version,decode_status FROM containers WHERE object_id=?",id))
                if(!status.Read()||status.Long(0)!=109||status.Text(1)!="complete")return fallback;
            using(var stream=new MemoryStream())using(var w=new BinaryWriter(stream))
            {
                w.Write(109);long countPosition=stream.Position;w.Write((ushort)0);int count=0;
                using(var rows=db.Query("SELECT id,prefab_hash,stack,quality,durability,x,y,equipped,picked_up,variant,crafter_id,crafter_name,world_level,cheated,extra_flags FROM inventory WHERE object_id=? ORDER BY item_order,id",id))
                    while(rows.Read())
                    {
                        long item=rows.Long(0),crafter=rows.Long(10);int prefab=(int)rows.Long(1),stack=checked((int)rows.Long(2)),quality=checked((int)rows.Long(3)),variant=(int)rows.Long(9);
                        var custom=new List<KeyValuePair<string,string>>();using(var properties=db.Query("SELECT key,value FROM item_data WHERE item_id=? ORDER BY key",item))while(properties.Read())custom.Add(new KeyValuePair<string,string>(properties.Text(0),properties.Text(1)));
                        int flags=(rows.Long(8)!=0?1:0)|(rows.Long(7)!=0?2:0)|(quality!=1?4:0)|(stack!=1?8:0)|(variant!=0?16:0)|(crafter!=0?32:0)|(prefab!=0?64:0)|(custom.Count>0?128:0);
                        w.Write(checked((int)Math.Round(rows.Double(4)*100,MidpointRounding.AwayFromZero)));w.Write(checked((byte)rows.Long(5)));w.Write(checked((byte)rows.Long(6)));w.Write(checked((byte)rows.Long(12)));w.Write((byte)flags);
                        if((flags&4)!=0)w.Write(checked((ushort)quality));if((flags&8)!=0)w.Write(checked((ushort)stack));if((flags&16)!=0)w.Write(variant);
                        if((flags&32)!=0){w.Write(crafter);w.Write(rows.Text(11));}if((flags&64)!=0)w.Write(prefab);
                        if((flags&128)!=0){WriteCount(w,custom.Count);foreach(var p in custom){w.Write(p.Key);w.Write(p.Value);}}
                        w.Write((byte)(((int)rows.Long(14)&~1)|(rows.Long(13)!=0?1:0)));count++;
                    }
                stream.Position=countPosition;w.Write(checked((ushort)count));return stream.ToArray();
            }
        }
        private static byte[] ReadDungeon(SqliteDatabase db,long id,byte[] original)
        {
            using(var stream=new MemoryStream())using(var w=new BinaryWriter(stream))
            {
                w.Write(0);int count=0;
                using(var rows=db.Query("SELECT prefab_hash,x,y,z,rotation_x,rotation_y,rotation_z,room_order FROM rooms WHERE object_id=? ORDER BY room_order",id))
                    while(rows.Read()){w.Write((int)rows.Long(0));for(int i=1;i<7;i++)w.Write(Float(rows,i,original,2+checked((int)rows.Long(7))*7+i-1));count++;}
                stream.Position=0;w.Write(count);return stream.ToArray();
            }
        }
        private static byte[] ReadTerrain(SqliteDatabase db,long id)
        {
            using(var stream=new MemoryStream())using(var w=new BinaryWriter(stream))
            using(var info=db.Query("SELECT version,operations,last_x,last_y,last_z,last_radius,vertex_count,pixel_count FROM terrain WHERE object_id=?",id))
            {
                if(!info.Read())throw new InvalidDataException("Missing terrain data");
                w.Write((int)info.Long(0));w.Write((int)info.Long(1));for(int i=2;i<6;i++)w.Write((float)info.Double(i));
                for(int kind=0;kind<2;kind++)
                {
                    int count=checked((int)info.Long(6+kind));if(count<0||count>1000000)throw new InvalidDataException("Invalid terrain size");w.Write(count);
                    using(var rows=db.Query(kind==0?"SELECT \"index\",level,smooth FROM terrain_heights WHERE object_id=? ORDER BY \"index\"":"SELECT \"index\",r,g,b,a FROM terrain_paint WHERE object_id=? ORDER BY \"index\"",id))
                    {
                        bool available=rows.Read();
                        for(int i=0;i<count;i++)
                        {
                            bool modified=available&&rows.Long(0)==i;w.Write(modified);
                            if(modified){for(int j=1;j<=(kind==0?2:4);j++)w.Write((float)rows.Double(j));available=rows.Read();}
                        }
                        if(available)throw new InvalidDataException("Terrain index outside stored bounds");
                    }
                }
                return NativeFormat.Deflate(stream.ToArray());
            }
        }
    }
}
