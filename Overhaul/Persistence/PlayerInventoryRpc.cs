using System;
using System.IO;
using System.Threading.Tasks;

namespace Overhaul.Persistence
{
    // Bound only by admission to an authenticated connection. This channel does not authenticate
    // accounts itself and never accepts a player ID, inventory contents or arbitrary SQL/rows.
    internal sealed class PlayerInventoryRpc : IDisposable
    {
        internal const string Request = "Overhaul_InventoryMove", Response = "Overhaul_InventoryResult";
        private readonly ZRpc rpc;
        private readonly PlayerAdmission.Session session;
        private readonly PlayerDatabaseWriter writer;
        private readonly PlayerInventoryRules rules;
        private Task<PlayerBatch> pending;
        private Task<PlayerSnapshot> resync;
        private PlayerInventoryMove request;
        private bool closed;
        private DateTime deadline;

        internal PlayerInventoryRpc(ZRpc authenticatedRpc, PlayerAdmission.Session session,
            PlayerDatabaseWriter writer, PlayerInventoryRules rules)
        {
            if (authenticatedRpc == null || session == null || writer == null || rules == null ||
                !ReferenceEquals(session.Connection, authenticatedRpc) || session.State != PlayerAdmission.Phase.Ready)
                throw new InvalidOperationException("Inventory channel requires an admitted connection");
            rpc = authenticatedRpc; this.session = session; this.writer = writer; this.rules = rules;
            rpc.Register<ZPackage>(Request, Receive);
        }

        internal static ZPackage EncodeRequest(string nonce, PlayerInventoryMove move)
        {
            if (!Guid.TryParseExact(nonce, "N", out _)) throw new ArgumentException("Invalid session nonce");
            var package = new ZPackage();
            package.Write(1); package.Write(nonce); package.Write(move.Operation); package.Write(move.Revision);
            package.Write(move.FromX); package.Write(move.FromY); package.Write(move.ToX); package.Write(move.ToY); package.Write(move.Amount);
            return package;
        }

        private void Receive(ZRpc sender, ZPackage package)
        {
            if (closed || !ReferenceEquals(sender, rpc) || session.State != PlayerAdmission.Phase.Ready) return;
            try
            {
                if (package.Size() > 256 || package.ReadInt() != 1) throw new InvalidDataException("Invalid inventory command envelope");
                string nonce = package.ReadString();
                if (nonce != session.Nonce) return; // Old connection/session packets are never actionable.
                var command = new PlayerInventoryMove(package.ReadString(), package.ReadLong(),
                    package.ReadInt(), package.ReadInt(), package.ReadInt(), package.ReadInt(), package.ReadInt());
                if (package.GetPos() != package.Size()) throw new InvalidDataException("Trailing inventory command data");
                // One action in flight per character. Do not enqueue retransmissions or allow unbounded queues.
                if (pending != null || resync != null) return;
                request = command;
                pending = writer.Move(session.Identity, command, rules);
                deadline = DateTime.UtcNow.AddSeconds(30);
            }
            catch (Exception error) { Fail(error); }
        }

        // Main-thread pump. No waiting for SQLite and no Unity/network calls on the writer thread.
        internal void Tick()
        {
            if (closed) return;
            if (!rpc.IsConnected() || session.State != PlayerAdmission.Phase.Ready) { Dispose(); return; }
            if ((pending != null || resync != null) && DateTime.UtcNow >= deadline)
            { Fail(new TimeoutException("Inventory commit timed out; reconnect to reload authoritative state")); return; }
            try
            {
                if (pending != null && pending.IsCompleted)
                {
                    var action = pending; pending = null;
                    if (action.IsCanceled) throw new IOException("Inventory action canceled");
                    if (action.IsFaulted)
                    {
                        var error = action.Exception.GetBaseException();
                        if (!(error is InvalidOperationException)) throw error;
                        // Ordinary rejection: return current inventory once, never rebase and retry the action.
                        resync = writer.InventoryState(session.Identity);
                    }
                    else Send(true, action.Result);
                }
                if (resync != null && resync.IsCompleted)
                {
                    var state = resync; resync = null;
                    if (state.IsCanceled) throw new IOException("Inventory synchronization canceled");
                    if (state.IsFaulted) throw state.Exception.GetBaseException();
                    Send(false, new PlayerBatch(request.Operation, state.Result.Revision, state.Result.Rows));
                }
            }
            catch (Exception error) { Fail(error); }
        }

        private void Send(bool accepted, PlayerBatch data)
        {
            var reply = new ZPackage(); reply.Write(1); reply.Write(session.Nonce); reply.Write(accepted);
            reply.Write(PlayerBatchFormat.Encode(data));
            rpc.Invoke(Response, reply);
            request = null;
        }

        private void Fail(Exception error)
        {
            session.Error = error; session.State = PlayerAdmission.Phase.Failed;
            Dispose(); rpc.GetSocket().Close();
        }
        private static void Ignore(ZRpc sender, ZPackage package) { }
        private static void Observe(Task task)
        {
            if (task != null) task.ContinueWith(t => { var ignored = t.Exception; },
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
        public void Dispose()
        {
            if (closed) return;
            closed = true; Observe(pending); Observe(resync); pending = null; resync = null; request = null;
            rpc.Register<ZPackage>(Request, Ignore);
        }
    }
}
