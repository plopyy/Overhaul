using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Overhaul.Persistence
{
    internal static class PlayerSharedMap
    {
        internal sealed class Result{internal PlayerChange[] Changes;internal byte[] Shared;}
        internal static Result Merge(PlayerChange[] source,byte[] compressed,bool write)
        {
            var dimension=source.FirstOrDefault(r=>r.Table=="state"&&(string)r.Values[0]=="map_size");
            if(dimension==null)throw new InvalidOperationException("Map exploration has not initialized yet");
            int size=Convert.ToInt32(dimension.Values[1]);if(size<1||size>4096)throw new InvalidDataException("Invalid map dimensions");
            long owner=Convert.ToInt64(source.Single(r=>r.Table=="state"&&(string)r.Values[0]=="player_id").Values[1]);
            int cells=checked(size*size);var shared=new byte[cells];var imported=new List<object[]>();
            if(compressed!=null)
            {
                using(var input=new MemoryStream(compressed,false))
                using(var gzip=new System.IO.Compression.GZipStream(input,System.IO.Compression.CompressionMode.Decompress))
                using(var r=new BinaryReader(gzip))
                {
                    int version=r.ReadInt32();if(version<1||version>3||r.ReadInt32()!=cells)throw new InvalidDataException("Shared map dimensions/version differ");
                    for(int i=0;i<cells;i++){byte bit=r.ReadByte();if(bit>1)throw new InvalidDataException("Invalid shared exploration bit");shared[i]=bit;}
                    int count=version>=2?r.ReadInt32():0;if(count<0||count>100000)throw new InvalidDataException("Invalid shared marker count");
                    for(int i=0;i<count;i++){long id=r.ReadInt64();string name=r.ReadString();float x=r.ReadSingle(),y=r.ReadSingle(),z=r.ReadSingle();int type=r.ReadInt32();bool check=r.ReadBoolean();string author=version>=3?r.ReadString():"";imported.Add(new object[]{id,name,x,y,z,type,check,author});}
                }
            }
            var blocks=source.Where(r=>r.Table=="map").Select(r=>r.Values).ToDictionary(v=>(string)v[0]+":"+Convert.ToInt32(v[1]),v=>(byte[])v[2]);
            var changes=new List<PlayerChange>();
            for(int start=0;start<cells;start+=PlayerMapFormat.CellsPerBlock)
            {
                int key=start/PlayerMapFormat.CellsPerBlock;blocks.TryGetValue("self:"+key,out var self);blocks.TryGetValue("others:"+key,out var others);
                var merged=others==null?new byte[PlayerMapFormat.BytesPerBlock]:(byte[])others.Clone();bool changed=false;
                for(int i=0;i<PlayerMapFormat.CellsPerBlock&&start+i<cells;i++)
                {
                    int mask=1<<(i%8);bool own=self!=null&&(self[i/8]&mask)!=0,old=(merged[i/8]&mask)!=0;
                    if(shared[start+i]!=0&&!own&&!old){merged[i/8]|=(byte)mask;changed=true;}
                    if(write&&(own||old))shared[start+i]=1;
                }
                if(changed)changes.Add(new PlayerChange("map",false,"others",key,merged));
            }
            var pins=source.Where(r=>r.Table=="pins").ToList();var keep=new HashSet<string>();
            var authors=source.Where(r=>r.Table=="knowledge"&&(string)r.Values[0]=="map_pin_author").ToDictionary(r=>(string)r.Values[1],r=>(string)r.Values[2]);
            foreach(var pin in imported)
            {
                var match=pins.FirstOrDefault(r=>{var v=r.Values;double x=Convert.ToDouble(v[3])-Convert.ToDouble(pin[2]),y=Convert.ToDouble(v[4])-Convert.ToDouble(pin[3]),z=Convert.ToDouble(v[5])-Convert.ToDouble(pin[4]);return x*x+y*y+z*z<1;});
                if(match!=null){keep.Add((string)match.Values[0]);continue;}
                if(Convert.ToInt64(pin[0])==owner)continue;
                string id=Guid.NewGuid().ToString("N");var added=new PlayerChange("pins",false,id,pin[5],pin[1],pin[2],pin[3],pin[4],pin[6],pin[0]);
                pins.Add(added);keep.Add(id);changes.Add(added);authors[id]=(string)pin[7];changes.Add(new PlayerChange("knowledge",false,"map_pin_author",id,pin[7]));
            }
            if(compressed!=null)
                foreach(var pin in pins.ToArray())
                {var v=pin.Values;if(Convert.ToInt64(v[7])!=0&&Convert.ToInt64(v[7])!=owner&&!keep.Contains((string)v[0])){pins.Remove(pin);changes.Add(new PlayerChange("pins",true,v[0]));changes.Add(new PlayerChange("knowledge",true,"map_pin_author",v[0]));}}
            byte[] output=null;
            if(write)
            {
                using(var buffer=new MemoryStream())using(var w=new BinaryWriter(buffer))
                {
                    w.Write(3);w.Write(cells);w.Write(shared);
                    var exported=pins.Where(r=>Convert.ToInt32(r.Values[1])!=(int)Minimap.PinType.Death).ToArray();w.Write(exported.Length);
                    foreach(var pin in exported){var v=pin.Values;long pinOwner=Convert.ToInt64(v[7]);w.Write(pinOwner==0?owner:pinOwner);w.Write((string)v[2]);w.Write(Convert.ToSingle(v[3]));w.Write(Convert.ToSingle(v[4]));w.Write(Convert.ToSingle(v[5]));w.Write(Convert.ToInt32(v[1]));w.Write(Convert.ToBoolean(v[6]));w.Write(authors.TryGetValue((string)v[0],out var author)?author:"");}
                    w.Flush();output=NativeFormat.Deflate(buffer.ToArray());
                }
            }
            return new Result{Changes=changes.ToArray(),Shared=output};
        }
    }
    internal sealed partial class PlayerDatabase
    {internal PlayerChange[] MapState()=>ReadTables("map","pins","knowledge","state");}
    internal sealed partial class PlayerDatabaseWriter
    {
        internal System.Threading.Tasks.Task<PlayerSharedMap.Result> ShareMap(PlayerIdentity identity,byte[] previous,bool write)=>Submit(()=>
        {var database=Get(identity);var result=PlayerSharedMap.Merge(database.MapState(),previous,write);if(result.Changes.Length!=0)database.CommitMap(result.Changes);return result;},true);
    }
}
