using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Island.Core;
using Island.Services;

namespace Island.UI;

public partial class IslandWindow
{
    bool _seeking, _pinnedLoaded, _volUpdating;

    // ---------------- медиа ----------------
    void OnMediaChanged()
    {
        if (_userHidden) return; // покажем актуальное при возврате (ShowIsland → UpdateCompact)
        if (_expanded && _page == "home") RefreshMedia();
        else PrepareGlow();
        UpdateTimers();
        LyricsOnMedia();
        if (!_expanded) UpdateCompact();
    }

    void RefreshHome()
    {
        RefreshMedia();
        ApplyLyricsOpen();
        if (!_pinnedLoaded) LoadPinned();
        var (v, m) = _ctl.GetVolume();
        SetVolumeUi(v, m);
    }

    void RefreshMedia()
    {
        var i = _media.Info;
        MediaTitle.Text = i.HasMedia ? i.Title : "Ничего не играет";
        MediaArtist.Text = i.HasMedia ? i.Artist : "Включи музыку — остров оживёт";
        MediaApp.Text = i.App;
        MediaAppBadge.Visibility = i.HasMedia && i.App.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CoverBigBrush.ImageSource = i.Cover;
        if (!ReferenceEquals(_glowFor, i.Cover)) SetGlow(i.Cover, i.Cover == null ? null : MakeGlow(i.Cover));
        CoverPlaceholder.Visibility = i.Cover == null ? Visibility.Visible : Visibility.Collapsed;
        PlayBtn.Content = i.IsPlaying ? "" : "";
        UpdateProgress();
    }

    ImageSource? _glowFor;

    void SetGlow(ImageSource? cover, ImageSource? glow)
    {
        _glowFor = cover;
        CoverGlow.Source = glow;
        CoverShade.Visibility = cover == null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Размытый фон обложки считаем заранее, в простое после смены трека, — а не в момент раскрытия
    /// (было ~15–20 мс задержки перед анимацией).</summary>
    /// Размытие стоит ~15 мс, поэтому ждём, пока плеер перестанет слать события и закончится анимация.
    System.Windows.Threading.DispatcherTimer? _glowTimer;
    void PrepareGlow()
    {
        if (ReferenceEquals(_glowFor, _media.Info.Cover)) return;
        if (_glowTimer == null)
        {
            _glowTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(700) };
            _glowTimer.Tick += (_, _) =>
            {
                if (Environment.TickCount64 < _animUntil) return; // ещё едем — попробуем на следующем тике
                _glowTimer.Stop();
                var cover = _media.Info.Cover;
                if (!ReferenceEquals(_glowFor, cover)) SetGlow(cover, cover == null ? null : MakeGlow(cover));
            };
        }
        _glowTimer.Stop(); _glowTimer.Start();
    }

    /// <summary>Размытая копия обложки 96×96 — рисуется один раз; растянутая на фон, она и так мягкая.</summary>
    static ImageSource? MakeGlow(ImageSource src)
    {
        try
        {
            const int S = 96;
            var img = new Image { Source = src, Stretch = Stretch.UniformToFill, Margin = new Thickness(-14), Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 12, KernelType = System.Windows.Media.Effects.KernelType.Gaussian } };
            var host = new Grid { Width = S, Height = S, ClipToBounds = true, Children = { img } };
            host.Measure(new Size(S, S)); host.Arrange(new Rect(0, 0, S, S));
            var rtb = new RenderTargetBitmap(S, S, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(host);
            rtb.Freeze();
            return rtb;
        }
        catch { return src; }
    }

    void UpdateProgress()
    {
        var i = _media.Info;
        var dur = i.Duration;
        var pos = i.CurrentPosition;
        if (dur.TotalSeconds > 0 && pos > dur) pos = dur;
        MediaPos.Text = Fmt(pos);
        MediaDur.Text = dur.TotalSeconds > 0 ? Fmt(dur) : "";
        if (!_seeking) MediaSeek.Value = dur.TotalSeconds > 0 ? pos.TotalSeconds / dur.TotalSeconds : 0;
        bool can = dur.TotalSeconds > 0 && i.CanSeek;
        if (MediaSeek.IsEnabled != can)
        {
            MediaSeek.IsEnabled = can;
            MediaSeek.Opacity = can ? 1 : 0.45;
            MediaSeek.ToolTip = can ? null : dur.TotalSeconds > 0 ? "Этот плеер не разрешает перематывать" : "Плеер не сообщает длину трека";
        }
    }

    static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    void Play_Click(object s, RoutedEventArgs e) => _media.PlayPause();
    void Next_Click(object s, RoutedEventArgs e) => _media.Next();
    void Prev_Click(object s, RoutedEventArgs e) => _media.Prev();
    void MediaSeek_Down(object s, MouseButtonEventArgs e) => _seeking = true;
    async void MediaSeek_Up(object s, MouseButtonEventArgs e)
    {
        var d = _media.Info.Duration;
        if (d.TotalSeconds <= 0) { _seeking = false; return; }
        var to = TimeSpan.FromSeconds(MediaSeek.Value * d.TotalSeconds);
        MediaPos.Text = Fmt(to);
        bool ok = await _media.SeekAsync(to);
        _seeking = false;
        if (!ok)
        {
            MediaArtist.Text = "Плеер не дал перемотать";
            UpdateProgress();
        }
    }

    // ---------------- громкость ----------------
    void SetVolumeUi(float v, bool mute)
    {
        _volUpdating = true;
        VolSlider.Value = v;
        _volUpdating = false;
        MuteBtn.Content = mute || v < 0.01 ? "" : v < 0.34 ? "" : v < 0.67 ? "" : "";
    }
    void Vol_Changed(object s, RoutedPropertyChangedEventArgs<double> e) { if (!_volUpdating) _ctl.SetVolume((float)e.NewValue); }
    void Mute_Click(object s, RoutedEventArgs e) => _ctl.ToggleMute();

    // ---------------- закреплённые приложения ----------------
    void LoadPinned()
    {
        _pinnedLoaded = true;
        PinnedApps.Items.Clear();
        foreach (var path in _s.PinnedApps)
        {
            var icon = FileIcon(path);
            if (icon == null) continue;
            var b = new Button
            {
                Style = (Style)FindResource("Icon"), Width = 34, Height = 34, Margin = new Thickness(0, 0, 4, 0),
                ToolTip = Path.GetFileNameWithoutExtension(path),
                Content = new Image { Source = icon, Width = 22, Height = 22 },
            };
            var p = path;
            b.Click += (_, _) => { Collapse(); ControlService.OpenUri(p); };
            PinnedApps.Items.Add(b);
        }
    }

    public static ImageSource? FileIcon(string path)
    {
        try
        {
            var fi = new Native.SHFILEINFO();
            Native.SHGetFileInfo(path, 0, ref fi, (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.SHFILEINFO>(), Native.SHGFI_ICON | Native.SHGFI_LARGEICON);
            if (fi.hIcon == IntPtr.Zero) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(fi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            Native.DestroyIcon(fi.hIcon);
            src.Freeze();
            return src;
        }
        catch { return null; }
    }

    // ---------------- быстрые действия ----------------
    async Task<object?> RunOverlay(OverlayMode mode)
    {
        _overlayOpen = true;
        Collapse();
        Hide();
        await Task.Delay(180); // даём острову исчезнуть, чтобы он не попал в снимок
        try
        {
            var ov = new ScreenOverlay(mode);
            ov.Show();
            return await ov.Result;
        }
        catch (Exception ex) { Toast("Ошибка: " + ex.Message, "", Mood.Alert, 4); return null; }
        finally
        {
            _overlayOpen = false;
            if (!_hiddenByFullscreen && !_userHidden) { Show(); PlaceWindow(_target); }
        }
    }

    async void Act_Region(object s, RoutedEventArgs e)
    {
        if (_overlayOpen) return;
        if (await RunOverlay(OverlayMode.Annotate) is BitmapSource img)
        {
            CaptureService.SaveCopy(img, _s.CaptureFolder);
            Toast("Готово — картинка уже в буфере", "", Mood.Happy);
        }
    }

    async void Act_Full(object s, RoutedEventArgs e)
    {
        _overlayOpen = true;
        Collapse(); Hide();
        await Task.Delay(200);
        try
        {
            var (b, _, _) = CaptureService.MonitorUnderCursor();
            var img = CaptureService.Capture(b.Left, b.Top, b.W, b.H);
            CaptureService.SaveCopy(img, _s.CaptureFolder);
            Toast("Скриншот экрана в буфере", "", Mood.Happy);
        }
        finally { _overlayOpen = false; if (!_hiddenByFullscreen && !_userHidden) { Show(); PlaceWindow(_target); } }
    }

    async void Act_Ocr(object s, RoutedEventArgs e)
    {
        if (await RunOverlay(OverlayMode.Select) is not BitmapSource img) return;
        Toast("Распознаю текст…", null, Mood.Thinking, 10);
        try
        {
            var text = await CaptureService.OcrAsync(img);
            if (string.IsNullOrWhiteSpace(text)) { Toast("Текст не найден", "", Mood.Alert); return; }
            ClipHelper.SetText(text);
            Toast("Текст скопирован", "", Mood.Happy);
        }
        catch (Exception ex) { Toast("OCR: " + ex.Message, "", Mood.Alert, 4); }
    }

    async void Act_Qr(object s, RoutedEventArgs e)
    {
        if (await RunOverlay(OverlayMode.Select) is not BitmapSource img) return;
        var text = await Task.Run(() => CaptureService.DecodeQr(img));
        if (text == null) { Toast("QR-код не найден", "", Mood.Alert); return; }
        ClipHelper.SetText(text);
        Toast(Uri.TryCreate(text, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http") ? "Ссылка из QR скопирована" : "QR: " + text, "", Mood.Happy, 3);
    }

    void Act_Mixer(object s, RoutedEventArgs e) => Expand("sound");

    async void Act_Record(object s, RoutedEventArgs e)
    {
        if (_rec.IsRecording) { await StopRecording(); return; }
        Collapse();
        Toast("Запись начнётся…", "", Mood.Alert, 1);
        await Task.Delay(400);
        if (await _rec.StartAsync())
        {
            _toastText = null;
            RecTileIcon.Text = ""; RecTileText.Text = "Стоп";
            UpdateCompact(force: true);
            UpdateTimers();
        }
    }

    async void RecStop_Click(object s, RoutedEventArgs e) { e.Handled = true; await StopRecording(); }

    async Task StopRecording()
    {
        RecTileIcon.Text = ""; RecTileText.Text = "Запись экрана";
        Toast("Сохраняю запись…", null, Mood.Thinking, 30);
        var path = await _rec.StopAsync();
        UpdateTimers();
        if (path == null) { Toast("Запись не удалась", "", Mood.Alert); return; }
        AddToShelf(new[] { path });
        ClipHelper.SetFiles(new[] { path });
        Toast("Запись сохранена и на полке", "", Mood.Happy, 3);
    }

    void Act_Plain(object s, RoutedEventArgs e)
    {
        try
        {
            if (!Clipboard.ContainsText()) { Toast("В буфере нет текста", "", Mood.Alert); return; }
            var t = Clipboard.GetText();
            ClipHelper.SetText(t);
            Toast("Форматирование убрано", "", Mood.Happy);
        }
        catch { Toast("Буфер занят другой программой", "", Mood.Alert); }
    }

    void Act_Folder(object s, RoutedEventArgs e) { Collapse(); ControlService.OpenUri(EnsureFolder()); }
}
