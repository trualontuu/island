using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Island.Core;

/// <summary>
/// Одно скрытое message-only окно на всё: горячие клавиши, слушатель буфера, иконка в трее.
/// Никаких таймеров — только сообщения Windows.
/// </summary>
public sealed class SystemHost : IDisposable
{
    readonly HwndSource _src;
    readonly Dictionary<int, Action> _hotkeys = new();
    int _nextId = 1;
    const int WM_TRAY = 0x8001;
    readonly uint _wmTaskbarCreated;
    Native.NOTIFYICONDATA _nid;
    IntPtr _hIcon;

    public event Action? ClipboardChanged;
    public event Action? TrayLeftClick;
    public event Action? TrayRightClick;

    public IntPtr Handle => _src.Handle;

    public SystemHost()
    {
        var p = new HwndSourceParameters("IslandHost") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ };
        _src = new HwndSource(p);
        _src.AddHook(WndProc);
        _wmTaskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        Native.AddClipboardFormatListener(_src.Handle);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_HOTKEY:
                if (_hotkeys.TryGetValue(w.ToInt32(), out var a)) a();
                handled = true; break;
            case Native.WM_CLIPBOARDUPDATE:
                ClipboardChanged?.Invoke(); break;
            case WM_TRAY:
                int ev = l.ToInt32() & 0xFFFF;
                if (ev == 0x0202) TrayLeftClick?.Invoke();       // WM_LBUTTONUP
                else if (ev == 0x0205) TrayRightClick?.Invoke(); // WM_RBUTTONUP
                break;
            default:
                if (msg == (int)_wmTaskbarCreated && _hIcon != IntPtr.Zero) Native.Shell_NotifyIcon(Native.NIM_ADD, ref _nid);
                break;
        }
        return IntPtr.Zero;
    }

    // ---------- горячие клавиши ----------
    public void ClearHotkeys()
    {
        foreach (var id in _hotkeys.Keys) Native.UnregisterHotKey(_src.Handle, id);
        _hotkeys.Clear();
    }

    /// <returns>false, если сочетание занято</returns>
    public bool RegisterHotkey(string combo, Action action)
    {
        if (!TryParse(combo, out uint mods, out uint vk)) return false;
        int id = _nextId++;
        if (!Native.RegisterHotKey(_src.Handle, id, mods | 0x4000 /* MOD_NOREPEAT */, vk)) return false;
        _hotkeys[id] = action;
        return true;
    }

    static bool TryParse(string combo, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "alt": mods |= 1; break;
                case "ctrl": case "control": mods |= 2; break;
                case "shift": mods |= 4; break;
                case "win": mods |= 8; break;
                case "space": vk = 0x20; break;
                case "enter": vk = 0x0D; break;
                case "tab": vk = 0x09; break;
                case "`": case "tilde": vk = 0xC0; break;
                case "left": vk = 0x25; break;
                case "up": vk = 0x26; break;
                case "right": vk = 0x27; break;
                case "down": vk = 0x28; break;
                default:
                    if (raw.Length == 1 && char.IsLetterOrDigit(raw[0])) vk = char.ToUpperInvariant(raw[0]);
                    else if (raw.Length >= 2 && (raw[0] == 'F' || raw[0] == 'f') && int.TryParse(raw[1..], out int f) && f is >= 1 and <= 24) vk = (uint)(0x70 + f - 1);
                    else return false;
                    break;
            }
        }
        return vk != 0;
    }

    // ---------- трей ----------
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint n);

    public void ShowTray(string tip)
    {
        // иконка из самого exe (та же, что на ярлыке); если не вышло — рисуем кодом
        var small = new IntPtr[1];
        try { if (Environment.ProcessPath != null && ExtractIconEx(Environment.ProcessPath, 0, null, small, 1) > 0) _hIcon = small[0]; } catch { }
        if (_hIcon == IntPtr.Zero) _hIcon = CreateTrayIcon();
        _nid = new Native.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
            hWnd = _src.Handle, uID = 1,
            uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP,
            uCallbackMessage = WM_TRAY, hIcon = _hIcon, szTip = tip,
            szInfo = "", szInfoTitle = "",
        };
        Native.Shell_NotifyIcon(Native.NIM_ADD, ref _nid);
    }

    /// <summary>Иконка рисуется кодом: чёрная капсула с двумя глазами — без .ico и System.Drawing.</summary>
    static IntPtr CreateTrayIcon()
    {
        const int S = 32;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(20, 20, 22)), new Pen(new SolidColorBrush(Color.FromRgb(90, 90, 96)), 1.5), new Rect(1, 8, 30, 16), 8, 8);
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(10, 12, 4, 8), 2, 2);
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(18, 12, 4, 8), 2, 2);
        }
        var rtb = new RenderTargetBitmap(S, S, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var px = new byte[S * S * 4];
        rtb.CopyPixels(px, S * 4, 0);
        var color = Native.CreateBitmap(S, S, 1, 32, px);
        var mask = Native.CreateBitmap(S, S, 1, 1, new byte[S * S / 8]);
        var ii = new Native.ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
        var h = Native.CreateIconIndirect(ref ii);
        Native.DeleteObject(color); Native.DeleteObject(mask);
        return h;
    }

    public void Dispose()
    {
        ClearHotkeys();
        Native.RemoveClipboardFormatListener(_src.Handle);
        if (_hIcon != IntPtr.Zero)
        {
            Native.Shell_NotifyIcon(Native.NIM_DELETE, ref _nid);
            Native.DestroyIcon(_hIcon);
        }
        _src.Dispose();
    }
}
