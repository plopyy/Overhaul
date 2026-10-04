using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
        private sealed class Command
        {
            internal Action<SqliteDatabase> Run;
            internal Action<Exception> Fail;
            internal bool Retry;
        }
        private readonly Queue<Command> commands = new Queue<Command>();
        private string failureText;
        internal string LastError { get { lock(gate)return failureText; } }
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
        // A capture preceding a slot reservation must not be coalesced away by a later
        // protected capture: a new container may not have any persisted slots yet.
        internal void CaptureInventory(ObjectRecord snapshot)
        {
            lock(gate)
            {
                if(stopping||disposed)throw new ObjectDisposedException(nameof(ProgressiveWriter));
                QueueCapture();
                commands.Enqueue(new Command { Run = db => db.Transaction(() => ObjectSql.WriteInventoryCapture(db,snapshot)), Fail = _ => { }, Retry=true });
                submitted++;
            }
            wake.Set();
        }
        // Inventory/world transactions share this executor. Their callers reserve participating
        // containers before submission, so later ordinary captures cannot overwrite the result.
        internal Task<T> Submit<T>(Func<SqliteDatabase,T> action)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock(gate)
            {
                if(stopping||disposed)throw new ObjectDisposedException(nameof(ProgressiveWriter));
                if(commands.Count>=1024)throw new InvalidOperationException("World command queue is full");
                QueueCapture();
                commands.Enqueue(new Command { Run = db=>{try{completion.SetResult(action(db));}catch(Exception error){completion.SetException(error);}},
                    Fail = error => completion.TrySetException(new IOException("World snapshot failed before inventory command",error)) });
                submitted++;
            }
            wake.Set();return completion.Task;
        }
        // Called under gate. Captures and accepted actions must retain their order,
        // including after an I/O failure: a newer chest snapshot cannot overtake a debit.
        private void QueueCapture()
        {
            if(pending.Count==0 && world==null)return;
            var changes=pending;var metadata=world;pending=new Dictionary<long,ObjectRecord>();world=null;
            commands.Enqueue(new Command{Retry=true,Fail=_=>{},Run=db=>db.Transaction(()=>
            {
                foreach(var item in changes)if(item.Value==null)ObjectSql.Delete(db,item.Key);else ObjectSql.Write(db,item.Value);
                if(metadata!=null)WorldSql.Write(db,metadata);
            })});
        }
        internal void Persist(Action<SqliteDatabase> action)
        {
            lock(gate)
            {
                if(stopping||disposed)throw new ObjectDisposedException(nameof(ProgressiveWriter));
                QueueCapture();commands.Enqueue(new Command{Run=action,Fail=_=>{},Retry=true});submitted++;
            }
            wake.Set();
        }
        // Close player connections on their owning thread even when a world snapshot fails.
        // This never runs gameplay writes after a failed snapshot.
        internal Task Cleanup(Action action)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action run = () => { try { action(); completion.TrySetResult(true); } catch (Exception error) { completion.TrySetException(error); } };
            lock(gate)
            {
                if(stopping||disposed)throw new ObjectDisposedException(nameof(ProgressiveWriter));
                QueueCapture();
                commands.Enqueue(new Command { Run = _ => run(), Fail = _ => run() });
                submitted++;
            }
            wake.Set(); return completion.Task;
        }
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
            SqliteDatabase db=null;
            var actions = new List<Command>();
            try
            {
                while(true)
                {
                    wake.WaitOne(5000);long version;bool createBackup;
                    lock(gate)
                    {
                        QueueCapture();
                        version=submitted;createBackup=backup;backup=false;
                        while(commands.Count>0)actions.Add(commands.Dequeue());
                    }
                    try
                    {
                        if(db==null)db=new SqliteDatabase(path);
                        while(actions.Count!=0){actions[0].Run(db);actions.RemoveAt(0);}
                        lock(gate){committed=version;failure=null;failureText=null;Monitor.PulseAll(gate);}
                        if(createBackup)Backup(db);
                    }
                    catch(Exception ex)
                    {
                        // Keep accepted mutations and captures, in order, for retry.
                        // Ordinary reads/cleanup retain their failure contract.
                        foreach(var action in actions.Where(a=>!a.Retry).ToArray()){action.Fail(ex);actions.Remove(action);}
                        lock(gate){failure=ex;failureText=ex.ToString();backup|=createBackup;Monitor.PulseAll(gate);}
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
