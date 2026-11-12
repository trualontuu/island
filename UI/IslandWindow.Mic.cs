using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Island.Services;

namespace Island.UI;

/// <summary>
/// Микрофон: точка в капсуле, пока им кто-то пользуется (Discord, TeamSpeak, браузер…),
/// кнопка и горячая клавиша «выключить везде», подсказка, если говоришь в выключенный микрофон.
/// </summary>
public partial class IslandWindow
{
    MicService? _mic;
    DispatcherTimer? _talkTimer;
    int _talkHits;
    long _talkWarnAt;

    void InitMic()
    {
        if (_mic != null) return;
        try { _mic = new MicService(Dispatcher); _mic.Changed += OnMicChanged; }
        catch { _mic = null; }
        OnMicChanged();
    }

    void OnMicChanged()
    {
        UpdateMicButton();
        if (!_expanded) UpdateCompact(); else UpdateMicMark();
        WatchMutedTalk();
    }

    void UpdateMicMark()
    {
        bool show = _s.MicIndicator && _mic?.InUse == true;
        MicMark.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        bool muted = _mic!.Muted;
        MicDot.Visibility = muted ? Visibility.Collapsed : Visibility.Visible;
        MicOffGlyph.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdateMicButton()
    {
        bool muted = _mic?.Muted == true;
        MicBtn.Content = muted ? "" : "";
        MicBtn.Foreground = muted ? new SolidColorBrush(Color.FromRgb(255, 69, 58)) : Brushes.White;
        var tip = _mic == null || !_mic.HasDevice ? "Микрофон не найден" : muted ? "Микрофон выключен — нажми, чтобы включить" : "Микрофон включён — нажми, чтобы выключить везде";
        if (_mic?.InUse == true) tip += "\nСейчас слушает: " + string.Join(", ", _mic.Users);
        if (!string.IsNullOrWhiteSpace(_s.HotkeyMic)) tip += "\n" + _s.HotkeyMic;
        MicBtn.ToolTip = tip;
    }

    void Mic_Click(object sender, RoutedEventArgs e) => ToggleMic();

    void ToggleMic()
    {
        if (_mic == null || !_mic.HasDevice) { Toast("Микрофон не найден", "", Mood.Alert, 2); return; }
        bool muted = _mic.ToggleMuteAll();
        Toast(muted ? "Микрофон выключен" : "Микрофон включён", muted ? "" : "", muted ? Mood.Alert : Mood.Happy, 1.6);
    }

    /// <summary>Микрофон выключен, а им пользуются (идёт звонок) — следим за уровнем: вдруг человек говорит.</summary>
    void WatchMutedTalk()
    {
        bool need = _mic != null && _mic.Muted && _mic.InUse;
        if (!need) { _talkTimer?.Stop(); _talkHits = 0; return; }
        if (_talkTimer == null)
        {
            _talkTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
            _talkTimer.Tick += (_, _) =>
            {
                if (_mic == null || _hiddenByFullscreen) return;
                if (_mic.Peak > 0.08f) _talkHits++; else if (_talkHits > 0) _talkHits--;
                long now = Environment.TickCount64;
                if (_talkHits >= 5 && now - _talkWarnAt > 20000)
                {
                    _talkWarnAt = now; _talkHits = 0;
                    Toast("Тебя не слышно — микрофон выключен", "", Mood.Alert, 3);
                }
            };
        }
        _talkTimer.Start();
    }
}
