using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace Overhaul.Persistence
{
    internal sealed class InventoryMoveController : IDisposable
    {
        private readonly string nonce;
        private readonly Action<byte[]> send;
        private readonly Action<InventoryMoveReply> apply;
        private readonly Action<InventoryMoveReply> applyServer;
        private readonly Action<Exception> failed;
        private InventoryMoveRequest pending;
        private DateTime deadline;
        private long snapshotRevision;
        private readonly Dictionary<int,long> slotRevisions = new Dictionary<int,long>();
        private readonly Queue<InventoryMoveReply> buffered = new Queue<InventoryMoveReply>();
        internal long PlayerRevision { get; private set; }
        internal long ContainerRevision { get; private set; }
        internal long ContainerUser { get; private set; }
        internal uint ContainerId { get; private set; }
        internal bool Busy => pending != null;
        internal bool Closed { get; private set; }
        internal InventoryMoveRequest Pending => pending;
        internal InventoryMoveController(string nonce, long revision, Action<byte[]> send, Action<InventoryMoveReply> apply, Action<Exception> failed, Action<InventoryMoveReply> applyServer = null)
        {
            if (!Guid.TryParseExact(nonce, "N", out _) || revision < 0) throw new ArgumentException("Invalid inventory session");
            this.nonce = nonce; PlayerRevision = revision; this.send = send; this.apply = apply; this.failed = failed;
            this.applyServer = applyServer;
        }
        internal void ReceiveServer(byte[] bytes)
        {
            if(Closed)return;
            try
            {
                var reply=InventoryMoveProtocol.Reply(bytes);
                if(reply.Nonce!=nonce)return;
                if(!reply.Accepted || reply.Snapshot || reply.Notification || reply.ContainerAllowed || reply.Container!=null)
                    throw new InvalidDataException("Invalid server inventory update");
                if(reply.Player.ExpectedRevision<PlayerRevision)return; // Repeated confirmed update.
                if(reply.Player.ExpectedRevision!=PlayerRevision || applyServer==null)
                    throw new InvalidDataException("Server inventory update revision mismatch");
                applyServer(reply);
                PlayerRevision=checked(PlayerRevision+1);
                // An already sent intent retains its original revision. The server
                // will reject/resynchronize it instead of applying it to new slots.
            }
            catch(Exception error){Fail(error);}
        }
        internal bool Open(long user, uint id)
        {
            if (Closed || Busy || id == 0) return false;
            return Send(new InventoryMoveRequest { Nonce = nonce, Open = true, ContainerUser = user, ContainerId = id,
                Action = new InventoryMoveAction(Guid.NewGuid().ToString("N"), PlayerRevision, 0, InventoryMoveKind.TakeAll, 1, 0, 0, 0, 0, 0, 1) });
        }
        internal bool Move(InventoryMoveKind kind, int from, int to, int fromX, int fromY, int toX, int toY, int amount)
        {
            if (Closed || Busy || (from == 1 || to == 1) && ContainerId == 0) return false;
            return Send(new InventoryMoveRequest { Nonce = nonce, ContainerUser = ContainerUser, ContainerId = ContainerId,
                Action = new InventoryMoveAction(Guid.NewGuid().ToString("N"), PlayerRevision, snapshotRevision, kind, from, to, fromX, fromY, toX, toY, amount, slotRevisions) });
        }
        internal bool Act(PlayerActionCommand action, int x = 0, int y = 0, int amount = 1)
        {
            if (Closed || Busy || action == null) return false;
            return Send(new InventoryMoveRequest { Nonce = nonce, Gameplay = action,
                Action = new InventoryMoveAction(Guid.NewGuid().ToString("N"), PlayerRevision, 0, InventoryMoveKind.Gameplay, 0, 0, x, y, 0, 0, amount) });
        }
        private bool Send(InventoryMoveRequest request)
        {
            pending = request; deadline = DateTime.UtcNow.AddSeconds(30);
            try { send(InventoryMoveProtocol.Encode(request)); return true; }
            catch (Exception error) { Fail(error); return false; }
        }
        internal void Receive(byte[] bytes)
        {
            if (Closed) return;
            try
            {
                var reply = InventoryMoveProtocol.Reply(bytes);
                if (reply.Nonce != nonce) return;
                if (reply.Notification)
                {
                    if (pending?.Open == true)
                    {
                        if (reply.ContainerUser == pending.ContainerUser && reply.ContainerId == pending.ContainerId)
                        { if (buffered.Count >= 256) throw new InvalidDataException("Inventory opening update queue exceeded"); buffered.Enqueue(reply); }
                    }
                    else Notify(reply);
                    return;
                }
                if (pending == null || reply.Player.Operation != pending.Action.Operation) return;
                if (reply.Snapshot ? reply.Player.ExpectedRevision < PlayerRevision : reply.Player.ExpectedRevision != PlayerRevision)
                    throw new InvalidDataException("Player inventory response revision mismatch");
                if (!reply.Snapshot && !reply.Accepted || pending.Open && !reply.Snapshot ||
                    !reply.Snapshot && pending.Action.UsesContainer && !reply.ContainerAllowed)
                    throw new InvalidDataException("Incomplete inventory response");
                if (reply.ContainerAllowed && (reply.ContainerUser != pending.ContainerUser || reply.ContainerId != pending.ContainerId))
                    throw new InvalidDataException("Container response identity mismatch");
                bool opening = pending.Open;
                if (reply.ContainerAllowed && !opening) Filter(reply);
                apply(reply); // Resolves both inventories before changing either one.
                PlayerRevision = reply.Snapshot ? reply.Player.ExpectedRevision : checked(PlayerRevision + 1);
                if (reply.ContainerAllowed)
                {
                    ContainerUser = pending.ContainerUser; ContainerId = pending.ContainerId;
                    if (opening) { slotRevisions.Clear(); snapshotRevision = 0; ContainerRevision = 0; }
                    Record(reply);
                }
                else if (pending.Open || pending.Action.UsesContainer) { ContainerId = 0; ContainerUser = 0; ContainerRevision = 0; snapshotRevision = 0; slotRevisions.Clear(); }
                pending = null;
                while (buffered.Count != 0) Notify(buffered.Dequeue());
            }
            catch (Exception error) { Fail(error); }
        }
        private void Notify(InventoryMoveReply reply)
        {
            if (!reply.Accepted || reply.Snapshot || !reply.ContainerAllowed || reply.Player.Changes.Any()) throw new InvalidDataException("Invalid container update");
            if (reply.ContainerUser != ContainerUser || reply.ContainerId != ContainerId || ContainerId == 0) return;
            Filter(reply); apply(reply); Record(reply);
        }
        private void Filter(InventoryMoveReply reply)
        {
            if (reply.Snapshot)
            {
                if (reply.Container.ExpectedRevision < snapshotRevision) throw new InvalidDataException("Stale container synchronization");
                reply.PreserveContainerSlots = new HashSet<int>(slotRevisions.Where(p => p.Value > reply.Container.ExpectedRevision).Select(p => p.Key));
            }
            else
            {
                long revision = checked(reply.Container.ExpectedRevision + 1);
                reply.Container = new PlayerBatch(reply.Container.Operation, reply.Container.ExpectedRevision, reply.Container.Changes.Where(row =>
                {
                    int key = Convert.ToInt32(row.Values[2]) * 256 + Convert.ToInt32(row.Values[1]);
                    return revision > (slotRevisions.TryGetValue(key, out long value) ? value : snapshotRevision);
                }));
            }
        }
        private void Record(InventoryMoveReply reply)
        {
            long version = reply.Snapshot ? reply.Container.ExpectedRevision : checked(reply.Container.ExpectedRevision + 1);
            if (reply.Snapshot)
            {
                snapshotRevision = version;
                foreach (int key in slotRevisions.Where(p => p.Value <= version).Select(p => p.Key).ToArray()) slotRevisions.Remove(key);
            }
            else foreach (int key in ContainerVersions.Slots(reply.Container)) slotRevisions[key] = version;
            ContainerRevision = Math.Max(ContainerRevision, version);
        }
        internal void Tick()
        { if (!Closed && Busy && DateTime.UtcNow >= deadline) Fail(new TimeoutException("Inventory response timed out")); }
        private void Fail(Exception error) { Dispose(); failed(error); }
        public void Dispose() { Closed = true; pending = null; buffered.Clear(); slotRevisions.Clear(); }
    }
}
