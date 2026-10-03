using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Overhaul.Persistence
{
    internal static class NativeMigration
    {
        private sealed class Chunk
        {
            internal int Id,Index,Size,Count;
            internal uint Version;
            internal string File;
        }
        internal static void Run(string directory,string destination,Func<byte[],byte[]> brotli,Action<int,string> progress,Action<long> afterObject=null)
        {
            if(File.Exists(destination))throw new IOException("World database already exists: " + destination);
            string temporary=destination+".migrating";
            if(File.Exists(temporary))
            {
                bool completed;
                using(var previous=new SqliteDatabase(temporary))
                {
                    if(!WorldSchema.Owned(previous))throw new InvalidDataException("Unrecognized migration file; refusing to replace it.");
                    completed=MigrationState.Complete(previous);
                    previous.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
                }
                if(completed){File.Move(temporary,destination);progress(100,"Completed migration recovered");return;}
                File.Move(temporary,temporary+".interrupted-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff"));
                progress(0,"Previous incomplete migration retained; restarting from the native save");
            }
            var generations=Directory.GetFiles(directory,"_main.*.ok").Select(p=>new{Path=p,Parts=Path.GetFileName(p).Split('.')})
                .Where(p=>p.Parts.Length==3&&uint.TryParse(p.Parts[1],out _)).OrderByDescending(p=>uint.Parse(p.Parts[1])).ToList();
            if(generations.Count==0)throw new InvalidDataException("No completed chunked native save is available for migration.");
            uint generation=uint.Parse(generations[0].Parts[1]);string prefix="_main."+generation;
            var hashes=new Dictionary<string,byte[]>();
            byte[] Read(string name)
            {
                if(Path.GetFileName(name)!=name)throw new InvalidDataException("Invalid native save path");
                byte[] data=File.ReadAllBytes(Path.Combine(directory,name));
                using(var digest=SHA256.Create())hashes[name]=digest.ComputeHash(data);return data;
            }
            progress(0,"Reading complete native save " + generation);
            using(var ok=NativeFormat.Reader(Read(prefix+".ok"))){if(ok.ReadInt32()!=NativeFormat.WorldVersion)throw new InvalidDataException("Invalid native completion marker");NativeFormat.End(ok);}
            var chunks=new List<Chunk>();int total;
            using(var map=NativeFormat.Reader(Read(prefix+".chunks")))
            {
                if(map.ReadUInt16()!=NativeFormat.WorldVersion)throw new InvalidDataException("Unsupported chunk mapping version");
                total=map.ReadInt32();int count=map.ReadInt32();if(total<0||count<0||count>65536)throw new InvalidDataException("Invalid native mapping count");
                for(int i=0;i<count;i++)
                {
                    int index=map.ReadUInt16(),size=map.ReadByte();uint version=map.ReadUInt32();int objects=map.ReadInt32();
                    if(size>3||objects<0)throw new InvalidDataException("Invalid native chunk mapping");
                    chunks.Add(new Chunk{Id=1+index+(size<<16),Index=index,Size=size,Version=version,Count=objects,File=(index>>8).ToString("x2")+"_"+(index&255).ToString("x2")+"__"+size+"_"+version+".chunk"});
                }
                NativeFormat.End(map);
            }
            var world=WorldSql.Parse(Read(prefix+".fwl2"),Read(prefix+".db2"),brotli);
            ObjectSql.PrefabName=NameCatalog.Prefab;
            using(var db=new SqliteDatabase(temporary))
            {
                WorldSchema.Create(db);
                db.Transaction(()=>
                {
                    db.Execute("INSERT INTO migration VALUES (1,?,?,?,?,?)",generation,NativeFormat.WorldVersion,directory,DateTime.UtcNow.ToString("O"),total);
                    WorldSql.Write(db,world);long id=0;int reported=-1;
                    foreach(var chunk in chunks)
                    {
                        new DatabaseRow("chunks","id,native_index,size_level,native_version,source_file,object_count",1,chunk.Id,chunk.Index,chunk.Size,chunk.Version,chunk.File,chunk.Count).Write(db);
                        using(var input=NativeFormat.Reader(Read(chunk.File)))
                        {
                            if(input.ReadUInt16()!=NativeFormat.WorldVersion||input.ReadInt32()!=chunk.Count)throw new InvalidDataException("Chunk header does not match mapping: "+chunk.File);
                            for(int i=0;i<chunk.Count;i++)
                            {
                                var record=NativeFormat.ReadObject(input,++id,chunk.Id,i,NameCatalog.Prefab,NameCatalog.Key);
                                ObjectSql.Write(db,record,true);afterObject?.Invoke(id);
                                int percent=(int)(id*95/Math.Max(1,total));if(percent!=reported){reported=percent;progress(percent,"Objects "+id+"/"+total);}
                            }
                            NativeFormat.End(input);
                        }
                    }
                    if(id!=total)throw new InvalidDataException("Imported object count does not match native mapping");
                    progress(96,"Checking source files have not changed");
                    foreach(var source in hashes)using(var digest=SHA256.Create())
                        if(!digest.ComputeHash(File.ReadAllBytes(Path.Combine(directory,source.Key))).SequenceEqual(source.Value))throw new IOException("Native save changed during migration: "+source.Key);
                    progress(98,"Checking database integrity and references");
                    MigrationState.Finish(db,world.Uid);
                    progress(99,"Committing migration");
                });
                db.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
            }
            // Re-open the committed database before publishing its final name.
            using(var verify=new SqliteDatabase(temporary,true))
                if(!MigrationState.Complete(verify,world.Uid))throw new InvalidDataException("Migration completion marker missing after commit");
            File.Move(temporary,destination);
            progress(100,"Migration complete: "+destination);
        }
    }
}
