using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Overhaul.Persistence
{
    // Main-thread state. Disk completion never changes the accepted gameplay state.
    internal sealed class PlayerLiveStore
    {
        private readonly Dictionary<string,PlayerSnapshot> players=new Dictionary<string,PlayerSnapshot>();
        private sealed class Container
        {
            internal PlayerSnapshot State;
            internal readonly Dictionary<int,long> Versions=new Dictionary<int,long>();
        }
        private readonly Dictionary<long,Container> containers=new Dictionary<long,Container>();
        internal PlayerSnapshot Find(PlayerIdentity id)=>players.TryGetValue(id.FileName,out var state)?state:null;
        internal void Bind(PlayerIdentity id,PlayerSnapshot state){if(!players.ContainsKey(id.FileName))players.Add(id.FileName,state);}
        internal void Apply(PlayerIdentity id,PlayerBatch batch,bool advance)
        {
            var state=Find(id)??throw new InvalidOperationException("Live character is unavailable");
            if(state.Revision!=batch.ExpectedRevision)throw new InvalidOperationException("Stale live character revision");
            players[id.FileName]=new PlayerSnapshot(state.Revision+(advance?1:0),PlayerProgressService.Overlay(state,batch.Changes).Rows);
        }
        internal void Map(PlayerIdentity id,IEnumerable<PlayerChange> rows)
        {var state=Find(id);if(state!=null)players[id.FileName]=PlayerProgressService.Overlay(state,rows);}
        private static int Slot(PlayerChange row)=>Convert.ToInt32(row.Values[2])*256+Convert.ToInt32(row.Values[1]);
        private static Dictionary<int,string> Prints(IEnumerable<PlayerChange> rows)=>rows.GroupBy(Slot).ToDictionary(g=>g.Key,g=>
            new PlayerBatch("00000000000000000000000000000000",0,g.OrderBy(r=>r.Table,StringComparer.Ordinal).ThenBy(r=>r.Table=="item_data"?(string)r.Values[3]:"",StringComparer.Ordinal)).Digest());
        internal void ForgetChest(long id)=>containers.Remove(id);
        internal bool Capture(long id,IEnumerable<PlayerChange> rows)
        {
            var snapshot=new PlayerSnapshot(0,rows);
            if(!containers.TryGetValue(id,out var container)){containers.Add(id,new Container{State=snapshot});return true;}
            var before=Prints(container.State.Rows);var after=Prints(snapshot.Rows);
            var changed=before.Keys.Union(after.Keys).Where(k=>!before.TryGetValue(k,out var a)||!after.TryGetValue(k,out var b)||a!=b).ToArray();
            if(changed.Length==0)return false;
            long revision=checked(container.State.Revision+1);container.State=new PlayerSnapshot(revision,snapshot.Rows);
            foreach(int slot in changed)container.Versions[slot]=revision;
            return true;
        }
        internal PlayerSnapshot Chest(long id)=>containers.TryGetValue(id,out var c)?c.State:throw new InvalidOperationException("Live container is unavailable");
        internal bool HasChest(long id)=>containers.ContainsKey(id);
        internal void Validate(long id,InventoryMoveAction action,IEnumerable<int> slots)
        {
            var container=containers[id];long revision=container.State.Revision;
            if(action.ContainerRevision>revision)throw new InvalidOperationException("Unknown live container revision");
            foreach(int slot in slots)if(action.SlotVersion(slot)>revision||container.Versions.TryGetValue(slot,out long changed)&&changed>action.SlotVersion(slot))
                throw new InvalidOperationException("Container slot changed");
        }
        internal PlayerBatch ApplyChest(long id,PlayerBatch batch)
        {
            var container=containers[id];var state=container.State;
            var result=new PlayerBatch(batch.Operation,state.Revision,batch.Changes);
            container.State=new PlayerSnapshot(checked(state.Revision+1),PlayerProgressService.Overlay(state,batch.Changes).Rows);
            foreach(int slot in ContainerVersions.Slots(batch))container.Versions[slot]=container.State.Revision;
            return result;
        }
    }
    internal sealed partial class PlayerDatabaseWriter
    {
        internal PlayerLiveStore Live {get;private set;}
        private sealed class PendingProgress
        {internal PlayerIdentity Identity;internal long Revision;internal DateTime Due;internal readonly List<PlayerChange> Rows=new List<PlayerChange>();}
        private readonly Dictionary<string,PendingProgress> pendingProgress=new Dictionary<string,PendingProgress>();
        internal void FlushLiveProgress(bool force=false)
        {foreach(var entry in pendingProgress.Values.Where(p=>force||DateTime.UtcNow>=p.Due).ToArray())FlushLiveProgress(entry);}
        private void FlushLiveProgress(PendingProgress entry)
        {
            var batch=new PlayerBatch(Guid.NewGuid().ToString("N"),entry.Revision,entry.Rows);
            shared.Persist(world=>{PrepareTransfers(world);Get(entry.Identity).CommitProgress(batch);});
            pendingProgress.Remove(entry.Identity.FileName);
        }
        internal void EnableLiveState(){if(shared==null)throw new InvalidOperationException("Live state requires a world writer");Live=new PlayerLiveStore();}
        internal PlayerBatch AcceptAction(PlayerIdentity identity,PlayerWorldAction action)
        {
            if(Live.Find(identity)?.Revision!=action.Player.ExpectedRevision)throw new InvalidOperationException("Stale live action");
            foreach(var pair in action.Containers)pair.Value.Validate(Live.Chest(pair.Key).Rows);
            if(pendingProgress.TryGetValue(identity.FileName,out var progress))FlushLiveProgress(progress);
            // Queue immutable values before publication; no worker callback touches Unity or Live.
            PersistAccepted(identity,action);
            foreach(var pair in action.Containers)action.CommittedContainers[pair.Key]=Live.ApplyChest(pair.Key,pair.Value.After);
            Live.Apply(identity,action.Player,true);return action.Player;
        }
        private void PersistAccepted(PlayerIdentity identity,PlayerWorldAction action)
        {
            var objects=action.Objects;var containers=action.Containers;var keys=action.WorldKeys;var batch=action.Player;
            bool attempted=false;
            shared.Persist(world=>
            {
                PrepareTransfers(world);if(attempted)PlayerTransferJournal.Recover(world,Get);attempted=true;
                var player=Get(identity);
                // A failed Finish can already have committed the player's half.
                if(player.Revision==batch.ExpectedRevision+1)return;
                if(player.Revision!=batch.ExpectedRevision)throw new InvalidDataException("Accepted action persistence order differs");
                if(objects.Count==0&&containers.Count==0){player.CommitDelta(batch);return;}
                PlayerTransferJournal.Stage(world,player,identity,batch,action.Digest(),objects.Keys.Concat(containers.Keys),db=>
                {
                    foreach(string key in keys)db.Write("INSERT OR IGNORE INTO world_keys(source,key) VALUES(?,?)","progress",key);
                    foreach(var pair in objects)if(pair.Value==null)ObjectSql.Delete(db,pair.Key);else ObjectSql.Write(db,pair.Value);
                    foreach(var pair in containers)pair.Value.Apply(db,pair.Key);
                });
                PlayerTransferJournal.Finish(world,player,identity,batch.Operation);
            });
        }
        internal InventoryMoveResult AcceptTransfer(PlayerIdentity identity,InventoryMoveAction request,InventoryMoveResult result,long objectId)
        {
            if(result.Player.Operation!=request.Operation||result.Player.ExpectedRevision!=request.PlayerRevision)throw new InvalidDataException("Wrong prepared transfer");
            if(request.UsesContainer)Live.Validate(objectId,request,ContainerVersions.Slots(result.Container));
            var containers=new Dictionary<long,PlayerContainerAction>();
            if(request.UsesContainer)containers.Add(objectId,new PlayerContainerAction(Live.Chest(objectId).Rows,result.Container));
            var action=new PlayerWorldAction(result.Player,new Dictionary<long,ObjectRecord>(),containers);
            AcceptAction(identity,action);
            return new InventoryMoveResult(result.Player,request.UsesContainer?action.CommittedContainers[objectId]:result.Container,result.Moved);
        }
        internal bool AcceptProgress(PlayerIdentity identity,PlayerBatch batch)
        {
            if(batch.Changes.Any(r=>!PlayerProgressService.Allowed(r)))throw new InvalidDataException("Invalid progress table");
            if(Live.Find(identity)?.Revision!=batch.ExpectedRevision)throw new InvalidOperationException("Stale live progress");
            if(!pendingProgress.TryGetValue(identity.FileName,out var entry))
                pendingProgress.Add(identity.FileName,entry=new PendingProgress{Identity=identity,Revision=batch.ExpectedRevision,Due=DateTime.UtcNow.AddSeconds(30)});
            if(entry.Rows.Count+batch.Changes.Count()>65536)
            {
                FlushLiveProgress(entry);
                pendingProgress.Add(identity.FileName,entry=new PendingProgress{Identity=identity,Revision=batch.ExpectedRevision,Due=DateTime.UtcNow.AddSeconds(30)});
            }
            entry.Rows.AddRange(batch.Changes);
            Live.Apply(identity,batch,false);
            if(batch.Changes.Any(r=>r.Table=="spawn"&&(string)r.Values[0]=="logout"))FlushLiveProgress(entry);
            return true;
        }
    }
}
