using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Overhaul.Persistence
{
    // Storage only. No Unity objects, file-system work or database connection crosses threads.
    // The gameplay/network authority layer must supply verified identities and mutations.
    internal sealed class PlayerIdentity
    {
        internal readonly long World;
        internal readonly string Provider, Account;
        internal PlayerIdentity(long world, string provider, string account)
        {
            if (world == 0 || string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(account) ||
                provider.Length > 32 || account.Length > 256 || provider.Any(c => !(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '_'))
                throw new ArgumentException("Invalid authenticated player identity");
            World = world; Provider = provider.ToLowerInvariant(); Account = account;
        }
        internal string FileName
        {
            get
            {
                // Steam IDs remain readable. Arbitrary identifiers are encoded, never treated as paths.
                if (Account.Length <= 32 && Account.All(c => c >= '0' && c <= '9')) return Provider + "_" + Account + ".db";
                using (var hash = SHA256.Create())
                    return Provider + "_hash_" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Account))).Replace("-", "").ToLowerInvariant() + ".db";
            }
        }
        internal string PathIn(string worldDirectory) => Path.Combine(Path.GetFullPath(worldDirectory), "players", FileName);
    }

    internal sealed class PlayerChange
    {
        internal readonly string Table;
        internal readonly bool Delete;
        private readonly object[] values;
        internal object[] Values => values.Select(Copy).ToArray();
        internal PlayerChange(string table, bool delete, params object[] values)
        {
            var definition = PlayerDatabase.Tables.SingleOrDefault(t => t.Name == table);
            if (definition == null || values == null || values.Length != (delete ? definition.Keys : definition.Columns.Length))
                throw new ArgumentException("Invalid character row shape: " + table);
            foreach (var value in values)
            {
                if (value != null && !(value is string) && !(value is byte[]) && !(value is int) && !(value is long) &&
                    !(value is bool) && !(value is double) && !(value is float)) throw new ArgumentException("Unsupported row value");
                if (value is double d && (double.IsNaN(d) || double.IsInfinity(d)) ||
                    value is float f && (float.IsNaN(f) || float.IsInfinity(f))) throw new ArgumentException("Non-finite character value");
                if (value is string text && Encoding.UTF8.GetByteCount(text) > 1024 * 1024 ||
                    value is byte[] bytes && bytes.Length > 4 * 1024 * 1024) throw new ArgumentException("Character value too large");
            }
            if (values.Take(definition.Keys).Any(v => v == null)) throw new ArgumentException("Missing character row key");
            Table = table; Delete = delete; this.values = values.Select(Copy).ToArray();
        }
        private static object Copy(object value) => value is byte[] bytes ? bytes.Clone() : value;
    }

    internal sealed class PlayerBatch
    {
        internal readonly string Operation;
        internal readonly long ExpectedRevision;
        private readonly PlayerChange[] changes;
        internal IEnumerable<PlayerChange> Changes => changes;
        internal PlayerBatch(string operation, long revision, IEnumerable<PlayerChange> changes)
        {
            if (!Guid.TryParseExact(operation, "N", out _) || revision < 0) throw new ArgumentException("Invalid character operation");
            Operation = operation; ExpectedRevision = revision;
            this.changes = changes.Take(65537).ToArray();
            if (this.changes.Length > 65536 || this.changes.Any(c => c == null)) throw new ArgumentException("Invalid character change batch");
        }
        internal string Digest()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            using (var hash = SHA256.Create())
            {
                writer.Write(ExpectedRevision);
                foreach (var change in changes)
                {
                    writer.Write(change.Table); writer.Write(change.Delete);
                    foreach (var v in change.Values)
                    {
                        if (v == null) writer.Write((byte)0);
                        else if (v is byte[] b) { writer.Write((byte)1); writer.Write(b.Length); writer.Write(b); }
                        else if (v is string s) { writer.Write((byte)2); writer.Write(s); }
                        else if (v is float || v is double) { writer.Write((byte)3); writer.Write(Convert.ToDouble(v)); }
                        else { writer.Write((byte)4); writer.Write(Convert.ToInt64(v)); }
                    }
                }
                writer.Flush();
                return BitConverter.ToString(hash.ComputeHash(stream.ToArray())).Replace("-", "");
            }
        }
    }

    internal sealed class PlayerDatabase : IDisposable
    {
        internal sealed class Table
        {
            internal readonly string Name, Ddl;
            internal readonly string[] Columns;
            internal readonly int Keys;
            internal Table(string name, string columns, int keys, string ddl)
            { Name = name; Columns = columns.Split(','); Keys = keys; Ddl = ddl; }
        }
        internal static readonly Table[] Tables = {
            new Table("state", "key,integer,real,text,blob", 1,
                "key TEXT PRIMARY KEY,integer INTEGER,real REAL,text TEXT,blob BLOB"),
            new Table("inventory", "bag,x,y,prefab,stack,quality,durability,equipped,variant,crafter_id,crafter_name,world_level,picked_up,cheated", 3,
                "bag TEXT NOT NULL,x INTEGER NOT NULL CHECK(x>=0 AND x<256),y INTEGER NOT NULL CHECK(y>=0 AND y<256),prefab INTEGER NOT NULL,stack INTEGER NOT NULL CHECK(stack>0),quality INTEGER NOT NULL CHECK(quality>0),durability REAL NOT NULL CHECK(durability>=0),equipped INTEGER NOT NULL CHECK(equipped IN (0,1)),variant INTEGER NOT NULL,crafter_id INTEGER NOT NULL,crafter_name TEXT NOT NULL,world_level INTEGER NOT NULL,picked_up INTEGER NOT NULL CHECK(picked_up IN (0,1)),cheated INTEGER NOT NULL CHECK(cheated IN (0,1)),PRIMARY KEY(bag,x,y)"),
            new Table("item_data", "bag,x,y,key,value", 4,
                "bag TEXT NOT NULL,x INTEGER NOT NULL,y INTEGER NOT NULL,key TEXT NOT NULL,value TEXT NOT NULL,PRIMARY KEY(bag,x,y,key),FOREIGN KEY(bag,x,y) REFERENCES inventory(bag,x,y) ON DELETE CASCADE"),
            new Table("skills", "id,level,accumulator", 1,
                "id INTEGER PRIMARY KEY,level REAL NOT NULL CHECK(level>=0),accumulator REAL NOT NULL CHECK(accumulator>=0)"),
            new Table("knowledge", "kind,key,value", 2,
                "kind TEXT NOT NULL,key TEXT NOT NULL,value TEXT NOT NULL,PRIMARY KEY(kind,key)"),
            new Table("food", "slot,prefab,remaining", 1,
                "slot INTEGER PRIMARY KEY CHECK(slot>=0),prefab TEXT NOT NULL,remaining REAL NOT NULL CHECK(remaining>=0)"),
            new Table("custom_data", "key,value", 1, "key TEXT PRIMARY KEY,value TEXT NOT NULL"),
            new Table("map", "layer,chunk,data", 2, "layer TEXT NOT NULL,chunk INTEGER NOT NULL,data BLOB NOT NULL,PRIMARY KEY(layer,chunk)"),
            new Table("pins", "id,type,label,x,y,z,checked,owner", 1,
                "id TEXT PRIMARY KEY,type INTEGER NOT NULL,label TEXT NOT NULL,x REAL NOT NULL,y REAL NOT NULL,z REAL NOT NULL,checked INTEGER NOT NULL,owner INTEGER NOT NULL"),
            new Table("spawn", "kind,x,y,z", 1, "kind TEXT PRIMARY KEY,x REAL NOT NULL,y REAL NOT NULL,z REAL NOT NULL")
        };
        private const int ApplicationId = 0x4F564850, Format = 1;
        private readonly SqliteDatabase db;
        private readonly FileStream ownership;
        private readonly PlayerIdentity identity;
        internal bool BelongsTo(PlayerIdentity player) => player != null && player.World == identity.World &&
            player.Provider == identity.Provider && player.Account == identity.Account;

        internal PlayerDatabase(string path, PlayerIdentity identity)
        {
            this.identity = identity;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            ownership = new FileStream(path + ".owner", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                db = new SqliteDatabase(path);
                using (var app = db.Query("PRAGMA application_id"))
                {
                    app.Read(); long id = app.Long(0);
                    if (id != ApplicationId && id != 0) throw new InvalidDataException("Not an Overhaul player database");
                    if (id == 0) using (var tables = db.Query("SELECT 1 FROM sqlite_master WHERE type='table' LIMIT 1"))
                        if (tables.Read()) throw new InvalidDataException("Refusing an unrelated SQLite database");
                }
                if (HasTable("identity")) VerifyIdentity();
            }
            catch { db?.Dispose(); ownership.Dispose(); throw; }
        }
        private bool HasTable(string name)
        { using (var row = db.Query("SELECT 1 FROM sqlite_master WHERE type='table' AND name=?", name)) return row.Read(); }
        private void VerifyIdentity()
        {
            using (var row = db.Query("SELECT world,provider,account,format FROM identity WHERE id=1"))
                if (!row.Read() || row.Long(0) != identity.World || row.Text(1) != identity.Provider ||
                    row.Text(2) != identity.Account || row.Long(3) != Format)
                    throw new InvalidDataException("Player database identity or format mismatch");
        }
        internal bool Complete
        {
            get
            {
                if (!HasTable("migration_state")) return false;
                VerifyIdentity();
                using (var row = db.Query("SELECT status FROM migration_state WHERE id=1")) return row.Read() && row.Text(0) == "complete";
            }
        }
        internal long Revision
        {
            get { using (var row = db.Query("SELECT revision FROM identity WHERE id=1")) { if (!row.Read()) throw new InvalidDataException("Missing player identity"); return row.Long(0); } }
        }
        // The caller must supply a fresh server character when import is disallowed.
        // Initial/imported rows are committed with the completion marker in ONE transaction.
        internal bool Initialize(bool allowClientMigration, IEnumerable<PlayerChange> fresh, IEnumerable<PlayerChange> imported = null)
        {
            if (Complete) return false; // Existing server state is never replaced, even when migration is enabled.
            var selected = (allowClientMigration && imported != null ? imported : fresh).ToArray();
            db.Transaction(() =>
            {
                db.Execute("PRAGMA application_id=" + ApplicationId);
                db.Execute("PRAGMA user_version=" + Format);
                // Only recognized incomplete schemas can be restarted; unrelated DBs were rejected at open.
                db.Execute("DROP TABLE IF EXISTS migration_state");
                db.Execute("DROP TABLE IF EXISTS operations");
                foreach (var table in Tables.Reverse()) db.Execute("DROP TABLE IF EXISTS \"" + table.Name + "\"");
                db.Execute("DROP TABLE IF EXISTS identity");
                db.Execute("CREATE TABLE identity(id INTEGER PRIMARY KEY CHECK(id=1),world INTEGER NOT NULL,provider TEXT NOT NULL,account TEXT NOT NULL,format INTEGER NOT NULL,revision INTEGER NOT NULL,origin TEXT NOT NULL)");
                db.Execute("INSERT INTO identity VALUES(1,?,?,?,?,0,?)", identity.World, identity.Provider, identity.Account, Format,
                    allowClientMigration && imported != null ? "client_import" : "fresh");
                foreach (var table in Tables) db.Execute("CREATE TABLE \"" + table.Name + "\"(" + table.Ddl + ")");
                db.Execute("CREATE TABLE operations(id TEXT PRIMARY KEY,revision INTEGER NOT NULL UNIQUE,digest TEXT NOT NULL)");
                ApplyRows(selected);
                using (var check = db.Query("PRAGMA foreign_key_check")) if (check.Read()) throw new InvalidDataException("Incomplete character references");
                db.Execute("CREATE TABLE migration_state(id INTEGER PRIMARY KEY CHECK(id=1),status TEXT NOT NULL CHECK(status='complete'),completed_at TEXT NOT NULL)");
                db.Execute("INSERT INTO migration_state VALUES(1,'complete',?)", DateTime.UtcNow.ToString("O"));
            });
            return true;
        }
        internal long Apply(PlayerBatch batch)
        {
            if (!Complete) throw new InvalidOperationException("Player has not completed initialization");
            long result = 0;
            db.Transaction(() => result = ApplyInsideTransaction(batch));
            return result;
        }
        private long ApplyInsideTransaction(PlayerBatch batch)
        {
            string digest = batch.Digest();
            using (var old = db.Query("SELECT revision,digest FROM operations WHERE id=?", batch.Operation))
                if (old.Read())
                {
                    if (old.Text(1) != digest) throw new InvalidDataException("Operation replay changed its contents");
                    return old.Long(0);
                }
            if (Revision != batch.ExpectedRevision) throw new InvalidOperationException("Stale player revision");
            ApplyRows(batch.Changes);
            long result = checked(batch.ExpectedRevision + 1);
            db.Write("UPDATE identity SET revision=? WHERE id=1", result);
            db.Write("INSERT INTO operations VALUES(?,?,?)", batch.Operation, result, digest);
            db.Write("DELETE FROM operations WHERE revision<=?", result - 1024);
            return result;
        }
        internal PlayerBatch Move(PlayerInventoryMove request, PlayerInventoryRules rules)
        {
            if (!Complete) throw new InvalidOperationException("Player has not completed initialization");
            db.Execute("CREATE TABLE IF NOT EXISTS inventory_actions(id TEXT PRIMARY KEY,request TEXT NOT NULL,revision INTEGER NOT NULL,payload BLOB NOT NULL)");
            PlayerBatch result = null;
            db.Transaction(() =>
            {
                using (var old = db.Query("SELECT request,payload FROM inventory_actions WHERE id=?", request.Operation))
                    if (old.Read())
                    {
                        if (old.Text(0) != request.Fingerprint) throw new InvalidDataException("Action replay changed its contents");
                        result = PlayerBatchFormat.Decode(old.Blob(1)); return;
                    }
                if (Revision != request.Revision) throw new InvalidOperationException("Stale inventory action revision");
                result = PlayerInventoryAuthority.Prepare(request, rules, ReadTables("inventory", "item_data"));
                long revision = ApplyInsideTransaction(result);
                db.Write("INSERT INTO inventory_actions VALUES(?,?,?,?)", request.Operation, request.Fingerprint, revision, PlayerBatchFormat.Encode(result));
                db.Write("DELETE FROM inventory_actions WHERE revision<=?", revision - 1024);
            });
            return result;
        }
        private void ApplyRows(IEnumerable<PlayerChange> changes)
        {
            // Ordering is supplied by the authority layer; never merge transfers or reorder their steps.
            foreach (var change in changes)
            {
                var table = Tables.Single(t => t.Name == change.Table);
                if (change.Delete)
                    db.Write("DELETE FROM \"" + table.Name + "\" WHERE " + string.Join(" AND ", table.Columns.Take(table.Keys).Select(c => "\"" + c + "\"=?")), change.Values);
                else new DatabaseRow(table.Name, string.Join(",", table.Columns), table.Keys, change.Values).Write(db);
            }
        }
        internal PlayerChange[] Read() => ReadTables();
        private PlayerChange[] ReadTables(params string[] tables)
        {
            if (!Complete) throw new InvalidOperationException("Incomplete player database");
            var rows = new List<PlayerChange>();
            foreach (var table in Tables.Where(t => tables.Length == 0 || tables.Contains(t.Name)))
                using (var data = db.Query("SELECT " + string.Join(",", table.Columns.Select(c => "\"" + c + "\"")) + " FROM \"" + table.Name + "\" ORDER BY " + string.Join(",", table.Columns.Take(table.Keys).Select(c => "\"" + c + "\""))))
                    while (data.Read()) rows.Add(new PlayerChange(table.Name, false, Enumerable.Range(0, table.Columns.Length).Select(data.Value).ToArray()));
            return rows.ToArray();
        }
        public void Dispose() { db.Dispose(); ownership.Dispose(); }
    }
}
