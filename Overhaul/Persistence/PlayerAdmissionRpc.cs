using System;
using System.Collections.Generic;
using System.IO;

namespace Overhaul.Persistence
{
    // One adapter per transport connection. Authentication and spawn gating belong to the
    // session lifecycle; no claimed account ID is present in this protocol.
    internal sealed class PlayerAdmissionRpc : IDisposable
    {
        private const string Request = "Overhaul_CharacterAdmission", Response = "Overhaul_CharacterState";
        private readonly ZRpc rpc;
        private readonly PlayerAdmissionChannel server;
        private readonly PlayerAdmissionClient client;
        private readonly string receiveName;
        private readonly Action<Exception> failed;
        private bool closed;
        private readonly PlayerAdmission.Session session;
        private InventoryMoveRpc inventory;
        internal Func<bool> CanReceive = () => true;
        private readonly Queue<byte[]> pending = new Queue<byte[]>();
        private long pendingBytes;

        internal PlayerAdmissionRpc(ZRpc authenticatedRpc, PlayerAdmission admission, PlayerAdmission.Session session,
            Func<byte[], IEnumerable<PlayerChange>> decodeImport, Action<Exception> failed)
        {
            if (authenticatedRpc == null || session == null || !ReferenceEquals(authenticatedRpc, session.Connection))
                throw new ArgumentException("Admission requires the authenticated session connection");
            rpc = authenticatedRpc; this.failed = failed ?? throw new ArgumentNullException(nameof(failed));
            this.session = session;
            receiveName = Request;
            server = new PlayerAdmissionChannel(admission, session, bytes => Send(Response, bytes), decodeImport, Fail);
            rpc.Register<ZPackage>(Request, Receive);
        }

        internal PlayerAdmissionRpc(ZRpc serverRpc, Func<byte[]> captureImport, Action<PlayerSnapshot> load, Action<Exception> failed)
        {
            rpc = serverRpc ?? throw new ArgumentNullException(nameof(serverRpc));
            this.failed = failed ?? throw new ArgumentNullException(nameof(failed));
            receiveName = Response;
            client = new PlayerAdmissionClient(bytes => Send(Request, bytes), captureImport, load, Fail);
            rpc.Register<ZPackage>(Response, Receive);
        }

        internal PlayerAdmissionClient Client => client;
        internal bool Closed => closed && (inventory == null || inventory.Finished) && inventory?.StorageFailed != true;
        private void Send(string name, byte[] bytes)
        {
            var package = new ZPackage(); package.Write(bytes); rpc.Invoke(name, package);
        }
        private void Receive(ZRpc sender, ZPackage package)
        {
            if (closed || !ReferenceEquals(sender, rpc)) return;
            try
            {
                if (package.Size() > PlayerAdmissionChannel.Limit + 68) throw new InvalidDataException("Character RPC exceeds limit");
                int size = package.ReadInt();
                if (size < 0 || size != package.Size() - package.GetPos()) throw new InvalidDataException("Invalid character RPC size");
                var bytes = package.ReadByteArray(size);
                if (server != null) server.Receive(bytes);
                else if (!CanReceive() || pending.Count != 0)
                {
                    if (pendingBytes + bytes.Length > PlayerAdmissionChannel.Limit + 68 || pending.Count >= 8)
                        throw new InvalidDataException("Pending character admission exceeds limit");
                    pending.Enqueue(bytes); pendingBytes += bytes.Length;
                }
                else client.Receive(bytes);
            }
            catch (Exception error) { Fail(error); }
        }
        internal void Tick()
        {
            if (closed) { if(Closed)server?.Dispose(); return; }
            if (!rpc.IsConnected()) { Fail(new IOException("Character connection closed")); return; }
            if (client != null && CanReceive())
                while (!closed && pending.Count != 0)
                { var bytes = pending.Dequeue(); pendingBytes -= bytes.Length; client.Receive(bytes); }
            if (server != null) server.Tick(); else client.Tick();
            if (inventory == null && !closed)
            {
                if (session != null && session.State == PlayerAdmission.Phase.Ready && GamePersistence.Active)
                {
                    inventory = InventoryMoveGame.BindServer(rpc, session);
                }
                // Spawn requests use this channel before the local avatar exists.
                else if (client != null && client.Ready)
                    inventory = InventoryMoveGame.BindClient(rpc, client.Nonce, client.Revision);
            }
        }
        private void Fail(Exception error)
        {
            if (closed) return;
            Dispose(); rpc.GetSocket().Close(); failed(error);
        }
        private static void Ignore(ZRpc sender, ZPackage package) { }
        public void Dispose()
        {
            if (closed) return;
            closed = true; inventory?.Dispose();
            pending.Clear(); pendingBytes = 0;
            // Keep the account admitted until the previous session's writes drain;
            // a rapid reconnect must not load an earlier revision from SQLite.
            if(Closed)server?.Dispose();
            client?.Dispose();
            rpc.Register<ZPackage>(receiveName, Ignore);
        }
    }
}
