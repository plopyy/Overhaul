using System;
using System.IO;

namespace Overhaul.Persistence
{
    // The admission lifecycle supplies the actual server connection and its current session nonce.
    // All methods run on the game thread; requests never change the displayed inventory optimistically.
    internal sealed class PlayerInventoryClient : IDisposable
    {
        private readonly ZRpc server;
        private readonly string nonce;
        private readonly PlayerInventoryView view;
        private readonly Action<bool> completed;
        private readonly Action<Exception> failed;
        private DateTime deadline;
        internal bool Closed { get; private set; }
        internal bool Busy => view.Busy;

        internal PlayerInventoryClient(ZRpc admittedServer, string nonce, PlayerInventoryView view,
            Action<bool> completed = null, Action<Exception> failed = null)
        {
            if (admittedServer == null || view == null || !Guid.TryParseExact(nonce, "N", out _)) throw new ArgumentException("Invalid inventory session");
            server = admittedServer; this.nonce = nonce; this.view = view; this.completed = completed; this.failed = failed;
            server.Register<ZPackage>(PlayerInventoryRpc.Response, Receive);
        }

        internal bool Move(int fromX, int fromY, int toX, int toY, int amount)
        {
            if (Closed || Busy) return false;
            if (!server.IsConnected()) { Fail(new IOException("Server disconnected")); return false; }
            var request = view.Begin(fromX, fromY, toX, toY, amount);
            deadline = DateTime.UtcNow.AddSeconds(30);
            try { server.Invoke(PlayerInventoryRpc.Request, PlayerInventoryRpc.EncodeRequest(nonce, request)); return true; }
            catch (Exception error) { Fail(error); return false; }
        }

        private void Receive(ZRpc sender, ZPackage package)
        {
            if (Closed || !ReferenceEquals(sender, server)) return;
            try
            {
                if (package.Size() > 16 * 1024 * 1024 + 128 || package.ReadInt() != 1)
                    throw new InvalidDataException("Invalid inventory reply envelope");
                if (package.ReadString() != nonce) return;
                bool accepted = package.ReadBool(); int length = package.ReadInt();
                if (length < 0 || length > 16 * 1024 * 1024 || length != package.Size() - package.GetPos())
                    throw new InvalidDataException("Invalid inventory reply length");
                var effect = PlayerBatchFormat.Decode(package.ReadByteArray(length));
                if (effect.Operation != view.PendingOperation) return;
                bool applied = accepted ? view.Confirm(effect) : view.Resynchronize(effect);
                if (!applied) throw new InvalidDataException("Inventory reply revision mismatch");
                // Rejections synchronize the current state; never resubmit the rejected intent automatically.
                completed?.Invoke(accepted);
            }
            catch (Exception error) { Fail(error); }
        }

        internal void Tick()
        {
            if (Closed) return;
            if (!server.IsConnected()) { Fail(new IOException("Server disconnected")); return; }
            if (Busy && DateTime.UtcNow >= deadline) Fail(new TimeoutException("Inventory response timed out; reconnect to reload server state"));
        }
        private void Fail(Exception error)
        {
            Dispose(); server.GetSocket().Close(); failed?.Invoke(error);
        }
        private static void Ignore(ZRpc sender, ZPackage package) { }
        public void Dispose()
        {
            if (Closed) return;
            Closed = true; server.Register<ZPackage>(PlayerInventoryRpc.Response, Ignore);
        }
    }
}
