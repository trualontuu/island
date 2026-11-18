using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Island.Services;

/// <summary>Одно сообщение из уведомления Telegram.</summary>
public sealed class TgMessage
{
    public long Id;
    public string Chat = "", Sender = "", Text = "", AppId = "";
    public DateTimeOffset At;
    /// <summary>Фото профиля из уведомления (Telegram кладёт его в tdata/temp), уже загруженное и замороженное.</summary>
    public System.Windows.Media.ImageSource? Avatar;
    /// <summary>Что писать крупно: имя (в личке) или «Отправитель · Чат» (в группе).</summary>
    public string Title => Sender.Length > 0 && Sender != Chat ? $"{Sender} · {Chat}" : Chat;
}

/// <summary>
/// Сообщения Telegram из центра уведомлений Windows — без входа в аккаунт и без API Telegram.
/// Telegram Desktop (и форки: Unigram, AyuGram, Kotatogram, 64Gram) кладёт сообщение в системное уведомление.
///
/// Как узнаём о новых, дёшево:
///  • раз в секунду — только размер/время файла базы уведомлений (wpndatabase.db-wal, ~15 мкс). FileSystemWatcher
///    на неё событий не получает, а NotificationChanged не упакованным программам Windows не отдаёт;
///  • база изменилась → один SQL-запрос «уведомления с Id больше последнего» через системный winsqlite3
///    (текст + аватарка сразу, ~1 мс, в фоне). Раньше брали весь список через UserNotificationListener — 25–50 мс CPU
///    на каждое изменение, от уведомлений любых программ;
///  • если базу прочитать нельзя (другая версия Windows) — запасной путь через UserNotificationListener.
/// </summary>
public sealed class TelegramWatcher : IDisposable
{
    static readonly string[] AppKeys = { "telegram", "unigram", "ayugram", "kotatogram", "64gram" };
    static readonly string DbDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications");

    readonly Dispatcher _ui;
    readonly DispatcherTimer _poll;
    readonly HashSet<uint> _seen = new(); // только для запасного пути
    UserNotificationListener? _l;
    (long, long, long, long) _stamp;
    long _lastId = -1;   // последний Id в базе, который уже видели
    bool _db, _busy, _again, _paused, _baselined, _disposed, _started;

    /// <summary>Пришло новое сообщение.</summary>
    public event Action<TgMessage>? Message;
    /// <summary>Пока остров был на паузе (игра на весь экран / скрыт), пришло столько сообщений; второе — последнее.</summary>
    public event Action<int, TgMessage?>? Missed;

    public string Status { get; private set; } = "запуск";
    /// <summary>Какие приложения считать Telegram (тест подставляет PowerShell).</summary>
    public string[] Keys = AppKeys;
    public double LastScanMs { get; private set; }
    public int Scans { get; private set; }
    /// <summary>Доставать фото профиля.</summary>
    public bool LoadAvatars = true;
    /// <summary>Для теста: принудительно запасной путь.</summary>
    public bool ForceListener;

    public TelegramWatcher(Dispatcher ui)
    {
        _ui = ui;
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += (_, _) => { var st = Stamp(); if (st != _stamp) { _stamp = st; _ = Scan(); } };
    }

    public async Task StartAsync()
    {
        _stamp = Stamp();
        long? max = ForceListener ? null : await Task.Run(WpnDb.MaxId);
        if (_disposed) return;
        if (max != null)
        {
            _db = true; _lastId = max.Value; _baselined = true;
            Status = "по базе уведомлений (проверка файла раз в 1 с)";
        }
        else
        {
            try
            {
                _l = UserNotificationListener.Current;
                var st = _l.GetAccessStatus();
                if (st == UserNotificationListenerAccessStatus.Unspecified) st = await _l.RequestAccessAsync();
                if (st != UserNotificationListenerAccessStatus.Allowed)
                {
                    Status = "Windows не дал доступ к уведомлениям (Параметры → Конфиденциальность → Уведомления)";
                    _l = null; return;
                }
            }
            catch (Exception e) { Status = "нет доступа: " + e.Message; _l = null; return; }
            if (_disposed) return;
            await Scan(); // запоминаем то, что уже висит, — старое не показываем
            Status = "через API уведомлений (проверка файла раз в 1 с)";
        }
        _started = true;
        if (!_paused && !_disposed) _poll.Start();
    }

    /// <summary>Размер и время изменения файлов базы уведомлений — меняются, когда уведомление приходит или уходит.</summary>
    static (long, long, long, long) Stamp()
    {
        try
        {
            var w = new FileInfo(Path.Combine(DbDir, "wpndatabase.db-wal"));
            var d = new FileInfo(Path.Combine(DbDir, "wpndatabase.db"));
            return (w.Exists ? w.Length : -1, w.Exists ? w.LastWriteTimeUtc.Ticks : 0, d.Exists ? d.Length : -1, d.Exists ? d.LastWriteTimeUtc.Ticks : 0);
        }
        catch { return default; }
    }

    /// <summary>Игра на весь экран / остров скрыт: не следим. Вернулись — одним уведомлением, сколько пропущено.</summary>
    public void Pause(bool on)
    {
        if (_paused == on) return;
        _paused = on;
        if (on) { _poll.Stop(); return; }
        if (!_started) return;
        _poll.Start();
        _stamp = Stamp();
        _ = Scan(fromPause: true);
    }

    bool IsTelegram(string? aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return false;
        var a = aumid.ToLowerInvariant();
        return Keys.Any(a.Contains);
    }

    async Task Scan(bool fromPause = false)
    {
        if (_disposed || (!_db && _l == null)) return;
        if (_paused && !fromPause) return;
        if (_busy) { _again = true; return; } // пришло ещё одно, пока читали, — перечитаем сразу после
        _busy = true;
        var sw = Stopwatch.StartNew();
        try
        {
            List<TgMessage> fresh;
            if (_db)
            {
                long after = _lastId;
                bool avatars = LoadAvatars;
                var keys = Keys;
                (fresh, long max) = await Task.Run(() => ScanDb(after, keys, avatars));
                if (max >= 0) _lastId = max;
            }
            else fresh = await ScanListener();
            LastScanMs = sw.Elapsed.TotalMilliseconds; Scans++;
            if (!_baselined) { _baselined = true; return; }
            if (fresh.Count == 0 || _disposed) return;
            fresh.Sort((a, b) => a.At.CompareTo(b.At));
            if (fromPause) { Missed?.Invoke(fresh.Count, fresh[^1]); return; }
            foreach (var m in fresh) Message?.Invoke(m);
        }
        catch { }
        finally
        {
            _busy = false;
            if (_again) { _again = false; _ = Scan(); }
        }
    }

    // ---------------- основной путь: SQL по базе ----------------
    static readonly Regex TextRx = new("<text[^>]*?(?:/>|>(.*?)</text>)", RegexOptions.Singleline | RegexOptions.Compiled);

    static (List<TgMessage>, long) ScanDb(long after, string[] keys, bool avatars)
    {
        var res = new List<TgMessage>();
        var rows = WpnDb.ToastsAfter(after);
        if (rows == null) return (res, -1);
        long max = after;
        foreach (var r in rows)
        {
            max = Math.Max(max, r.Id);
            var a = r.App.ToLowerInvariant();
            if (!keys.Any(a.Contains)) continue;
            // тексты по порядку, включая пустые: [чат, текст, ""] в личке, [чат, отправитель, текст] в группе
            var t = TextRx.Matches(r.Xml).Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim()).Where(x => x.Length > 0).ToList();
            var m = Make(t, r.App, r.Id, DateTimeOffset.FromFileTime(r.Arrival));
            if (m == null) continue;
            if (avatars) m.Avatar = LoadImage(WpnDb.LogoPath(r.Xml));
            res.Add(m);
        }
        // база пересоздана (Id пошли заново) — начинаем с текущего максимума
        if (rows.Count == 0) { var mx = WpnDb.MaxId(); if (mx != null && mx < after) max = mx.Value; }
        return (res, max);
    }

    // ---------------- запасной путь: UserNotificationListener ----------------
    async Task<List<TgMessage>> ScanListener()
    {
        var list = await _l!.GetNotificationsAsync(NotificationKinds.Toast);
        var fresh = new List<TgMessage>();
        var alive = new HashSet<uint>();
        foreach (var n in list)
        {
            alive.Add(n.Id);
            if (!_seen.Add(n.Id)) continue;
            string? app = null;
            try { app = n.AppInfo?.AppUserModelId; } catch { }
            if (!IsTelegram(app)) continue;
            try
            {
                var b = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                var t = b?.GetTextElements().Select(x => (x.Text ?? "").Trim()).Where(x => x.Length > 0).ToList();
                var m = t == null ? null : Make(t, app!, n.Id, n.CreationTime);
                if (m != null) fresh.Add(m);
            }
            catch { }
        }
        _seen.IntersectWith(alive); // прочитанные Telegram сам убирает — забываем их id
        if (LoadAvatars && fresh.Count > 0)
            await Task.Run(() => { foreach (var m in fresh) m.Avatar = LoadImage(WpnDb.LogoPath(WpnDb.Payload(m.Id))); });
        return fresh;
    }

    static TgMessage? Make(List<string> t, string app, long id, DateTimeOffset at)
    {
        if (t.Count == 0) return null;
        var m = new TgMessage { Id = id, AppId = app, At = at, Chat = t[0] };
        if (t.Count >= 3) { m.Sender = t[1]; m.Text = string.Join(" ", t.Skip(2)); }
        else if (t.Count == 2) m.Text = t[1];
        m.Text = m.Text.Replace('\n', ' ').Replace('\r', ' ');
        return m;
    }

    static System.Windows.Media.ImageSource? LoadImage(string? path)
    {
        if (path == null) return null;
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; // Telegram чистит tdata/temp — читаем сразу
            bmp.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = 96;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>Открыть Telegram: запуск по AUMID выводит уже открытое окно на передний план.</summary>
    public static void Open(string appId)
    {
        try
        {
            if (appId.Contains('!')) Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + appId) { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("tg://") { UseShellExecute = true });
        }
        catch { }
    }

    public void Dispose()
    {
        _disposed = true;
        _poll.Stop();
    }
}
