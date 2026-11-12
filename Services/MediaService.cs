using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Island.Core;
using Windows.Media.Control;

namespace Island.Services;

public sealed class MediaInfo
{
    public string Title = "", Artist = "", App = "", Album = "";
    /// <summary>Идентификатор плеера в Windows (SourceAppUserModelId), например ru.yandex.desktop.music.</summary>
    public string AppId = "";
    public ImageSource? Cover;
    public bool IsPlaying;
    public TimeSpan Position, Duration;
    public DateTime PositionStamp = DateTime.UtcNow;
    /// <summary>Плеер разрешает перемотку; Start/MinSeek/MaxSeek — его шкала времени.</summary>
    public bool CanSeek = true;
    public TimeSpan Start, MinSeek, MaxSeek;
    public bool HasMedia => Title.Length > 0;
    public TimeSpan CurrentPosition => IsPlaying ? Position + (DateTime.UtcNow - PositionStamp) : Position;
}

/// <summary>
/// Медиа через SMTC (события, без опроса). Если SMTC-сессий нет — запасной путь через Winamp API
/// (Winamp и AIMP в режиме эмуляции Winamp), с редкой проверкой раз в 3 с.
/// </summary>
public sealed class MediaService
{
    readonly Dispatcher _ui;
    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    GlobalSystemMediaTransportControlsSession? _session;
    readonly DispatcherTimer _winampTimer;
    string _coverKey = "";

    public MediaInfo Info { get; private set; } = new();
    public event Action? Changed;

    public MediaService(Dispatcher ui)
    {
        _ui = ui;
        _winampTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _winampTimer.Tick += (_, _) => PollWinamp();
    }

    public async Task InitAsync()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.CurrentSessionChanged += (_, _) => _ui.InvokeAsync(ScheduleAttach);
            _mgr.SessionsChanged += (_, _) => _ui.InvokeAsync(ScheduleAttach);
        }
        catch { }
        Attach();
    }

    // ---------- выбор сессии: только музыка ----------
    readonly System.Collections.Generic.List<GlobalSystemMediaTransportControlsSession> _watched = new();
    DispatcherTimer? _reattach;
    int _attachGen;

    static readonly string[] Browsers = { "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "arc", "browser", "yandex", "chromium", "thorium", "floorp", "librewolf", "waterfox" };
    static readonly string[] VideoApps = { "vlc", "mpc-hc", "mpc-be", "potplayer", "zunevideo", "movies", "mpv", "kmplayer", "netflix", "kinopoisk", "plex", "smplayer", "gom", "daum" };

    /// <summary>Музыка ли это. YouTube в браузере Windows видит как «Music», поэтому у браузеров
    /// признак музыки — альбом (его заполняют YouTube Music, Яндекс Музыка в вебе и т.п., а обычное видео — нет).</summary>
    internal static async Task<bool> IsMusic(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            var id = s.SourceAppUserModelId.ToLowerInvariant();
            var p = await s.TryGetMediaPropertiesAsync();
            if (p == null) return false;
            if (p.PlaybackType == Windows.Media.MediaPlaybackType.Video) return false;
            foreach (var v in VideoApps) if (id.Contains(v)) return false;
            if (id.Contains("music") || id.Contains("spotify") || id.Contains("aimp") || id.Contains("foobar") || id.Contains("winamp") || id.Contains("musicbee") || id.Contains("tidal") || id.Contains("deezer")) return true;
            foreach (var b in Browsers)
                if (id.Contains(b))
                {
                    if (!string.IsNullOrWhiteSpace(p.AlbumTitle)) return true;
                    // Сайт по заголовку окна браузера (если вкладка с роликом открыта): YouTube Shorts и т.п.
                    // тоже отдают квадратную обложку, так что одной формы обложки мало.
                    var site = SiteOf(p.Title);
                    if (site == Site.Video) return false;
                    if (site == Site.Music) return true;
                    if (HashTag.IsMatch(p.Title ?? "")) return false; // «#shorts #3дпечать» — признак ролика
                    // Музыкальные сайты (SoundCloud, ВК, YouTube Music…) отдают квадратную обложку,
                    // видео (YouTube и др.) — вытянутое превью 16:9 / 4:3. Результат кэшируем по треку.
                    string key = id + "|" + p.Title + "|" + p.Artist;
                    if (_squareCache.TryGetValue(key, out bool sq)) return sq;
                    sq = p.Thumbnail != null && await IsSquare(p.Thumbnail);
                    if (_squareCache.Count > 64) _squareCache.Clear();
                    _squareCache[key] = sq;
                    return sq;
                }
            return true;
        }
        catch { return false; }
    }

    static readonly System.Collections.Generic.Dictionary<string, bool> _squareCache = new();

    internal enum Site { Unknown, Music, Video }
    static readonly System.Text.RegularExpressions.Regex HashTag = new(@"(^|\s)#\w", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    static readonly string[] MusicSites = { "YouTube Music", "SoundCloud", "Яндекс Музыка", "Яндекс.Музыка", "Spotify", "Deezer", "Bandcamp", "Zvuk", "Звук", "VK Музыка", "VK Music" };
    static readonly string[] VideoSites = { "YouTube", "VK Видео", "VK Video", "VK Клипы", "VK Clips", "Rutube", "Twitch", "TikTok", "Кинопоиск", "Дзен", "Dzen", "Vimeo", "Netflix", "Okko" };

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int max);

    /// <summary>Ищет окно браузера, где активна вкладка с этим названием, и определяет сайт по хвосту заголовка
    /// («Название - YouTube - Google Chrome»).</summary>
    internal static Site SiteOf(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return Site.Unknown;
        string probe = title.Trim();
        if (probe.Length > 30) probe = probe[..30];
        var found = Site.Unknown;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            int n = GetWindowTextLength(h);
            if (n < probe.Length || n > 2000) return true;
            var sb = new System.Text.StringBuilder(n + 1);
            GetWindowText(h, sb, sb.Capacity);
            var t = sb.ToString();
            int i = t.IndexOf(probe, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return true;
            var tail = t[(i + probe.Length)..];
            foreach (var m in MusicSites) if (tail.Contains(m, StringComparison.OrdinalIgnoreCase)) { found = Site.Music; return false; }
            foreach (var v in VideoSites) if (tail.Contains(v, StringComparison.OrdinalIgnoreCase)) { found = Site.Video; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static async Task<bool> IsSquare(Windows.Storage.Streams.IRandomAccessStreamReference r)
    {
        try
        {
            using var rs = await r.OpenReadAsync();
            using var st = rs.AsStreamForRead();
            var dec = BitmapDecoder.Create(st, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None); // читает только заголовок
            double w = dec.Frames[0].PixelWidth, h = dec.Frames[0].PixelHeight;
            return h > 0 && Math.Abs(w / h - 1) < 0.1;
        }
        catch { return false; }
    }

    void OnAnyChanged(GlobalSystemMediaTransportControlsSession s, object e) => _ui.InvokeAsync(ScheduleAttach);

    void ScheduleAttach()
    {
        // события сыплются пачками (название, обложка, статус) — пересобираем выбор не чаще раза в 150 мс
        if (_reattach == null)
        {
            _reattach = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromMilliseconds(150) };
            _reattach.Tick += (_, _) => { _reattach.Stop(); Attach(); };
        }
        _reattach.Stop(); _reattach.Start();
    }

    async void Attach()
    {
        int gen = ++_attachGen;
        GlobalSystemMediaTransportControlsSession? s = null;
        if (_mgr != null)
        {
            System.Collections.Generic.IReadOnlyList<GlobalSystemMediaTransportControlsSession> all;
            try { all = _mgr.GetSessions(); } catch { all = Array.Empty<GlobalSystemMediaTransportControlsSession>(); }
            // следим за всеми сессиями: вкладка с YouTube Music может стать музыкой в любой момент
            foreach (var w in _watched.ToArray())
                if (!all.Contains(w)) { try { w.PlaybackInfoChanged -= OnAnyChanged; w.MediaPropertiesChanged -= OnAnyChanged; } catch { } _watched.Remove(w); }
            foreach (var x in all)
                if (!_watched.Contains(x)) { try { x.PlaybackInfoChanged += OnAnyChanged; x.MediaPropertiesChanged += OnAnyChanged; } catch { } _watched.Add(x); }

            GlobalSystemMediaTransportControlsSession? cur = null;
            try { cur = _mgr.GetCurrentSession(); } catch { }
            int best = -1;
            foreach (var x in all)
            {
                if (!await IsMusic(x)) continue;
                if (gen != _attachGen) return;
                bool playing = false;
                try { playing = x.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; } catch { }
                int score = (playing ? 2 : 0) + (ReferenceEquals(x, cur) || x.SourceAppUserModelId == cur?.SourceAppUserModelId ? 1 : 0);
                if (score > best) { best = score; s = x; }
            }
            if (gen != _attachGen) return;
        }

        if (!ReferenceEquals(s, _session))
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnProps;
                _session.PlaybackInfoChanged -= OnPlayback;
                _session.TimelinePropertiesChanged -= OnTimeline;
            }
            _session = s;
            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnProps;
                _session.PlaybackInfoChanged += OnPlayback;
                _session.TimelinePropertiesChanged += OnTimeline;
            }
        }

        if (_session == null)
        {
            bool had = Info.HasMedia && Info.App != "Winamp";
            if (had) Info = new MediaInfo();
            _coverKey = "";
            _winampTimer.Start();
            PollWinamp();
            if (had) Changed?.Invoke(); // видео из браузера больше не показываем — остров убирает карточку
            return;
        }
        _winampTimer.Stop();
        _ = RefreshProps();
        RefreshPlayback();
        RefreshTimeline();
    }

    void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _ui.InvokeAsync(() => RefreshProps());
    void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => _ui.InvokeAsync(RefreshPlayback);
    void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => _ui.InvokeAsync(RefreshTimeline);

    async Task RefreshProps(int attempt = 0)
    {
        var s = _session;
        if (s == null) return;
        try
        {
            var p = await s.TryGetMediaPropertiesAsync();
            if (s != _session || p == null) return;
            Info.Title = p.Title ?? "";
            Info.Artist = string.IsNullOrEmpty(p.Artist) ? p.AlbumArtist ?? "" : p.Artist;
            Info.App = AppName(s.SourceAppUserModelId);
            Info.AppId = s.SourceAppUserModelId ?? "";
            Info.Album = p.AlbumTitle ?? "";
            // ключ учитывает и наличие обложки: многие плееры (Яндекс, браузеры) присылают её отдельным, вторым событием
            string key = Info.Title + "|" + Info.Artist + "|" + (p.Thumbnail != null);
            if (key != _coverKey)
            {
                bool sameTrack = _coverKey.StartsWith(Info.Title + "|" + Info.Artist + "|", StringComparison.Ordinal);
                _coverKey = key;
                if (!sameTrack) Info.Cover = null;
                if (p.Thumbnail != null)
                {
                    var bmp = await LoadCover(p.Thumbnail);
                    if (s != _session) return;
                    if (bmp != null) Info.Cover = bmp;
                    else _coverKey = ""; // не прочиталась — попробуем ещё раз при следующем обновлении
                }
                if (Info.Cover == null && attempt < 3)
                {
                    // плеер ещё не отдал картинку и может не прислать событие — переспросим сами
                    int next = attempt + 1;
                    _ = Task.Delay(700 * next).ContinueWith(_ => _ui.InvokeAsync(() => RefreshProps(next)));
                    if (_coverKey == key) _coverKey = "";
                }
            }
        }
        catch { }
        Changed?.Invoke();
    }

    static async Task<ImageSource?> LoadCover(Windows.Storage.Streams.IRandomAccessStreamReference r)
    {
        try
        {
            using var ras = await r.OpenReadAsync();
            if (ras.Size == 0) return null;
            using var stream = ras.AsStreamForRead();
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            if (ms.Length == 0) return null;
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = 200; // не держим в памяти оригинал 1000×1000
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    void RefreshPlayback()
    {
        try
        {
            var pi = _session?.GetPlaybackInfo();
            if (pi == null) return;
            var pos = Info.CurrentPosition;
            Info.IsPlaying = pi.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            Info.CanSeek = pi.Controls.IsPlaybackPositionEnabled;
            Info.Position = pos; Info.PositionStamp = DateTime.UtcNow;
        }
        catch { }
        Changed?.Invoke();
    }

    void RefreshTimeline()
    {
        try
        {
            var t = _session?.GetTimelineProperties();
            if (t == null) return;
            Info.Start = t.StartTime; Info.MinSeek = t.MinSeekTime; Info.MaxSeek = t.MaxSeekTime;
            Info.Duration = t.EndTime - t.StartTime;
            var stamp = t.LastUpdatedTime.UtcDateTime;
            // сразу после нашей перемотки некоторые плееры (Яндекс Музыка, браузеры) ещё отдают старую позицию —
            // не откатываем ползунок назад, пока не придёт свежая
            if (!(DateTime.UtcNow - _seekAt < TimeSpan.FromSeconds(4) && stamp < _seekAt))
            {
                Info.Position = t.Position - t.StartTime < TimeSpan.Zero ? TimeSpan.Zero : t.Position - t.StartTime;
                Info.PositionStamp = stamp > DateTime.UtcNow || stamp.Year < 2000 ? DateTime.UtcNow : stamp;
            }
        }
        catch { }
        Changed?.Invoke();
    }

    static string AppName(string aumid)
    {
        var a = aumid.ToLowerInvariant();
        if (a.Contains("spotify")) return "Spotify";
        if (a.Contains("chrome")) return "Chrome";
        if (a.Contains("msedge")) return "Edge";
        if (a.Contains("firefox")) return "Firefox";
        if (a.Contains("yandex")) return "Яндекс";
        if (a.Contains("aimp")) return "AIMP";
        if (a.Contains("zune") || a.Contains("media")) return "Медиаплеер";
        var n = Path.GetFileNameWithoutExtension(aumid.Split('!')[0]);
        return n.Length > 18 ? n[..18] : n;
    }

    // ------------- управление -------------
    public async void PlayPause()
    {
        if (_session != null) { try { await _session.TryTogglePlayPauseAsync(); } catch { } return; }
        WinampCmd(Info.IsPlaying ? 40046 : 40045);
    }
    public async void Next()
    {
        if (_session != null) { try { await _session.TrySkipNextAsync(); } catch { } return; }
        WinampCmd(40048);
    }
    public async void Prev()
    {
        if (_session != null) { try { await _session.TrySkipPreviousAsync(); } catch { } return; }
        WinampCmd(40044);
    }
    DateTime _seekAt;

    /// <summary>Перемотка. false — плеер отказал (например, не разрешает перематывать).</summary>
    public async Task<bool> SeekAsync(TimeSpan pos)
    {
        if (_session != null)
        {
            var s = _session;
            // позиция — по шкале плеера (у некоторых она начинается не с нуля) и в пределах разрешённого
            var target = Info.Start + pos;
            if (Info.MaxSeek > Info.MinSeek) target = target < Info.MinSeek ? Info.MinSeek : target > Info.MaxSeek ? Info.MaxSeek : target;
            bool ok = false;
            for (int attempt = 0; attempt < 2 && !ok; attempt++)
            {
                try { ok = await s.TryChangePlaybackPositionAsync(target.Ticks); } catch { }
                if (!ok) await Task.Delay(150);
            }
            if (ok)
            {
                // показываем новую позицию сразу, не дожидаясь, пока плеер пришлёт её сам
                _seekAt = DateTime.UtcNow;
                Info.Position = pos; Info.PositionStamp = _seekAt;
                Changed?.Invoke();
            }
            return ok;
        }
        var h = WinampWnd();
        if (h == IntPtr.Zero) return false;
        Native.SendMessage(h, 0x400, new IntPtr((int)pos.TotalMilliseconds), new IntPtr(106)); // IPC_JUMPTOTIME
        return true;
    }

    // ------------- Winamp / AIMP (Winamp API) -------------
    static IntPtr WinampWnd() => Native.FindWindow("Winamp v1.x", null);

    void WinampCmd(int cmd)
    {
        var h = WinampWnd();
        if (h == IntPtr.Zero) return;
        Native.PostMessage(h, 0x111, new IntPtr(cmd), IntPtr.Zero);
        _ui.InvokeAsync(PollWinamp, DispatcherPriority.Background);
    }

    void PollWinamp()
    {
        var h = WinampWnd();
        if (h == IntPtr.Zero)
        {
            if (Info.App == "Winamp") { Info = new MediaInfo(); Changed?.Invoke(); }
            return;
        }
        var sb = new StringBuilder(512);
        Native.GetWindowText(h, sb, 512);
        var t = sb.ToString();
        foreach (var suffix in new[] { " - Winamp", " - AIMP" })
        {
            int i = t.LastIndexOf(suffix, StringComparison.Ordinal);
            if (i > 0) t = t[..i];
        }
        int dot = t.IndexOf(". ", StringComparison.Ordinal);
        if (dot > 0 && dot < 6 && int.TryParse(t[..dot], out _)) t = t[(dot + 2)..];
        string artist = "", title = t.Trim();
        int dash = title.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) { artist = title[..dash]; title = title[(dash + 3)..]; }

        int st = Native.SendMessage(h, 0x400, IntPtr.Zero, new IntPtr(104)).ToInt32();       // IPC_ISPLAYING
        int posMs = Native.SendMessage(h, 0x400, IntPtr.Zero, new IntPtr(105)).ToInt32();    // IPC_GETOUTPUTTIME (мс)
        int lenS = Native.SendMessage(h, 0x400, new IntPtr(1), new IntPtr(105)).ToInt32();   // длина (с)

        bool changed = Info.Title != title || Info.IsPlaying != (st == 1) || Info.App != "Winamp";
        Info.App = "Winamp";
        Info.Title = title; Info.Artist = artist; Info.Cover = null;
        Info.IsPlaying = st == 1;
        Info.Position = TimeSpan.FromMilliseconds(Math.Max(0, posMs));
        Info.PositionStamp = DateTime.UtcNow;
        Info.Duration = TimeSpan.FromSeconds(Math.Max(0, lenS));
        if (changed || title.Length == 0) Changed?.Invoke();
    }
}
