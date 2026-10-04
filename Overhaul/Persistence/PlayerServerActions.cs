using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Overhaul.Persistence
{
    // Only server code can enqueue these actions. The same gate as client intents
    // protects preparation and publication of the accepted state in memory.
    internal sealed class PlayerServerActions
    {
        private sealed class Event
        { internal Func<PlayerSnapshot,PlayerActionPlan> Prepare; internal Action Confirm; }
        private readonly Queue<Event> queued=new Queue<Event>();
        private readonly PlayerIdentity identity;
        private readonly PlayerDatabaseWriter writer;
        private readonly Action<PlayerBatch> publish;
        private readonly Action<Exception> failed;
        private Event active;
        private PlayerActionPlan plan;
        private Task<PlayerSnapshot> read;
        private Task<PlayerBatch> commit;
        private bool closing;
        internal bool Failed { get; private set; }
        internal bool Busy=>active!=null;
        internal bool Finished=>!Busy && queued.Count==0;
        internal Func<PlayerSnapshot> Snapshot;
        internal PlayerServerActions(PlayerIdentity identity,PlayerDatabaseWriter writer,Action<PlayerBatch> publish,Action<Exception> failed)
        {this.identity=identity;this.writer=writer;this.publish=publish;this.failed=failed;}
        internal bool Enqueue(Func<PlayerSnapshot,PlayerActionPlan> prepare,Action confirmed=null)
        {
            if(closing || Failed)return false;
            if(prepare==null)throw new ArgumentNullException(nameof(prepare));
            if(queued.Count>=256){Fail(new InvalidOperationException("Server inventory action queue exceeded"));return false;}
            queued.Enqueue(new Event{Prepare=prepare,Confirm=confirmed});return true;
        }
        internal void Close()=>closing=true;
        private void Confirm()
        {var completed=active;active=null;plan=null;completed.Confirm?.Invoke();}
        internal void Tick(bool otherActionBusy)
        {
            if(Failed)return;
            try
            {
                if(!Busy && !otherActionBusy && queued.Count!=0)
                {active=queued.Dequeue();read=Snapshot==null?writer.ActionState(identity):Task.FromResult(Snapshot());}
                if(read?.IsCompleted==true)
                {
                    var state=read.GetAwaiter().GetResult();read=null;
                    plan=active.Prepare(state);
                    if(plan==null)Confirm();
                    else
                    {
                        if(plan.Change.Player.ExpectedRevision!=state.Revision || plan.Ready!=null || plan.NextLayout!=null)
                            throw new InvalidDataException("Invalid immediate server inventory action");
                        commit=writer.CommitAction(identity,plan.Change);
                    }
                }
                if(commit?.IsCompleted==true)
                {
                    var result=commit.GetAwaiter().GetResult();commit=null;
                    plan.Publish();publish(result);Confirm();
                }
            }
            catch(Exception error){Fail(error);}
        }
        private void Fail(Exception error)
        {
            // Keep reservations on uncertain writes; host recovery resolves them.
            Failed=true;failed(error);
        }
    }
}
