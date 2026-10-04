using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Overhaul.Persistence
{
    internal sealed partial class PlayerDatabase
    {
        internal void CommitProgress(PlayerBatch batch)
        {
            if(!Complete || batch.Changes.Any(r=>!PlayerProgressService.Allowed(r)))throw new InvalidDataException("Server simulation cannot modify this character table");
            db.Transaction(()=>{if(Revision!=batch.ExpectedRevision)throw new InvalidOperationException("Progress overlapped another player action");ApplyRows(batch.Changes);});
        }
    }
    internal sealed partial class PlayerDatabaseWriter
    {
        internal Task<bool> CommitProgress(PlayerIdentity identity,PlayerBatch batch)
        {
            if(shared==null)return Submit(()=>{Get(identity).CommitProgress(batch);return true;},true);
            return SubmitWorld(world=>
            {
                PrepareTransfers(world);
                using(var pending=world.Query("SELECT 1 FROM player_transfers WHERE provider=? AND account=? AND complete=0",identity.Provider,identity.Account))
                    if(pending.Read())throw new InvalidOperationException("Progress is waiting for transfer recovery");
                Get(identity).CommitProgress(batch);return true;
            });
        }
    }
    // Server simulation events share the same per-player gate as inventory actions.
    // They leave the inventory revision unchanged and never read a client snapshot.
    internal sealed class PlayerProgressService
    {
        internal static bool Allowed(PlayerChange row)=>row.Table=="skills" || row.Table=="knowledge" || row.Table=="food" || row.Table=="effects" ||
            row.Table=="state" && !row.Delete && (PlayerResources.IsKey((string)row.Values[0]) || (string)row.Values[0]==PlayerFoodClock.Key || (string)row.Values[0]==PlayerEffectClock.Key);
        private readonly PlayerIdentity identity;
        private readonly PlayerDatabaseWriter writer;
        private readonly Action<PlayerBatch> publish;
        private readonly Action<Exception> failed;
        private readonly Queue<Func<PlayerSnapshot,IEnumerable<PlayerChange>>> queued=new Queue<Func<PlayerSnapshot,IEnumerable<PlayerChange>>>();
        private Func<PlayerSnapshot,IEnumerable<PlayerChange>>[] active;
        private Task<PlayerSnapshot> read;
        private Task<bool> commit;
        private PlayerBatch result;
        private bool closing,broken;
        internal bool Failed=>broken;
        internal bool Busy=>read!=null || commit!=null;
        internal bool Finished=>!Busy && queued.Count==0;
        internal PlayerProgressService(PlayerIdentity identity,PlayerDatabaseWriter writer,Action<PlayerBatch> publish,Action<Exception> failed)
        {this.identity=identity;this.writer=writer;this.publish=publish;this.failed=failed;}
        internal bool Enqueue(Func<PlayerSnapshot,IEnumerable<PlayerChange>> action)
        {
            if(closing || broken)return false;
            if(queued.Count>=256){broken=true;failed(new InvalidOperationException("Server progress queue exceeded"));return false;}
            queued.Enqueue(action??throw new ArgumentNullException(nameof(action)));return true;
        }
        internal void Close()=>closing=true;
        internal void Tick(bool playerActionBusy)
        {
            if(broken)return;
            try
            {
                if(commit?.IsCompleted==true)
                {commit.GetAwaiter().GetResult();commit=null;var confirmed=result;result=null;active=null;publish(confirmed);}
                if(read?.IsCompleted==true)
                {
                    var snapshot=read.GetAwaiter().GetResult();read=null;var current=snapshot.Rows.ToList();var changes=new List<PlayerChange>();
                    foreach(var action in active)
                    {
                        var delta=action(new PlayerSnapshot(snapshot.Revision,current)).ToArray();
                        foreach(var row in delta)
                        {
                            if(!Allowed(row))throw new InvalidDataException("Invalid server simulation table");
                            int keys=PlayerDatabase.Tables.Single(t=>t.Name==row.Table).Keys;
                            bool Same(PlayerChange old)=>old.Table==row.Table && (row.Table=="skills" || row.Table=="food" || row.Table=="effects"
                                ? Convert.ToInt32(old.Values[0])==Convert.ToInt32(row.Values[0])
                                : old.Values.Take(keys).SequenceEqual(row.Values.Take(keys)));
                            current.RemoveAll(old=>Same(old));if(!row.Delete)current.Add(row);
                            changes.RemoveAll(old=>Same(old));changes.Add(row);
                        }
                    }
                    if(changes.Count==0)active=null;
                    else{result=new PlayerBatch(Guid.NewGuid().ToString("N"),snapshot.Revision,changes);commit=writer.CommitProgress(identity,result);}
                }
                if(!Busy && !playerActionBusy && queued.Count!=0)
                {active=queued.ToArray();queued.Clear();read=writer.ActionState(identity);}
            }
            catch(Exception error){broken=true;failed(error);}
        }
    }
}
