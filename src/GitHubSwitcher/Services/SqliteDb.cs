using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace GitHubSwitcher.Services;

/// <summary>
/// 极简 SQLite 封装，直接 P/Invoke Windows 自带的 winsqlite3.dll。
/// 这样无需任何 NuGet 包，离线也能构建运行（已验证 System32\winsqlite3.dll 存在，版本 3.51.1）。
/// </summary>
public sealed class SqliteDb : IDisposable
{
    private const string Lib = "winsqlite3.dll";

    private const int SQLITE_OK = 0;
    private const int SQLITE_ROW = 100;
    private const int SQLITE_DONE = 101;
    private const int SQLITE_OPEN_READONLY = 0x1;
    private const int SQLITE_OPEN_READWRITE = 0x2;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr db);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr tail);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr stmt);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr stmt);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr stmt, int i);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_blob(IntPtr stmt, int i);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_bytes(IntPtr stmt, int i);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern long sqlite3_column_int64(IntPtr stmt, int i);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_errmsg(IntPtr db);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] val, int n, IntPtr destructor);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_blob(IntPtr stmt, int index, byte[] val, int n, IntPtr destructor);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_int64(IntPtr stmt, int index, long val);

    private static readonly IntPtr SQLITE_TRANSIENT = new(-1);

    private IntPtr _db;

    private SqliteDb(IntPtr db) => _db = db;

    public static SqliteDb Open(string path, bool readOnly)
    {
        var flags = readOnly ? SQLITE_OPEN_READONLY : SQLITE_OPEN_READWRITE;
        var bytes = Encoding.UTF8.GetBytes(path + '\0');
        var rc = sqlite3_open_v2(bytes, out var db, flags, IntPtr.Zero);
        if (rc != SQLITE_OK)
        {
            if (db != IntPtr.Zero) sqlite3_close(db);
            throw new InvalidOperationException($"打开数据库失败（rc={rc}）：{path}");
        }
        return new SqliteDb(db);
    }

    public string LastError => _db == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(sqlite3_errmsg(_db)) ?? "";

    /// <summary>执行一条非查询语句。</summary>
    public void Execute(string sql, params object?[] args)
    {
        var stmt = Prepare(sql, args);
        try
        {
            var rc = sqlite3_step(stmt);
            if (rc != SQLITE_DONE && rc != SQLITE_ROW)
                throw new InvalidOperationException($"执行失败（rc={rc}）：{LastError}");
        }
        finally { sqlite3_finalize(stmt); }
    }

    /// <summary>查询并逐行回调。</summary>
    public List<object?[]> Query(string sql, params object?[] args)
    {
        var stmt = Prepare(sql, args);
        var rows = new List<object?[]>();
        var colCount = sqlite3_column_count(stmt);
        try
        {
            int rc;
            while ((rc = sqlite3_step(stmt)) == SQLITE_ROW)
            {
                var row = new object?[colCount];
                for (int i = 0; i < colCount; i++)
                    row[i] = ReadColumn(stmt, i);
                rows.Add(row);
            }
            if (rc != SQLITE_DONE)
                throw new InvalidOperationException($"查询失败（rc={rc}）：{LastError}");
        }
        finally { sqlite3_finalize(stmt); }
        return rows;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_count(IntPtr stmt);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_type(IntPtr stmt, int i);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern double sqlite3_column_double(IntPtr stmt, int i);

    private const int SQLITE_INTEGER = 1;
    private const int SQLITE_FLOAT = 2;
    private const int SQLITE_BLOB = 4;
    private const int SQLITE_NULL = 5;

    /// <summary>
    /// 按声明的列类型取值。
    /// 注意：encrypted_value 在不同 Chrome 版本可能是 TEXT 或 BLOB，
    /// 若一律按 BLOB 读取，TEXT 列会因字节数为 0 而被误当空值（这是必须避免的坑）。
    /// </summary>
    private static object? ReadColumn(IntPtr stmt, int i)
    {
        switch (sqlite3_column_type(stmt, i))
        {
            case SQLITE_NULL:
                return null;
            case SQLITE_INTEGER:
                return sqlite3_column_int64(stmt, i);
            case SQLITE_FLOAT:
                return sqlite3_column_double(stmt, i);
            case SQLITE_BLOB:
            {
                var ptr = sqlite3_column_blob(stmt, i);
                var len = sqlite3_column_bytes(stmt, i);
                if (ptr == IntPtr.Zero || len <= 0) return Array.Empty<byte>();
                var buf = new byte[len];
                Marshal.Copy(ptr, buf, 0, len);
                return buf;
            }
            default:
            {
                var ptr = sqlite3_column_text(stmt, i);
                return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
            }
        }
    }

    private IntPtr Prepare(string sql, object?[] args)
    {
        var sqlBytes = Encoding.UTF8.GetBytes(sql + '\0');
        var rc = sqlite3_prepare_v2(_db, sqlBytes, -1, out var stmt, IntPtr.Zero);
        if (rc != SQLITE_OK)
            throw new InvalidOperationException($"SQL 编译失败（rc={rc}）：{LastError}\n{sql}");

        for (int i = 0; i < args.Length; i++)
        {
            var idx = i + 1;
            var a = args[i];
            switch (a)
            {
                case null:
                    sqlite3_bind_text(stmt, idx, Array.Empty<byte>(), 0, SQLITE_TRANSIENT);
                    break;
                case byte[] b:
                    sqlite3_bind_blob(stmt, idx, b, b.Length, SQLITE_TRANSIENT);
                    break;
                case long l:
                    sqlite3_bind_int64(stmt, idx, l);
                    break;
                case int n:
                    sqlite3_bind_int64(stmt, idx, n);
                    break;
                default:
                    var s = Encoding.UTF8.GetBytes(a.ToString() ?? "");
                    sqlite3_bind_text(stmt, idx, s, s.Length, SQLITE_TRANSIENT);
                    break;
            }
        }
        return stmt;
    }

    public void Dispose()
    {
        if (_db != IntPtr.Zero)
        {
            sqlite3_close(_db);
            _db = IntPtr.Zero;
        }
    }
}
