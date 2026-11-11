using System;
using System.Text;
using System.Windows.Threading;

namespace Island.Core;

/// <summary>
/// Следит, развернуто ли активное окно на весь экран (игры, видео, F11 в браузере).
/// Работает на WinEvent-хуках — без опроса по таймеру.
/// </summary>
public sealed class FullscreenWatcher : IDisposable
{
    readonly Native.WinEventProc _proc; // держим ссылку, чтобы GC не собрал делегат
    readonly IntPtr _hookFg, _hookLoc;
    readonly IntPtr _ownHwndCheck;
    readonly DispatcherTimer _debounce;
    IntPtr _fg;
    bool _state;

    public event Action<bool>? Changed;
    public bool IsFullscreen => _state;

    public FullscreenWatcher(IntPtr islandHwnd)
    {
        _ownHwndCheck = islandHwnd;
        _proc = OnEvent;
        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Evaluate(); };
        _hookFg = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        _hookLoc = Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        _fg = Native.GetForegroundWindow();
        Evaluate();
    }

    void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObj, int idChild, uint thread, uint time)
    {
        if (ev == Native.EVENT_SYSTEM_FOREGROUND) { _fg = hwnd; Kick(); }
        // смена размера только активного окна (idObject == OBJID_WINDOW == 0) — остальное игнорим дёшево
        else if (idObj == 0 && hwnd == _fg) Kick();
    }

    void Kick() { if (!_debounce.IsEnabled) _debounce.Start(); }

    void Evaluate()
    {
        bool fs = Compute();
        if (fs != _state) { _state = fs; Changed?.Invoke(fs); }
    }

    bool Compute()
    {
        // D3D-игры в эксклюзивном полноэкранном режиме / презентации
        if (Native.SHQueryUserNotificationState(out int st) == 0 && (st == 3 || st == 4)) return true;

        var h = Native.GetForegroundWindow();
        if (h == IntPtr.Zero || h == _ownHwndCheck || h == Native.GetShellWindow() || h == Native.GetDesktopWindow()) return false;
        var sb = new StringBuilder(64);
        Native.GetClassName(h, sb, 64);
        var cls = sb.ToString();
        if (cls is "WorkerW" or "Progman" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow") return false;

        var mon = Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONEAREST);
        if (_ownHwndCheck != IntPtr.Zero && mon != Native.MonitorFromWindow(_ownHwndCheck, Native.MONITOR_DEFAULTTONEAREST)) return false;
        // развёрнутое окно с заголовком — не полноэкранное (актуально при автоскрытии панели задач)
        if (Native.IsZoomed(h) && (Native.GetWindowLong(h, -16) & 0x00C00000) == 0x00C00000) return false;
        if (!Native.GetWindowRect(h, out var r)) return false;
        var (b, _, _, _) = Native.MonitorOf(mon);
        return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
    }

    public void Dispose()
    {
        Native.UnhookWinEvent(_hookFg);
        Native.UnhookWinEvent(_hookLoc);
        _debounce.Stop();
    }
}
