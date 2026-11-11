using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Island.Core;

namespace Island.UI;

/// <summary>
/// Спящий режим: если мышь не двигалась <see cref="SleepAfterMs"/> мс, остров уезжает вверх за край экрана
/// и окно прячется (ничего не рисуется). Любое движение мыши, уведомление или горячая клавиша — возвращает.
/// Проверка — GetCursorPos 4 раза в секунду (без хуков, ~0% CPU).
/// </summary>
public partial class IslandWindow
{
    const int SleepAfterMs = 20_000;
    readonly DispatcherTimer _idleTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(250) };
    Native.POINT _lastCursor;
    long _lastMove;
    bool _asleep, _idleHooked;

    void StartIdleWatch()
    {
        if (!_idleHooked) { _idleHooked = true; _idleTimer.Tick += (_, _) => IdleTick(); }
        if (!_s.AutoSleep && _asleep) Wake();
        if (!_s.ClickThrough) SetPassing(false);
        if ((!_s.AutoSleep && !_s.AutoExpand && !_s.ClickThrough) || _userHidden) { _idleTimer.Stop(); SetPassing(false); return; }
        Native.GetCursorPos(out _lastCursor);
        _lastMove = Environment.TickCount64;
        _idleTimer.Start();
    }

    void IdleTick()
    {
        if (!Native.GetCursorPos(out var p)) return;
        long now = Environment.TickCount64;
        bool moved = p.X != _lastCursor.X || p.Y != _lastCursor.Y;
        if (moved)
        {
            _lastCursor = p; _lastMove = now;
            if (_asleep) Wake();
        }
        else if (_s.AutoSleep && !_asleep && now - _lastMove >= SleepAfterMs && CanSleep()) Sleep();
        // «не мешать вкладкам» + автораскрытие: рядом с островом опрашиваем часто (отклик), вдали — редко (нагрузка ~0)
        bool near = PassTick(p);
        near |= _s.AutoExpand && HoverTick(p, now);
        var want = TimeSpan.FromMilliseconds(near ? 15 : _s.AutoExpand || _s.ClickThrough ? 80 : 250);
        if (_idleTimer.Interval != want) _idleTimer.Interval = want;
    }

    bool CanSleep() => !_expanded && !_userHidden && !_hiddenByFullscreen && !_overlayOpen
        && _toastText == null && _agent?.Pending == null && !Shell.IsMouseOver;

    void Sleep()
    {
        _asleep = true;
        SetEq(false);
        Slide(-(Math.Max(Shell.ActualHeight, 32) + 14), 0, 520, new CubicEase { EasingMode = EasingMode.EaseInOut },
              () => { if (_asleep) { Hide(); Native.TrimMemory(); } });
    }

    /// <summary>Проснуться. Если остров скрыт по другой причине (полный экран / «Скрыть») — только сбросить позицию.</summary>
    void Wake()
    {
        if (!_asleep) return;
        _asleep = false;
        if (_userHidden || _hiddenByFullscreen) { ResetSlide(); return; }
        if (!IsVisible)
        {
            Show();
            _placed = default;
            PlaceWindow(CurrentBox());
        }
        Slide(0, 1, 620, Theme.Spring(0.75, 7.2), null);
        UpdateCompact(force: true);
        UpdateTimers();
    }

    void ResetSlide()
    {
        ShellSlide.BeginAnimation(TranslateTransform.YProperty, null);
        Shell.BeginAnimation(OpacityProperty, null);
        ShellSlide.Y = 0; Shell.Opacity = 1;
    }

    void Slide(double y, double opacity, int ms, IEasingFunction ease, Action? done)
    {
        var a = new DoubleAnimation(y, Theme.Ms(ms)) { EasingFunction = ease };
        var o = new DoubleAnimation(opacity, Theme.Ms(ms * 0.8)) { BeginTime = opacity > 0 ? TimeSpan.Zero : Theme.Ms(ms * 0.2) };
        Timeline.SetDesiredFrameRate(a, Fps); Timeline.SetDesiredFrameRate(o, Fps);
        if (done != null) a.Completed += (_, _) => done();
        ShellSlide.BeginAnimation(TranslateTransform.YProperty, a);
        Shell.BeginAnimation(OpacityProperty, o);
    }
}
