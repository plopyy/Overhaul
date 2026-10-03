using System;
using System.IO;

namespace Overhaul.Persistence
{
    internal static class MigrationState
    {
        internal const int Format = 1;
        internal const int ApplicationId = 0x4F564857;

        internal static bool Complete(SqliteDatabase db, long? world = null)
        {
            using (var table = db.Query("SELECT 1 FROM sqlite_master WHERE type='table' AND name='migration_state'"))
                if (!table.Read()) return false;
            using (var row = db.Query("SELECT id,status,schema_version,world_uid FROM migration_state"))
            {
                if (!row.Read()) return false;
                if (row.Long(0) != 1 || row.Text(1) != "complete") return false;
                if (row.Long(2) != Format) throw new InvalidDataException("Unsupported Overhaul world database version.");
                if (world.HasValue && row.Long(3) != world.Value) throw new InvalidDataException("The world database belongs to another world.");
                if (row.Read()) throw new InvalidDataException("Invalid migration marker: more than one row.");
                return true;
            }
        }

        // Called LAST, inside the same transaction as the validated migration.
        // SQLite rolls back both the table creation and its row if import fails.
        internal static void Finish(SqliteDatabase db, long world)
        {
            using (var check = db.Query("PRAGMA integrity_check"))
                if (!check.Read() || check.Text(0) != "ok" || check.Read()) throw new InvalidDataException("World database integrity check failed.");
            using (var check = db.Query("PRAGMA foreign_key_check"))
                if (check.Read()) throw new InvalidDataException("World database has missing references.");
            db.Execute("CREATE TABLE migration_state (id INTEGER PRIMARY KEY CHECK(id=1),status TEXT NOT NULL CHECK(status='complete'),schema_version INTEGER NOT NULL,world_uid INTEGER NOT NULL,completed_at TEXT NOT NULL)");
            db.Execute("INSERT INTO migration_state VALUES (1,'complete',?,?,?)", Format, world, DateTime.UtcNow.ToString("O"));
        }
    }
}
