using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;

namespace Overhaul.Persistence
{
    internal static class WorldSchema
    {
        internal static void Create(SqliteDatabase db)
        {
            db.Execute("PRAGMA application_id=" + MigrationState.ApplicationId);
            db.Execute("PRAGMA user_version=" + MigrationState.Format);
            using (var stream = typeof(WorldSchema).Assembly.GetManifestResourceStream("Overhaul.Persistence.schema.sql"))
            using (var reader = new StreamReader(stream ?? throw new InvalidDataException("Missing world schema")))
                foreach (string sql in reader.ReadToEnd().Split(';')) if (!string.IsNullOrWhiteSpace(sql)) db.Execute(sql);
        }
        internal static bool Owned(SqliteDatabase db)
        {
            using (var result = db.Query("PRAGMA application_id")) return result.Read() && result.Long(0) == MigrationState.ApplicationId;
        }
    }

    // Immutable rows can be queued to the writer without sharing game objects.
    internal sealed class DatabaseRow
    {
        private static readonly ConcurrentDictionary<string,string> Statements=new ConcurrentDictionary<string,string>();
        internal readonly string Table, Columns;
        internal readonly int Keys;
        internal readonly object[] Values;
        internal DatabaseRow(string table, string columns, int keys, params object[] values)
        { Table = table; Columns = columns; Keys = keys; Values = values; }
        internal string Identity => Table + ":" + string.Join(":", Values.Take(Keys));
        internal void Write(SqliteDatabase db)
        {
            string sql=Statements.GetOrAdd(Table+":"+Columns+":"+Keys,_=>Sql());
            db.Write(sql, Values);
        }
        private string Sql()
        {
            string[] columns = Columns.Split(',').Select(c => "\"" + c + "\"").ToArray();
            var changes = columns.Skip(Keys).ToArray();
            string sql = "INSERT INTO \"" + Table + "\"(" + string.Join(",", columns) + ") VALUES (" + string.Join(",", columns.Select(_ => "?")) + ") ON CONFLICT(" + string.Join(",", columns.Take(Keys)) + ") ";
            sql += changes.Length == 0 ? "DO NOTHING" : "DO UPDATE SET " + string.Join(",", changes.Select(c => c + "=excluded." + c)) + " WHERE " + string.Join(" OR ", changes.Select(c => "\"" + Table + "\"." + c + " IS NOT excluded." + c));
            return sql;
        }
    }

    internal sealed class ObjectRecord
    {
        internal long Id, User;
        internal uint NetworkId;
        internal int Chunk, Prefab, Flags, Order;
        internal string Name;
        internal float[] Position, Rotation;
        internal int ConnectionType, ConnectionHash;
        internal long? TargetUser;
        internal uint TargetId;
        internal readonly List<PropertyRecord> Properties = new List<PropertyRecord>();
    }
    internal sealed class PropertyRecord
    {
        internal int Key;
        internal string Name, Type;
        internal object Value;
    }
}
