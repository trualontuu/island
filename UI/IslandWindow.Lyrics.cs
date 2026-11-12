using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Island.Services;

namespace Island.UI;

/// <summary>
/// Текст песни: текущая строчка в капсуле и весь текст на главной (вместо плиток, кнопка у плеера).
/// Строки меняются по одноразовому таймеру ровно к началу следующей строки — без опроса каждые N мс.
/// </summary>
public partial class IslandWindow
{
    enum LyrState { None, Loading, Ready, NotFound }

    Lyrics? _lyr;
    LyrState _lyrState;
    string _lyrKey = "";
    int _lyrIdx = -2;
    CancellationTokenSource? _lyrCts;
    DispatcherTimer? _lyrTimer;
    bool _capsuleLyrics;          // капсула сейчас в режиме «строчка песни»
    bool _lyrKeepWide;            // новый трек, текст ещё грузится — капсула остаётся широкой, чтобы не прыгать
    long _lyrManualUntil;         // пользователь крутит текст колёсиком — не дёргаем автопрокрутку
    TextBlock? _lyrCur;
    const double LyricLeadSec = 0.2; // чуть раньше: глазу приятнее, когда строка появляется вместе с голосом

    void LyricsOnMedia()
    {
        var i = _media.Info;
        string key = _s.Lyrics && i.HasMedia ? LyricsService.Key(i.Artist, i.Title) : "";
        if (key != _lyrKey)
        {
            _lyrKey = key;
            _lyrKeepWide = _capsuleLyrics;
            _lyr = null; _lyrIdx = -2;
            _lyrState = key.Length == 0 ? LyrState.None : LyrState.Loading;
            _lyrCts?.Cancel();
            RebuildLyricsView();
            if (key.Length > 0) _ = LoadLyricsAsync(key, _lyrCts = new CancellationTokenSource());
        }
        LyricsTick();
    }

    async Task LoadLyricsAsync(string key, CancellationTokenSource cts)
    {
        try
        {
            // плеер присылает название, а длительность — отдельным событием чуть позже; она нужна для точного поиска
            await Task.Delay(700, cts.Token);
            var i = _media.Info;
            string a = i.Artist, t = i.Title, al = i.Album; var d = i.Duration;
            var l = await Task.Run(() => LyricsService.GetAsync(a, t, al, d, cts.Token));
            if (cts.IsCancellationRequested || key != _lyrKey) return;
            _lyr = l;
            _lyrState = l == null ? LyrState.NotFound : LyrState.Ready;
            _lyrIdx = -2;
            RebuildLyricsView();
            if (!_expanded) UpdateCompact(); else LyricsTick();
        }
        catch { if (!cts.IsCancellationRequested && key == _lyrKey) { _lyrState = LyrState.NotFound; RebuildLyricsView(); } }
    }

    bool LyricsOnScreen => _lyr?.Synced == true && IsVisible && !_hiddenByFullscreen
        && ((!_expanded && _capsuleLyrics) || (_expanded && _page == "home" && _s.LyricsOpen));

    void LyricsTick()
    {
        _lyrTimer?.Stop();
        var l = _lyr; var i = _media.Info;
        if (l == null || !LyricsOnScreen) return;
        var pos = i.CurrentPosition + TimeSpan.FromSeconds(LyricLeadSec);
        int idx = l.Lines.FindLastIndex(x => x.At <= pos);
        if (idx != _lyrIdx) { _lyrIdx = idx; ShowLyricLine(); }
        if (!i.IsPlaying || idx + 1 >= l.Lines.Count) return;
        if (_lyrTimer == null)
        {
            _lyrTimer = new DispatcherTimer(DispatcherPriority.Normal);
            _lyrTimer.Tick += (_, _) => LyricsTick();
        }
        var wait = l.Lines[idx + 1].At - pos;
        _lyrTimer.Interval = wait < TimeSpan.FromMilliseconds(20) ? TimeSpan.FromMilliseconds(20) : wait;
        _lyrTimer.Start();
    }

    string CurrentLyric()
    {
        var l = _lyr;
        if (l == null || _lyrIdx < 0 || _lyrIdx >= l.Lines.Count) return "♪";
        var t = l.Lines[_lyrIdx].Text;
        return string.IsNullOrWhiteSpace(t) ? "♪" : t;
    }

    void ShowLyricLine()
    {
        if (!_expanded && _capsuleLyrics)
        {
            CompactText.Text = CurrentLyric();
            var a = new DoubleAnimation(0.15, 1, Theme.Ms(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Timeline.SetDesiredFrameRate(a, Fps);
            CompactText.BeginAnimation(OpacityProperty, a);
        }
        if (_expanded && _s.LyricsOpen && _lyr?.Synced == true) HighlightLyric(animate: true);
    }

    // ---------------- текст на главной ----------------
    void RebuildLyricsView()
    {
        LyricsList.Children.Clear();
        _lyrCur = null;
        LyricsShift.BeginAnimation(TranslateTransform.YProperty, null);
        LyricsShift.Y = 0;
        var l = _lyr;
        LyricsStatus.Text = _lyrState switch
        {
            LyrState.Loading => "Ищу текст…",
            LyrState.NotFound => "Текст этой песни не нашёлся",
            LyrState.None => _s.Lyrics ? "Включи музыку — здесь появится текст" : "Поиск текстов выключен в настройках",
            _ => l?.Instrumental == true && !l.Synced && l.Plain == null ? "Инструментал — без слов" : "",
        };
        if (l == null) return;
        if (l.Synced)
            foreach (var ln in l.Lines) LyricsList.Children.Add(LyricBlock(string.IsNullOrWhiteSpace(ln.Text) ? "♪" : ln.Text, dim: true));
        else if (l.Plain != null)
            foreach (var ln in l.Plain.Split('\n')) LyricsList.Children.Add(LyricBlock(ln.TrimEnd('\r'), dim: false));
    }

    TextBlock LyricBlock(string text, bool dim)
    {
        var tb = new TextBlock { Text = text, FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3), FontWeight = FontWeights.SemiBold };
        if (dim) tb.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        return tb;
    }

    void HighlightLyric(bool animate)
    {
        if (_lyr?.Synced != true || LyricsList.Children.Count != _lyr.Lines.Count) return;
        if (_lyrCur != null) _lyrCur.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        _lyrCur = null;
        double target = 0;
        LyricsList.UpdateLayout();
        if (_lyrIdx >= 0 && _lyrIdx < LyricsList.Children.Count)
        {
            var tb = (TextBlock)LyricsList.Children[_lyrIdx];
            tb.Foreground = Brushes.White;
            _lyrCur = tb;
            var top = tb.TranslatePoint(new Point(0, 0), LyricsList).Y;
            target = LyricsCanvas.ActualHeight * 0.38 - top - tb.ActualHeight / 2; // текущая строка — чуть выше середины
        }
        target = Math.Min(0, target);
        if (Environment.TickCount64 < _lyrManualUntil) return;
        if (!animate) { LyricsShift.BeginAnimation(TranslateTransform.YProperty, null); LyricsShift.Y = target; return; }
        var a = new DoubleAnimation(target, Theme.Ms(520)) { EasingFunction = Theme.Spring(1.0, 7.0) };
        Timeline.SetDesiredFrameRate(a, Fps);
        LyricsShift.BeginAnimation(TranslateTransform.YProperty, a);
    }

    void ApplyLyricsOpen()
    {
        bool open = _s.LyricsOpen;
        Tiles.Visibility = open ? Visibility.Hidden : Visibility.Visible;
        LyricsView.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        LyricsBtn.SetResourceReference(ForegroundProperty, open ? "Accent" : "Dim");
        if (!open) return;
        _lyrIdx = -2;
        LyricsTick();
        if (_lyrIdx == -2 || _lyr?.Synced != true) return;
        Dispatcher.BeginInvoke(() => HighlightLyric(animate: false), DispatcherPriority.Loaded);
    }

    void Lyrics_Click(object sender, RoutedEventArgs e)
    {
        _s.LyricsOpen = !_s.LyricsOpen;
        try { _s.Save(); } catch { }
        ApplyLyricsOpen();
    }

    void Lyrics_Wheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        double max = Math.Max(0, LyricsList.ActualHeight - LyricsCanvas.ActualHeight);
        LyricsShift.BeginAnimation(TranslateTransform.YProperty, null);
        LyricsShift.Y = Math.Clamp(LyricsShift.Y + e.Delta / 3.0, -max, 0);
        _lyrManualUntil = Environment.TickCount64 + 4000; // покрутил сам — 4 с не прокручиваем за песней
    }
}
