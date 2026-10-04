using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Overhaul.Persistence
{
    // One worker for all players. Commands contain managed values, never Player/Inventory/Unity objects.
    // Live gameplay methods complete on acceptance in memory. Admission, migration
    // and explicit Flush still use the worker's completion as their disk barrier.
    internal sealed partial class PlayerDatabaseWriter : IDisposable
    {
        private sealed class Entry
        {
            internal PlayerIdentity Identity;
            internal PlayerDatabase Database;
            internal DateTime LastUse;
        }
        private readonly Dictionary<string, Entry> open = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<Action> jobs = new Queue<Action>();
        private readonly object gate = new object();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Thread worker;
        private readonly string directory;
        private readonly ProgressiveWriter shared;
        private bool stopping;
        internal PlayerDatabaseWriter(string worldDirectory) : this(worldDirectory, null) { }
        internal PlayerDatabaseWriter(string worldDirectory, ProgressiveWriter shared)
        {
            directory = Path.GetFullPath(worldDirectory);
            this.shared = shared;
            if (shared != null) return;
            worker = new Thread(Run) { Name = "Overhaul player SQLite", IsBackground = true };
            worker.Start();
        }
        private PlayerDatabase Get(PlayerIdentity identity)
        {
            string path = identity.PathIn(directory);
            if (!open.TryGetValue(path, out var entry))
            {
                entry = new Entry { Identity = identity, Database = new PlayerDatabase(path, identity) };
                open.Add(path, entry);
            }
            if (entry.Identity.World != identity.World || entry.Identity.Provider != identity.Provider || entry.Identity.Account != identity.Account)
                throw new InvalidDataException("Player database session identity mismatch");
            entry.LastUse = DateTime.UtcNow;
            return entry.Database;
        }
        private Task<T> Submit<T>(Func<T> action, bool immediate)
        {
            if (shared != null)
            {
                lock (gate)
                {
                    if (stopping) throw new ObjectDisposedException(nameof(PlayerDatabaseWriter));
                    return shared.Submit(_ => action());
                }
            }
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                if (stopping) throw new ObjectDisposedException(nameof(PlayerDatabaseWriter));
                if (jobs.Count >= 1024) throw new InvalidOperationException("Player persistence queue is full; stop accepting gameplay changes");
                jobs.Enqueue(() =>
                {
                    try { completion.SetResult(action()); }
                    catch (Exception error) { completion.SetException(error); }
                });
            }
            if (immediate) wake.Set();
            return completion.Task;
        }
        private Task<T> SubmitWorld<T>(Func<SqliteDatabase, T> action)
        {
            lock (gate)
            {
                if (stopping) throw new ObjectDisposedException(nameof(PlayerDatabaseWriter));
                if (shared == null) throw new InvalidOperationException("World executor is unavailable");
                return shared.Submit(action);
            }
        }
        internal Task<PlayerChange[]> Open(PlayerIdentity identity, bool allowClientMigration,
            IEnumerable<PlayerChange> fresh, IEnumerable<PlayerChange> imported = null)
        {
            var initial = fresh.ToArray(); var migration = imported?.ToArray();
            return Submit(() =>
            {
                var db = Get(identity);
                db.Initialize(allowClientMigration, initial, migration);
                return db.Read();
            }, true);
        }
        // Wake immediately for every validated action. Completion follows the durable commit.
        internal Task<long> Write(PlayerIdentity identity, PlayerBatch batch) => Submit(() => Get(identity).Apply(batch), true);
        internal Task<PlayerBatch> Move(PlayerIdentity identity, PlayerInventoryMove request, PlayerInventoryRules rules)
            => Submit(() => Get(identity).Move(request, rules), true);
        internal Task<PlayerSnapshot> InventoryState(PlayerIdentity identity) => Live?.Find(identity)!=null ? Task.FromResult(new PlayerSnapshot(Live.Find(identity).Revision,Live.Find(identity).Rows.Where(r=>r.Table=="inventory"||r.Table=="item_data"))) : Submit(() => Get(identity).InventoryState(), true);
        internal Task<PlayerSnapshot> Lookup(PlayerIdentity identity) => Live?.Find(identity)!=null ? Task.FromResult(Live.Find(identity)) : Submit(() =>
        {
            var db = Get(identity);
            return db.Complete ? new PlayerSnapshot(db.Revision, db.Read()) : null;
        }, true);
        internal Task<PlayerSnapshot> Initialize(PlayerIdentity identity, Func<IEnumerable<PlayerChange>> initial, bool imported) => Submit(() =>
        {
            var db = Get(identity);
            // Recheck on the writer thread: a delayed import must never replace a completed character.
            if (!db.Complete)
            {
                var rows = initial().ToArray();
                db.Initialize(imported, rows, imported ? rows : null);
            }
            return new PlayerSnapshot(db.Revision, db.Read());
        }, true);
        internal Task<long> Revision(PlayerIdentity identity) => Submit(() => Get(identity).Revision, true);
        // FIFO barrier. Earlier failed writes are surfaced by their own tasks and must not be ignored.
        internal Task<bool> Flush() => Submit(() => true, true);
        private void Run()
        {
            try
            {
                while (true)
                {
                    wake.WaitOne(5000);
                    Action[] batch; bool stop;
                    lock (gate) { batch = jobs.ToArray(); jobs.Clear(); stop = stopping; }
                    foreach (var job in batch) job();
                    foreach (var path in open.Where(p => (DateTime.UtcNow - p.Value.LastUse).TotalMinutes > 5).Select(p => p.Key).ToArray())
                    { open[path].Database.Dispose(); open.Remove(path); }
                    if (stop) return;
                }
            }
            finally { foreach (var entry in open.Values) entry.Database.Dispose(); open.Clear(); }
        }
        public void Dispose()
        {
            if(!stopping)FlushLiveProgress(true);
            lock (gate) { if (stopping) return; stopping = true; }
            if (shared != null)
            {
                try { shared.Cleanup(() => { foreach (var entry in open.Values) entry.Database.Dispose(); open.Clear(); }).GetAwaiter().GetResult(); }
                finally { wake.Dispose(); }
                return;
            }
            wake.Set(); worker.Join(); wake.Dispose();
        }
    }
}
