using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Island.Services;

/// <summary>
/// Чтение XML уведомления прямо из базы центра уведомлений Windows (wpndatabase.db), только на чтение.
/// UserNotificationListener отдаёт лишь текст, а Telegram кладёт в уведомление ещё и аватарку
/// (&lt;image placement="appLogoOverride" src="file:///…/tdata/temp/….png"/&gt;). SQLite — системный winsqlite3.dll,
/// своих библиотек не тащим. Id в базе совпадает с UserNotification.Id.
/// </summary>
static class WpnDb
{
    const string Dll = "winsqlite3.dll";
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_bind_int64(IntPtr stmt, int i, long v);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_column_bytes(IntPtr stmt, int col);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_finalize(IntPtr stmt);

    const int SQLITE_OPEN_READONLY = 0x1, SQLITE_ROW = 100;

    static readonly string DbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications", "wpndatabase.db");

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern long sqlite3_column_int64(IntPtr stmt, int col);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_column_type(IntPtr stmt, int col);

    /// <summary>Выполнить запрос на чтение; row вызывается на каждую строку. false — база недоступна.</summary>
    static bool Query(string sql, long? arg, Action<IntPtr> row)
    {
        IntPtr db = IntPtr.Zero, st = IntPtr.Zero;
        try
        {
            if (sqlite3_open_v2(Encoding.UTF8.GetBytes(DbPath + "\0"), out db, SQLITE_OPEN_READONLY, IntPtr.Zero) != 0) return false;
            sqlite3_busy_timeout(db, 300);
            if (sqlite3_prepare_v2(db, Encoding.UTF8.GetBytes(sql + "\0"), -1, out st, IntPtr.Zero) != 0) return false;
            if (arg != null) sqlite3_bind_int64(st, 1, arg.Value);
            int rc;
            while ((rc = sqlite3_step(st)) == SQLITE_ROW) row(st);
            return rc == 101; // SQLITE_DONE
        }
        catch { return false; } // нет winsqlite3 (старая Windows), другая схема или база занята
        finally
        {
            if (st != IntPtr.Zero) sqlite3_finalize(st);
            if (db != IntPtr.Zero) sqlite3_close_v2(db);
        }
    }

    static string Blob(IntPtr st, int col)
    {
        int n = sqlite3_column_bytes(st, col);
        var p = sqlite3_column_blob(st, col);
        if (n <= 0 || p == IntPtr.Zero) return "";
        var buf = new byte[n];
        Marshal.Copy(p, buf, 0, n);
        return Encoding.UTF8.GetString(buf);
    }

    /// <summary>XML уведомления по его Id или null.</summary>
    public static string? Payload(long id)
    {
        string? res = null;
        Query("SELECT Payload FROM Notification WHERE Id=?", id, st => res = Blob(st, 0));
        return string.IsNullOrEmpty(res) ? null : res;
    }

    /// <summary>Самый большой Id уведомления (null — база недоступна).</summary>
    public static long? MaxId()
    {
        long max = 0;
        return Query("SELECT IFNULL(MAX(Id),0) FROM Notification", null, st => max = sqlite3_column_int64(st, 0)) ? max : null;
    }

    public readonly record struct Row(long Id, string App, string Xml, long Arrival);

    /// <summary>Всплывающие уведомления с Id больше заданного (null — база недоступна).</summary>
    public static System.Collections.Generic.List<Row>? ToastsAfter(long after)
    {
        var list = new System.Collections.Generic.List<Row>();
        bool ok = Query("SELECT n.Id, h.PrimaryId, n.Payload, n.ArrivalTime FROM Notification n JOIN NotificationHandler h ON h.RecordId = n.HandlerId " +
                        "WHERE n.Id > ? AND n.Type LIKE 'toast%' ORDER BY n.Id LIMIT 200", after,
                        st => list.Add(new Row(sqlite3_column_int64(st, 0), Blob(st, 1), Blob(st, 2), sqlite3_column_int64(st, 3))));
        return ok ? list : null;
    }

    static readonly Regex LogoRx = new("<image[^>]*placement=\"appLogoOverride\"[^>]*src=\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex LogoRx2 = new("<image[^>]*src=\"([^\"]+)\"[^>]*placement=\"appLogoOverride\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Путь к картинке-аватарке из XML уведомления (только локальные файлы).</summary>
    public static string? LogoPath(string? xml)
    {
        if (xml == null) return null;
        var m = LogoRx.Match(xml);
        if (!m.Success) m = LogoRx2.Match(xml);
        if (!m.Success) return null;
        var src = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
        try
        {
            if (src.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) src = new Uri(src).LocalPath;
            else if (src.Contains("://")) return null;
            return File.Exists(src) ? src : null;
        }
        catch { return null; }
    }
}
