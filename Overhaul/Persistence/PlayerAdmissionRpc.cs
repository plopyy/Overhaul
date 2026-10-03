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

        internal PlayerAdmissionRpc(ZRpc authenticatedRpc, PlayerAdmission admission, PlayerAdmission.Session session,
            Func<byte[], IEnumerable<PlayerChange>> decodeImport, Action<Exception> failed)
        {
            if (authenticatedRpc == null || session == null || !ReferenceEquals(authenticatedRpc, session.Connection))
                throw new ArgumentException("Admission requires the authenticated session connection");
            rpc = authenticatedRpc; this.failed = failed ?? throw new ArgumentNullException(nameof(failed));
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
                if (server != null) server.Receive(bytes); else client.Receive(bytes);
            }
            catch (Exception error) { Fail(error); }
        }
        internal void Tick()
        {
            if (closed) return;
            if (!rpc.IsConnected()) { Fail(new IOException("Character connection closed")); return; }
            if (server != null) server.Tick(); else client.Tick();
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
            closed = true; server?.Dispose(); client?.Dispose();
            rpc.Register<ZPackage>(receiveName, Ignore);
        }
    }
}
