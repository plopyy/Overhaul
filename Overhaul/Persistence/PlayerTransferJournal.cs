using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Overhaul.Persistence
{
    // Recovery coordinator for world <-> player writes. Must run on the WORLD writer thread,
    // with gameplay/ordinary snapshots of the participants locked until Finish/Recover succeeds.
    // This does not make two WAL databases one physical transaction: the durable world intent
    // is the commit decision. Login/world publication must wait for replay of every pending intent.
    internal static class PlayerTransferJournal
    {
        internal static void PrepareSchema(SqliteDatabase world)
        {
            if (!WorldSchema.Owned(world) || !MigrationState.Complete(world)) throw new InvalidDataException("Transfer journal requires a complete Overhaul world");
            world.Transaction(() =>
            {
                world.Execute("CREATE TABLE IF NOT EXISTS player_transfers(id TEXT PRIMARY KEY,world INTEGER NOT NULL,provider TEXT NOT NULL,account TEXT NOT NULL,revision INTEGER NOT NULL,digest TEXT NOT NULL,world_digest TEXT NOT NULL,payload BLOB NOT NULL,complete INTEGER NOT NULL DEFAULT 0 CHECK(complete IN (0,1)))");
                world.Execute("CREATE TABLE IF NOT EXISTS player_transfer_locks(object_id INTEGER PRIMARY KEY,operation TEXT NOT NULL REFERENCES player_transfers(id) ON DELETE CASCADE)");
                world.Execute("CREATE TABLE IF NOT EXISTS player_transfer_heads(provider TEXT NOT NULL,account TEXT NOT NULL,revision INTEGER NOT NULL,PRIMARY KEY(provider,account))");
                world.Execute("CREATE INDEX IF NOT EXISTS player_transfers_pending ON player_transfers(complete,provider,account)");
            });
        }
        internal static bool IsLocked(SqliteDatabase world, long objectId)
        {
            using (var row = world.Query("SELECT 1 FROM player_transfer_locks WHERE object_id=?", objectId)) return row.Read();
        }
        internal static bool HasPending(SqliteDatabase world)
        { using (var row = world.Query("SELECT 1 FROM player_transfers WHERE complete=0 LIMIT 1")) return row.Read(); }

        // worldDigest identifies the immutable, validated world mutation (including before/after values).
        // updateWorld must check those preconditions and update native/normalized world rows together.
        internal static void Stage(SqliteDatabase world, PlayerDatabase player, PlayerIdentity identity, PlayerBatch batch,
            string worldDigest, IEnumerable<long> objectIds, Action<SqliteDatabase> updateWorld)
        {
            if (!player.BelongsTo(identity) || !player.Complete || !MigrationState.Complete(world, identity.World))
                throw new InvalidDataException("Transfer participants do not belong to this world/account");
            if (worldDigest == null || worldDigest.Length != 64 || worldDigest.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Expected SHA256 world mutation digest");
            var objects = objectIds.Distinct().OrderBy(id => id).ToArray();
            if (objects.Length == 0 || objects.Length > 128 || objects.Any(id => id <= 0) || updateWorld == null) throw new ArgumentException("Invalid transfer targets");
            byte[] payload = PlayerBatchFormat.Encode(batch); string digest = batch.Digest();
            world.Transaction(() =>
            {
                using (var receipt = world.Query("SELECT world,provider,account,digest,world_digest FROM player_transfers WHERE id=?", batch.Operation))
                    if (receipt.Read())
                    {
                        if (receipt.Long(0) != identity.World || receipt.Text(1) != identity.Provider || receipt.Text(2) != identity.Account ||
                            receipt.Text(3) != digest || receipt.Text(4) != worldDigest) throw new InvalidDataException("Transfer replay changed its identity or contents");
                        return;
                    }
                using (var pending = world.Query("SELECT 1 FROM player_transfers WHERE provider=? AND account=? AND complete=0", identity.Provider, identity.Account))
                    if (pending.Read()) throw new InvalidOperationException("Previous player transfer requires recovery");
                if (player.Revision != batch.ExpectedRevision) throw new InvalidOperationException("Stale player transfer revision");
                using (var head = world.Query("SELECT revision FROM player_transfer_heads WHERE provider=? AND account=?", identity.Provider, identity.Account))
                    if (head.Read() && batch.ExpectedRevision < head.Long(0)) throw new InvalidDataException("Player save is older than the world's transfer history");
                foreach (long id in objects) if (IsLocked(world, id)) throw new InvalidOperationException("World inventory is locked by another transfer");
                world.Write("INSERT INTO player_transfers(id,world,provider,account,revision,digest,world_digest,payload) VALUES(?,?,?,?,?,?,?,?)",
                    batch.Operation, identity.World, identity.Provider, identity.Account, batch.ExpectedRevision, digest, worldDigest, payload);
                foreach (long id in objects) world.Write("INSERT INTO player_transfer_locks VALUES(?,?)", id, batch.Operation);
                updateWorld(world);
            });
        }
        internal static void Finish(SqliteDatabase world, PlayerDatabase player, PlayerIdentity identity, string operation)
        {
            if (!player.BelongsTo(identity) || !MigrationState.Complete(world, identity.World))
                throw new InvalidDataException("Wrong world/player for transfer completion");
            byte[] payload; string digest;
            using (var row = world.Query("SELECT world,provider,account,payload,digest,complete FROM player_transfers WHERE id=?", operation))
            {
                if (!row.Read()) throw new InvalidDataException("Missing world transfer intent");
                if (row.Long(0) != identity.World || row.Text(1) != identity.Provider || row.Text(2) != identity.Account)
                    throw new InvalidDataException("Wrong transfer identity");
                if (row.Long(5) != 0) return;
                payload = row.Blob(3); digest = row.Text(4);
            }
            var batch = PlayerBatchFormat.Decode(payload);
            if (batch.Operation != operation || batch.Digest() != digest) throw new InvalidDataException("Corrupt player transfer payload");
            // Idempotent: after a crash here, player.Apply returns its durable receipt on replay.
            long revision = player.Apply(batch);
            world.Transaction(() =>
            {
                world.Write("UPDATE player_transfers SET complete=1 WHERE id=?", operation);
                world.Write("DELETE FROM player_transfer_locks WHERE operation=?", operation);
                new DatabaseRow("player_transfer_heads", "provider,account,revision", 2, identity.Provider, identity.Account, revision).Write(world);
                world.Write("DELETE FROM player_transfers WHERE provider=? AND account=? AND complete=1 AND revision<?", identity.Provider, identity.Account, revision - 1024);
            });
        }
        internal static void Recover(SqliteDatabase world, Func<PlayerIdentity, PlayerDatabase> openPlayer)
        {
            var pending = new List<Tuple<PlayerIdentity, string>>();
            using (var rows = world.Query("SELECT world,provider,account,id FROM player_transfers WHERE complete=0 ORDER BY revision,id"))
                while (rows.Read()) pending.Add(Tuple.Create(new PlayerIdentity(rows.Long(0), rows.Text(1), rows.Text(2)), rows.Text(3)));
            foreach (var entry in pending) Finish(world, openPlayer(entry.Item1), entry.Item1, entry.Item2);
        }
    }
}
