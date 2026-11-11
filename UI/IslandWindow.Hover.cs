using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Island.Core;

namespace Island.UI;

/// <summary>
/// Автораскрытие: подвёл курсор к острову и чуть задержал — он раскрывается сам, без клика.
/// Ушёл курсором — сворачивается. Работает на том же опросе GetCursorPos, что и спящий режим.
/// </summary>
public partial class IslandWindow
{
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

    const int HoverDwellMs = 50;    // сколько держать курсор в зоне до раскрытия (защита от случайных пролётов)
    const int LeaveDelayMs = 450;   // сколько курсор должен быть вне острова до сворачивания
    long _zoneSince = -1, _outsideSince = -1;
    bool _zoneArmed = true, _autoExpanded, _hoverTest;

    static bool MouseDown() => (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;

    /// <returns>true — курсор рядом с островом (нужен частый опрос)</returns>
    bool HoverTick(Native.POINT p, long now)
    {
        if (_userHidden || _hiddenByFullscreen || _asleep || !IsVisible || _overlayOpen || (SnapDir != null && !_hoverTest)) { _zoneSince = -1; return false; }
        Rect r;
        double k;
        try
        {
            if (PresentationSource.FromVisual(Shell) == null) return false;
            r = new Rect(Shell.PointToScreen(new Point(0, 0)), Shell.PointToScreen(new Point(Shell.ActualWidth, Shell.ActualHeight)));
            k = VisualTreeHelper.GetDpi(this).DpiScaleX;
        }
        catch { return false; }
        var pt = new Point(p.X, p.Y);

        if (!_expanded)
        {
            // зона: остров + 28 px по бокам, до самого верха экрана и 22 px ниже
            var zone = new Rect(r.Left - 28 * k, r.Top - 40 * k, r.Width + 56 * k, r.Height + 62 * k);
            bool inZone = zone.Contains(pt);
            if (!inZone) { _zoneSince = -1; _zoneArmed = true; }
            else if (_passCtx)
            {
                // над браузером: зона вокруг — это вкладки, по ним не раскрываемся.
                // Раскрываемся, только если курсор на самом острове и зажат Ctrl/Alt или курсор замер.
                _zoneSince = -1;
                var over = r; over.Inflate(4 * k, 4 * k);
                if (_zoneArmed && over.Contains(pt) && !MouseDown() && (ModKey() || now - Math.Max(_lastMove, _passClickAt) >= PassDwellMs))
                {
                    _zoneArmed = false; _autoExpanded = true;
                    SetPassing(false);
                    Expand(_agent?.Pending != null ? "agent" : _page);
                }
            }
            else if (_zoneArmed && !MouseDown())
            {
                if (_zoneSince < 0) _zoneSince = now;
                else if (now - _zoneSince >= HoverDwellMs)
                {
                    _zoneSince = -1; _zoneArmed = false; _autoExpanded = true;
                    Expand(_agent?.Pending != null ? "agent" : _page);
                }
            }
            else _zoneSince = -1;
            var nearZone = zone; nearZone.Inflate(220 * k, 160 * k); // заранее переходим на частый опрос
            return nearZone.Contains(pt);
        }

        if (!_autoExpanded) return false; // раскрыли кликом/хоткеем — сворачивается по-старому
        var inside = r; inside.Inflate(14 * k, 14 * k);
        if (inside.Contains(pt)) { _outsideSince = -1; return true; }
        if (_pinned || _overlayOpen || MouseDown() || (ChatInput.IsKeyboardFocused && IsActive)) { _outsideSince = -1; return true; }
        if (_outsideSince < 0) _outsideSince = now;
        else if (now - _outsideSince >= LeaveDelayMs) { _outsideSince = -1; Collapse(); _zoneArmed = true; } // курсор уже далеко — можно снова раскрывать
        return true;
    }
}
