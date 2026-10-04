using System;
using System.IO;
using System.Threading.Tasks;

namespace Overhaul.Persistence
{
    internal sealed class InventoryMoveLease : IDisposable
    {
        internal readonly long ObjectId;
        internal readonly InventoryMoveLayout Layout;
        private readonly Action<PlayerBatch> publish;
        private readonly Func<int[],bool> reserve;
        private readonly Action<int[]> release;
        private int[] slots = new int[0];
        private bool closed;
        internal InventoryMoveLease(long objectId, InventoryMoveLayout layout, Action<PlayerBatch> publish, Func<int[],bool> reserve, Action<int[]> release)
        { ObjectId = objectId; Layout = layout; this.publish = publish; this.reserve = reserve; this.release = release; }
        internal bool Reserve(int[] keys) { if (slots.Length != 0 || !reserve(keys)) return false; slots = keys; return true; }
        internal void Publish(PlayerBatch effect) => publish(effect);
        public void Dispose() { if (closed) return; closed = true; release(slots); }
    }

    // Pumped on the main thread, with all database work on the shared world executor.
    // The lease resolver authenticates container access and freezes mutations before submission.
    internal sealed class InventoryMoveService : IDisposable
    {
        private readonly PlayerAdmission.Session session;
        private readonly PlayerDatabaseWriter writer;
        private readonly InventoryMoveLayout layout;
        private readonly Func<InventoryMoveRequest, InventoryMoveLease> reserve;
        private readonly Action<byte[]> send;
        private readonly Action<Exception> failed;
        private readonly Func<InventoryMoveRequest,PlayerSnapshot,PlayerActionPlan> prepareAction;
        private Task<PlayerSnapshot> actionState;
        private Task<PlayerBatch> actionCommit;
        private PlayerActionPlan actionPlan;
        private Task<InventoryMoveResult> move;
        private Task<InventoryMoveResult> plan;
        private Task<PlayerSnapshot> playerState, containerState;
        private InventoryMoveRequest request;
        private InventoryMoveLease lease;
        private bool closed, disconnected;
        private DateTime deadline;
        internal bool Busy => request != null;
        internal bool StorageFailed { get; private set; }

        internal InventoryMoveService(PlayerAdmission.Session session, PlayerDatabaseWriter writer, InventoryMoveLayout layout,
            Func<InventoryMoveRequest, InventoryMoveLease> reserve, Action<byte[]> send, Action<Exception> failed,
            Func<InventoryMoveRequest,PlayerSnapshot,PlayerActionPlan> prepareAction = null)
        {
            if (session == null || session.State != PlayerAdmission.Phase.Ready) throw new InvalidOperationException("Character admission is incomplete");
            this.session = session; this.writer = writer; this.layout = layout; this.reserve = reserve; this.send = send; this.failed = failed;
            this.prepareAction = prepareAction;
        }
        internal void Receive(byte[] bytes)
        {
            if (closed || disconnected || session.State != PlayerAdmission.Phase.Ready) return;
            try
            {
                var incoming = InventoryMoveProtocol.Request(bytes);
                if (incoming.Nonce != session.Nonce || Busy) return;
                request = incoming; deadline = DateTime.UtcNow.AddSeconds(30);
                try
                {
                    if (incoming.Open || incoming.Action.UsesContainer) lease = reserve(incoming);
                    if ((incoming.Open || incoming.Action.UsesContainer) && lease == null) throw new InvalidOperationException("Container access denied");
                    if (incoming.Gameplay != null)
                    {
                        if (prepareAction == null) throw new InvalidOperationException("Player action handler is unavailable");
                        actionState = writer.ActionState(session.Identity);
                    }
                    else if (incoming.Open) Synchronize();
                    else plan = writer.PlanTransfer(session.Identity, incoming.Action, layout, lease?.ObjectId ?? 0, lease?.Layout);
                }
                catch (InvalidOperationException) { Synchronize(); }
            }
            catch (Exception error) { Fail(error, false); }
        }
        private void Synchronize()
        {
            playerState = writer.InventoryState(session.Identity);
            if (lease != null) containerState = writer.ContainerState(lease.ObjectId);
        }
        internal void Tick()
        {
            if (closed) return;
            if (session.State != PlayerAdmission.Phase.Ready) disconnected = true;
            try
            {
                if (actionState != null && actionState.IsCompleted)
                {
                    var state = actionState.GetAwaiter().GetResult(); actionState = null;
                    if (state.Revision != request.Action.PlayerRevision) Synchronize();
                    else
                    {
                        try { actionPlan = prepareAction(request,state); }
                        catch (InvalidOperationException) { Synchronize(); }
                        if (actionPlan != null) actionCommit = writer.CommitAction(session.Identity,actionPlan.Change);
                    }
                }
                if (actionCommit != null && actionCommit.IsCompleted)
                {
                    var result = actionCommit.GetAwaiter().GetResult(); actionCommit = null;
                    actionPlan.Publish(); actionPlan = null;
                    Complete(new InventoryMoveReply { Nonce = session.Nonce, Accepted = true, Player = result });
                }
                if (plan != null && plan.IsCompleted)
                {
                    var task = plan; plan = null;
                    if (task.IsFaulted && task.Exception.GetBaseException() is InvalidOperationException) Synchronize();
                    else
                    {
                        var prepared = task.GetAwaiter().GetResult();
                        if (lease != null && !lease.Reserve(ContainerVersions.Slots(prepared.Container))) Synchronize();
                        else move = writer.CommitTransfer(session.Identity, request.Action, prepared, lease?.ObjectId ?? 0);
                    }
                }
                if (move != null && move.IsCompleted)
                {
                    var task = move; move = null;
                    if (task.IsFaulted && task.Exception.GetBaseException() is InvalidOperationException) Synchronize();
                    else
                    {
                        var result = task.GetAwaiter().GetResult();
                        // Even a disconnected client needs its committed world result published before unlock.
                        lease?.Publish(result.Container);
                        Complete(new InventoryMoveReply { Nonce = session.Nonce, Accepted = true, Player = result.Player,
                            Container = result.Container, ContainerAllowed = lease != null });
                    }
                }
                if (playerState != null && playerState.IsCompleted && (containerState == null || containerState.IsCompleted))
                {
                    var player = playerState.GetAwaiter().GetResult(); var chest = containerState?.GetAwaiter().GetResult();
                    Complete(new InventoryMoveReply { Nonce = session.Nonce, Snapshot = true, Accepted = request.Open && chest != null,
                        ContainerAllowed = chest != null, Player = new PlayerBatch(request.Action.Operation, player.Revision, player.Rows),
                        Container = chest == null ? null : new PlayerBatch(request.Action.Operation, chest.Revision, chest.Rows) });
                }
                // Do not release an accepted write's world reservation on timeout: it may still commit.
                if (Busy && !disconnected && DateTime.UtcNow >= deadline)
                { disconnected = true; failed(new TimeoutException("Inventory action timed out; committed state will be recovered on reconnect")); }
                if (disconnected && !Busy) closed = true;
            }
            catch (Exception error) { Fail(error, true); }
        }
        private void Complete(InventoryMoveReply reply)
        {
            reply.ContainerUser = request.ContainerUser; reply.ContainerId = request.ContainerId;
            lease?.Dispose(); lease = null; request = null; playerState = null; containerState = null;
            if (!disconnected && session.State == PlayerAdmission.Phase.Ready) send(InventoryMoveProtocol.Encode(reply));
        }
        private void Fail(Exception error, bool storage)
        {
            // Storage failure after a staged cross-DB operation must retain the reservation.
            // The host shuts down/reloads rather than publishing uncertain inventory contents.
            StorageFailed = storage;
            Observe(plan); Observe(move); Observe(playerState); Observe(containerState); Observe(actionState); Observe(actionCommit);
            disconnected = true; closed = true; failed(error);
        }
        private static void Observe(Task task)
        { task?.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously); }
        public void Dispose()
        {
            disconnected = true;
            if (!Busy) { lease?.Dispose(); lease = null; closed = true; }
            // The host continues Tick while Busy to finish accepted writes before discarding this service.
        }
    }
}
