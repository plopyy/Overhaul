using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Overhaul.Persistence
{
    // All access to a connection belongs to its owning thread.
    internal sealed class SqliteDatabase : IDisposable
    {
        private IntPtr handle;
        private readonly Dictionary<string, Statement> commands = new Dictionary<string, Statement>();
        private static readonly object LibraryLock = new object();
        private static IntPtr library;
        private static Open open;
        private static Close close;
        private static Prepare prepare;
        private static Step step;
        private static FinalizeStatement finalize;
        private static FinalizeStatement reset;
        private static BindBytes bindBlob, bindText;
        private static BindLong bindLong;
        private static BindDouble bindDouble;
        private static BindNull bindNull;
        private static ColumnBytes columnBlob, columnText;
        private static ColumnSize columnSize;
        private static ColumnLong columnLong;
        private static ColumnDouble columnDouble;
        private static ColumnSize columnType;
        private static ErrorMessage error;
        private static LibraryVersion version;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Open(byte[] path, out IntPtr db, int flags, IntPtr vfs);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Close(IntPtr db);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Prepare(IntPtr db, byte[] sql, int length, out IntPtr statement, IntPtr tail);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Step(IntPtr statement);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FinalizeStatement(IntPtr statement);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int BindBytes(IntPtr statement, int index, byte[] value, int length, IntPtr destructor);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int BindLong(IntPtr statement, int index, long value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int BindDouble(IntPtr statement, int index, double value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int BindNull(IntPtr statement, int index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ColumnBytes(IntPtr statement, int index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ColumnSize(IntPtr statement, int index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long ColumnLong(IntPtr statement, int index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate double ColumnDouble(IntPtr statement, int index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ErrorMessage(IntPtr db);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LibraryVersion();
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("libdl.so.2")] private static extern IntPtr dlopen(string path, int flags);
        [DllImport("libdl.so.2")] private static extern IntPtr dlsym(IntPtr module, string name);

        private static T Function<T>(string name) where T : class
        {
            var address = Environment.OSVersion.Platform == PlatformID.Win32NT ? GetProcAddress(library, name) : dlsym(library, name);
            if (address == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
        }

        private static void LoadLibrary()
        {
            lock (LibraryLock)
            {
                if (open != null) return;
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    if (IntPtr.Size != 8) throw new PlatformNotSupportedException("SQLite requires a 64-bit server.");
                    var assembly = typeof(SqliteDatabase).Assembly;
                    string directory = Path.Combine(Path.GetDirectoryName(assembly.Location), "native", "sqlite-3.53.4");
                    Directory.CreateDirectory(directory);
                    string path = Path.Combine(directory, "overhaul_sqlite3.dll");
                    using (var source = assembly.GetManifestResourceStream("Overhaul.sqlite3.win-x64.dll"))
                    {
                        if (source == null) throw new FileNotFoundException("Embedded SQLite library is missing.");
                        if (!File.Exists(path))
                        {
                            // Unique temporary file, then publish atomically for multiple local servers.
                            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                            try
                            {
                                using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write)) source.CopyTo(target);
                                try { File.Move(temporary, path); }
                                catch (IOException) { if (!File.Exists(path)) throw; }
                            }
                            finally { if (File.Exists(temporary)) File.Delete(temporary); }
                        }
                    }
                    library = LoadLibraryW(path);
                }
                else library = dlopen("libsqlite3.so.0", 2);
                if (library == IntPtr.Zero) throw new DllNotFoundException("SQLite native library could not be loaded.");
                version = Function<LibraryVersion>("sqlite3_libversion_number");
                // WAL reset race fixed in 3.51.3, 3.52.0 and newer; use a recent supported engine.
                if (version() < 3052000) throw new NotSupportedException("SQLite 3.52.0 or newer is required.");
                close = Function<Close>("sqlite3_close_v2");
                prepare = Function<Prepare>("sqlite3_prepare_v2");
                step = Function<Step>("sqlite3_step");
                finalize = Function<FinalizeStatement>("sqlite3_finalize");
                reset = Function<FinalizeStatement>("sqlite3_reset");
                bindBlob = Function<BindBytes>("sqlite3_bind_blob");
                bindText = Function<BindBytes>("sqlite3_bind_text");
                bindLong = Function<BindLong>("sqlite3_bind_int64");
                bindDouble = Function<BindDouble>("sqlite3_bind_double");
                bindNull = Function<BindNull>("sqlite3_bind_null");
                columnBlob = Function<ColumnBytes>("sqlite3_column_blob");
                columnText = Function<ColumnBytes>("sqlite3_column_text");
                columnSize = Function<ColumnSize>("sqlite3_column_bytes");
                columnLong = Function<ColumnLong>("sqlite3_column_int64");
                columnDouble = Function<ColumnDouble>("sqlite3_column_double");
                columnType = Function<ColumnSize>("sqlite3_column_type");
                error = Function<ErrorMessage>("sqlite3_errmsg");
                open = Function<Open>("sqlite3_open_v2");
            }
        }

        internal SqliteDatabase(string path, bool readOnly = false)
        {
            LoadLibrary();
            int result = open(Utf8(path), out handle, readOnly ? 1 : 2 | 4, IntPtr.Zero);
            try
            {
                Check(result);
                Execute("PRAGMA busy_timeout=5000");
                if (!readOnly)
                {
                    using (var command = Query("PRAGMA journal_mode=WAL"))
                        if (!command.Read() || command.Text(0) != "wal") throw new IOException("SQLite WAL mode is unavailable.");
                    Execute("PRAGMA synchronous=FULL");
                    Execute("PRAGMA foreign_keys=ON");
                }
            }
            catch { Dispose(); throw; }
        }
        private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + "\0");
        private void Check(int result)
        {
            if (result != 0) throw new IOException("SQLite " + result + ": " + (handle == IntPtr.Zero ? "open failed" : Marshal.PtrToStringAnsi(error(handle))));
        }
        internal Statement Query(string sql, params object[] values) => new Statement(this, sql, values);
        internal void Execute(string sql, params object[] values) { using (var command = Query(sql, values)) while (command.Read()) { } }
        internal void Write(string sql, params object[] values)
        {
            if (!commands.TryGetValue(sql, out var command)) commands.Add(sql, command = Query(sql, values));
            else command.Bind(values);
            while (command.Read()) { }
        }
        internal void Transaction(Action action)
        {
            Execute("BEGIN IMMEDIATE");
            try { action(); Execute("COMMIT"); }
            catch { try { Execute("ROLLBACK"); } catch { } throw; }
        }
        public void Dispose()
        {
            foreach (var command in commands.Values) command.Dispose();
            commands.Clear();
            if (handle != IntPtr.Zero) { close(handle); handle = IntPtr.Zero; }
        }

        internal sealed class Statement : IDisposable
        {
            private readonly SqliteDatabase database;
            private IntPtr statement;
            internal Statement(SqliteDatabase database, string sql, object[] values)
            {
                this.database = database;
                try
                {
                    database.Check(prepare(database.handle, Utf8(sql), -1, out statement, IntPtr.Zero));
                    Bind(values);
                }
                catch { Dispose(); throw; }
            }
            internal void Bind(object[] values)
            {
                    database.Check(reset(statement));
                    for (int i = 0; i < values.Length; i++)
                    {
                        object value = values[i];
                        if (value == null) database.Check(bindNull(statement, i + 1));
                        else if (value is double || value is float) database.Check(bindDouble(statement, i + 1, Convert.ToDouble(value)));
                        else if (value is byte[] bytes) database.Check(bindBlob(statement, i + 1, bytes.Length == 0 ? new byte[1] : bytes, bytes.Length, new IntPtr(-1)));
                        else if (value is string text)
                        {
                            byte[] encoded = Utf8(text);
                            database.Check(bindText(statement, i + 1, encoded, encoded.Length - 1, new IntPtr(-1)));
                        }
                        else database.Check(bindLong(statement, i + 1, Convert.ToInt64(value)));
                    }
            }
            internal bool Read()
            {
                int result = step(statement);
                if (result == 100) return true;
                if (result == 101) return false;
                database.Check(result); return false;
            }
            internal long Long(int column) => columnLong(statement, column);
            internal double Double(int column) => columnDouble(statement, column);
            internal bool IsNull(int column) => columnType(statement, column) == 5;
            internal object Value(int column)
            {
                switch(columnType(statement,column))
                { case 1:return Long(column);case 2:return Double(column);case 3:return Text(column);case 4:return Blob(column);default:return null; }
            }
            internal byte[] Blob(int column)
            {
                int count = columnSize(statement, column);
                var bytes = new byte[count];
                if (count > 0) Marshal.Copy(columnBlob(statement, column), bytes, 0, count);
                return bytes;
            }
            internal string Text(int column)
            {
                int count = columnSize(statement, column);
                var bytes = new byte[count];
                if (count > 0) Marshal.Copy(columnText(statement, column), bytes, 0, count);
                return Encoding.UTF8.GetString(bytes);
            }
            public void Dispose() { if (statement != IntPtr.Zero) { finalize(statement); statement = IntPtr.Zero; } }
        }
    }
}
