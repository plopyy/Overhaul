using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Overhaul.Persistence
{
    internal sealed class PlayerSnapshot
    {
        internal readonly long Revision;
        private readonly PlayerChange[] rows;
        internal IEnumerable<PlayerChange> Rows => rows.Select(r => r);
        internal PlayerSnapshot(long revision, IEnumerable<PlayerChange> rows)
        { Revision = revision; this.rows = rows.ToArray(); }
    }

    // Main-thread state machine. The caller owns the authenticated connection object, not a client-supplied ID.
    // Ready is reached only after the DB commit and the client's acknowledgement of that exact revision.
    internal sealed class PlayerAdmission
    {
        internal enum Phase { Lookup, AwaitingImport, Initializing, AwaitingLoad, Ready, Failed, Closed }
        internal sealed class Session
        {
            internal readonly object Connection;
            internal readonly PlayerIdentity Identity;
            internal readonly string Nonce = Guid.NewGuid().ToString("N");
            internal readonly bool ImportAllowed;
            internal Phase State;
            internal PlayerSnapshot Snapshot;
            internal Exception Error;
            internal DateTime Deadline;
            internal Task<PlayerSnapshot> Pending;
            internal readonly Func<IEnumerable<PlayerChange>> Fresh;
            internal Session(object connection, PlayerIdentity identity, bool allowImport, Func<IEnumerable<PlayerChange>> fresh)
            { Connection = connection; Identity = identity; ImportAllowed = allowImport; Fresh = fresh; }
        }
        private readonly PlayerDatabaseWriter writer;
        private readonly Func<DateTime> clock;
        private readonly Dictionary<object, Session> sessions = new Dictionary<object, Session>();
        internal PlayerAdmission(PlayerDatabaseWriter writer, Func<DateTime> clock = null)
        { this.writer = writer; this.clock = clock ?? (() => DateTime.UtcNow); }
        internal Session Begin(object authenticatedConnection, PlayerIdentity identity, bool allowImport, Func<IEnumerable<PlayerChange>> fresh)
        {
            if (authenticatedConnection == null || identity == null || fresh == null) throw new ArgumentNullException();
            if (sessions.ContainsKey(authenticatedConnection) || sessions.Values.Any(s =>
                s.Identity.World == identity.World && s.Identity.Provider == identity.Provider && s.Identity.Account == identity.Account))
                throw new InvalidOperationException("Player already has a login or active session");
            var session = new Session(authenticatedConnection, identity, allowImport, fresh)
            { State = Phase.Lookup, Deadline = clock().AddSeconds(120) };
            session.Pending = writer.Lookup(identity);
            sessions.Add(authenticatedConnection, session);
            return session;
        }
        internal void Tick()
        {
            foreach (var session in sessions.Values)
            {
                if (session.State == Phase.Ready || session.State == Phase.Failed || session.State == Phase.Closed) continue;
                if (clock() >= session.Deadline) { Fail(session, new TimeoutException("Player loading timed out")); continue; }
                if (session.Pending == null || !session.Pending.IsCompleted) continue;
                var pending = session.Pending; session.Pending = null;
                if (pending.IsFaulted) { Fail(session, pending.Exception.GetBaseException()); continue; }
                if (pending.IsCanceled) { Fail(session, new IOException("Player loading was canceled")); continue; }
                var snapshot = pending.Result;
                if (snapshot != null)
                {
                    session.Snapshot = snapshot; session.State = Phase.AwaitingLoad;
                    session.Deadline = clock().AddSeconds(120); continue;
                }
                if (session.State != Phase.Lookup) { Fail(session, new IOException("Character initialization returned no state")); continue; }
                if (session.ImportAllowed) session.State = Phase.AwaitingImport;
                else Initialize(session, session.Fresh, false);
            }
        }
        // Called only after the import envelope has been bounded and decoded. Do not expose rows as a gameplay RPC.
        internal bool Import(object connection, string nonce, Func<IEnumerable<PlayerChange>> decodeImport)
        {
            if (decodeImport == null || !TrySession(connection, nonce, Phase.AwaitingImport, out var session) || !session.ImportAllowed) return false;
            Initialize(session, decodeImport, true); return true;
        }
        internal bool StartFresh(object connection, string nonce)
        {
            if (!TrySession(connection, nonce, Phase.AwaitingImport, out var session)) return false;
            Initialize(session, session.Fresh, false); return true;
        }
        private void Initialize(Session session, Func<IEnumerable<PlayerChange>> rows, bool imported)
        {
            try
            {
                session.State = Phase.Initializing;
                session.Pending = writer.Initialize(session.Identity, rows, imported);
                session.Deadline = clock().AddSeconds(120);
            }
            catch (Exception error) { Fail(session, error); }
        }
        internal bool Loaded(object connection, string nonce, long revision)
        {
            if (!TrySession(connection, nonce, Phase.AwaitingLoad, out var session) || session.Snapshot.Revision != revision) return false;
            session.State = Phase.Ready; return true;
        }
        internal bool CanPlay(object connection) => connection != null && sessions.TryGetValue(connection, out var s) && s.State == Phase.Ready;
        private bool TrySession(object connection, string nonce, Phase phase, out Session session)
        {
            session = null;
            return connection != null && sessions.TryGetValue(connection, out session) && session.Nonce == nonce && session.State == phase && clock() < session.Deadline;
        }
        private static void Fail(Session session, Exception error)
        { session.Error = error; session.State = Phase.Failed; Observe(session.Pending); session.Pending = null; }
        private static void Observe(Task task)
        {
            if (task != null) task.ContinueWith(t => { var ignored = t.Exception; },
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
        internal void Close(object connection)
        {
            if (connection == null || !sessions.TryGetValue(connection, out var session)) return;
            session.State = Phase.Closed; Observe(session.Pending); session.Pending = null;
            sessions.Remove(connection);
        }
    }
}
