using System.Linq;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Island.Services;

namespace Island.UI;

public partial class IslandWindow
{
    async Task RunSnap(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new StringBuilder();
        try
        {
            // лайки Яндекс Музыки на подставном сервере (ISLAND_YM_API/ISLAND_YM_OAUTH → mock_ym.py), настоящий аккаунт не трогаем
            if (Environment.GetEnvironmentVariable("ISLAND_YM_TEST") == "1")
            {
                YandexMusic.Logout();
                log.AppendLine($"before: loggedIn={YandexMusic.LoggedIn}");
                var dc = await YandexMusic.StartLogin();
                log.AppendLine($"code: {dc.UserCode} url={dc.Url}");
                bool ok = await YandexMusic.WaitLogin(dc, default);
                log.AppendLine($"login: ok={ok} uid={YandexMusic.Uid} login={YandexMusic.Login}");
                await Task.Delay(500);
                var m = new MediaInfo { Title = "Группа крови", Artist = "Кино", AppId = "ru.yandex.desktop.music", Duration = TimeSpan.FromSeconds(286) };
                var t = await YandexMusic.Resolve(m);
                log.AppendLine($"resolve: {t} liked={t != null && YandexMusic.IsLiked(t)}");
                var miss = await YandexMusic.Resolve(new MediaInfo { Title = "Нет такой", Artist = "Никто" });
                log.AppendLine($"resolve-miss: {miss ?? "null"}");
                if (t != null)
                {
                    log.AppendLine($"like: {await YandexMusic.SetLike(t, true)} liked={YandexMusic.IsLiked(t)}");
                    // подменяем трек в плеере и рисуем главную с красным сердечком
                    _media.Info.Title = m.Title; _media.Info.Artist = m.Artist; _media.Info.AppId = m.AppId; _media.Info.App = "Яндекс"; _media.Info.Duration = m.Duration;
                    Expand("home"); RefreshMedia(); await Task.Delay(1200); Snap(dir, "ym_liked");
                    log.AppendLine($"unlike: {!await YandexMusic.SetLike(t, false)} liked={YandexMusic.IsLiked(t)}");
                    _likeFor = ""; RefreshMedia(); await Task.Delay(700); Snap(dir, "ym_unliked");
                }
                // токен на диске: зашифрован, сам токен в файле не виден
                var file = Path.Combine(Island.Core.Settings.Dir, Environment.GetEnvironmentVariable("ISLAND_YM_TOKENFILE") ?? "yandex.bin");
                log.AppendLine($"tokenfile: exists={File.Exists(file)} plain={File.Exists(file) && Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains("MOCKTOKEN")}");
                YandexMusic.Logout();
                log.AppendLine($"logout: loggedIn={YandexMusic.LoggedIn} fileLeft={File.Exists(file)}");
                File.WriteAllText(Path.Combine(dir, "log.txt"), log.ToString());
                Application.Current.Shutdown();
                return;
            }

            // автообновление: скачать последнюю сборку (даже той же версии), подменить свой exe и запустить новую копию
            if (Environment.GetEnvironmentVariable("ISLAND_UPD_TEST") == "1")
            {
                var exe = Environment.ProcessPath!;
                if (JustUpdated)
                {
                    log.AppendLine($"after-update: version={Island.Services.Updater.Show(Island.Services.Updater.Current)} exe={System.IO.Path.GetFileName(exe)} oldLeft={File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exe)!, "Island.old.exe"))}");
                }
                else
                {
                    var up = new Island.Services.Updater();
                    var sw0 = System.Diagnostics.Stopwatch.StartNew();
                    bool ok = await up.CheckAsync(force: true);
                    log.AppendLine($"check: ok={ok} ready={up.Ready} {sw0.ElapsedMilliseconds}ms");
                    var dir2 = Path.Combine(dir, "after"); Directory.CreateDirectory(dir2);
                    bool applied = ok && up.Apply(false, $"--snap \"{dir2}\"");
                    log.AppendLine($"apply: {applied} oldExists={File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exe)!, "Island.old.exe"))}");
                }
                File.WriteAllText(Path.Combine(dir, "log.txt"), log.ToString());
                Application.Current.Shutdown();
                return;
            }
            if (Environment.GetEnvironmentVariable("ISLAND_SNAP_QUICK") == "1")
            {
                if (Environment.GetEnvironmentVariable("ISLAND_SNAP_SETTINGS") == "1")
                {
                    var sw2 = new SettingsWindow(_s, this) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -9000, Top = -3000, ShowActivated = false };
                    sw2.Show(); await Task.Delay(500);
                    sw2.SnapPages(dir);
                    sw2.Close();
                }
                // быстрый прогон: тема/прозрачность — главная + система + капсула
                await Task.Delay(400);
                Snap(dir, "q_compact");
                Expand("home"); await Task.Delay(900); Snap(dir, "q_home");
                Expand("system"); await Task.Delay(3300); Snap(dir, "q_system");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var cpu0 = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
                for (int i = 0; i < 20; i++) _sys!.Sample();
                log.AppendLine($"sysmon sample: {sw.Elapsed.TotalMilliseconds / 20:F2} ms wall, {(System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds / 20:F2} ms cpu; cpu={_sys.CpuNow:0} ram={_sys.RamNow:0} gpu={_sys.GpuNow:0} disk={_sys.DiskNow:0} down={_sys.DownNow:0} tPdh={_sys.TPdh:F2} tNet={_sys.TNet:F2}");
                log.AppendLine($"fps={Fps} theme={_s.Theme} opacity={_s.Opacity}");
                File.WriteAllText(Path.Combine(dir, "log.txt"), log.ToString());
                Application.Current.Shutdown();
                return;
            }
            _media.Changed += OnMediaChanged;
            await _media.InitAsync();
            _agent = new AgentServer(47399, Dispatcher);
            _agent.Changed += OnAgentChanged;
            log.AppendLine("agent server: " + _agent.Start());

            try
            {
                var mg = await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                foreach (var ss in mg.GetSessions())
                {
                    var pp = await ss.TryGetMediaPropertiesAsync();
                    string dims = "нет";
                    if (pp.Thumbnail != null)
                    {
                        using var rs = await pp.Thumbnail.OpenReadAsync();
                        var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(rs), System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None);
                        dims = $"{dec.Frames[0].PixelWidth}x{dec.Frames[0].PixelHeight}";
                    }
                    log.AppendLine($"smtc: {ss.SourceAppUserModelId} thumb={dims} type={pp.PlaybackType} album='{pp.AlbumTitle}' tracks={pp.AlbumTrackCount} genres={pp.Genres.Count} status={ss.GetPlaybackInfo().PlaybackStatus} title='{pp.Title}' artist='{pp.Artist}' site={Island.Services.MediaService.SiteOf(pp.Title)} music={await Island.Services.MediaService.IsMusic(ss)}");
                    var tl = ss.GetTimelineProperties(); var pc = ss.GetPlaybackInfo().Controls;
                    log.AppendLine($"  timeline: start={tl.StartTime} end={tl.EndTime} pos={tl.Position} minSeek={tl.MinSeekTime} maxSeek={tl.MaxSeekTime} upd={tl.LastUpdatedTime:HH:mm:ss} seekEnabled={pc.IsPlaybackPositionEnabled} next={pc.IsNextEnabled} prev={pc.IsPreviousEnabled}");
                }
            }
            catch (Exception ex) { log.AppendLine("smtc err " + ex.Message); }
            await Task.Delay(600);
            Snap(dir, "0_compact");
            Toast("Готово — картинка уже в буфере", "\uE8C8", Mood.Happy, 10);
            await Task.Delay(700);
            Snap(dir, "1_toast");
            _toastText = null;

            // замер плавности: интервалы кадров в первые 300 мс каждого перехода
            {
                var wall = System.Diagnostics.Stopwatch.StartNew();
                var cur = new System.Collections.Generic.List<double>();
                double lw = 0, start = 0; bool on = false;
                var at = new System.Collections.Generic.List<double>();
                EventHandler h = (_, _) => { double n = wall.Elapsed.TotalMilliseconds; if (on && n - start < 300) { cur.Add(n - lw); at.Add(n - start); } lw = n; };
                CompositionTarget.Rendering += h;
                // какие операции диспетчера дольше 4 мс (что именно тормозит кадр)
                var ops = new System.Collections.Generic.List<string>();
                var opSw = new System.Collections.Generic.Dictionary<System.Windows.Threading.DispatcherOperation, double>();
                Dispatcher.Hooks.OperationStarted += (_, e) => opSw[e.Operation] = wall.Elapsed.TotalMilliseconds;
                Dispatcher.Hooks.OperationCompleted += (_, e) =>
                {
                    if (!opSw.Remove(e.Operation, out var t1)) return;
                    double d = wall.Elapsed.TotalMilliseconds - t1;
                    if (on && d > 4) { var m = e.Operation.GetType().GetField("_method", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(e.Operation) as Delegate; ops.Add($"{t1 - start:0}:{d:0.0}:{e.Operation.Priority}:{m?.Method.DeclaringType?.Name}.{m?.Method.Name}"); }
                };
                var all = new System.Collections.Generic.List<double>();
                async Task Step(string name, Action act)
                {
                    cur.Clear(); at.Clear(); ops.Clear(); start = lw = wall.Elapsed.TotalMilliseconds; on = true;
                    var t0 = wall.Elapsed.TotalMilliseconds; act(); var tAct = wall.Elapsed.TotalMilliseconds - t0;
                    await Task.Delay(450); on = false;
                    all.AddRange(cur);
                    log.AppendLine($"  {name,-16} call={tAct:F1}ms frames={cur.Count} max={(cur.Count > 0 ? cur.Max() : 0):F1} >8ms={cur.Count(x => x > 8)} first3=[{string.Join(", ", cur.Take(3).Select(x => x.ToString("F1")))}] spikes=[{string.Join(" ", cur.Select((x, i) => (x, i)).Where(q => q.x > 8).Select(q => $"{at[q.i]:0}:{q.x:0.0}"))}] ops=[{string.Join(" ", ops)}]");
                }
                await Step("expand home", () => Expand("home"));
                foreach (var pg in new[] { "sound", "control", "home", "system", "shelf", "chat", "home", "sound", "control", "system", "home" }) await Step("→ " + pg, () => Expand(pg));
                await Step("collapse", Collapse);
                await Step("expand home", () => Expand("home"));
                CompositionTarget.Rendering -= h;
                all.Sort();
                log.AppendLine($"frames ({(_s.SoftwareRendering ? "software" : "GPU")}): n={all.Count} p50={all[all.Count / 2]:F2} p95={all[(int)(all.Count * 0.95)]:F2} max={all[^1]:F1} >8ms={all.Count(x => x > 8)}");
                Collapse(); await Task.Delay(500);
            }

            foreach (var p in new[] { "home", "control", "sound", "shelf", "system" })
            {
                Expand(p);
                await Task.Delay(p == "control" ? 6000 : p == "system" ? 3200 : 1200);
                Snap(dir, "2_" + p);
                if (p == "sound")
                {
                    var mx = new System.Collections.Generic.Dictionary<string, float>();
                    for (int i = 0; i < 30; i++)
                    {
                        foreach (var a in _mixer.Apps)
                            foreach (var ss in a.Sessions)
                            {
                                string k = $"{a.Name} pid={a.Key} sess='{ss.DisplayName}' state={ss.State}";
                                float v = 0; try { v = ss.AudioMeterInformation.MasterPeakValue; } catch { }
                                mx[k] = Math.Max(mx.TryGetValue(k, out var o) ? o : 0, v);
                            }
                        await Task.Delay(100);
                    }
                    foreach (var kv in mx) log.AppendLine($"peak {kv.Value:F3}  {kv.Key}");
                    var seq = new System.Text.StringBuilder();
                    for (int i = 0; i < 15; i++)
                    {
                        seq.Append(i == 0 ? "" : " | ").Append($"m{_mixer.SamplePeaks():F3}");
                        foreach (var a in _mixer.Apps) if (!a.IsSystem) seq.Append($" {a.Name[..3]}{a.Peak:F3}/{a.ShownPeak:F3}");
                        await Task.Delay(60);
                    }
                    log.AppendLine("seq: " + seq);
                    var pr = System.Diagnostics.Process.GetCurrentProcess();
                    var t0 = pr.TotalProcessorTime; var sw = System.Diagnostics.Stopwatch.StartNew();
                    await Task.Delay(4000);
                    pr.Refresh();
                    log.AppendLine($"sound tab cpu: {(pr.TotalProcessorTime - t0).TotalMilliseconds / sw.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100:F2}% of all cores");
                    Collapse(); await Task.Delay(1500);
                    t0 = pr.TotalProcessorTime; sw.Restart();
                    await Task.Delay(4000);
                    pr.Refresh();
                    log.AppendLine($"collapsed cpu: {(pr.TotalProcessorTime - t0).TotalMilliseconds / sw.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100:F2}%");
                    OpenFlyout(); await Task.Delay(800);
                    t0 = pr.TotalProcessorTime; sw.Restart();
                    await Task.Delay(4000);
                    pr.Refresh();
                    log.AppendLine($"flyout cpu: {(pr.TotalProcessorTime - t0).TotalMilliseconds / sw.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100:F2}%");
                    OpenFlyout();
                    await Task.Delay(500);
                    HideIsland(); await Task.Delay(1500);
                    t0 = pr.TotalProcessorTime; sw.Restart();
                    await Task.Delay(6000);
                    pr.Refresh();
                    log.AppendLine($"hidden cpu: {(pr.TotalProcessorTime - t0).TotalMilliseconds / sw.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100:F3}% (в тесте окно за экраном, WPF всё время переотправляет кадр — в реальном запуске 0%)  visible={IsVisible}");
                    ShowIsland(); await Task.Delay(600);
                    log.AppendLine($"after show: visible={IsVisible}");
                    // автораскрытие: курсор у острова → раскрылся; ушёл далеко → свернулся
                    {
                        _idleTimer.Stop(); // настоящий курсор далеко от тестового окна за экраном — не мешаем
                        _hoverTest = true;
                        var c = Shell.PointToScreen(new Point(Shell.ActualWidth / 2, Shell.ActualHeight + 10));
                        var near = new Island.Core.Native.POINT { X = (int)c.X, Y = (int)c.Y };
                        var far = new Island.Core.Native.POINT { X = (int)c.X, Y = (int)c.Y + 900 };
                        long h0 = Environment.TickCount64;
                        log.AppendLine($"hover pre: exp={_expanded} vis={IsVisible} asleep={_asleep} armed={_zoneArmed} since={_zoneSince} auto={_autoExpanded} md={MouseDown()} fs={_hiddenByFullscreen} ov={_overlayOpen} near={near.X},{near.Y}");
                        bool n1 = HoverTick(near, h0); var s1 = _zoneSince; HoverTick(near, h0 + 200);
                        log.AppendLine($"hover after: n1={n1} since1={s1} exp={_expanded} armed={_zoneArmed}");
                        await Task.Delay(800);
                        bool opened = _expanded;
                        HoverTick(far, h0 + 1200); HoverTick(far, h0 + 1800);
                        await Task.Delay(800);
                        bool closed = !_expanded;
                        HoverTick(near, h0 + 2700); HoverTick(near, h0 + 2900);
                        bool rearmedOpen = _expanded;
                        log.AppendLine($"autoexpand: opened={opened} closed={closed} reopen={rearmedOpen}");
                        Collapse(); _hoverTest = false; await Task.Delay(600);
                    }
                    // «не мешать вкладкам»: курсор над островом над браузером → клики насквозь; замер на 600 мс → раскрылся
                    {
                        _idleTimer.Stop(); _hoverTest = true; _passForce = true; var keepCt = _s.ClickThrough; _s.ClickThrough = true;
                        var c = Shell.PointToScreen(new Point(Shell.ActualWidth / 2, Shell.ActualHeight / 2));
                        var on = new Island.Core.Native.POINT { X = (int)c.X, Y = (int)c.Y };
                        var far = new Island.Core.Native.POINT { X = (int)c.X, Y = (int)c.Y + 900 };
                        long p0 = Environment.TickCount64;
                        _lastMove = p0; _zoneArmed = true;
                        PassTick(on); HoverTick(on, p0 + 100);
                        int ex = Island.Core.Native.GetWindowLong(_hwnd, Island.Core.Native.GWL_EXSTYLE);
                        await Task.Delay(400);
                        log.AppendLine($"pass: passing={_passing} transparent={(ex & Island.Core.Native.WS_EX_TRANSPARENT) != 0} op={Opacity:0.00} exp={_expanded} (ждём: True True 0.28 False)");
                        PassTick(on); HoverTick(on, p0 + 700);
                        await Task.Delay(500);
                        ex = Island.Core.Native.GetWindowLong(_hwnd, Island.Core.Native.GWL_EXSTYLE);
                        log.AppendLine($"pass dwell: exp={_expanded} passing={_passing} transparent={(ex & Island.Core.Native.WS_EX_TRANSPARENT) != 0} op={Opacity:0.00} (ждём: True False False 1.00)");
                        Collapse(); await Task.Delay(600);
                        PassTick(on); PassTick(far);
                        log.AppendLine($"pass far: passing={_passing}");
                        _passForce = false; _hoverTest = false; _s.ClickThrough = keepCt;
                    }
                    // смена трека во время сворачивания: плеер шлёт пачку событий — сворачивание не должно перезапускаться
                    {
                        Expand("home"); await Task.Delay(900);
                        var cw = System.Diagnostics.Stopwatch.StartNew();
                        double lastF = 0, worst = 0; int restarts = _animGen;
                        EventHandler fh = (_, _) => { double n = cw.Elapsed.TotalMilliseconds; if (lastF > 0) worst = Math.Max(worst, n - lastF); lastF = n; };
                        CompositionTarget.Rendering += fh;
                        Collapse();
                        double doneAt = -1;
                        for (int i = 0; i < 60; i++)
                        {
                            if (i is 1 or 2 or 4 or 6 or 9) OnMediaChanged();
                            await Task.Delay(20);
                            if (doneAt < 0 && Shell.ActualHeight <= 32.5) doneAt = cw.Elapsed.TotalMilliseconds;
                        }
                        CompositionTarget.Rendering -= fh;
                        log.AppendLine($"collapse+media: done={doneAt:0}ms restarts={_animGen - restarts - 1} worstFrame={worst:0.0}ms");
                    }
                    // загрузки + интернет
                    {
                        var dld = Path.Combine(dir, "dl"); Directory.CreateDirectory(dld);
                        _dl = new DownloadWatcher(Dispatcher, dld);
                        _dl.Changed += OnDownloadsChanged; _dl.Finished += OnDownloadFinished;
                        var tmp = Path.Combine(dld, "ubuntu-24.04-desktop-amd64.iso.crdownload");
                        var chunk = new byte[1 << 20];
                        using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                        {
                            for (int i = 0; i < 18; i++) { f.Write(chunk); f.Flush(); await Task.Delay(200); }
                            log.AppendLine($"download: capsule={_capsuleDownload} text='{CompactText.Text}' w={_target.Width}");
                            Snap(dir, "10_download");
                        }
                        File.Move(tmp, Path.Combine(dld, "ubuntu-24.04-desktop-amd64.iso"));
                        await Task.Delay(700);
                        log.AppendLine($"download done: toast='{_toastText}' click={_toastClick != null} capsule={_capsuleDownload}");
                        Snap(dir, "10_download_done");
                        _toastText = null; _toastClick = null; _dl.Dispose(); _dl = null; UpdateCompact(force: true); await Task.Delay(600);
                        { var p1 = await NetWatcher.Ping(); var p2 = await NetWatcher.Ping(); var p3 = await NetWatcher.Ping("http://www.gstatic.com/generate_204"); var bad = await NetWatcher.Ping("http://10.255.255.1/", 1500); log.AppendLine($"ping: ya {p1}/{p2} ms, gstatic {p3} ms, недоступный адрес {bad}"); }
                        OnNetChanged(false, TimeSpan.Zero); await Task.Delay(700); Snap(dir, "11_net_lost");
                        OnNetChanged(true, TimeSpan.FromSeconds(83)); await Task.Delay(700); Snap(dir, "11_net_back");
                        _toastText = null; UpdateCompact(force: true); await Task.Delay(500);
                    }
                    // текст песни + микрофон
                    {
                        InitMic(); await Task.Delay(300);
                        Snap(dir, "9_mic_capsule");
                        log.AppendLine($"mic: mark={MicMark.Visibility} device={_mic?.HasDevice} muted={_mic?.Muted} users=[{string.Join(", ", _mic?.Users ?? Array.Empty<string>())}] peak={_mic?.Peak:0.000}");
                        var swl = System.Diagnostics.Stopwatch.StartNew();
                        var lq = await LyricsService.GetAsync("Queen", "Bohemian Rhapsody", "", TimeSpan.FromSeconds(354), default);
                        log.AppendLine($"lyrics net: {swl.ElapsedMilliseconds}ms synced={lq?.Lines.Count} plain={lq?.Plain?.Length} first='{lq?.Lines.FirstOrDefault(x => x.Text.Length > 0)?.Text}'");
                        swl.Restart();
                        var lq2 = await LyricsService.GetAsync("Queen", "Bohemian Rhapsody", "", TimeSpan.FromSeconds(354), default);
                        log.AppendLine($"lyrics cached: {swl.ElapsedMilliseconds}ms same={ReferenceEquals(lq, lq2)}");
                        var ly = await LyricsService.GetAsync("Кино", "Группа крови (Official Video)", "", TimeSpan.Zero, default);
                        log.AppendLine($"lyrics search: synced={ly?.Lines.Count} first='{ly?.Lines.FirstOrDefault(x => x.Text.Length > 0)?.Text}'");
                        // имитируем играющую песню и снимаем капсулу и главную с текстом
                        var info = _media.Info;
                        var keep = (info.Title, info.Artist, info.Album, info.IsPlaying, info.Duration, info.Position, info.PositionStamp, info.Cover);
                        info.Title = "Bohemian Rhapsody"; info.Artist = "Queen"; info.Album = ""; info.IsPlaying = true; info.Cover = null;
                        info.Duration = TimeSpan.FromSeconds(354); info.Position = TimeSpan.FromSeconds(62); info.PositionStamp = DateTime.UtcNow;
                        LyricsOnMedia(); UpdateCompact(force: true);
                        for (int i = 0; i < 40 && _lyrState == LyrState.Loading; i++) await Task.Delay(100);
                        await Task.Delay(900);
                        log.AppendLine($"lyrics ui: state={_lyrState} capsule={_capsuleLyrics} idx={_lyrIdx} text='{CompactText.Text}' w={_target.Width} timer={_lyrTimer?.IsEnabled}");
                        Snap(dir, "8_lyrics_capsule");
                        var keepOpen = _s.LyricsOpen; _s.LyricsOpen = true;
                        Expand("home"); await Task.Delay(1200);
                        log.AppendLine($"lyrics home: lines={LyricsList.Children.Count} shift={LyricsShift.Y:0} cur='{_lyrCur?.Text}'");
                        Snap(dir, "8_lyrics_home");
                        _s.LyricsOpen = keepOpen; ApplyLyricsOpen();
                        Collapse(); await Task.Delay(700);
                        (info.Title, info.Artist, info.Album, info.IsPlaying, info.Duration, info.Position, info.PositionStamp, info.Cover) = keep;
                        LyricsOnMedia(); UpdateCompact(force: true); await Task.Delay(700);
                    }
                    // игра на весь экран + играет музыка: остров спрятан, курсор не опрашивается, хука мыши нет
                    {
                        await Task.Delay(800);
                        StartIdleWatch();
                        bool hookBefore = _volHook != null;
                        OnFullscreenChanged(true);
                        var pr2 = System.Diagnostics.Process.GetCurrentProcess();
                        var c0 = pr2.TotalProcessorTime; var gw = System.Diagnostics.Stopwatch.StartNew(); int gen0 = _animGen;
                        for (int i = 0; i < 8; i++) { OnMediaChanged(); await Task.Delay(1000); } // «смена трека» раз в секунду
                        pr2.Refresh();
                        log.AppendLine($"game: visible={IsVisible} idleTimer={_idleTimer.IsEnabled} hook={hookBefore}->{_volHook != null} anims={_animGen - gen0} cpu={(pr2.TotalProcessorTime - c0).TotalMilliseconds / gw.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100:F3}%");
                        OnFullscreenChanged(false); await Task.Delay(300);
                        log.AppendLine($"game exit: visible={IsVisible} idleTimer={_idleTimer.IsEnabled} hook={_volHook != null}");
                        _volHook?.Dispose(); _volHook = null;
                    }
                    // спящий режим: «мышь не двигалась 20 с» → уезжает и прячется; уведомление и движение будят
                    Island.Core.Native.GetCursorPos(out _lastCursor); _lastMove = Environment.TickCount64 - SleepAfterMs - 1;
                    IdleTick(); await Task.Delay(700);
                    log.AppendLine($"sleep: asleep={_asleep} visible={IsVisible} y={ShellSlide.Y:0} op={Shell.Opacity:0.00}");
                    Toast("Будильник", null, Mood.Happy, 0.6); await Task.Delay(300);
                    log.AppendLine($"toast wake: asleep={_asleep} visible={IsVisible}");
                    await Task.Delay(1300); IdleTick(); await Task.Delay(700);
                    log.AppendLine($"re-sleep: asleep={_asleep} visible={IsVisible}");
                    _lastCursor.X += 5; IdleTick(); await Task.Delay(700);
                    log.AppendLine($"move wake: asleep={_asleep} visible={IsVisible} y={ShellSlide.Y:0} op={Shell.Opacity:0.00}");
                }
            }

            if (Environment.GetEnvironmentVariable("ISLAND_CHAT_TEST") == "1")
            {
                var keep = (_s.Provider, _s.Ai.TryGetValue("custom", out var kc) ? kc : null, _s.Ai.TryGetValue("openrouter", out var ko) ? ko : null);
                try
                {
                    _s.Provider = "custom";
                    _s.Ai["custom"] = new Island.Core.AiProvider { Key = "test-key", BaseUrl = "http://127.0.0.1:8799/v1", Model = "mock" };
                    var streamed = new StringBuilder();
                    var r1 = await _chat.SendAsync("первый", t => streamed.Append(t));
                    var r2 = await _chat.SendAsync("второй", _ => { });
                    log.AppendLine($"chat mock: r1='{r1}' streamed='{streamed}' r2='{r2}'");
                    _chat.Reset();
                    _s.Provider = "openrouter";
                    _s.Ai["openrouter"] = new Island.Core.AiProvider { Key = "test-key", BaseUrl = "http://127.0.0.1:8799/v1", Model = "dead:free" };
                    var r3 = await _chat.SendAsync("фолбэк", _ => { });
                    log.AppendLine($"chat fallback: r3='{r3}'");
                    _chat.Reset();
                    var mig = new Island.Core.Settings(); mig.Ai["openrouter"] = new Island.Core.AiProvider { Model = "deepseek/deepseek-v4-flash:free" };
                    log.AppendLine("migrated model: " + mig.AiFor("openrouter").Model);
                    _s.Provider = "openrouter";
                    _s.Ai["openrouter"] = new Island.Core.AiProvider { Key = "sk-or-invalid-test", BaseUrl = "https://openrouter.ai/api/v1", Model = "deepseek/deepseek-v4-flash:free" };
                    try { await _chat.SendAsync("тест", _ => { }); log.AppendLine("openrouter: неожиданно ответил"); }
                    catch (Exception ex) { log.AppendLine("openrouter bad key: " + ex.Message); }
                }
                catch (Exception ex) { log.AppendLine("chat test error: " + ex); }
                finally
                {
                    _s.Provider = keep.Item1;
                    if (keep.Item2 != null) _s.Ai["custom"] = keep.Item2; else _s.Ai.Remove("custom");
                    if (keep.Item3 != null) _s.Ai["openrouter"] = keep.Item3; else _s.Ai.Remove("openrouter");
                    _chat.Reset();
                }
            }

            Expand("chat");
            ChatList.Children.Remove(ChatHint);
            AddBubble("Сколько будет 27 × 453?", true);
            AddBubble("27 × 453 = 12 231", false);
            await Task.Delay(900);
            Snap(dir, "3_chat");
            Collapse();
            await Task.Delay(1200);
            Snap(dir, "3b_compact_" + (_media.Info.IsPlaying ? "media" : "idle"));
            log.AppendLine($"media: '{_media.Info.Title}' / {_media.Info.App}, cover: {(_media.Info.Cover != null ? "есть" : "нет")}");

            // сквозная проверка: «Claude Code» шлёт запрос разрешения → остров раскрывается → отвечаем «Разрешить»
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var ev = "{\"hook_event_name\":\"PreToolUse\",\"cwd\":\"C:\\\\proj\\\\tg-bot\",\"tool_name\":\"Edit\",\"tool_input\":{\"file_path\":\"C:\\\\proj\\\\tg-bot\\\\bot.py\"}}";
            await http.PostAsync("http://127.0.0.1:47399/claude/event", new StringContent(ev));
            await Task.Delay(800);
            Snap(dir, "4_agent_working");

            var perm = "{\"hook_event_name\":\"PermissionRequest\",\"cwd\":\"C:\\\\proj\\\\tg-bot\",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"git push origin main\"}}";
            var permTask = http.PostAsync("http://127.0.0.1:47399/claude/permission", new StringContent(perm));
            await Task.Delay(1500);
            Snap(dir, "5_agent_permission");
            _agent.Answer("allow");
            var resp = await (await permTask).Content.ReadAsStringAsync();
            log.AppendLine("permission response: " + resp);

            var q = "{\"hook_event_name\":\"PermissionRequest\",\"cwd\":\"C:\\\\proj\\\\tg-bot\",\"tool_name\":\"AskUserQuestion\",\"tool_input\":{\"questions\":[{\"question\":\"Какую тему сделать по умолчанию?\",\"header\":\"Тема\",\"multiSelect\":false,\"options\":[{\"label\":\"Тёмная\",\"description\":\"\"},{\"label\":\"Светлая\",\"description\":\"\"}]}]}}";
            var qTask = http.PostAsync("http://127.0.0.1:47399/claude/permission", new StringContent(q));
            await Task.Delay(1500);
            Snap(dir, "6_agent_question");
            _agent.Answer("Тёмная");
            log.AppendLine("question response: " + await (await qTask).Content.ReadAsStringAsync());

            await http.PostAsync("http://127.0.0.1:47399/claude/event", new StringContent("{\"hook_event_name\":\"Stop\",\"cwd\":\"C:\\\\proj\\\\tg-bot\"}"));
            await Task.Delay(1200);
            Snap(dir, "7_agent_done");

            var proc = System.Diagnostics.Process.GetCurrentProcess();
            log.AppendLine($"memory: WS={proc.WorkingSet64 / 1048576}MB private={proc.PrivateMemorySize64 / 1048576}MB");
        }
        catch (Exception ex) { log.AppendLine("ERROR: " + ex); }
        File.WriteAllText(Path.Combine(dir, "log.txt"), log.ToString());
        Application.Current.Shutdown();
    }

    void Snap(string dir, string name)
    {
        Root.UpdateLayout();
        int w = (int)Math.Ceiling(Root.ActualWidth), h = (int)Math.Ceiling(Root.ActualHeight);
        if (w < 2 || h < 2) return;
        var rtb = new RenderTargetBitmap(w * 2, h * 2, 192, 192, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (Environment.GetEnvironmentVariable("ISLAND_SNAP_CLEAR") != "1") dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(90, 120, 150)), null, new Rect(0, 0, w, h)); // «обои», чтобы видеть края
            dc.DrawRectangle(new VisualBrush(Root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Top, Viewbox = new Rect(0, 0, w, h), ViewboxUnits = BrushMappingMode.Absolute }, null, new Rect(0, 0, w, h));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(Path.Combine(dir, name + ".png"));
        enc.Save(fs);
    }
}
