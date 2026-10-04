using System;
using System.Collections.Generic;

namespace Overhaul.Persistence
{
    // The local host uses the same serialized admission/action protocol as remote players.
    internal sealed class PlayerLocalSocket : ISocket
    {
        private readonly Queue<ZPackage> incoming = new Queue<ZPackage>();
        private PlayerLocalSocket other;
        private bool connected = true;
        internal static void Pair(out ZRpc client, out ZRpc server)
        {
            var a = new PlayerLocalSocket(); var b = new PlayerLocalSocket(); a.other = b; b.other = a;
            client = new ZRpc(a); server = new ZRpc(b);
        }
        public bool IsConnected() => connected && other.connected;
        public void Send(ZPackage package)
        {
            if (!IsConnected()) return;
            if (other.incoming.Count >= 1024) throw new InvalidOperationException("Local character message queue exceeded");
            other.incoming.Enqueue(new ZPackage(package.GetArray()));
        }
        public ZPackage Recv() => incoming.Count == 0 ? null : incoming.Dequeue();
        public bool GotNewData() => incoming.Count != 0;
        public void Close() { connected = false; incoming.Clear(); }
        public void Dispose() => Close();
        public int GetSendQueueSize() => 0;
        public int GetCurrentSendRate() => 0;
        public bool IsHost() => false;
        public string GetEndPointString() => "local-character";
        public string GetHostName() => "local-character";
        public int GetHostPort() => 0;
        public ISocket Accept() => null;
        public bool Flush() => true;
        public void VersionMatch() { }
        public void GetAndResetStats(out int sent, out int received) { sent = received = 0; }
        public void GetConnectionQuality(out float local, out float remote, out int ping, out float outgoing, out float incoming)
        { local = remote = outgoing = incoming = 0; ping = 0; }
    }
}
