using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Overhaul.Persistence
{
    // Transport-independent login channel. The caller must bind it to an authenticated
    // connection and block spawning until Ready. Import rows are accepted only at first login.
    internal sealed class PlayerAdmissionChannel : IDisposable
    {
        internal enum Message : byte { ImportRequest = 1, Import = 2, Fresh = 3, Load = 4, Loaded = 5, Ready = 6 }
        internal const int Limit = 16 * 1024 * 1024;
        private readonly PlayerAdmission admission;
        private readonly PlayerAdmission.Session session;
        private readonly Action<byte[]> send;
        private readonly Action<Exception> failed;
        private readonly Func<byte[], IEnumerable<PlayerChange>> decodeImport;
        private PlayerAdmission.Phase? announced;
        internal bool Closed { get; private set; }

        internal PlayerAdmissionChannel(PlayerAdmission admission, PlayerAdmission.Session session,
            Action<byte[]> send, Func<byte[], IEnumerable<PlayerChange>> decodeImport, Action<Exception> failed)
        {
            this.admission = admission ?? throw new ArgumentNullException(nameof(admission));
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.send = send ?? throw new ArgumentNullException(nameof(send));
            this.decodeImport = decodeImport ?? throw new ArgumentNullException(nameof(decodeImport));
            this.failed = failed ?? throw new ArgumentNullException(nameof(failed));
        }

        internal static byte[] Encode(Message kind, string nonce, long revision = 0, byte[] payload = null)
        {
            if (!Guid.TryParseExact(nonce, "N", out _) || revision < 0 || (payload?.Length ?? 0) > Limit)
                throw new InvalidDataException("Invalid character admission message");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(1); writer.Write((byte)kind); writer.Write(nonce); writer.Write(revision);
                writer.Write(payload?.Length ?? 0); if (payload != null) writer.Write(payload);
                return stream.ToArray();
            }
        }

        internal static void Decode(byte[] bytes, out Message kind, out string nonce, out long revision, out byte[] payload)
        {
            if (bytes == null || bytes.Length > Limit + 64) throw new InvalidDataException("Invalid character admission size");
            using (var reader = NativeFormat.Reader(bytes))
            {
                if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported character admission protocol");
                kind = (Message)reader.ReadByte(); nonce = reader.ReadString(); revision = reader.ReadInt64();
                int length = reader.ReadInt32();
                if (!Enum.IsDefined(typeof(Message), kind) || !Guid.TryParseExact(nonce, "N", out _) || revision < 0 ||
                    length < 0 || length > Limit || length != reader.BaseStream.Length - reader.BaseStream.Position)
                    throw new InvalidDataException("Invalid character admission envelope");
                payload = reader.ReadBytes(length);
            }
        }

        internal void Receive(byte[] bytes)
        {
            if (Closed) return;
            try
            {
                Decode(bytes, out var kind, out var nonce, out var revision, out var payload);
                if (nonce != session.Nonce) return;
                switch (kind)
                {
                    case Message.Import:
                        if (revision != 0 || payload.Length == 0) throw new InvalidDataException("Invalid character import");
                        // Decode on the DB worker only after the admission state has authorized this import.
                        admission.Import(session.Connection, nonce, () => decodeImport(payload));
                        break;
                    case Message.Fresh:
                        if (revision != 0 || payload.Length != 0) throw new InvalidDataException("Invalid fresh character request");
                        admission.StartFresh(session.Connection, nonce);
                        break;
                    case Message.Loaded:
                        if (payload.Length != 0) throw new InvalidDataException("Invalid character load acknowledgement");
                        admission.Loaded(session.Connection, nonce, revision);
                        break;
                    default: throw new InvalidDataException("Unexpected client admission message");
                }
            }
            catch (Exception error) { Fail(error); }
        }

        internal void Tick()
        {
            if (Closed) return;
            if (session.State == PlayerAdmission.Phase.Closed) { Dispose(); return; }
            if (session.State == PlayerAdmission.Phase.Failed) { Fail(session.Error); return; }
            if (announced == session.State) return;
            try
            {
                switch (session.State)
                {
                    case PlayerAdmission.Phase.AwaitingImport:
                        send(Encode(Message.ImportRequest, session.Nonce)); break;
                    case PlayerAdmission.Phase.AwaitingLoad:
                        send(Encode(Message.Load, session.Nonce, session.Snapshot.Revision,
                            PlayerBatchFormat.Encode(new PlayerBatch(session.Nonce, session.Snapshot.Revision, session.Snapshot.Rows))));
                        break;
                    case PlayerAdmission.Phase.Ready:
                        send(Encode(Message.Ready, session.Nonce, session.Snapshot.Revision)); break;
                }
                announced = session.State;
            }
            catch (Exception error) { Fail(error); }
        }
        private void Fail(Exception error)
        {
            session.Error = error; Dispose(); failed(error);
        }
        public void Dispose()
        {
            if (Closed) return;
            Closed = true; admission.Close(session.Connection);
        }
    }

    internal sealed class PlayerAdmissionClient : IDisposable
    {
        private readonly Action<byte[]> send;
        private readonly Func<byte[]> captureImport;
        private readonly Action<PlayerSnapshot> load;
        private readonly Action<Exception> failed;
        private string nonce;
        private long revision;
        private bool imported, loaded;
        private readonly Func<DateTime> clock;
        private DateTime deadline;
        internal bool Ready { get; private set; }
        internal bool Closed { get; private set; }
        internal string Nonce => nonce;
        internal long Revision => revision;

        internal PlayerAdmissionClient(Action<byte[]> send, Func<byte[]> captureImport,
            Action<PlayerSnapshot> load, Action<Exception> failed, Func<DateTime> clock = null)
        {
            this.send = send ?? throw new ArgumentNullException(nameof(send));
            this.captureImport = captureImport ?? throw new ArgumentNullException(nameof(captureImport));
            this.load = load ?? throw new ArgumentNullException(nameof(load));
            this.failed = failed ?? throw new ArgumentNullException(nameof(failed));
            this.clock = clock ?? (() => DateTime.UtcNow); deadline = this.clock().AddSeconds(120);
        }
        // Only the current server transport may invoke this method. Loading must finish before ack.
        internal void Receive(byte[] bytes)
        {
            if (Closed) return;
            try
            {
                PlayerAdmissionChannel.Decode(bytes, out var kind, out var token, out var version, out var payload);
                if (nonce != null && nonce != token) return;
                if (Ready) return;
                switch (kind)
                {
                    case PlayerAdmissionChannel.Message.ImportRequest:
                        if (version != 0 || payload.Length != 0 || loaded) throw new InvalidDataException("Unexpected import request");
                        nonce = token;
                        if (imported) return;
                        imported = true;
                        deadline = clock().AddSeconds(120);
                        var data = captureImport();
                        send(PlayerAdmissionChannel.Encode(data == null ? PlayerAdmissionChannel.Message.Fresh : PlayerAdmissionChannel.Message.Import,
                            nonce, payload: data));
                        break;
                    case PlayerAdmissionChannel.Message.Load:
                        if (loaded) return;
                        var batch = PlayerBatchFormat.Decode(payload);
                        if (batch.Operation != token || batch.ExpectedRevision != version || batch.Changes.Any(r => r.Delete))
                            throw new InvalidDataException("Character load envelope mismatch");
                        nonce = token;
                        load(new PlayerSnapshot(version, batch.Changes));
                        revision = version; loaded = true;
                        deadline = clock().AddSeconds(120);
                        send(PlayerAdmissionChannel.Encode(PlayerAdmissionChannel.Message.Loaded, nonce, revision));
                        break;
                    case PlayerAdmissionChannel.Message.Ready:
                        if (!loaded || version != revision || payload.Length != 0) throw new InvalidDataException("Premature character activation");
                        Ready = true; break;
                    default: throw new InvalidDataException("Unexpected server admission message");
                }
            }
            catch (Exception error) { Dispose(); failed(error); }
        }
        internal void Tick()
        {
            if (!Closed && !Ready && clock() >= deadline)
            { Dispose(); failed(new TimeoutException("Server character loading timed out")); }
        }
        public void Dispose() { Closed = true; Ready = false; }
    }
}
