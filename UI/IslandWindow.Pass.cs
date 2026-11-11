using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Island.Core;

namespace Island.UI;

/// <summary>
/// «Не мешать вкладкам»: если под островом браузер (или любое окно — по настройке), то пока курсор над свёрнутым
/// островом, окно становится прозрачным для мыши (WS_EX_TRANSPARENT) и бледнеет — клики уходят во вкладку под ним.
/// Открыть остров: зажать Ctrl/Alt (становится кликабельным) или задержать курсор на нём неподвижно.
/// </summary>
public partial class IslandWindow
{
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    const int PassDwellMs = 600;     // сколько держать курсор неподвижно над островом, чтобы он раскрылся
    const double PassOpacity = 0.28;

    static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "browser" /* Яндекс */, "opera", "brave", "vivaldi", "arc", "chromium",
        "librewolf", "waterfox", "floorp", "thorium", "zen", "palemoon", "yandex",
    };
    static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "Windows.UI.Core.CoreWindow",
    };

    IntPtr _fgCache;
    int _fgKind;            // 0 — рабочий стол/панель/наш процесс, 1 — обычное окно, 2 — браузер
    bool _passCtx, _passing;
    long _passClickAt;
    internal bool _passForce; // для --snap теста: считать, что под островом браузер

    static bool ModKey() => (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0;

    bool IslandScreenRect(out Rect r, out double k)
    {
        r = Rect.Empty; k = 1;
        try
        {
            if (PresentationSource.FromVisual(Shell) == null) return false;
            r = new Rect(Shell.PointToScreen(new Point(0, 0)), Shell.PointToScreen(new Point(Shell.ActualWidth, Shell.ActualHeight)));
            k = VisualTreeHelper.GetDpi(this).DpiScaleX;
            return true;
        }
        catch { return false; }
    }

    /// <returns>true — курсор рядом с островом (нужен частый опрос)</returns>
    bool PassTick(Native.POINT p)
    {
        _passCtx = false;
        bool want = false, near = false;
        if (_s.ClickThrough && !_expanded && IsVisible && !_asleep && !_overlayOpen && !_userHidden && !_hiddenByFullscreen
            && (SnapDir == null || _passForce) && IslandScreenRect(out var r, out var k))
        {
            var pt = new Point(p.X, p.Y);
            var nearR = r; nearR.Inflate(220 * k, 160 * k);
            near = nearR.Contains(pt);
            if (near && (_passForce || AppUnder(r)))
            {
                _passCtx = true;
                var over = r; over.Inflate(4 * k, 4 * k);
                want = over.Contains(pt) && !ModKey();
                if (MouseDown()) _passClickAt = Environment.TickCount64; // кликнули по вкладке сквозь остров — отсчёт задержки заново
            }
        }
        SetPassing(want);
        return near;
    }

    /// <summary>Активное окно заходит под остров и это браузер (или любое окно, если так выбрано).</summary>
    bool AppUnder(Rect r)
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd || !Native.GetWindowRect(fg, out var w)) return false;
        if (w.Left >= r.Right || w.Right <= r.Left || w.Top >= r.Bottom || w.Bottom <= r.Top) return false;
        if (fg != _fgCache) { _fgCache = fg; _fgKind = KindOf(fg); }
        return _fgKind == 2 || (_fgKind == 1 && _s.ClickThroughAll);
    }

    static int KindOf(IntPtr h)
    {
        try
        {
            var sb = new StringBuilder(64);
            Native.GetClassName(h, sb, sb.Capacity);
            if (ShellClasses.Contains(sb.ToString())) return 0;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId) return 0;
            using var pr = Process.GetProcessById((int)pid);
            return Browsers.Contains(pr.ProcessName) ? 2 : 1;
        }
        catch { return 1; }
    }

    void SetPassing(bool on)
    {
        if (on == _passing || _hwnd == IntPtr.Zero) return;
        _passing = on;
        int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, on ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT);
        var a = new DoubleAnimation(on ? PassOpacity : 1, Theme.Ms(on ? 140 : 200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Timeline.SetDesiredFrameRate(a, Fps);
        BeginAnimation(OpacityProperty, a);
    }
}
