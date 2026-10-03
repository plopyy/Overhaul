using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Overhaul.Persistence
{
    internal sealed class ProgressiveWriter : IDisposable
    {
        private readonly string path;
        private readonly object gate=new object();
        private readonly AutoResetEvent wake=new AutoResetEvent(false);
        private readonly Thread thread;
        private readonly FileStream ownership;
        private Dictionary<long,ObjectRecord> pending=new Dictionary<long,ObjectRecord>();
        private WorldRecord world;
        private long submitted,committed;
        private bool stopping,disposed,backup;
        private Exception failure;
        internal string LastError { get { lock(gate)return failure?.ToString(); } }
        internal long Committed { get { lock(gate)return committed; } }
        internal ProgressiveWriter(string path,FileStream existingOwnership=null)
        {
            this.path=path;
            // Prevent two running servers from logically overwriting the same world.
            ownership=existingOwnership??new FileStream(path+".owner",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            try
            {
                using(var db=new SqliteDatabase(path,true))if(!MigrationState.Complete(db))throw new InvalidDataException("Incomplete world migration");
                thread=new Thread(Run){Name="Overhaul SQLite writer",IsBackground=true};thread.Start();
            }
            catch{ownership.Dispose();throw;}
        }
        internal void Enqueue(IEnumerable<ObjectRecord> changes,IEnumerable<long> deletions,WorldRecord metadata=null)
        {
            lock(gate)
            {
                if(stopping||disposed)throw new ObjectDisposedException(nameof(ProgressiveWriter));
                if(changes!=null)foreach(var item in changes)pending[item.Id]=item;
                if(deletions!=null)foreach(long id in deletions)pending[id]=null;
                if(metadata!=null)world=metadata;
                submitted++;
            }
        }
        internal void RequestFlush(bool createBackup=false){lock(gate)backup|=createBackup;wake.Set();}
        internal void Flush()
        {
            lock(gate)
            {
                long target=submitted;wake.Set();
                while(committed<target)
                {
                    if(failure!=null)throw new IOException("SQLite world changes are still pending; commit failed.",failure);
                    Monitor.Wait(gate,1000);
                }
            }
        }
        private void Run()
        {
            SqliteDatabase db=null;var batch=new Dictionary<long,ObjectRecord>();WorldRecord metadata=null;
            try
            {
                while(true)
                {
                    wake.WaitOne(5000);long version;bool createBackup;
                    lock(gate)
                    {
                        if(batch.Count==0){var empty=batch;batch=pending;pending=empty;}
                        else {foreach(var item in pending)batch[item.Key]=item.Value;pending.Clear();}
                        if(world!=null){metadata=world;world=null;}
                        version=submitted;createBackup=backup;backup=false;
                    }
                    try
                    {
                        if(db==null)db=new SqliteDatabase(path);
                        if(batch.Count>0||metadata!=null)db.Transaction(()=>
                        {
                            // Related changes captured in one batch are committed together.
                            foreach(var item in batch)if(item.Value==null)ObjectSql.Delete(db,item.Key);else ObjectSql.Write(db,item.Value);
                            if(metadata!=null)WorldSql.Write(db,metadata);
                        });
                        batch.Clear();metadata=null;
                        lock(gate){committed=version;failure=null;Monitor.PulseAll(gate);}
                        if(createBackup)Backup(db);
                    }
                    catch(Exception ex)
                    {
                        lock(gate){failure=ex;backup|=createBackup;Monitor.PulseAll(gate);}
                        db?.Dispose();db=null;
                    }
                    lock(gate)if(stopping)break;
                }
            }
            finally{db?.Dispose();}
        }
        private void Backup(SqliteDatabase db)
        {
            string directory=Path.Combine(Path.GetDirectoryName(path),"sqlite-backups");Directory.CreateDirectory(directory);
            string prefix=Path.GetFileNameWithoutExtension(path)+"-";
            string destination=Path.Combine(directory,prefix+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff")+".db");
            db.Execute("VACUUM INTO ?",destination);
            foreach(string file in Directory.GetFiles(directory,prefix+"*.db").OrderByDescending(f=>f,StringComparer.Ordinal).Skip(3))
            {
                string name=Path.GetFileNameWithoutExtension(file).Substring(prefix.Length);
                if(DateTime.TryParseExact(name,"yyyyMMdd-HHmmss-fffffff",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out _))File.Delete(file);
            }
        }
        public void Dispose()
        {
            lock(gate){if(disposed)return;disposed=true;}
            Exception error=null;
            try{Flush();}catch(Exception ex){error=ex;}
            lock(gate)stopping=true;wake.Set();thread.Join();wake.Dispose();ownership.Dispose();
            if(error!=null)throw error;
        }
    }
}
