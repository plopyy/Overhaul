using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using HarmonyLib;

namespace Overhaul.Persistence
{
    internal sealed partial class PlayerDatabase
    {
        // Map writes share the writer thread but do not invalidate inventory
        // revisions. Only the changed exploration blocks enter this transaction.
        internal void CommitMap(PlayerChange[] rows)
        {
            if(!Complete||rows.Any(r=>r.Table!="map"&&r.Table!="pins"&&!(r.Table=="knowledge"&&(string)r.Values[0]=="map_pin_author")&&!(r.Table=="state"&&((string)r.Values[0]=="map_size"||(string)r.Values[0]=="map_public"))))throw new InvalidOperationException("Invalid exploration transaction");
            db.Transaction(()=>ApplyRows(rows));
        }
    }
    internal sealed partial class PlayerDatabaseWriter
    {
        internal Task<bool> CommitMap(PlayerIdentity identity,PlayerChange[] rows)=>Submit(()=>{Get(identity).CommitMap(rows);return true;},true);
    }
    internal sealed class GameMapSession
    {
        private readonly PlayerDatabaseWriter writer;
        private readonly PlayerIdentity identity;
        private readonly Action<PlayerChange[],bool> publish;
        private readonly Dictionary<int,byte[]> blocks=new Dictionary<int,byte[]>();
        private readonly List<PlayerChange> edits=new List<PlayerChange>();
        private Task<bool> pending;
        private PlayerChange[] changes;
        private int size,lastX=int.MinValue,lastY=int.MinValue;
        private double next;
        private Vector3? finalPoint;
        private MapTable shareTable;
        private ZDOID shareId;
        private bool shareWrite,shareQueued;
        private Task<PlayerSharedMap.Result> sharing;
        internal bool Finished=>pending==null&&edits.Count==0&&!shareQueued&&sharing==null&&!finalPoint.HasValue;
        internal void Share(MapTable table,bool write)
        {
            if(!table||shareQueued||sharing!=null||size==0||!GameCartographyRuntime.Reserve(table.m_nview.GetZDO().m_uid))return;
            shareTable=table;shareId=table.m_nview.GetZDO().m_uid;shareWrite=write;shareQueued=true;
        }
        internal void AbortShare(){GameCartographyRuntime.Release(shareId);shareTable=null;shareQueued=false;sharing=null;}
        internal void Edit(PlayerChange[] rows)
        {
            if(rows.Length>64)throw new InvalidOperationException("Map edit exceeds limit");
            foreach(var row in rows)
            {
                var v=row.Values;
                if(row.Table=="pins")
                {
                    if(((string)v[0]).Length>64||string.IsNullOrEmpty((string)v[0]))throw new InvalidOperationException("Invalid map marker id");
                    if(!row.Delete&&(((string)v[2]).Length>256||!Enum.IsDefined(typeof(Minimap.PinType),Convert.ToInt32(v[1]))||v.Skip(3).Take(3).Any(n=>double.IsNaN(Convert.ToDouble(n))||double.IsInfinity(Convert.ToDouble(n))||Math.Abs(Convert.ToDouble(n))>1000000)))throw new InvalidOperationException("Invalid map marker");
                }
                else if(row.Table=="knowledge")
                {if((string)v[0]!="map_pin_author"||((string)v[1]).Length>64||!row.Delete&&((string)v[2]).Length>256)throw new InvalidOperationException("Invalid map marker author");}
                else if(row.Table!="state"||row.Delete||(string)v[0]!="map_public"||v[1]==null||Convert.ToInt32(v[1])<0||Convert.ToInt32(v[1])>1)throw new InvalidOperationException("Client map edit cannot modify exploration or character state");
                edits.RemoveAll(old=>old.SameKey(row));edits.Add(row);
            }
            if(edits.Count>1024)throw new InvalidOperationException("Too many queued map edits");
        }
        internal GameMapSession(PlayerDatabaseWriter writer,PlayerIdentity identity,PlayerSnapshot state,Action<PlayerChange[],bool> publish)
        {
            this.writer=writer;this.identity=identity;this.publish=publish;
            var dimension=state.Rows.FirstOrDefault(r=>r.Table=="state"&&(string)r.Values[0]=="map_size");size=dimension==null?0:Convert.ToInt32(dimension.Values[1]);
            foreach(var row in state.Rows.Where(r=>r.Table=="map"&&(string)r.Values[0]=="self")){var values=row.Values;blocks.Add(Convert.ToInt32(values[1]),(byte[])values[2]);}
        }
        internal static PlayerChange[] Explore(Dictionary<int,byte[]> known,int size,int x,int y,int radius)
        {
            if(size<1||size>4096||radius<0||radius>256)throw new InvalidOperationException("Invalid exploration dimensions");
            var changed=new Dictionary<int,byte[]>();
            for(int py=Math.Max(0,y-radius);py<=Math.Min(size-1,y+radius);py++)
                for(int px=Math.Max(0,x-radius);px<=Math.Min(size-1,x+radius);px++)
                {
                    if((px-x)*(px-x)+(py-y)*(py-y)>radius*radius)continue;
                    int cell=py*size+px,key=cell/PlayerMapFormat.CellsPerBlock,bit=cell%PlayerMapFormat.CellsPerBlock;
                    byte mask=(byte)(1<<(bit%8));int offset=bit/8;
                    if(!changed.TryGetValue(key,out var bits))
                    {
                        known.TryGetValue(key,out var saved);
                        if(saved!=null&&(saved[offset]&mask)!=0)continue;
                        bits=saved==null?new byte[PlayerMapFormat.BytesPerBlock]:(byte[])saved.Clone();changed.Add(key,bits);
                    }
                    bits[offset]|=mask;
                }
            return changed.Select(p=>new PlayerChange("map",false,"self",p.Key,p.Value)).ToArray();
        }
        internal void Tick(ZDO actor,bool final=false)
        {
            if(final&&actor!=null)finalPoint=actor.GetPosition();
            if(sharing!=null)
            {
                if(!sharing.IsCompleted)return;
                try
                {
                    var result=sharing.GetAwaiter().GetResult();
                    if(shareWrite&&shareTable&&shareTable.m_nview&&shareTable.m_nview.IsValid()&&shareTable.m_nview.GetZDO().m_uid==shareId)
                    {shareTable.m_nview.GetZDO().Set(ZDOVars.s_data,result.Shared);shareTable.m_writeEffects.Create(shareTable.transform.position,shareTable.transform.rotation);}
                    publish(result.Changes,true);
                }
                finally{AbortShare();}
            }
            if(pending!=null)
            {
                if(!pending.IsCompleted)return;pending.GetAwaiter().GetResult();pending=null;
                foreach(var row in changes.Where(r=>r.Table=="map")){var values=row.Values;blocks[Convert.ToInt32(values[1])]=(byte[])values[2];}
                publish(changes,false);changes=null;
            }
            if(edits.Count!=0){changes=edits.ToArray();edits.Clear();pending=writer.CommitMap(identity,changes);return;}
            if(shareQueued)
            {
                if(!shareTable||!shareTable.m_nview||!shareTable.m_nview.IsValid()){AbortShare();return;}
                var data=shareTable.m_nview.GetZDO().GetByteArray(ZDOVars.s_data,null);sharing=writer.ShareMap(identity,data==null?null:(byte[])data.Clone(),shareWrite);shareQueued=false;return;
            }
            var map=Minimap.instance;
            if(!map){finalPoint=null;return;}
            if(actor==null&&!finalPoint.HasValue||!final&&!finalPoint.HasValue&&Time.timeAsDouble<next)return;next=Time.timeAsDouble+1;
            if(map.m_textureSize<1||map.m_textureSize>4096||map.m_pixelSize<=0||float.IsNaN(map.m_pixelSize)||float.IsInfinity(map.m_pixelSize)){finalPoint=null;return;}
            if(size!=0&&size!=map.m_textureSize)throw new InvalidOperationException("Saved map dimensions differ from the server map");
            var point=finalPoint??actor.GetPosition();finalPoint=null;
            if(float.IsNaN(point.x)||float.IsInfinity(point.x)||float.IsNaN(point.z)||float.IsInfinity(point.z))return;
            int x=Utils.RoundToInt(point.x/map.m_pixelSize+map.m_textureSize/2),y=Utils.RoundToInt(point.z/map.m_pixelSize+map.m_textureSize/2);
            if(lastX==x&&lastY==y)return;
            int radius=Mathf.CeilToInt(map.m_exploreRadius/map.m_pixelSize);
            changes=Explore(blocks,map.m_textureSize,x,y,radius);
            if(size==0){size=map.m_textureSize;changes=changes.Concat(new[]{new PlayerChange("state",false,"map_size",size,null,null,null)}).ToArray();}
            lastX=x;lastY=y;
            if(changes.Length!=0)pending=writer.CommitMap(identity,changes);
        }
    }
    internal static class GameMapView
    {
        private static readonly Dictionary<string,PlayerChange> received=new Dictionary<string,PlayerChange>();
        internal static void Clear()=>received.Clear();
        internal static void Receive(PlayerChange[] rows,bool shared=false)
        {
            GameMapPins.Confirm(rows);
            if(shared)GameMapPins.ApplyShared(rows);
            foreach(var row in rows)
            {
                if(row.Table!="map"||row.Delete)continue;var values=row.Values;
                if((string)values[0]!="self"&&(string)values[0]!="others")throw new InvalidOperationException("Invalid exploration layer");
                received[(string)values[0]+":"+Convert.ToInt32(values[1])]=row;
            }
            Apply(rows.Where(r=>r.Table=="map"&&!r.Delete));
        }
        private static void Apply(IEnumerable<PlayerChange> rows)
        {
            var map=Minimap.instance;if(!map||map.m_explored==null||!map.m_fogTexture||map.m_explored.Length!=map.m_textureSize*map.m_textureSize)return;
            bool changed=false;
            foreach(var row in rows)
            {
                var values=row.Values;var bits=(byte[])values[2];int start=Convert.ToInt32(values[1])*PlayerMapFormat.CellsPerBlock;
                bool shared=(string)values[0]=="others";var explored=shared?map.m_exploredOthers:map.m_explored;
                if(start<0||start>=map.m_explored.Length||bits.Length!=PlayerMapFormat.BytesPerBlock)throw new InvalidOperationException("Invalid exploration block");
                for(int i=0;i<PlayerMapFormat.CellsPerBlock&&start+i<map.m_explored.Length;i++)
                    if((bits[i/8]&(1<<(i%8)))!=0&&!explored[start+i])changed|=shared?map.ExploreOthers((start+i)%map.m_textureSize,(start+i)/map.m_textureSize):map.Explore((start+i)%map.m_textureSize,(start+i)/map.m_textureSize);
            }
            if(changed)map.m_fogTexture.Apply();
        }
        [HarmonyPatch(typeof(Minimap),"UpdateExplore")]
        private static class Explore{private static bool Prefix()=>!PlayerSessionGame.Managed;}
        [HarmonyPatch(typeof(Minimap),"LoadMapData")]
        private static class Loaded{private static void Postfix(){if(PlayerSessionGame.Managed)Apply(received.Values);}}
    }
}
