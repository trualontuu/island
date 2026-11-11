using System;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace Island.Core;

/// <summary>
/// Клик по значку громкости Windows (рядом с часами) открывает наш микшер.
/// Хук мыши живёт в отдельном потоке и делает только сравнение координат — мышь не тормозит.
/// Координаты значка ищутся через UI Automation в фоне (не внутри хука — иначе можно повесить Explorer).
/// </summary>
public sealed class VolumeIconHook : IDisposable
{
    delegate IntPtr LowLevelProc(int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LowLevelProc proc, IntPtr mod, uint tid);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? t);
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr h; public uint msg; public IntPtr w, l; public uint t; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct MSLL { public int x, y; public uint data, flags, time; public IntPtr extra; }

    const int WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_MOUSEMOVE = 0x200;
    static readonly Regex VolumeName = new(@"^(Том|Громкость|Volume|Динамик|Наушник|Speaker|Headphone)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    readonly Action _onClick;
    readonly Thread _thread;
    LowLevelProc? _proc;
    uint _tid;
    IntPtr _hook;

    // кэш прямоугольника значка (физические пиксели) и полосы панели задач
    volatile int _l, _t, _r, _b, _taskbarTop = int.MaxValue;
    bool _down, _wasInTaskbar;
    long _lastScan;
    int _scanning;

    public VolumeIconHook(Action onClick)
    {
        _onClick = onClick;
        _thread = new Thread(Run) { IsBackground = true, Name = "VolumeIconHook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        Rescan();
    }

    void Run()
    {
        _tid = GetCurrentThreadId();
        _proc = Proc;
        _hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _proc, GetModuleHandle(null), 0);
        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
    }

    IntPtr Proc(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            int msg = w.ToInt32();
            var p = Marshal.PtrToStructure<MSLL>(l);
            if (msg == WM_MOUSEMOVE)
            {
                // зашли на панель задач — освежаем координаты значка (часы могли поменять ширину)
                bool inBar = p.y >= _taskbarTop;
                if (inBar && !_wasInTaskbar) Rescan();
                _wasInTaskbar = inBar;
            }
            else if (msg == WM_LBUTTONDOWN && Inside(p.x, p.y)) { _down = true; return (IntPtr)1; }
            else if (msg == WM_LBUTTONUP && _down)
            {
                _down = false;
                if (Inside(p.x, p.y)) ThreadPool.QueueUserWorkItem(_ => _onClick());
                return (IntPtr)1; // глотаем клик — штатная панель Windows не открывается
            }
        }
        return CallNextHookEx(_hook, code, w, l);
    }

    bool Inside(int x, int y) => _r > _l && x >= _l && x < _r && y >= _t && y < _b;

    /// <summary>Поиск значка через UI Automation — в пуле потоков, не чаще раза в 1.5 с.</summary>
    public void Rescan()
    {
        long now = Environment.TickCount64;
        if (now - _lastScan < 1500 || Interlocked.Exchange(ref _scanning, 1) == 1) return;
        _lastScan = now;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var bar = FindWindow("Shell_TrayWnd", null);
                if (bar == IntPtr.Zero) return;
                if (Native.GetWindowRect(bar, out var br)) _taskbarTop = br.Top;
                var root = AutomationElement.FromHandle(bar);
                var items = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayIcon"));
                foreach (AutomationElement e in items)
                {
                    var c = e.Current;
                    if (!c.ClassName.StartsWith("SystemTray.Omni", StringComparison.Ordinal) || !VolumeName.IsMatch(c.Name)) continue;
                    var r = c.BoundingRectangle;
                    _l = (int)r.Left; _t = (int)r.Top; _r = (int)r.Right; _b = (int)r.Bottom;
                    return;
                }
                _l = _r = 0; // не нашли — ничего не перехватываем
            }
            catch { }
            finally { Interlocked.Exchange(ref _scanning, 0); }
        });
    }

    public void Dispose()
    {
        if (_tid != 0) PostThreadMessage(_tid, 0x12 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
    }
}
